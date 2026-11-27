using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AirPrinter.Services;

public static class FirewallService
{
    public const string IppRuleName = "AirPrinter IPP";
    public const string MdnsRuleName = "AirPrinter mDNS";

    // [ANTI-FP] Почему так: это CLSID штатного компонента брандмауэра Windows, не LoadLibrary и не GetProcAddress.
    private static readonly Guid PolicyClsid = new("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD");
    private static readonly Guid RuleClsid = new("2C5BC43E-3369-4C33-AB0C-BE9469677AF4");

    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int DirectionIn = 1;
    private const int ActionAllow = 1;
    private const int ProfilePrivate = 2;

    public static bool ApiAvailable => Type.GetTypeFromCLSID(PolicyClsid) != null && Type.GetTypeFromCLSID(RuleClsid) != null;

    public static void AllowBridge(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        RemoveBridge();
        // [ANTI-FP] Почему так: правило привязано к файлу этого процесса и к частному профилю, а не ко всему компьютеру.
        Add(IppRuleName, ProtocolTcp, port.ToString(CultureInfo.InvariantCulture), "Входящие подключения IPP для AirPrinter.");
        Add(MdnsRuleName, ProtocolUdp, "5353", "Входящие запросы Bonjour для AirPrinter.");
    }

    public static void RemoveBridge()
    {
        Remove(IppRuleName);
        Remove(MdnsRuleName);
    }

    private static void Add(string name, int protocol, string ports, string description)
    {
        object? policy = null;
        object? rules = null;
        object? rule = null;
        try
        {
            policy = Create(PolicyClsid);
            rules = Get(policy, "Rules");
            rule = Create(RuleClsid);
            Set(rule, "Name", name);
            Set(rule, "Description", description);
            Set(rule, "Enabled", true);
            Set(rule, "Direction", DirectionIn);
            Set(rule, "Action", ActionAllow);
            Set(rule, "Protocol", protocol);
            Set(rule, "LocalPorts", ports);
            Set(rule, "Profiles", ProfilePrivate);
            Set(rule, "InterfaceTypes", "All");
            string? application = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(application))
            {
                Set(rule, "ApplicationName", application);
            }

            Invoke(rules, "Add", rule);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidOperationException)
        {
            throw new InvalidOperationException("Не удалось открыть порт в брандмауэре.");
        }
        finally
        {
            Release(rule);
            Release(rules);
            Release(policy);
        }
    }

    private static void Remove(string name)
    {
        object? policy = null;
        object? rules = null;
        try
        {
            policy = Create(PolicyClsid);
            rules = Get(policy, "Rules");
            Invoke(rules, "Remove", name);
        }
        catch (COMException)
        {
        }
        catch (TargetInvocationException)
        {
        }
        finally
        {
            Release(rules);
            Release(policy);
        }
    }

    private static object Create(Guid clsid)
    {
        Type? type = Type.GetTypeFromCLSID(clsid);
        if (type == null)
        {
            throw new InvalidOperationException("Компонент брандмауэра Windows недоступен.");
        }

        return Activator.CreateInstance(type) ?? throw new InvalidOperationException("Компонент брандмауэра Windows недоступен.");
    }

    private static object Get(object target, string name)
    {
        return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null)
            ?? throw new InvalidOperationException("Брандмауэр не вернул " + name + ".");
    }

    private static void Set(object target, string name, object value)
    {
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);
    }

    private static void Invoke(object target, string name, object argument)
    {
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, [argument]);
    }

    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
        {
            Marshal.FinalReleaseComObject(com);
        }
    }
}
