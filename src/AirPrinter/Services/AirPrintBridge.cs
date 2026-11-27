using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirPrinter.Services;

public sealed class AirPrintBridge : IDisposable
{
    private readonly object _gate = new();
    private readonly BonjourAdvertiser _bonjour = new();
    private readonly MdnsPublisher _mdns = new();
    private bool _builtinMdns;
    private readonly PrintRenderer _renderer = new();
    private IppServer? _server;
    private PrinterSnapshot _snapshot = new();
    private bool _disposed;

    public event Action<string>? Logged;

    public bool IsRunning => _server != null;

    public int Port { get; private set; }

    public void Start(InstalledPrinter printer, bool share, string shareName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            throw new InvalidOperationException("Мост уже включён.");
        }

        EnsureBonjour();
        var server = new IppServer(_renderer, Snapshot, Log);
        int port = Bind(server);
        Port = port;
        try
        {
            Elevation.Apply(new SetupRequest
            {
                AllowFirewall = true,
                Port = port,
                ChangeShare = true,
                Printer = printer.Name,
                Share = share,
                ShareName = shareName,
            });
            Log("В частной сети открыт TCP-порт " + port + ".");
            Log(share
                ? "Принтер расшарен как «" + shareName + "». Для iPhone открыты только IPP и Bonjour, не вся группа общего доступа Windows."
                : "Общий доступ Windows для «" + printer.Name + "» выключен.");
        }
        catch (Exception ex)
        {
            server.Stop();
            server.Dispose();
            Log("Настройка: " + ex.GetType().Name);
            throw;
        }

        _server = server;
        lock (_gate)
        {
            _snapshot = CreateSnapshot(printer, port);
        }

        try
        {
            Publish();
        }
        catch
        {
            Stop();
            throw;
        }

        Log("AirPrint включён. Принтер «" + _snapshot.ServiceName + "», порт " + port + ".");
        Log("Адреса: " + string.Join(", ", _snapshot.Hosts));
        Log("iPhone должен быть в той же сети Wi-Fi. Не давайте компьютеру уснуть.");
        if (printer.IsOffline)
        {
            Log("Очередь Windows сейчас офлайн. Печать начнётся, когда принтер снова будет готов.");
        }
    }

    public void Update(InstalledPrinter printer, bool share, string shareName)
    {
        if (!IsRunning)
        {
            return;
        }

        Elevation.Apply(new SetupRequest
        {
            ChangeShare = true,
            Printer = printer.Name,
            Share = share,
            ShareName = shareName,
        });
        _server?.DropClients();
        lock (_gate)
        {
            _snapshot = CreateSnapshot(printer, Port);
        }

        Publish();
        Log("Старое объявление снято. Принтер AirPrint переключён на «" + printer.Name + "».");
    }

    public void Stop()
    {
        try
        {
            if (Port != 0)
            {
                Elevation.Apply(new SetupRequest { RemoveFirewall = true });
            }
        }
        catch (Exception ex)
        {
            Log("Брандмауэр: " + ex.GetType().Name);
        }

        _bonjour.Stop();
        _mdns.Stop();
        _server?.Stop();
        _server?.Dispose();
        _server = null;
        if (Port != 0)
        {
            Log("AirPrint выключен.");
        }

        Port = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _bonjour.Dispose();
        _mdns.Dispose();
    }

    private void EnsureBonjour()
    {
        BonjourStatus status = BonjourAdvertiser.GetStatus();
        if (status.DllInstalled && status.ServiceInstalled && status.ServiceStatus != System.ServiceProcess.ServiceControllerStatus.Running)
        {
            try
            {
                BonjourAdvertiser.TryStartService();
            }
            catch (Exception ex)
            {
                Log("Служба Bonjour не запустилась: " + ex.GetType().Name);
            }
        }

        status = BonjourAdvertiser.GetStatus();
        _builtinMdns = !status.IsReady;
        if (_builtinMdns)
        {
            Log("Apple Bonjour не запущен. Принтер объявляется встроенным mDNS.");
        }
    }

    private void Publish()
    {
        AirPrintAdvertisement advertisement = CreateAdvertisement();
        if (_builtinMdns)
        {
            _bonjour.Stop();
            _mdns.Start(advertisement, Environment.MachineName, LocalAddresses());
            return;
        }

        _mdns.Stop();
        _bonjour.Advertise(advertisement);
    }

    private static int Bind(IppServer server)
    {
        foreach (int candidate in new[] { 631, 6310 })
        {
            try
            {
                server.Start(candidate);
                return server.Port;
            }
            catch (SocketException)
            {
            }
        }

        server.Start(0);
        return server.Port;
    }

    private PrinterSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PrinterSnapshot
            {
                QueueName = _snapshot.QueueName,
                Model = _snapshot.Model,
                ServiceName = _snapshot.ServiceName,
                UuidUrn = _snapshot.UuidUrn,
                Color = _snapshot.Color,
                Duplex = _snapshot.Duplex,
                Port = _snapshot.Port,
                Hosts = _snapshot.Hosts.ToList(),
            };
        }
    }

    private PrinterSnapshot CreateSnapshot(InstalledPrinter printer, int port)
    {
        return new PrinterSnapshot
        {
            QueueName = printer.Name,
            Model = string.IsNullOrWhiteSpace(printer.DriverName) ? printer.Name : printer.DriverName,
            ServiceName = BonjourAdvertiser.ServiceName(printer.Name),
            UuidUrn = IdentityStore.GetOrCreate(printer.Name),
            Color = printer.SupportsColor,
            Duplex = printer.CanDuplex,
            Port = port,
            Hosts = LocalHosts(),
        };
    }

    private AirPrintAdvertisement CreateAdvertisement()
    {
        PrinterSnapshot snapshot = Snapshot();
        return new AirPrintAdvertisement
        {
            ServiceName = snapshot.ServiceName,
            Model = snapshot.Model,
            UuidUrn = snapshot.UuidUrn,
            Port = snapshot.Port,
            Color = snapshot.Color,
            Duplex = snapshot.Duplex,
        };
    }

    private static List<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                {
                    addresses.Add(address.Address);
                }
            }
        }

        return addresses;
    }

    private static List<string> LocalHosts()
    {
        var hosts = new List<string>();
        string machine = Environment.MachineName.Trim();
        if (machine.Length > 0)
        {
            hosts.Add(machine + ".local");
        }

        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address.Address))
                {
                    continue;
                }

                string text = address.Address.ToString();
                if (!hosts.Contains(text))
                {
                    hosts.Add(text);
                }
            }
        }

        if (hosts.Count == 0)
        {
            hosts.Add("127.0.0.1");
        }

        return hosts;
    }

    private void Log(string message) => Logged?.Invoke(message);
}
