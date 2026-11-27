using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace AirPrinter.Services;

public sealed record BonjourStatus(bool DllInstalled, bool ServiceInstalled, ServiceControllerStatus? ServiceStatus)
{
    public bool IsReady => DllInstalled && ServiceStatus == ServiceControllerStatus.Running;

    public string Message
    {
        get
        {
            if (!DllInstalled && !ServiceInstalled)
            {
                return "Bonjour Apple не установлен. Мост сам объявит принтер по mDNS. Пакет Apple можно поставить дополнительно.";
            }

            if (!DllInstalled)
            {
                return "Служба Bonjour есть, но dnssd.dll не найден. Переустановите пакет Apple.";
            }

            if (!ServiceInstalled)
            {
                return "dnssd.dll найден, но служба Bonjour не установлена. Поставьте пакет с сайта Apple.";
            }

            return ServiceStatus == ServiceControllerStatus.Running
                ? "Bonjour найден, служба запущена."
                : "Bonjour установлен, но служба остановлена.";
        }
    }
}

public sealed class AirPrintAdvertisement
{
    public required string ServiceName { get; init; }
    public required string Model { get; init; }
    public required string UuidUrn { get; init; }
    public required int Port { get; init; }
    public required bool Color { get; init; }
    public required bool Duplex { get; init; }
}

public sealed class BonjourAdvertiser : IDisposable
{
    public const string DownloadUrl = "https://support.apple.com/en-us/106380";

    private Registration? _ipp;
    private Registration? _printer;
    private bool _disposed;

    public static BonjourStatus GetStatus()
    {
        bool dll = File.Exists(Path.Combine(Environment.SystemDirectory, "dnssd.dll"));
        try
        {
            using var controller = new ServiceController("Bonjour Service");
            return new BonjourStatus(dll, true, controller.Status);
        }
        catch (InvalidOperationException)
        {
            return new BonjourStatus(dll, false, null);
        }
    }

    public static void TryStartService()
    {
        using var controller = new ServiceController("Bonjour Service");
        if (controller.Status == ServiceControllerStatus.Running)
        {
            return;
        }

        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
    }

    public static void OpenDownloadPage()
    {
        Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
    }

    public void Advertise(AirPrintAdvertisement advertisement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool replacing = _ipp != null || _printer != null;
        Stop();
        if (replacing)
        {
            // Служба Bonjour рассылает прощание со старым именем уже после закрытия регистрации.
            Thread.Sleep(400);
        }

        byte[] name = CString(advertisement.ServiceName);
        byte[] ippTxt = BuildTxt(AirPrintRecords(advertisement));
        byte[] printerTxt = BuildTxt(LegacyPrinterRecords(advertisement));
        // Форма для DNSServiceRegister: тип,протокол,подтип. Имя _universal._sub._ipp._tcp Bonjour для Windows отвергает как неверный параметр.
        _ipp = Registration.Start(name, "_ipp._tcp,_universal", advertisement.Port, ippTxt);
        try
        {
            _printer = Registration.Start(name, "_printer._tcp", 0, printerTxt);
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        _ipp?.Dispose();
        _printer?.Dispose();
        _ipp = null;
        _printer = null;
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

    public static IReadOnlyList<(string Key, string Value)> AirPrintRecords(AirPrintAdvertisement advertisement)
    {
        string shown = Truncate(advertisement.ServiceName, 120);
        string model = Truncate(string.IsNullOrWhiteSpace(advertisement.Model) ? shown : advertisement.Model, 120);
        return
        [
            ("txtvers", "1"),
            ("qtotal", "1"),
            ("rp", "ipp/print"),
            ("ty", shown),
            ("product", "(" + model + ")"),
            ("note", "AirPrinter"),
            ("priority", "50"),
            ("pdl", Pdl),
            ("URF", UrfCaps(advertisement.Color, advertisement.Duplex)),
            ("Color", advertisement.Color ? "T" : "F"),
            ("Duplex", advertisement.Duplex ? "T" : "F"),
            ("Copies", "T"),
            ("UUID", BareUuid(advertisement.UuidUrn)),
            ("usb_MFG", "AirPrinter"),
            ("usb_MDL", DeviceModel(advertisement.ServiceName, advertisement.Model)),
            ("mopria-certified", "1.3"),
            ("print_wfds", "T"),
            ("PaperMax", "legal-A4"),
        ];
    }

    public static string DeviceModel(string serviceName, string? model)
    {
        string shown = string.IsNullOrWhiteSpace(model) ? serviceName : model;
        var cleaned = new string(shown.Where(ch => ch >= ' ' && ch != ';' && ch != ':').ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "Printer";
        }

        return Truncate(cleaned, 63);
    }

    public static string DeviceId(string serviceName, string? model)
    {
        return "MFG:AirPrinter;MDL:" + DeviceModel(serviceName, model) + ";CMD:PDF,PWG,URF;";
    }

    public static string UrfCaps(bool color, bool duplex)
    {
        string duplexToken = duplex ? "DM3" : "DM1";
        return color
            ? "W8,SRGB24,V1.4,RS300-600," + duplexToken
            : "W8,V1.4,RS300-600," + duplexToken;
    }

    public static string BareUuid(string urn)
    {
        const string prefix = "urn:uuid:";
        return urn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? urn[prefix.Length..] : urn;
    }

    public const string Pdl = "application/pdf,image/jpeg,image/png,image/urf,image/pwg-raster";

    public static IReadOnlyList<(string Key, string Value)> LegacyPrinterRecords(AirPrintAdvertisement advertisement)
    {
        string shown = Truncate(advertisement.ServiceName, 120);
        string model = Truncate(string.IsNullOrWhiteSpace(advertisement.Model) ? shown : advertisement.Model, 120);
        return
        [
            ("txtvers", "1"),
            ("qtotal", "1"),
            ("ty", shown),
            ("product", "(" + model + ")"),
            ("note", "AirPrinter"),
            ("pdl", Pdl),
        ];
    }

    public static byte[] BuildTxt(IReadOnlyList<(string Key, string Value)> pairs)
    {
        using var stream = new MemoryStream();
        foreach ((string key, string value) in pairs)
        {
            string text = key + "=" + value;
            while (System.Text.Encoding.UTF8.GetByteCount(text) > 255 && text.Length > key.Length + 1)
            {
                text = text[..^1];
            }

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        return stream.ToArray();
    }

    public static string ServiceName(string printerName)
    {
        var cleaned = new string(printerName.Where(ch => ch >= ' ' && ch != '\\').ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "Printer";
        }

        return TruncateUtf8(cleaned, 63);
    }

    public static ushort ToNetworkOrder(int port)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        ushort value = (ushort)port;
        return (ushort)((value << 8) | (value >> 8));
    }

    private static string Truncate(string value, int maxChars)
    {
        return value.Length <= maxChars ? value : value[..maxChars];
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var buffer = new System.Text.StringBuilder();
        int used = 0;
        foreach (char ch in value)
        {
            int size = System.Text.Encoding.UTF8.GetByteCount(new[] { ch });
            if (used + size > maxBytes)
            {
                break;
            }

            buffer.Append(ch);
            used += size;
        }

        return buffer.ToString();
    }

    private static byte[] CString(string value)
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes(value);
        var buffer = new byte[text.Length + 1];
        Buffer.BlockCopy(text, 0, buffer, 0, text.Length);
        return buffer;
    }

    private static string FormatError(int code)
    {
        return code switch
        {
            0 => "нет ошибки",
            -65548 => "имя уже занято в сети",
            -65547 => "служба уже зарегистрирована",
            -65540 => "неверный параметр",
            -65551 => "служба Bonjour несовместима",
            _ => "код " + code,
        };
    }

    private sealed class Registration : IDisposable
    {
        private readonly RegisterReply _callback;
        private readonly Thread _pump;
        private IntPtr _service;
        private int _stop;

        private Registration(IntPtr service, RegisterReply callback)
        {
            _service = service;
            _callback = callback;
            _pump = new Thread(Pump)
            {
                IsBackground = true,
                Name = "Bonjour",
            };
        }

        public static Registration Start(byte[] name, string type, int port, byte[] txt)
        {
            var ready = new ManualResetEventSlim(false);
            int callbackError = 0;
            RegisterReply callback = (_, _, errorCode, _, _, _, _) =>
            {
                if (errorCode != 0)
                {
                    Interlocked.CompareExchange(ref callbackError, errorCode, 0);
                }

                try
                {
                    ready.Set();
                }
                catch (ObjectDisposedException)
                {
                }
            };

            int error = DNSServiceRegister(
                out IntPtr service,
                0,
                0,
                name,
                CString(type),
                IntPtr.Zero,
                IntPtr.Zero,
                ToNetworkOrder(port),
                (ushort)txt.Length,
                txt,
                callback,
                IntPtr.Zero);
            if (error != 0)
            {
                ready.Dispose();
                throw new InvalidOperationException("Bonjour не зарегистрировал службу " + type + ": " + FormatError(error));
            }

            var registration = new Registration(service, callback);
            registration._pump.Start();
            bool signaled = ready.Wait(TimeSpan.FromSeconds(5));
            int reported = Volatile.Read(ref callbackError);
            ready.Dispose();
            if (!signaled)
            {
                registration.Dispose();
                throw new System.TimeoutException("Bonjour не подтвердил регистрацию " + type + ".");
            }

            if (reported != 0)
            {
                registration.Dispose();
                throw new InvalidOperationException("Bonjour отклонил " + type + ": " + FormatError(reported));
            }

            return registration;
        }

        public void Dispose()
        {
            IntPtr service = Interlocked.Exchange(ref _service, IntPtr.Zero);
            if (service == IntPtr.Zero)
            {
                return;
            }

            Volatile.Write(ref _stop, 1);
            try
            {
                DNSServiceRefDeallocate(service);
            }
            catch (DllNotFoundException)
            {
            }

            if (Thread.CurrentThread != _pump && _pump.IsAlive)
            {
                _pump.Join(TimeSpan.FromSeconds(2));
            }
        }

        private void Pump()
        {
            while (Volatile.Read(ref _stop) == 0)
            {
                IntPtr service = Volatile.Read(ref _service);
                if (service == IntPtr.Zero)
                {
                    break;
                }

                int error = DNSServiceProcessResult(service);
                if (error != 0)
                {
                    break;
                }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RegisterReply(
            IntPtr sdRef,
            int flags,
            int errorCode,
            IntPtr name,
            IntPtr regType,
            IntPtr domain,
            IntPtr context);

        // [ANTI-FP] Почему так: прямое имя DNSServiceRegister в DllImport, без LoadLibrary и GetProcAddress.
        [DllImport("dnssd.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DNSServiceRegister(
            out IntPtr sdRef,
            int flags,
            uint interfaceIndex,
            byte[] name,
            byte[] regtype,
            IntPtr domain,
            IntPtr host,
            ushort port,
            ushort txtLen,
            byte[] txtRecord,
            RegisterReply callBack,
            IntPtr context);

        [DllImport("dnssd.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int DNSServiceProcessResult(IntPtr sdRef);

        [DllImport("dnssd.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void DNSServiceRefDeallocate(IntPtr sdRef);
    }
}
