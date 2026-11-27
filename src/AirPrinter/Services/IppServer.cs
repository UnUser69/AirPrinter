using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirPrinter.Services;

public sealed class PrinterSnapshot
{
    public string QueueName { get; set; } = "";
    public string Model { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string UuidUrn { get; set; } = "";
    public bool Color { get; set; }
    public bool Duplex { get; set; }
    public int Port { get; set; }
    public List<string> Hosts { get; set; } = [];
}

public sealed class IppServer : IDisposable
{
    private const int MaxBodyBytes = 32 * 1024 * 1024;
    private const string Rejected = "Запрос отклонён.";

    private readonly IJobPrinter _printer;
    private readonly Func<PrinterSnapshot> _snapshot;
    private readonly Action<string> _log;
    private readonly object _jobGate = new();
    private readonly object _printGate = new();
    private readonly List<TrackedJob> _jobs = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _nextJobId;
    private const int MaxClients = 8;
    private int _clients;
    private int _sawAttributes;
    private readonly object _clientGate = new();
    private readonly List<TcpClient> _clientsLive = [];
    private bool _disposed;

    public IppServer(IJobPrinter printer, Func<PrinterSnapshot> snapshot, Action<string> log)
    {
        _printer = printer;
        _snapshot = snapshot;
        _log = log;
    }

    public int Port { get; private set; }

    public void Start(int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener != null)
        {
            throw new InvalidOperationException("IPP-сервер уже запущен.");
        }

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
    }

    internal int OpenClients
    {
        get
        {
            lock (_clientGate)
            {
                return _clientsLive.Count;
            }
        }
    }

    public void DropClients()
    {
        TcpClient[] open;
        lock (_clientGate)
        {
            open = _clientsLive.ToArray();
        }

        foreach (TcpClient client in open)
        {
            try
            {
                client.Close();
            }
            catch (SocketException)
            {
            }
        }

        if (open.Length > 0)
        {
            _log("Закрыто подключений IPP: " + open.Length + ". Телефон заново увидит выбранный принтер.");
        }
    }

    public void Stop()
    {
        DropClients();
        _cts?.Cancel();
        _listener?.Stop();
        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _acceptLoop = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void AcceptLoop(CancellationToken cancellationToken)
    {
        TcpListener? listener = _listener;
        if (listener == null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = Task.Run(() =>
            {
                if (Interlocked.Increment(ref _clients) > MaxClients)
                {
                    Interlocked.Decrement(ref _clients);
                    client.Close();
                    return;
                }

                lock (_clientGate)
                {
                    _clientsLive.Add(client);
                }

                try
                {
                    HandleClient(client);
                }
                finally
                {
                    lock (_clientGate)
                    {
                        _clientsLive.Remove(client);
                    }

                    Interlocked.Decrement(ref _clients);
                }
            }, cancellationToken);
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            client.ReceiveTimeout = 180000;
            client.SendTimeout = 30000;
            NetworkStream stream = client.GetStream();
            try
            {
                while (client.Connected)
                {
                    HttpRequestHeader? header = ReadHeader(stream);
                    if (header == null)
                    {
                        break;
                    }

                    if (!header.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteAscii(stream, "HTTP/1.1 405 Method Not Allowed\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                        break;
                    }

                    if (header.ExpectContinue)
                    {
                        WriteAscii(stream, "HTTP/1.1 100 Continue\r\n\r\n");
                    }

                    byte[] body = ReadBody(stream, header);
                    IPAddress remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                    byte[] response = HandleIpp(body, remoteAddress);
                    WriteAscii(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/ipp\r\nContent-Length: " + response.Length.ToString() + "\r\nConnection: " + (header.KeepAlive ? "keep-alive" : "close") + "\r\n\r\n");
                    stream.Write(response);
                    stream.Flush();
                    if (!header.KeepAlive)
                    {
                        break;
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                _log("Ошибка IPP: " + ex.GetType().Name);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
            {
            }
            catch (Exception ex)
            {
                _log("Ошибка соединения: " + ex.GetType().Name);
            }
        }
    }

    private byte[] HandleIpp(byte[] body, IPAddress remote)
    {
        IppMessage request;
        try
        {
            request = IppCodec.Parse(body);
        }
        catch (InvalidDataException)
        {
            return Error(2, 0, 1, IppCodec.BadRequest, Rejected);
        }

        try
        {
            return request.OperationOrStatus switch
            {
                IppCodec.GetPrinterAttributes => PrinterAttributes(request),
                IppCodec.ValidateJob => Validate(request),
                IppCodec.PrintJob => PrintJob(request, remote),
                IppCodec.CreateJob => CreateJob(request, remote),
                IppCodec.SendDocument => SendDocument(request),
                IppCodec.GetJobs => GetJobs(request),
                IppCodec.GetJobAttributes => GetJobAttributes(request),
                IppCodec.CancelJob => Cancel(request, remote),
                _ => Error(request, IppCodec.BadRequest, Rejected),
            };
        }
        catch (DocumentFormatException)
        {
            return Error(request, IppCodec.DocumentFormatNotSupported, "Формат не поддерживается.");
        }
        catch (InvalidDataException)
        {
            return Error(request, IppCodec.BadRequest, Rejected);
        }
        catch (KeyNotFoundException)
        {
            return Error(request, IppCodec.NotFound, Rejected);
        }
        catch (Exception)
        {
            return Error(request, 0x0500, Rejected);
        }
    }

    private byte[] PrinterAttributes(IppMessage request)
    {
        if (Interlocked.Exchange(ref _sawAttributes, 1) == 0)
        {
            _log("Устройство запросило атрибуты принтера.");
        }

        PrinterSnapshot snapshot = _snapshot();
        string[] uris = PrinterUris(snapshot);
        string model = string.IsNullOrWhiteSpace(snapshot.Model) ? snapshot.QueueName : snapshot.Model;
        string[] urf = UrfKeywords(snapshot.Color, snapshot.Duplex);
        string[] sides = snapshot.Duplex
            ? ["one-sided", "two-sided-long-edge", "two-sided-short-edge"]
            : ["one-sided"];
        int[] operations =
        [
            IppCodec.PrintJob,
            IppCodec.ValidateJob,
            IppCodec.CreateJob,
            IppCodec.SendDocument,
            IppCodec.CancelJob,
            IppCodec.GetJobAttributes,
            IppCodec.GetJobs,
            IppCodec.GetPrinterAttributes,
        ];

        var writer = Begin(request, IppCodec.Ok);
        writer.Group(IppCodec.PrinterGroup);
        writer.Uri("printer-uri-supported", uris);
        writer.Keyword("uri-security-supported", Enumerable.Repeat("none", uris.Length).ToArray());
        writer.Keyword("uri-authentication-supported", Enumerable.Repeat("none", uris.Length).ToArray());
        writer.Name("printer-name", snapshot.ServiceName);
        writer.Name("printer-dns-sd-name", snapshot.ServiceName);
        writer.Text("printer-info", snapshot.QueueName);
        writer.Text("printer-make-and-model", model);
        writer.Text("printer-location", "AirPrinter");
        writer.Uri("printer-uuid", snapshot.UuidUrn);
        writer.Boolean("printer-is-accepting-jobs", true);
        int state = ActiveJobCount() > 0 ? 4 : 3;
        writer.Enum("printer-state", state);
        writer.Keyword("printer-state-reasons", "none");
        writer.Integer("queued-job-count", ActiveJobCount());
        writer.Keyword("ipp-versions-supported", "1.1", "2.0");
        writer.Enum("operations-supported", operations);
        writer.Charset("charset-configured", "utf-8");
        writer.Charset("charset-supported", "utf-8");
        writer.Language("natural-language-configured", "en-us");
        writer.Language("generated-natural-language-supported", "en-us");
        writer.Mime("document-format-default", "application/pdf");
        writer.Mime("document-format-supported", "application/pdf", "image/jpeg", "image/png", "image/urf", "image/pwg-raster");
        writer.Mime("document-format-preferred", "application/pdf");
        writer.Keyword("pdl-override-supported", "not-attempted");
        writer.Keyword("compression-supported", "none");
        writer.Keyword("urf-supported", urf);
        writer.Keyword("pwg-raster-document-type-supported", snapshot.Color ? ["sgray_8", "srgb_8"] : ["sgray_8"]);
        writer.Keyword("pdf-versions-supported", "iso-32000-1_2008");
        writer.Keyword("ipp-features-supported", "ipp-everywhere", "airprint-1.8", "airprint-1.4");
        writer.Resolution("pwg-raster-document-resolution-supported", (300, 300), (600, 600));
        if (snapshot.Duplex)
        {
            writer.Keyword("pwg-raster-document-sheet-back", "normal");
        }

        writer.Text("printer-device-id", BonjourAdvertiser.DeviceId(snapshot.ServiceName, model));
        writer.Boolean("color-supported", snapshot.Color);
        writer.Keyword("print-color-mode-default", snapshot.Color ? "color" : "monochrome");
        writer.Keyword("print-color-mode-supported", snapshot.Color ? ["color", "monochrome"] : ["monochrome"]);
        writer.Keyword("sides-default", "one-sided");
        writer.Keyword("sides-supported", sides);
        writer.Range("copies-supported", 1, 99);
        writer.Integer("copies-default", 1);
        string[] mediaNames = MediaSizes.Catalog.Select(item => item.Name).ToArray();
        writer.Keyword("media-default", mediaNames[0]);
        writer.Keyword("media-supported", mediaNames);
        writer.Keyword("media-ready", mediaNames[0]);
        writer.Keyword("media-col-supported", "media-size", "media-top-margin", "media-left-margin", "media-right-margin", "media-bottom-margin", "media-type", "media-source");
        writer.Integer("media-bottom-margin-supported", 0);
        writer.Integer("media-left-margin-supported", 0);
        writer.Integer("media-right-margin-supported", 0);
        writer.Integer("media-top-margin-supported", 0);
        writer.Keyword("media-source-supported", "main", "auto");
        writer.Keyword("media-type-supported", "stationery", "auto");
        WriteMediaCollection(writer, "media-col-default", MediaSizes.Catalog[0]);
        WriteMediaCollection(writer, "media-col-ready", MediaSizes.Catalog[0]);
        for (int i = 0; i < MediaSizes.Catalog.Length; i++)
        {
            (_, double widthMm, double heightMm) = MediaSizes.Catalog[i];
            writer.Collection(i == 0 ? "media-size-supported" : "", size =>
            {
                size.Integer("x-dimension", (int)Math.Round(widthMm * 100));
                size.Integer("y-dimension", (int)Math.Round(heightMm * 100));
            });
        }

        for (int i = 0; i < MediaSizes.Catalog.Length; i++)
        {
            WriteMediaCollection(writer, i == 0 ? "media-col-database" : "", MediaSizes.Catalog[i]);
        }

        string infoHost = snapshot.Hosts.FirstOrDefault(host => !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Hosts.FirstOrDefault()
            ?? "127.0.0.1";
        writer.Uri("printer-more-info", "http://" + infoHost + ":" + snapshot.Port + "/");
        writer.Keyword("output-bin-default", "face-down");
        writer.Keyword("output-bin-supported", "face-down");
        writer.Enum("finishings-default", 3);
        writer.Enum("finishings-supported", 3);
        writer.Enum("print-quality-default", 4);
        writer.Enum("print-quality-supported", 3, 4, 5);
        writer.Enum("orientation-requested-default", 3);
        writer.Enum("orientation-requested-supported", 3, 4);
        writer.Boolean("multiple-document-jobs-supported", false);
        writer.Keyword("which-jobs-supported", "not-completed", "completed");
        writer.End();
        return writer.ToArray();
    }

    private byte[] Validate(IppMessage request)
    {
        string? format = request.GetText("document-format");
        if (!string.IsNullOrWhiteSpace(format) && !IsAcceptedFormat(format))
        {
            throw new DocumentFormatException("Формат не поддерживается: " + format);
        }

        return Success(request);
    }

    private byte[] PrintJob(IppMessage request, IPAddress remote)
    {
        if (request.Document.Length == 0)
        {
            throw new InvalidDataException("Пустое задание.");
        }

        if (AnotherJobPrinting(null))
        {
            return Error(request, IppCodec.Busy, Rejected);
        }

        string format = PrintRenderer.ResolveFormat(request.GetText("document-format"), request.Document);
        TrackedJob job = AddJob(request, remote, format, request.Document);
        Queue(job);
        return JobResponse(request, job);
    }

    private byte[] CreateJob(IppMessage request, IPAddress remote)
    {
        if (AnotherJobPrinting(null))
        {
            return Error(request, IppCodec.Busy, Rejected);
        }

        string? declared = request.GetText("document-format");
        if (!string.IsNullOrWhiteSpace(declared) && !IsAcceptedFormat(declared))
        {
            throw new DocumentFormatException("Формат не поддерживается.");
        }

        TrackedJob job = AddJob(request, remote, string.IsNullOrWhiteSpace(declared) ? "application/pdf" : declared, null);
        return JobResponse(request, job);
    }

    private byte[] SendDocument(IppMessage request)
    {
        int? id = RequestedJobId(request);
        TrackedJob job = FindJob(id) ?? throw new KeyNotFoundException("Задание не найдено.");
        if (job.Started || job.State is 7 or 8 or 9)
        {
            throw new InvalidDataException("Задание уже завершено.");
        }

        if (request.Document.Length == 0)
        {
            throw new InvalidDataException("Пустой документ.");
        }

        string format = PrintRenderer.ResolveFormat(request.GetText("document-format") ?? job.Format, request.Document);
        lock (_jobGate)
        {
            job.Format = format;
            job.Document = request.Document;
            job.Copies = ReadCopies(request);
        }

        bool last = request.GetBoolean("last-document") ?? true;
        if (last)
        {
            if (AnotherJobPrinting(job))
            {
                return Error(request, IppCodec.Busy, Rejected);
            }

            Queue(job);
        }

        return JobResponse(request, job);
    }

    private byte[] GetJobs(IppMessage request)
    {
        string which = request.GetText("which-jobs") ?? "not-completed";
        string? user = request.GetBoolean("my-jobs") == true ? request.GetText("requesting-user-name") : null;
        int limit = request.GetInteger("limit") ?? 50;
        List<TrackedJob> jobs;
        lock (_jobGate)
        {
            IEnumerable<TrackedJob> query = _jobs;
            query = which == "completed"
                ? query.Where(job => job.State is 7 or 8 or 9)
                : query.Where(job => job.State is 3 or 4 or 5 or 6);
            if (!string.IsNullOrWhiteSpace(user))
            {
                query = query.Where(job => string.Equals(job.User, user, StringComparison.Ordinal));
            }

            jobs = query.Take(Math.Max(1, limit)).ToList();
        }

        var writer = Begin(request, IppCodec.Ok);
        foreach (TrackedJob job in jobs)
        {
            WriteJob(writer, request, job);
        }

        writer.End();
        return writer.ToArray();
    }

    private byte[] GetJobAttributes(IppMessage request)
    {
        TrackedJob job = FindJob(RequestedJobId(request)) ?? throw new KeyNotFoundException("Задание не найдено.");
        var writer = Begin(request, IppCodec.Ok);
        WriteJob(writer, request, job);
        writer.End();
        return writer.ToArray();
    }

    private byte[] Cancel(IppMessage request, IPAddress remote)
    {
        TrackedJob job = FindJob(RequestedJobId(request)) ?? throw new KeyNotFoundException("Задание не найдено.");
        if (!SamePeer(job.RemoteAddress, remote.ToString()))
        {
            return Error(request, IppCodec.NotFound, Rejected);
        }

        job.Cancel.Cancel();
        if (!job.Started)
        {
            job.State = 7;
            job.Reason = "job-canceled-by-user";
        }

        _log("Задание " + job.Id + " отменяется.");
        return JobResponse(request, job);
    }

    internal static bool SamePeer(string owner, string caller)
    {
        return string.Equals(owner, caller, StringComparison.OrdinalIgnoreCase);
    }

    private bool AnotherJobPrinting(TrackedJob? current)
    {
        lock (_jobGate)
        {
            return _jobs.Any(job => !ReferenceEquals(job, current) && job.Started && job.State is 3 or 5);
        }
    }

    private TrackedJob AddJob(IppMessage request, IPAddress remote, string format, byte[]? document)
    {
        PrinterSnapshot snapshot = _snapshot();
        var job = new TrackedJob
        {
            Id = Interlocked.Increment(ref _nextJobId),
            Name = request.GetText("job-name") ?? "AirPrint",
            RemoteAddress = remote.ToString(),
            User = Clip(request.GetText("requesting-user-name") ?? "guest", 80),
            Format = format,
            Document = document,
            Copies = ReadCopies(request),
            PrinterName = snapshot.QueueName,
            State = 3,
            Reason = "none",
        };
        string? sides = request.GetText("sides");
        if (sides is "two-sided-long-edge" or "two-sided-short-edge")
        {
            job.Duplex = true;
            job.ShortEdge = sides == "two-sided-short-edge";
        }

        string? media = request.GetText("media");
        if (!string.IsNullOrWhiteSpace(media))
        {
            job.Media = Clip(media, 80);
        }

        lock (_jobGate)
        {
            _jobs.Add(job);
            if (_jobs.Count > 50)
            {
                _jobs.RemoveAt(0);
            }
        }

        _log($"Задание {job.Id}: {format}, {document?.Length ?? 0} байт, копий {job.Copies}.");
        return job;
    }

    private void Queue(TrackedJob job)
    {
        job.Started = true;
        byte[] document = job.Document ?? throw new InvalidDataException("В задании нет документа.");
        var request = new PrintJobRequest
        {
            PrinterName = job.PrinterName,
            Format = job.Format,
            Document = document,
            Copies = job.Copies,
            Duplex = job.Duplex,
            ShortEdge = job.ShortEdge,
            Media = job.Media,
        };
        job.State = 3;
        job.Reason = "none";
        _ = Task.Run(() =>
        {
            try
            {
                lock (_printGate)
                {
                    if (job.Cancel.IsCancellationRequested)
                    {
                        job.State = 7;
                        job.Reason = "job-canceled-by-user";
                        _log("Задание " + job.Id + " отменено.");
                        return;
                    }

                    job.State = 5;
                    job.Reason = "job-printing";
                    _printer.Print(request, job.Cancel.Token);
                }
                if (job.Cancel.IsCancellationRequested)
                {
                    job.State = 7;
                    job.Reason = "job-canceled-by-user";
                    _log("Задание " + job.Id + " отменено.");
                    return;
                }

                job.State = 9;
                job.Reason = "job-completed-successfully";
                _log("Задание " + job.Id + " напечатано.");
            }
            catch (OperationCanceledException)
            {
                job.State = 7;
                job.Reason = "job-canceled-by-user";
                _log("Задание " + job.Id + " отменено.");
            }
            catch (Exception ex)
            {
                job.State = 8;
                job.Reason = "aborted-by-system";
                _log("Задание " + job.Id + ": " + ex.GetType().Name);
            }
        });
    }

    private TrackedJob? FindJob(int? id)
    {
        if (id == null)
        {
            throw new InvalidDataException("Не указан job-id.");
        }

        lock (_jobGate)
        {
            return _jobs.FirstOrDefault(job => job.Id == id.Value);
        }
    }

    private int ActiveJobCount()
    {
        lock (_jobGate)
        {
            return _jobs.Count(job => job.State is 3 or 4 or 5 or 6);
        }
    }

    private static int ReadCopies(IppMessage request)
    {
        int copies = request.GetInteger("copies") ?? 1;
        return Math.Clamp(copies, 1, 99);
    }

    private static bool IsAcceptedFormat(string format)
    {
        string value = format.Trim().ToLowerInvariant();
        return value is "application/pdf" or "image/jpeg" or "image/jpg" or "image/png" or "image/urf" or "image/pwg-raster" or "application/octet-stream";
    }

    private byte[] JobResponse(IppMessage request, TrackedJob job)
    {
        var writer = Begin(request, IppCodec.Ok);
        WriteJob(writer, request, job);
        writer.End();
        return writer.ToArray();
    }

    private void WriteJob(IppWriter writer, IppMessage request, TrackedJob job)
    {
        PrinterSnapshot snapshot = _snapshot();
        string host = JobHost(request, snapshot);
        writer.Group(IppCodec.JobGroup);
        writer.Uri("job-uri", $"ipp://{host}:{snapshot.Port}/ipp/print/{job.Id}");
        writer.Uri("job-printer-uri", $"ipp://{host}:{snapshot.Port}/ipp/print");
        writer.Integer("job-id", job.Id);
        writer.Enum("job-state", job.State);
        writer.Keyword("job-state-reasons", job.Reason);
        writer.Name("job-name", job.Name);
        writer.Name("job-originating-user-name", job.User);
    }

    private static string[] UrfKeywords(bool color, bool duplex)
    {
        return BonjourAdvertiser.UrfCaps(color, duplex).Split(',');
    }

    private static void WriteMediaCollection(IppWriter writer, string name, (string Name, double WidthMm, double HeightMm) media)
    {
        writer.Collection(name, collection =>
        {
            collection.Collection("media-size", size =>
            {
                size.Integer("x-dimension", (int)Math.Round(media.WidthMm * 100));
                size.Integer("y-dimension", (int)Math.Round(media.HeightMm * 100));
            });
            collection.Integer("media-bottom-margin", 0);
            collection.Integer("media-left-margin", 0);
            collection.Integer("media-right-margin", 0);
            collection.Integer("media-top-margin", 0);
            collection.Keyword("media-source", "main");
            collection.Keyword("media-type", "stationery");
        });
    }

    private static string Clip(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }

    private static string[] PrinterUris(PrinterSnapshot snapshot)
    {
        IEnumerable<string> hosts = snapshot.Hosts.Count > 0 ? snapshot.Hosts : ["127.0.0.1"];
        return hosts
            .OrderBy(host => host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            .Select(host => $"ipp://{host}:{snapshot.Port}/ipp/print")
            .ToArray();
    }

    private static string JobHost(IppMessage request, PrinterSnapshot snapshot)
    {
        if (TryUriHost(request.GetText("printer-uri"), out string printerHost) || TryUriHost(request.GetText("job-uri"), out printerHost))
        {
            return printerHost;
        }

        return snapshot.Hosts.FirstOrDefault(host => !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Hosts.FirstOrDefault()
            ?? "127.0.0.1";
    }

    private static bool TryUriHost(string? text, out string host)
    {
        host = "";
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
        {
            return false;
        }

        host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.Host + "]" : uri.Host;
        return true;
    }

    private static int? RequestedJobId(IppMessage request)
    {
        int? id = request.GetInteger("job-id");
        if (id != null)
        {
            return id;
        }

        string? uri = request.GetText("job-uri");
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        int slash = uri.LastIndexOf('/');
        if (slash >= 0 && slash < uri.Length - 1 && int.TryParse(uri[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        return null;
    }

    private static IppWriter Begin(IppMessage request, ushort status)
    {
        var writer = new IppWriter();
        writer.Begin(request.Major == 0 ? (byte)2 : request.Major, request.Minor, status, request.RequestId);
        writer.Group(IppCodec.OperationGroup);
        writer.Charset("attributes-charset", "utf-8");
        writer.Language("attributes-natural-language", "en");
        return writer;
    }

    private static byte[] Success(IppMessage request)
    {
        IppWriter writer = Begin(request, IppCodec.Ok);
        writer.End();
        return writer.ToArray();
    }

    private static byte[] Error(IppMessage request, ushort status, string message)
    {
        return Error(request.Major == 0 ? (byte)2 : request.Major, request.Minor, request.RequestId, status, message);
    }

    private static byte[] Error(byte major, byte minor, int requestId, ushort status, string message)
    {
        var writer = new IppWriter();
        writer.Begin(major, minor, status, requestId);
        writer.Group(IppCodec.OperationGroup);
        writer.Charset("attributes-charset", "utf-8");
        writer.Language("attributes-natural-language", "en");
        writer.Text("status-message", message);
        writer.End();
        return writer.ToArray();
    }

    private static HttpRequestHeader? ReadHeader(NetworkStream stream)
    {
        string? start = ReadLine(stream);
        if (start == null)
        {
            return null;
        }

        string[] parts = start.Split(' ');
        if (parts.Length < 2)
        {
            throw new InvalidDataException("Некорректная строка HTTP.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int headerCount = 0;
        while (true)
        {
            if (++headerCount > 64)
            {
                throw new InvalidDataException("Слишком много HTTP-заголовков.");
            }
            string? line = ReadLine(stream);
            if (line == null)
            {
                throw new EndOfStreamException();
            }

            if (line.Length == 0)
            {
                break;
            }

            int split = line.IndexOf(':');
            if (split > 0)
            {
                headers[line[..split].Trim()] = line[(split + 1)..].Trim();
            }
        }

        bool chunked = headers.TryGetValue("Transfer-Encoding", out string? transfer)
            && transfer.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        int length = 0;
        if (!chunked && headers.TryGetValue("Content-Length", out string? contentLength))
        {
            length = int.Parse(contentLength, System.Globalization.CultureInfo.InvariantCulture);
        }

        bool keepAlive = !headers.TryGetValue("Connection", out string? connection)
            || !connection.Contains("close", StringComparison.OrdinalIgnoreCase);
        bool expect = headers.TryGetValue("Expect", out string? expectValue)
            && expectValue.Contains("100-continue", StringComparison.OrdinalIgnoreCase);
        return new HttpRequestHeader(parts[0], chunked, length, keepAlive, expect);
    }

    private static byte[] ReadBody(NetworkStream stream, HttpRequestHeader header)
    {
        if (header.Chunked)
        {
            return ReadChunked(stream);
        }

        if (header.ContentLength == 0)
        {
            return [];
        }

        if (header.ContentLength < 0 || header.ContentLength > MaxBodyBytes)
        {
            throw new InvalidDataException("Слишком большой документ.");
        }

        return ReadExact(stream, header.ContentLength);
    }

    private static byte[] ReadChunked(NetworkStream stream)
    {
        using var body = new MemoryStream();
        byte[] scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int chunks = 0;
            while (true)
            {
                if (++chunks > 200_000)
                {
                    throw new InvalidDataException("Слишком много фрагментов документа.");
                }
                string? line = ReadLine(stream) ?? throw new EndOfStreamException();
                int size = IppHex.ParseChunkSize(line);
                if (size == 0)
                {
                    while (true)
                    {
                        string? trailer = ReadLine(stream);
                        if (string.IsNullOrEmpty(trailer))
                        {
                            break;
                        }
                    }

                    break;
                }

                if (size < 0 || body.Length + size > MaxBodyBytes)
                {
                    throw new InvalidDataException("Слишком большой документ.");
                }

                int left = size;
                while (left > 0)
                {
                    int take = Math.Min(left, scratch.Length);
                    ReadExact(stream, scratch, take);
                    body.Write(scratch, 0, take);
                    left -= take;
                }

                string? end = ReadLine(stream);
                if (end == null)
                {
                    throw new EndOfStreamException();
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(scratch);
        }

        return body.ToArray();
    }

    private static byte[] ReadExact(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        ReadExact(stream, buffer, count);
        return buffer;
    }

    private static void ReadExact(NetworkStream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private static string? ReadLine(NetworkStream stream)
    {
        var buffer = new StringBuilder();
        while (true)
        {
            int value = stream.ReadByte();
            if (value < 0)
            {
                return buffer.Length == 0 ? null : buffer.ToString();
            }

            if (value == '\n')
            {
                return buffer.ToString().TrimEnd('\r');
            }

            if (buffer.Length > 8192)
            {
                throw new InvalidDataException("Слишком длинный HTTP-заголовок.");
            }

            buffer.Append((char)value);
        }
    }

    private static void WriteAscii(Stream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes);
    }

    private sealed record HttpRequestHeader(string Method, bool Chunked, int ContentLength, bool KeepAlive, bool ExpectContinue);

    private sealed class TrackedJob
    {
        public int Id { get; init; }
        public string Name { get; init; } = "AirPrint";
        public string RemoteAddress { get; init; } = "";
        public string User { get; init; } = "guest";
        public string Format { get; set; } = "application/pdf";
        public byte[]? Document { get; set; }
        public int Copies { get; set; } = 1;
        public bool Duplex { get; set; }
        public bool ShortEdge { get; set; }
        public string? Media { get; set; }
        public string PrinterName { get; init; } = "";
        public int State { get; set; }
        public string Reason { get; set; } = "none";
        public bool Started { get; set; }
        public CancellationTokenSource Cancel { get; } = new();
    }
}
