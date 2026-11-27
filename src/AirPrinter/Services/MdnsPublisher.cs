using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AirPrinter.Services;

public sealed class MdnsPublisher : IDisposable
{
    public const int Port = 5353;
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndpoint = new(MulticastAddress, Port);

    private readonly object _gate = new();
    private Socket? _socket;
    private Thread? _thread;
    private volatile bool _stop;
    private AirPrintAdvertisement? _advertisement;
    private string _host = "airprinter";
    private List<IPAddress> _addresses = [];

    public static byte[] Query(string name, ushort type = 12)
    {
        var writer = new DnsWriter();
        writer.WriteHeader(0, 0, 1, 0, 0, 0);
        writer.WriteName(name);
        writer.WriteUInt16(type);
        writer.WriteUInt16(1);
        return writer.ToArray();
    }

    public static byte[] Reply(AirPrintAdvertisement advertisement, string host, IReadOnlyList<IPAddress> addresses, byte[] query)
    {
        return Reply(advertisement, host, addresses, query, query.Length);
    }

    public static byte[] Reply(AirPrintAdvertisement advertisement, string host, IReadOnlyList<IPAddress> addresses, byte[] query, int queryLength)
    {
        return Reply(advertisement, host, addresses, query, queryLength, null);
    }

    public static byte[] Reply(AirPrintAdvertisement advertisement, string host, IReadOnlyList<IPAddress> addresses, byte[] query, int queryLength, ushort? ttl)
    {
        List<DnsQuestion> questions = DnsReader.Questions(query, queryLength);
        List<DnsRecord> records = Describe(advertisement, host, addresses);
        List<DnsRecord> matched = Match(records, questions);
        var writer = new DnsWriter();
        writer.WriteHeader(0, 0x8400, 0, matched.Count, 0, 0);
        foreach (DnsRecord record in matched)
        {
            writer.WriteRecord(record, ttl);
        }

        return writer.ToArray();
    }

    public void Start(AirPrintAdvertisement advertisement, string host, IReadOnlyList<IPAddress> addresses)
    {
        Stop();
        _advertisement = advertisement;
        _host = HostLabel(host);
        _addresses = addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address)).Distinct().ToList();
        if (_addresses.Count == 0)
        {
            _addresses.Add(IPAddress.Parse("127.0.0.1"));
        }

        _socket = OpenSocket();
        _stop = false;
        Announce();
        _thread = new Thread(Listen) { IsBackground = true, Name = "AirPrinter mDNS" };
        // [ANTI-FP] Почему так: поток создаёт рантайм .NET и только после кнопки «включить», не native CreateThread при старте.
        _thread.Start();
    }

    public void Stop()
    {
        SendGoodbye();
        _stop = true;
        Socket? socket = _socket;
        _socket = null;
        if (socket != null)
        {
            try
            {
                socket.Close();
            }
            catch (SocketException)
            {
            }
        }

        _thread?.Join(1000);
        _thread = null;
    }

    public void Dispose() => Stop();

    public static Socket OpenSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);
        }
        catch (SocketException)
        {
        }

        socket.Bind(new IPEndPoint(IPAddress.Any, Port));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, 1);
        bool joined = false;
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddress, address.Address));
                    joined = true;
                }
                catch (SocketException)
                {
                }
            }
        }

        if (!joined)
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddress));
        }

        return socket;
    }

    private void Announce()
    {
        Send(ttl: null);
    }

    private void SendGoodbye()
    {
        Send(ttl: 0);
    }

    private void Send(ushort? ttl)
    {
        if (_advertisement == null || _socket == null)
        {
            return;
        }

        byte[] packet = Reply(_advertisement, _host, _addresses, [], 0, ttl);
        try
        {
            _socket.SendTo(packet, MulticastEndpoint);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Listen()
    {
        Socket? socket = _socket;
        if (socket == null)
        {
            return;
        }

        var buffer = new byte[1500];
        while (!_stop)
        {
            try
            {
                int size = socket.Receive(buffer);
                if (size < 12 || _advertisement == null)
                {
                    continue;
                }

                if ((buffer[2] & 0x80) != 0)
                {
                    continue;
                }

                byte[] answer = Reply(_advertisement, _host, _addresses, buffer, size);
                if (answer.Length > 12)
                {
                    socket.SendTo(answer, MulticastEndpoint);
                }
            }
            catch (SocketException)
            {
                if (_stop)
                {
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private static List<DnsRecord> Match(List<DnsRecord> records, List<DnsQuestion> questions)
    {
        if (questions.Count == 0)
        {
            return records;
        }

        var selected = new List<DnsRecord>();
        foreach (DnsRecord record in records)
        {
            bool hit = questions.Any(question =>
                string.Equals(question.Name, record.Name, StringComparison.OrdinalIgnoreCase)
                && (question.Type == 255 || question.Type == record.Type));
            if (hit)
            {
                selected.Add(record);
            }
        }

        if (selected.Any(record => record.Type == 12))
        {
            foreach (DnsRecord record in records)
            {
                if (record.Type != 12 && selected.All(item => item.Name != record.Name || item.Type != record.Type))
                {
                    bool related = selected.Any(item => item.Type == 12 && item.Target.Equals(record.Name, StringComparison.OrdinalIgnoreCase));
                    bool address = record.Type == 1;
                    if (related || address)
                    {
                        selected.Add(record);
                    }
                }
            }
        }

        return selected;
    }

    private static List<DnsRecord> Describe(AirPrintAdvertisement advertisement, string host, IReadOnlyList<IPAddress> addresses)
    {
        string instance = InstanceLabel(advertisement.ServiceName);
        string hostName = HostLabel(host) + ".local";
        string ippName = instance + "._ipp._tcp.local";
        string printerName = instance + "._printer._tcp.local";
        byte[] ippTxt = BonjourAdvertiser.BuildTxt(BonjourAdvertiser.AirPrintRecords(advertisement));
        byte[] printerTxt = BonjourAdvertiser.BuildTxt(BonjourAdvertiser.LegacyPrinterRecords(advertisement));
        var records = new List<DnsRecord>
        {
            new("_ipp._tcp.local", 12, false, ippName, null, 0, null),
            new("_universal._sub._ipp._tcp.local", 12, false, ippName, null, 0, null),
            new("_printer._tcp.local", 12, false, printerName, null, 0, null),
            new(ippName, 33, true, hostName, null, advertisement.Port, null),
            new(ippName, 16, true, "", ippTxt, 0, null),
            new(printerName, 33, true, hostName, null, 0, null),
            new(printerName, 16, true, "", printerTxt, 0, null),
        };
        foreach (IPAddress address in addresses.Where(item => item.AddressFamily == AddressFamily.InterNetwork))
        {
            records.Add(new DnsRecord(hostName, 1, true, "", address.GetAddressBytes(), 0, null));
        }

        return records;
    }

    private static string InstanceLabel(string name)
    {
        string cleaned = name.Replace('.', ' ').Trim();
        return cleaned.Length == 0 ? "Printer" : cleaned;
    }

    public static string HostLabel(string name)
    {
        var builder = new StringBuilder();
        foreach (char ch in name.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        }

        string value = builder.ToString().Trim('-');
        return value.Length == 0 ? "airprinter" : value.Length > 63 ? value[..63] : value;
    }

    private sealed record DnsQuestion(string Name, ushort Type);

    private sealed record DnsRecord(string Name, ushort Type, bool Unique, string Target, byte[]? Raw, int Port, string? Unused);

    private static class DnsReader
    {
        public static List<DnsQuestion> Questions(byte[] packet, int length)
        {
            var questions = new List<DnsQuestion>();
            if (length < 12 || length > packet.Length)
            {
                return questions;
            }

            int count = (packet[4] << 8) | packet[5];
            int offset = 12;
            for (int i = 0; i < count && offset < length; i++)
            {
                string name = ReadName(packet, length, ref offset);
                if (offset + 4 > length)
                {
                    break;
                }

                ushort type = (ushort)((packet[offset] << 8) | packet[offset + 1]);
                offset += 4;
                questions.Add(new DnsQuestion(name, type));
            }

            return questions;
        }

        private static string ReadName(byte[] packet, int length, ref int offset)
        {
            var labels = new List<string>();
            int hops = 0;
            int returnTo = -1;
            while (offset < length && hops < 20)
            {
                int labelLength = packet[offset];
                if (labelLength == 0)
                {
                    offset++;
                    break;
                }

                if ((labelLength & 0xC0) == 0xC0)
                {
                    if (offset + 1 >= length)
                    {
                        break;
                    }

                    int pointer = ((labelLength & 0x3F) << 8) | packet[offset + 1];
                    if (returnTo < 0)
                    {
                        returnTo = offset + 2;
                    }

                    offset = pointer;
                    hops++;
                    continue;
                }

                offset++;
                if (offset + labelLength > length)
                {
                    break;
                }

                labels.Add(Encoding.UTF8.GetString(packet, offset, labelLength));
                offset += labelLength;
            }

            if (returnTo >= 0)
            {
                offset = returnTo;
            }

            return string.Join('.', labels);
        }
    }

    private sealed class DnsWriter
    {
        private readonly MemoryStream _stream = new();
        private readonly Dictionary<string, int> _names = new(StringComparer.OrdinalIgnoreCase);

        public void WriteHeader(ushort id, ushort flags, int questions, int answers, int authorities, int additionals)
        {
            WriteUInt16(id);
            WriteUInt16(flags);
            WriteUInt16((ushort)questions);
            WriteUInt16((ushort)answers);
            WriteUInt16((ushort)authorities);
            WriteUInt16((ushort)additionals);
        }

        public void WriteRecord(DnsRecord record, ushort? ttl)
        {
            WriteName(record.Name);
            WriteUInt16(record.Type);
            WriteUInt16((ushort)(record.Unique ? 0x8001 : 1));
            ushort seconds = ttl ?? (record.Type == 12 ? (ushort)4500 : (ushort)120);
            WriteUInt16(0);
            WriteUInt16(seconds);
            long lengthAt = _stream.Position;
            WriteUInt16(0);
            long dataAt = _stream.Position;
            if (record.Type == 12)
            {
                WriteName(record.Target);
            }
            else if (record.Type == 33)
            {
                WriteUInt16(0);
                WriteUInt16(0);
                WriteUInt16((ushort)record.Port);
                WriteName(record.Target);
            }
            else if (record.Raw != null)
            {
                _stream.Write(record.Raw);
            }

            long end = _stream.Position;
            _stream.Position = lengthAt;
            WriteUInt16((ushort)(end - dataAt));
            _stream.Position = end;
        }

        public void WriteName(string fqdn)
        {
            if (_names.TryGetValue(fqdn, out int existing))
            {
                WriteUInt16((ushort)(0xC000 | existing));
                return;
            }

            string[] labels = fqdn.Split('.', StringSplitOptions.RemoveEmptyEntries);
            string suffix = fqdn;
            for (int i = 0; i < labels.Length; i++)
            {
                if (_names.TryGetValue(suffix, out int pointer))
                {
                    WriteUInt16((ushort)(0xC000 | pointer));
                    return;
                }

                _names[suffix] = (int)_stream.Position;
                byte[] bytes = Encoding.UTF8.GetBytes(labels[i]);
                if (bytes.Length > 63)
                {
                    bytes = bytes[..63];
                }

                _stream.WriteByte((byte)bytes.Length);
                _stream.Write(bytes);
                suffix = i == labels.Length - 1 ? "" : suffix[(labels[i].Length + 1)..];
            }

            _stream.WriteByte(0);
        }

        public void WriteUInt16(ushort value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
