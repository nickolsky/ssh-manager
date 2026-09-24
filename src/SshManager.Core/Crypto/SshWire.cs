using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace SshManager.Core.Crypto;

/// <summary>SSH wire encoding (RFC 4251): uint32, string, mpint.</summary>
public sealed class SshWriter
{
    private readonly MemoryStream _ms = new();

    public SshWriter UInt32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        _ms.Write(b);
        return this;
    }

    public SshWriter Byte(byte v)
    {
        _ms.WriteByte(v);
        return this;
    }

    public SshWriter Raw(ReadOnlySpan<byte> data)
    {
        _ms.Write(data);
        return this;
    }

    public SshWriter String(ReadOnlySpan<byte> data)
    {
        UInt32((uint)data.Length);
        _ms.Write(data);
        return this;
    }

    public SshWriter String(string s) => String(Encoding.UTF8.GetBytes(s));

    /// <summary>mpint from an unsigned big-endian magnitude.</summary>
    public SshWriter MpInt(ReadOnlySpan<byte> unsignedBigEndian)
    {
        int i = 0;
        while (i < unsignedBigEndian.Length && unsignedBigEndian[i] == 0) i++;
        var mag = unsignedBigEndian[i..];
        if (mag.Length == 0) return UInt32(0);
        bool pad = (mag[0] & 0x80) != 0;
        UInt32((uint)(mag.Length + (pad ? 1 : 0)));
        if (pad) _ms.WriteByte(0);
        _ms.Write(mag);
        return this;
    }

    public byte[] ToArray() => _ms.ToArray();
    public int Length => (int)_ms.Length;
}

public sealed class SshReader
{
    private readonly byte[] _buf;
    private int _pos;

    public SshReader(byte[] buf, int offset = 0)
    {
        _buf = buf;
        _pos = offset;
    }

    public bool EndOfData => _pos >= _buf.Length;
    public int Remaining => _buf.Length - _pos;

    public uint UInt32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32BigEndian(_buf.AsSpan(_pos, 4));
        _pos += 4;
        return v;
    }

    public byte Byte()
    {
        Need(1);
        return _buf[_pos++];
    }

    public byte[] Raw(int count)
    {
        Need(count);
        var r = _buf.AsSpan(_pos, count).ToArray();
        _pos += count;
        return r;
    }

    public byte[] String()
    {
        var len = UInt32();
        if (len > int.MaxValue) throw new FormatException("SSH string too long");
        return Raw((int)len);
    }

    public string StringUtf8() => Encoding.UTF8.GetString(String());

    /// <summary>Reads an mpint and returns the unsigned big-endian magnitude (no leading zeros).</summary>
    public byte[] MpInt()
    {
        var b = String();
        int i = 0;
        while (i < b.Length && b[i] == 0) i++;
        return b[i..];
    }

    private void Need(int n)
    {
        if (n < 0 || _pos + n > _buf.Length) throw new FormatException("Unexpected end of SSH data");
    }
}

internal static class BigEndian
{
    public static BigInteger ToBigInteger(byte[] unsignedBigEndian) =>
        new(unsignedBigEndian, isUnsigned: true, isBigEndian: true);

    public static byte[] FromBigInteger(BigInteger v, int length = 0)
    {
        var bytes = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (length == 0 || bytes.Length == length) return bytes;
        if (bytes.Length > length) throw new ArgumentException("Value does not fit");
        var r = new byte[length];
        bytes.CopyTo(r, length - bytes.Length);
        return r;
    }

    public static byte[] Pad(byte[] b, int length) => FromBigInteger(ToBigInteger(b), length);
}
