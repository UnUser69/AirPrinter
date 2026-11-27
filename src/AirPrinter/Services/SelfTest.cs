using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Text;
using AirPrinter.Services;

namespace AirPrinter;

public static class SelfTest
{
    public static int Run(TextWriter output)
    {
        int failures = 0;
        Check(output, ref failures, "Имя ресурса", () =>
        {
            string name = PrinterShareService.MakeShareName("HP LaserJet 1020");
            if (name != "HPLaserJet1020")
            {
                throw new InvalidOperationException(name);
            }

            if (PrinterShareService.ValidateShareName("HP 1020") == null)
            {
                throw new InvalidOperationException("Пробел в имени должен отклоняться.");
            }
        });
        Check(output, ref failures, "TXT AirPrint", () =>
        {
            var advertisement = new AirPrintAdvertisement
            {
                ServiceName = "Kitchen",
                Model = "HP LaserJet",
                UuidUrn = "urn:uuid:11111111-2222-3333-4444-555555555555",
                Port = 631,
                Color = true,
                Duplex = false,
            };
            var records = BonjourAdvertiser.AirPrintRecords(advertisement).ToDictionary(item => item.Key, item => item.Value);
            foreach (string key in new[] { "txtvers", "qtotal", "rp", "ty", "product", "pdl", "URF", "Color", "Duplex", "Copies", "UUID", "priority", "note", "usb_MFG", "usb_MDL", "mopria-certified", "print_wfds" })
            {
                if (!records.ContainsKey(key))
                {
                    throw new InvalidOperationException("Нет ключа " + key);
                }
            }

            if (records["rp"] != "ipp/print" || records["UUID"] != BonjourAdvertiser.BareUuid(advertisement.UuidUrn) || records["ty"] != advertisement.ServiceName || records["mopria-certified"] != "1.3" || records["usb_MDL"] != "HP LaserJet" || !records["pdl"].Contains("application/pdf", StringComparison.Ordinal) || !records["URF"].Contains("V1.4", StringComparison.Ordinal) || !records["URF"].Contains("RS300-600", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Неверные значения TXT.");
            }

            if (BonjourAdvertiser.ToNetworkOrder(631) != 0x7702)
            {
                throw new InvalidOperationException("Порт не в сетевом порядке.");
            }

            byte[] txt = BonjourAdvertiser.BuildTxt(BonjourAdvertiser.AirPrintRecords(advertisement));
            if (txt.Length < 20 || txt[0] != txt[1] + 1 && txt[1] != (byte)'t')
            {
                // Длина первой строки txtvers=1 равна 9.
            }

            if (txt[0] != 9)
            {
                throw new InvalidOperationException("Длина первой TXT-строки " + txt[0]);
            }
        });
        Check(output, ref failures, "URF", () =>
        {
            using Bitmap bitmap = PrintRenderer.DecodeUrf(SampleUrf()).Single();
            if (bitmap.Width != 2 || bitmap.Height != 2)
            {
                throw new InvalidOperationException(bitmap.Width + "x" + bitmap.Height);
            }

            if (bitmap.GetPixel(0, 0).R != 0x11 || bitmap.GetPixel(1, 0).R != 0x22
                || bitmap.GetPixel(0, 1).R != 0x33 || bitmap.GetPixel(1, 1).R != 0xFF)
            {
                throw new InvalidOperationException("Пиксели URF не совпали.");
            }
        });
        Check(output, ref failures, "JPEG", () =>
        {
            using var source = new Bitmap(4, 3);
            source.SetPixel(0, 0, Color.Red);
            using var stream = new MemoryStream();
            source.Save(stream, ImageFormat.Jpeg);
            using Bitmap decoded = PrintRenderer.DecodeJpeg(stream.ToArray());
            if (decoded.Width != 4 || decoded.Height != 3)
            {
                throw new InvalidOperationException("Размер JPEG не сохранился.");
            }
        });
        Check(output, ref failures, "PDF", () =>
        {
            using Bitmap page = PrintRenderer.RenderPdf(MinimalPdf()).Single();
            if (page.Width < 10 || page.Height < 10)
            {
                throw new InvalidOperationException("PDF не отрисован.");
            }
        });
        Check(output, ref failures, "IPP", () => RunIpp(output));
        Check(output, ref failures, "Одно задание", RunSingleJob);
        Check(output, ref failures, "Адрес отмены", () =>
        {
            if (IppServer.SamePeer("10.0.0.2", "10.0.0.8") || !IppServer.SamePeer("10.0.0.2", "10.0.0.2"))
            {
                throw new InvalidOperationException("Чужой адрес не отделён.");
            }
        });
        BonjourStatus bonjour = BonjourAdvertiser.GetStatus();
        if (!bonjour.IsReady)
        {
            output.WriteLine("SKIP Bonjour: " + bonjour.Message);
        }
        else
        {
            Check(output, ref failures, "Bonjour", () =>
            {
                using var advertiser = new BonjourAdvertiser();
                advertiser.Advertise(new AirPrintAdvertisement
                {
                    ServiceName = "AirPrinter SelfTest",
                    Model = "AirPrinter",
                    UuidUrn = "urn:uuid:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                    Port = 9,
                    Color = true,
                    Duplex = false,
                });
                advertiser.Stop();
            });
        }

        try
        {
            _ = PrinterShareService.List();
            output.WriteLine("OK  Список принтеров");
        }
        catch (Exception ex)
        {
            failures++;
            output.WriteLine("FAIL Список принтеров: " + ex.Message);
        }

        Check(output, ref failures, "PWG", () =>
        {
            byte[] page = BuildPwgPage(2, 1, 0x10);
            using Bitmap bitmap = PrintRenderer.DecodePwg(page)[0];
            if (bitmap.Width != 2 || bitmap.Height != 1 || bitmap.GetPixel(0, 0).R != 0x10)
            {
                throw new InvalidOperationException("PWG не декодирован.");
            }
        });
        Check(output, ref failures, "Коллекция IPP", () =>
        {
            var writer = new IppWriter();
            writer.Begin(1, 1, 0, 1);
            writer.Group(0x04);
            writer.Collection("media-col-default", collection => collection.Integer("media-left-margin", 0));
            writer.End();
            byte[] bytes = writer.ToArray();
            if (Array.IndexOf(bytes, (byte)0x4A) < 0)
            {
                throw new InvalidOperationException("Вложенный атрибут без тега 0x4A.");
            }
        });
        Check(output, ref failures, "mDNS", () =>
        {
            var advertisement = new AirPrintAdvertisement
            {
                ServiceName = "Kitchen",
                Model = "HP LaserJet",
                UuidUrn = "urn:uuid:11111111-2222-3333-4444-555555555555",
                Port = 631,
                Color = true,
                Duplex = false,
            };
            byte[] answer = MdnsPublisher.Reply(advertisement, "pc", [System.Net.IPAddress.Parse("10.0.0.8")], MdnsPublisher.Query("_ipp._tcp.local"));
            string text = Encoding.UTF8.GetString(answer);
            if (!text.Contains("Kitchen", StringComparison.Ordinal) || !text.Contains("_ipp", StringComparison.Ordinal) || !answer.Contains((byte)0x02) || !answer.Contains((byte)0x77))
            {
                throw new InvalidOperationException("Ответ mDNS не содержит службу или порт 631.");
            }

            byte[] subtype = MdnsPublisher.Reply(advertisement, "pc", [System.Net.IPAddress.Parse("10.0.0.8")], MdnsPublisher.Query("_universal._sub._ipp._tcp.local"));
            if (!Encoding.UTF8.GetString(subtype).Contains("Kitchen", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Подтип AirPrint не объявлен.");
            }

            byte[] query = MdnsPublisher.Query("_ipp._tcp.local");
            byte[] goodbye = MdnsPublisher.Reply(advertisement, "pc", [System.Net.IPAddress.Parse("10.0.0.8")], query, query.Length, 0);
            if (goodbye.Contains((byte)0x11) && goodbye.Contains((byte)0x94))
            {
                throw new InvalidOperationException("Прощание mDNS сохранило долгий TTL.");
            }

            using var publisher = new MdnsPublisher();
            using Socket listener = MdnsPublisher.OpenSocket();
            publisher.Start(advertisement, "pc", [System.Net.IPAddress.Parse("10.0.0.8")]);
            listener.ReceiveTimeout = 2000;
            listener.SendTo(MdnsPublisher.Query("_ipp._tcp.local"), new System.Net.IPEndPoint(System.Net.IPAddress.Parse("224.0.0.251"), MdnsPublisher.Port));
            var buffer = new byte[1500];
            int size = listener.Receive(buffer);
            if (size < 12 || !Encoding.UTF8.GetString(buffer, 0, size).Contains("Kitchen", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Живой mDNS не ответил.");
            }
        });
        Check(output, ref failures, "Брандмауэр API", () =>
        {
            if (!FirewallService.ApiAvailable)
            {
                throw new InvalidOperationException("COM-компонент брандмауэра Windows недоступен.");
            }
        });
        output.WriteLine(failures == 0 ? "SELF-TEST PASSED" : "SELF-TEST FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void RunSingleJob()
    {
        var hold = new HoldingPrinter();
        var snapshot = new PrinterSnapshot
        {
            QueueName = "Office",
            Model = "Desk Printer",
            ServiceName = "Office",
            UuidUrn = "urn:uuid:11111111-2222-3333-4444-555555555555",
            Color = true,
            Duplex = false,
            Hosts = ["127.0.0.1"],
        };
        using var server = new IppServer(hold, () => snapshot, _ => { });
        server.Start(0);
        snapshot.Port = server.Port;
        byte[] jpeg;
        using (var picture = new Bitmap(2, 2))
        using (var memory = new MemoryStream())
        {
            picture.Save(memory, ImageFormat.Jpeg);
            jpeg = memory.ToArray();
        }

        byte[] first = IppCodec.BuildRequest(2, 0, IppCodec.PrintJob, 1, writer =>
        {
            writer.Charset("attributes-charset", "utf-8");
            writer.Language("attributes-natural-language", "en");
            writer.Uri("printer-uri", "ipp://127.0.0.1/ipp/print");
            writer.Mime("document-format", "image/jpeg");
        }, jpeg);
        IppMessage accepted = Exchange(server.Port, first, chunked: false);
        if (accepted.OperationOrStatus != IppCodec.Ok)
        {
            throw new InvalidOperationException("Первое задание не принято.");
        }

        if (!hold.Entered.Wait(TimeSpan.FromSeconds(5)))
        {
            hold.Release.Set();
            throw new InvalidOperationException("Печать не началась.");
        }

        IppMessage second = Exchange(server.Port, first, chunked: false);
        hold.Release.Set();
        if (second.OperationOrStatus != IppCodec.Busy)
        {
            throw new InvalidOperationException("Второе задание принято, статус " + second.OperationOrStatus);
        }

        using var client = new TcpClient("127.0.0.1", server.Port);
        client.ReceiveTimeout = 1000;
        NetworkStream stream = client.GetStream();
        string header = "POST /ipp/print HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/ipp\r\nContent-Length: 40000000\r\nConnection: close\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(header));
        var buffer = new byte[32];
        int read;
        try
        {
            read = stream.Read(buffer, 0, buffer.Length);
        }
        catch (IOException)
        {
            read = 0;
        }

        if (read > 0 && Encoding.ASCII.GetString(buffer, 0, read).Contains("200", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Слишком большой документ принят.");
        }
    }

    private static void RunIpp(TextWriter output)
    {
        var fake = new FakePrinter();
        var snapshot = new PrinterSnapshot
        {
            QueueName = "Office",
            Model = "Desk Printer",
            ServiceName = "Office",
            UuidUrn = "urn:uuid:11111111-2222-3333-4444-555555555555",
            Color = true,
            Duplex = true,
            Port = 0,
            Hosts = ["127.0.0.1"],
        };
        using var server = new IppServer(fake, () => snapshot, output.WriteLine);
        server.Start(0);
        snapshot.Port = server.Port;

        byte[] attributes = IppCodec.BuildRequest(2, 0, IppCodec.GetPrinterAttributes, 4, writer =>
        {
            writer.Charset("attributes-charset", "utf-8");
            writer.Language("attributes-natural-language", "en");
            writer.Uri("printer-uri", "ipp://127.0.0.1:" + server.Port + "/ipp/print");
        });
        IppMessage described = Exchange(server.Port, attributes, chunked: false);
        if (described.OperationOrStatus != IppCodec.Ok)
        {
            throw new InvalidOperationException("Статус атрибутов " + described.OperationOrStatus);
        }

        if (described.GetText("printer-uuid") != snapshot.UuidUrn || described.GetText("printer-name") != "Office" || described.GetText("printer-dns-sd-name") != "Office")
        {
            throw new InvalidOperationException("UUID или имя принтера не совпали.");
        }

        string urf = string.Join(",", described.Attributes.Where(item => item.Name == "urf-supported").SelectMany(item => item.Values).Select(Encoding.UTF8.GetString));
if (!urf.Contains("SRGB24", StringComparison.Ordinal) || !urf.Contains("V1.4", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("urf-supported: " + urf);
            }

            string features = string.Join(",", described.Attributes.Where(item => item.Name == "ipp-features-supported").SelectMany(item => item.Values).Select(Encoding.UTF8.GetString));
            if (!features.Contains("airprint-1.8", StringComparison.Ordinal) || !features.Contains("ipp-everywhere", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Нет airprint-1.8 или ipp-everywhere.");
            }

            if (described.GetText("printer-device-id") != "MFG:AirPrinter;MDL:Desk Printer;CMD:PDF,PWG,URF;")
            {
                throw new InvalidOperationException("printer-device-id: " + described.GetText("printer-device-id"));
            }

            if (!described.Attributes.Any(item => item.Name == "pwg-raster-document-resolution-supported" && item.Tag == 0x32))
            {
                throw new InvalidOperationException("Нет разрешения PWG Raster.");
            }

            if (!described.Attributes.Any(item => item.Name == "media-col-database"))
            {
                throw new InvalidOperationException("Нет media-col-database.");
            }

            string formats = string.Join(",", described.Attributes.Where(item => item.Name == "document-format-supported").SelectMany(item => item.Values).Select(Encoding.UTF8.GetString));
            if (!formats.Contains("image/pwg-raster", StringComparison.Ordinal) || !formats.Contains("image/png", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Форматы: " + formats);
            }

        byte[] jpeg;
        using (var picture = new Bitmap(2, 2))
        using (var memory = new MemoryStream())
        {
            picture.Save(memory, ImageFormat.Jpeg);
            jpeg = memory.ToArray();
        }

        byte[] print = IppCodec.BuildRequest(2, 0, IppCodec.PrintJob, 8, writer =>
        {
            writer.Charset("attributes-charset", "utf-8");
            writer.Language("attributes-natural-language", "en");
            writer.Uri("printer-uri", "ipp://10.1.2.3/ipp/print");
            writer.Name("requesting-user-name", "selftest");
            writer.Mime("document-format", "image/jpeg");
            writer.Integer("copies", 2);
        }, jpeg);
        IppMessage accepted = Exchange(server.Port, print, chunked: true);
        if (accepted.OperationOrStatus != IppCodec.Ok || accepted.GetInteger("job-id") != 1)
        {
            throw new InvalidOperationException("Print-Job не принят.");
        }

        string? jobUri = accepted.GetText("job-uri");
        if (jobUri == null || !jobUri.Contains("10.1.2.3", StringComparison.Ordinal) || jobUri.EndsWith("/1", StringComparison.Ordinal) == false)
        {
            throw new InvalidOperationException("job-uri: " + jobUri);
        }

        byte[] poll = IppCodec.BuildRequest(2, 0, IppCodec.GetJobAttributes, 11, writer =>
        {
            writer.Charset("attributes-charset", "utf-8");
            writer.Language("attributes-natural-language", "en");
            writer.Uri("printer-uri", "ipp://10.1.2.3/ipp/print");
            writer.Uri("job-uri", jobUri);
        });
        IppMessage status = Exchange(server.Port, poll, chunked: false);
        if (status.OperationOrStatus != IppCodec.Ok || status.GetInteger("job-id") != 1)
        {
            throw new InvalidOperationException("Статус задания по job-uri: " + status.OperationOrStatus);
        }

        WaitUntil(() => fake.Last != null, TimeSpan.FromSeconds(5));
        if (fake.Last?.Format != "image/jpeg" || fake.Last.Copies != 2 || fake.Last.Document.Length != jpeg.Length)
        {
            throw new InvalidOperationException("Задание дошло в неверном виде.");
        }

        byte[] bad = IppCodec.BuildRequest(2, 0, IppCodec.PrintJob, 9, writer =>
        {
            writer.Charset("attributes-charset", "utf-8");
            writer.Language("attributes-natural-language", "en");
            writer.Mime("document-format", "text/plain");
        }, [1, 2, 3, 4]);
        IppMessage rejected = Exchange(server.Port, bad, chunked: false);
        if (rejected.OperationOrStatus != IppCodec.DocumentFormatNotSupported)
        {
            throw new InvalidOperationException("Чужой формат принят, статус " + rejected.OperationOrStatus);
        }

        using var held = new TcpClient("127.0.0.1", server.Port);
        WaitUntil(() => server.OpenClients > 0, TimeSpan.FromSeconds(2));
        server.DropClients();
        WaitUntil(() => server.OpenClients == 0, TimeSpan.FromSeconds(2));
        _ = held;
    }

    private static IppMessage Exchange(int port, byte[] ipp, bool chunked)
    {
        using var client = new TcpClient("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        if (!chunked)
        {
            string header = "POST /ipp/print HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Type: application/ipp\r\nContent-Length: " + ipp.Length + "\r\nConnection: close\r\n\r\n";
            stream.Write(Encoding.ASCII.GetBytes(header));
            stream.Write(ipp);
        }
        else
        {
            string header = "POST /ipp/print HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Type: application/ipp\r\nExpect: 100-continue\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
            stream.Write(Encoding.ASCII.GetBytes(header));
            stream.Flush();
            string prelude = ReadHeaders(stream);
            if (!prelude.Contains("100", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Нет ответа 100 Continue: " + prelude);
            }

            WriteChunk(stream, ipp);
            WriteChunk(stream, []);
        }

        stream.Flush();
        _ = ReadHeaders(stream);
        return IppCodec.Parse(ReadBody(stream));
    }

    private static void WriteChunk(NetworkStream stream, byte[] data)
    {
        string size = data.Length.ToString("x") + "\r\n";
        stream.Write(Encoding.ASCII.GetBytes(size));
        stream.Write(data);
        stream.Write(Encoding.ASCII.GetBytes("\r\n"));
    }

    private static string ReadHeaders(NetworkStream stream)
    {
        var text = new StringBuilder();
        while (true)
        {
            string line = ReadLine(stream) ?? throw new EndOfStreamException();
            text.AppendLine(line);
            if (line.Length == 0)
            {
                return text.ToString();
            }
        }
    }

    private static byte[] ReadBody(NetworkStream stream)
    {
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            body.Write(buffer, 0, read);
        }

        return body.ToArray();
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

            buffer.Append((char)value);
        }
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("Задание не дошло до печати.");
            }

            Thread.Sleep(20);
        }
    }

    private static byte[] SampleUrf()
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("UNIRAST"));
        stream.WriteByte(0);
        WriteBe(stream, 1);
        stream.WriteByte(8);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        WriteBe(stream, 0);
        WriteBe(stream, 0);
        WriteBe(stream, 2);
        WriteBe(stream, 2);
        WriteBe(stream, 300);
        WriteBe(stream, 0);
        WriteBe(stream, 0);
        stream.WriteByte(0);
        stream.WriteByte(unchecked((byte)-1));
        stream.WriteByte(0x11);
        stream.WriteByte(0x22);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0x33);
        stream.WriteByte(0x80);
        return stream.ToArray();
    }

    private static byte[] BuildPwgPage(int width, int height, byte gray)
    {
        var data = new byte[4 + 1796 + 3];
        data[0] = (byte)'R';
        data[1] = (byte)'a';
        data[2] = (byte)'S';
        data[3] = (byte)'2';
        void Be(int index, int value)
        {
            data[index] = (byte)(value >> 24);
            data[index + 1] = (byte)(value >> 16);
            data[index + 2] = (byte)(value >> 8);
            data[index + 3] = (byte)value;
        }

        Be(4 + 372, width);
        Be(4 + 376, height);
        Be(4 + 388, 8);
        data[4 + 1796] = 0;
        data[4 + 1796 + 1] = (byte)(width - 1);
        data[4 + 1796 + 2] = gray;
        return data;
    }

    private static byte[] MinimalPdf()
    {
        string streamText = "BT /F1 24 Tf 40 100 Td (AirPrinter) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + streamText.Length + " >>\nstream\n" + streamText + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var file = new StringBuilder();
        file.Append("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(file.Length);
            file.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = file.Length;
        file.Append("xref\n0 6\n");
        file.Append("0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            file.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        file.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(file.ToString());
    }

    private static void WriteBe(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void Check(TextWriter output, ref int failures, string name, Action body)
    {
        try
        {
            body();
            output.WriteLine("OK  " + name);
        }
        catch (Exception ex)
        {
            failures++;
            output.WriteLine("FAIL " + name + ": " + ex.Message);
        }
    }

    private sealed class HoldingPrinter : IJobPrinter
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public void Print(PrintJobRequest request, CancellationToken cancellationToken)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class FakePrinter : IJobPrinter
    {
        public PrintJobRequest? Last { get; private set; }

        public void Print(PrintJobRequest request, CancellationToken cancellationToken)
        {
            Last = request;
        }
    }
}
