using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace AirPrinter.Services;

public sealed class SetupRequest
{
    public bool AllowFirewall { get; set; }
    public bool RemoveFirewall { get; set; }
    public int Port { get; set; }
    public bool ChangeShare { get; set; }
    public string Printer { get; set; } = "";
    public bool Share { get; set; }
    public string ShareName { get; set; } = "";
}

internal static class Elevation
{
    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void Apply(SetupRequest request)
    {
        if (IsElevated())
        {
            ElevatedSetup.ExecuteCore(request);
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "AirPrinter");
        Directory.CreateDirectory(directory);
        string requestPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        string resultPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".result.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "",
            Arguments = "--elevated-setup \"" + requestPath + "\" \"" + resultPath + "\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using Process? process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запросить права администратора.");
            process.WaitForExit();
            if (!File.Exists(resultPath))
            {
                throw new InvalidOperationException("Операция администратора не завершилась.");
            }

            SetupResult? result = JsonSerializer.Deserialize<SetupResult>(File.ReadAllText(resultPath));
            if (result == null || !result.Ok)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result?.Error) ? "Операция администратора не выполнена." : result.Error);
            }
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Права администратора не подтверждены.");
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    public static bool TryStartMedium(string[] args)
    {
        IntPtr process = IntPtr.Zero;
        IntPtr token = IntPtr.Zero;
        IntPtr primary = IntPtr.Zero;
        try
        {
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero)
            {
                return false;
            }

            GetWindowThreadProcessId(shell, out uint pid);
            process = OpenProcess(0x0400, false, pid);
            if (process == IntPtr.Zero || !OpenProcessToken(process, 0x000A, out token))
            {
                return false;
            }

            if (!DuplicateTokenEx(token, 0x018B, IntPtr.Zero, 2, 1, out primary))
            {
                return false;
            }

            string executable = Environment.ProcessPath ?? "";
            string command = "\"" + executable + "\"";
            foreach (string arg in args)
            {
                command += " \"" + arg.Replace("\"", "") + "\"";
            }

            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessWithTokenW(primary, 1, null, command, 0, IntPtr.Zero, null, ref startup, out ProcessInformation info))
            {
                return false;
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return true;
        }
        finally
        {
            if (primary != IntPtr.Zero)
            {
                CloseHandle(primary);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }

            if (process != IntPtr.Zero)
            {
                CloseHandle(process);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, int flags, string? application, string command, int creation, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}

internal static class ElevatedSetup
{
    public static int Execute(string requestPath, string resultPath)
    {
        var result = new SetupResult();
        try
        {
            SetupRequest? request = JsonSerializer.Deserialize<SetupRequest>(File.ReadAllText(requestPath));
            if (request == null)
            {
                throw new InvalidOperationException("Операция администратора не выполнена.");
            }

            ExecuteCore(request);
            result.Ok = true;
        }
        catch (InvalidOperationException ex)
        {
            result.Error = ex.Message;
        }
        catch (Exception)
        {
            result.Error = "Операция администратора не выполнена.";
        }

        File.WriteAllText(resultPath, JsonSerializer.Serialize(result));
        return result.Ok ? 0 : 1;
    }

    public static void ExecuteCore(SetupRequest request)
    {
        if (request.Port is < 0 or > 65535)
        {
            throw new InvalidOperationException("Операция администратора не выполнена.");
        }

        if (request.RemoveFirewall)
        {
            FirewallService.RemoveBridge();
        }

        if (request.AllowFirewall)
        {
            FirewallService.AllowBridge(request.Port);
        }

        if (!request.ChangeShare)
        {
            return;
        }

        try
        {
            PrinterShareService.SetShared(request.Printer, request.Share, request.ShareName);
        }
        catch
        {
            if (request.AllowFirewall)
            {
                FirewallService.RemoveBridge();
            }

            throw;
        }
    }
}

internal sealed class SetupResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
}
