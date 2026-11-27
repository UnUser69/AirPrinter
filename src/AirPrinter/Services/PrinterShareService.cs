using System.Management;
using System.Runtime.InteropServices;

namespace AirPrinter.Services;

public sealed class InstalledPrinter
{
    public required string Name { get; init; }
    public required string PortName { get; init; }
    public required string DriverName { get; init; }
    public required string StatusText { get; init; }
    public bool IsOffline { get; init; }
    public bool IsShared { get; init; }
    public string ShareName { get; init; } = "";
    public bool SupportsColor { get; init; }
    public bool CanDuplex { get; init; }
    public bool IsVirtual { get; init; }

    public override string ToString() => Name;
}

public static class PrinterShareService
{
    private const uint PrinterAllAccess = 0x000F000C;
    private const uint PrinterAttributeShared = 0x00000008;

    public static IReadOnlyList<InstalledPrinter> List()
    {
        var list = new List<InstalledPrinter>();
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementObject printer in results)
        {
            using (printer)
            {
                bool local = ReadBool(printer, "Local");
                bool network = ReadBool(printer, "Network");
                if (!local || network)
                {
                    continue;
                }

                string name = ReadString(printer, "Name");
                if (name.Length == 0)
                {
                    continue;
                }

                bool color = false;
                bool duplex = false;
                try
                {
                    var settings = new System.Drawing.Printing.PrinterSettings { PrinterName = name };
                    if (settings.IsValid)
                    {
                        color = settings.SupportsColor;
                        duplex = settings.CanDuplex;
                    }
                }
                catch (Exception)
                {
                    color = false;
                    duplex = false;
                }

                int status = ReadInt(printer, "PrinterStatus");
                bool offline = ReadBool(printer, "WorkOffline") || status == 7;
                list.Add(new InstalledPrinter
                {
                    Name = name,
                    PortName = ReadString(printer, "PortName"),
                    DriverName = ReadString(printer, "DriverName"),
                    StatusText = DescribeStatus(status, offline),
                    IsOffline = offline,
                    IsShared = ReadBool(printer, "Shared"),
                    ShareName = ReadString(printer, "ShareName"),
                    SupportsColor = color,
                    CanDuplex = duplex,
                    IsVirtual = IsVirtualPrinter(name),
                });
            }
        }

        return list
            .OrderBy(item => item.IsVirtual)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsVirtualPrinter(string name)
    {
        string lower = name.Trim().ToLowerInvariant();
        return lower.StartsWith("microsoft print to pdf", StringComparison.Ordinal)
            || lower.StartsWith("microsoft xps", StringComparison.Ordinal)
            || lower.StartsWith("onenote", StringComparison.Ordinal)
            || lower.StartsWith("send to onenote", StringComparison.Ordinal)
            || lower.StartsWith("fax", StringComparison.Ordinal);
    }

    public static void PrintTestPage(string printerName)
    {
        ManagementObject printer = Find(printerName) ?? throw new InvalidOperationException("Принтер не найден.");
        using (printer)
        {
            object? result = printer.InvokeMethod("PrintTestPage", null);
            if (result != null && Convert.ToUInt32(result) != 0)
            {
                throw new InvalidOperationException($"Windows не напечатала пробную страницу (код {result}).");
            }
        }
    }

    public static void SetShared(string printerName, bool shared, string? shareName)
    {
        if (shared)
        {
            string? error = ValidateShareName(shareName ?? "");
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }
        }

        ManagementObject printer = Find(printerName) ?? throw new InvalidOperationException("Принтер не найден.");
        using (printer)
        {
            try
            {
                printer["Shared"] = shared;
                if (shared)
                {
                    printer["ShareName"] = shareName;
                }

                printer.Put();
                printer.Get();
                if (ReadBool(printer, "Shared") == shared)
                {
                    return;
                }
            }
            catch (ManagementException)
            {
                // Ниже тот же флаг выставляется через winspool.
            }
        }

        SetSharedWinspool(printerName, shared, shareName ?? "");
    }

    public static string MakeShareName(string printerName)
    {
        var buffer = new System.Text.StringBuilder();
        foreach (char ch in printerName)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                buffer.Append(ch);
            }
        }

        string name = buffer.ToString();
        if (name.Length == 0)
        {
            name = "Printer";
        }

        return name.Length <= 80 ? name : name[..80];
    }

    public static string? ValidateShareName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Укажите имя общего доступа.";
        }

        if (name.Length > 80)
        {
            return "Имя общего доступа длиннее 80 символов.";
        }

        if (name.Any(char.IsWhiteSpace))
        {
            return "Имя общего доступа должно быть без пробелов.";
        }

        if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            return "Имя общего доступа содержит недопустимый символ.";
        }

        return null;
    }

    private static string DescribeStatus(int status, bool offline)
    {
        if (offline)
        {
            return "Офлайн";
        }

        return status switch
        {
            4 => "Печатает",
            5 => "Прогрев",
            6 => "Остановлен",
            _ => "Готов",
        };
    }

    private static ManagementObject? Find(string printerName)
    {
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementObject printer in results)
        {
            if (string.Equals(ReadString(printer, "Name"), printerName, StringComparison.OrdinalIgnoreCase))
            {
                return printer;
            }

            printer.Dispose();
        }

        return null;
    }

    private static string ReadString(ManagementObject printer, string property)
    {
        return printer[property]?.ToString() ?? "";
    }

    private static bool ReadBool(ManagementObject printer, string property)
    {
        object? value = printer[property];
        return value switch
        {
            bool flag => flag,
            null => false,
            _ => Convert.ToBoolean(value),
        };
    }

    private static int ReadInt(ManagementObject printer, string property)
    {
        object? value = printer[property];
        return value == null ? 0 : Convert.ToInt32(value);
    }

    private static void SetSharedWinspool(string printerName, bool shared, string shareName)
    {
        var defaults = new PrinterDefaults { DesiredAccess = PrinterAllAccess };
        if (!OpenPrinter(printerName, out IntPtr handle, ref defaults))
        {
            throw new InvalidOperationException("Не удалось открыть очередь принтера.");
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            GetPrinter(handle, 2, IntPtr.Zero, 0, out int needed);
            buffer = Marshal.AllocHGlobal(needed);
            if (!GetPrinter(handle, 2, buffer, needed, out _))
            {
                throw new InvalidOperationException("Не удалось прочитать очередь принтера.");
            }

            var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
            info.Attributes = shared
                ? info.Attributes | PrinterAttributeShared
                : info.Attributes & ~PrinterAttributeShared;
            if (shared)
            {
                info.pShareName = shareName;
            }

            if (!SetPrinter(handle, 2, ref info, 0))
            {
                info.pSecurityDescriptor = IntPtr.Zero;
                if (!SetPrinter(handle, 2, ref info, 0))
                {
                    throw new InvalidOperationException("Не удалось изменить общий доступ.");
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            ClosePrinter(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterDefaults
    {
        public string? pDatatype;
        public IntPtr pDevMode;
        public uint DesiredAccess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo2
    {
        public string? pServerName;
        public string? pPrinterName;
        public string? pShareName;
        public string? pPortName;
        public string? pDriverName;
        public string? pComment;
        public string? pLocation;
        public IntPtr pDevMode;
        public string? pSepFile;
        public string? pPrintProcessor;
        public string? pDatatype;
        public string? pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, ref PrinterDefaults pDefault);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetPrinter(IntPtr hPrinter, uint level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetPrinter(IntPtr hPrinter, uint level, ref PrinterInfo2 pPrinter, uint command);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);
}
