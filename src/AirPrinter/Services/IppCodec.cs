using System.Globalization;
using System.Text;

namespace AirPrinter.Services;

public sealed class IppAttribute
{
    public byte Group { get; init; }
    public byte Tag { get; init; }
    public required string Name { get; init; }
    public List<byte[]> Values { get; } = [];
}

public sealed class IppMessage
{
    public byte Major { get; init; }
    public byte Minor { get; init; }
    public ushort OperationOrStatus { get; init; }
    public int RequestId { get; init; }
    public List<IppAttribute> Attributes { get; } = [];
    public byte[] Document { get; set; } = [];

    public string? GetText(string name)
    {
        IppAttribute? attribute = Attributes.FirstOrDefault(item => item.Name == name && item.Values.Count > 0);
        return attribute == null ? null : Encoding.UTF8.GetString(attribute.Values[0]);
    }

    public int? GetInteger(string name)
    {
        IppAttribute? attribute = Attributes.FirstOrDefault(item => item.Name == name && item.Values.Count > 0 && item.Values[0].Length >= 4);
        if (attribute == null)
        {
            return null;
        }

        byte[] value = attribute.Values[0];
        return (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];
    }

    public bool? GetBoolean(string name)
    {
        IppAttribute? attribute = Attributes.FirstOrDefault(item => item.Name == name && item.Values.Count > 0 && item.Values[0].Length > 0);
        return attribute == null ? null : attribute.Values[0][0] != 0;
    }
}

public static class IppCodec
{
    public const ushort PrintJob = 0x0002;
    public const ushort ValidateJob = 0x0004;
    public const ushort CreateJob = 0x0005;
    public const ushort SendDocument = 0x0006;
    public const ushort CancelJob = 0x0008;
    public const ushort GetJobAttributes = 0x0009;
    public const ushort GetJobs = 0x000A;
    public const ushort GetPrinterAttributes = 0x000B;

    public const ushort Ok = 0x0000;
    public const ushort BadRequest = 0x0400;
    public const ushort NotFound = 0x0406;
    public const ushort DocumentFormatNotSupported = 0x040A;
    public const ushort Busy = 0x0507;

    public const byte OperationGroup = 0x01;
    public const byte JobGroup = 0x02;
    public const byte EndTag = 0x03;
    public const byte PrinterGroup = 0x04;

    public static IppMessage Parse(byte[] data)
    {
        if (data.Length < 9)
        {
            throw new InvalidDataException("Слишком короткое IPP-сообщение.");
        }

        int index = 0;
        var message = new IppMessage
        {
            Major = data[index++],
            Minor = data[index++],
            OperationOrStatus = ReadUInt16(data, ref index),
            RequestId = ReadInt32(data, ref index),
        };

        byte group = 0;
        IppAttribute? current = null;
        while (index < data.Length)
        {
            byte tag = data[index++];
            if (tag == EndTag)
            {
                var document = new byte[data.Length - index];
                Buffer.BlockCopy(data, index, document, 0, document.Length);
                message.Document = document;
                return message;
            }

            if (tag < 0x10)
            {
                group = tag;
                current = null;
                continue;
            }

            string name = ReadCountedString(data, ref index);
            byte[] value = ReadCountedBytes(data, ref index);
            if (tag == 0x37)
            {
                continue;
            }

            if (name.Length == 0 && current != null && tag != 0x34)
            {
                current.Values.Add(value);
                continue;
            }

            current = new IppAttribute
            {
                Group = group,
                Tag = tag,
                Name = name,
            };
            current.Values.Add(value);
            message.Attributes.Add(current);
        }

        throw new InvalidDataException("В IPP-сообщении нет конца атрибутов.");
    }

    public static byte[] BuildRequest(byte major, byte minor, ushort operation, int requestId, Action<IppWriter> write, byte[]? document = null)
    {
        var writer = new IppWriter();
        writer.Begin(major, minor, operation, requestId);
        writer.Group(OperationGroup);
        write(writer);
        writer.End();
        if (document is { Length: > 0 })
        {
            writer.Append(document);
        }

        return writer.ToArray();
    }

    private static ushort ReadUInt16(byte[] data, ref int index)
    {
        if (index + 2 > data.Length)
        {
            throw new InvalidDataException("IPP оборван.");
        }

        ushort value = (ushort)((data[index] << 8) | data[index + 1]);
        index += 2;
        return value;
    }

    private static int ReadInt32(byte[] data, ref int index)
    {
        if (index + 4 > data.Length)
        {
            throw new InvalidDataException("IPP оборван.");
        }

        int value = (data[index] << 24) | (data[index + 1] << 16) | (data[index + 2] << 8) | data[index + 3];
        index += 4;
        return value;
    }

    private static string ReadCountedString(byte[] data, ref int index)
    {
        byte[] bytes = ReadCountedBytes(data, ref index);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] ReadCountedBytes(byte[] data, ref int index)
    {
        ushort length = ReadUInt16(data, ref index);
        if (index + length > data.Length)
        {
            throw new InvalidDataException("IPP оборван.");
        }

        var bytes = new byte[length];
        Buffer.BlockCopy(data, index, bytes, 0, length);
        index += length;
        return bytes;
    }
}

public sealed class IppWriter
{
    private readonly MemoryStream _stream = new();
    private int _collectionDepth;

    public void Begin(byte major, byte minor, ushort status, int requestId)
    {
        _stream.WriteByte(major);
        _stream.WriteByte(minor);
        WriteUInt16(status);
        WriteInt32(requestId);
    }

    public void Group(byte tag) => _stream.WriteByte(tag);

    public void Charset(string name, string value) => Emit(0x47, name, Encoding.UTF8.GetBytes(value));

    public void Language(string name, string value) => Emit(0x48, name, Encoding.UTF8.GetBytes(value));

    public void Text(string name, string value) => Emit(0x41, name, Encoding.UTF8.GetBytes(value));

    public void Name(string name, string value) => Emit(0x42, name, Encoding.UTF8.GetBytes(value));

    public void Keyword(string name, params string[] values) => Texts(0x44, name, values);

    public void Uri(string name, params string[] values) => Texts(0x45, name, values);

    public void Mime(string name, params string[] values) => Texts(0x49, name, values);

    public void Integer(string name, int value) => Emit(0x21, name, BigEndian(value));

    public void Enum(string name, params int[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            Emit(0x23, i == 0 ? name : "", BigEndian(values[i]));
        }
    }

    public void Boolean(string name, bool value) => Emit(0x22, name, [(byte)(value ? 1 : 0)]);

    public void Resolution(string name, params (int X, int Y)[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            var bytes = new byte[9];
            Buffer.BlockCopy(BigEndian(values[i].X), 0, bytes, 0, 4);
            Buffer.BlockCopy(BigEndian(values[i].Y), 0, bytes, 4, 4);
            bytes[8] = 3;
            Emit(0x32, i == 0 ? name : "", bytes);
        }
    }

    public void Range(string name, int lower, int upper)
    {
        var bytes = new byte[8];
        byte[] left = BigEndian(lower);
        byte[] right = BigEndian(upper);
        Buffer.BlockCopy(left, 0, bytes, 0, 4);
        Buffer.BlockCopy(right, 0, bytes, 4, 4);
        Emit(0x33, name, bytes);
    }

    public void Collection(string name, Action<IppWriter> members)
    {
        if (_collectionDepth > 0 && name.Length > 0)
        {
            Raw(0x4A, "", Encoding.ASCII.GetBytes(name));
            Raw(0x34, "", []);
        }
        else
        {
            Raw(0x34, name, []);
        }

        _collectionDepth++;
        try
        {
            members(this);
        }
        finally
        {
            _collectionDepth--;
        }

        Raw(0x37, "", []);
    }

    public void End() => _stream.WriteByte(IppCodec.EndTag);

    public void Append(byte[] document) => _stream.Write(document);

    public byte[] ToArray() => _stream.ToArray();

    private void Texts(byte tag, string name, string[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            Emit(tag, i == 0 ? name : "", Encoding.UTF8.GetBytes(values[i]));
        }
    }

    private void Emit(byte tag, string name, byte[] value)
    {
        if (_collectionDepth > 0 && name.Length > 0)
        {
            Raw(0x4A, "", Encoding.ASCII.GetBytes(name));
            Raw(tag, "", value);
            return;
        }

        Raw(tag, name, value);
    }

    private void Raw(byte tag, string name, byte[] value)
    {
        _stream.WriteByte(tag);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        WriteUInt16((ushort)nameBytes.Length);
        _stream.Write(nameBytes);
        WriteUInt16((ushort)value.Length);
        _stream.Write(value);
    }

    private void WriteUInt16(ushort value)
    {
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }

    private void WriteInt32(int value)
    {
        byte[] bytes = BigEndian(value);
        _stream.Write(bytes);
    }

    private static byte[] BigEndian(int value)
    {
        return
        [
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value,
        ];
    }
}

public static class IppHex
{
    public static int ParseChunkSize(string line)
    {
        string hex = line.Split(';')[0].Trim();
        return int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
