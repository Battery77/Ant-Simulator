using System.Buffers.Binary;
using System.Text;
using AntSim.Core.Util;

namespace AntSim.Core.Serialization;

/// <summary>쓰는 모든 바이트의 FNV-1a 64 체크섬을 함께 계산하는 리틀 엔디언 기록기.</summary>
internal sealed class ChecksumWriter(Stream stream)
{
    private Fnv1a64 _hash = Fnv1a64.Create();

    public ulong Checksum => _hash.Value;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        _hash.Add(bytes);
        stream.Write(bytes);
    }

    public void WriteByte(byte value) => Write([value]);

    public void WriteUInt16(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        Write(b);
    }

    public void WriteInt32(int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        Write(b);
    }

    public void WriteInt64(long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value);
        Write(b);
    }

    public void WriteSingle(float value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, value);
        Write(b);
    }

    public void WriteDouble(double value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, value);
        Write(b);
    }

    public void WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(bytes.Length);
        Write(bytes);
    }

    /// <summary>체크섬 자체(마지막 8바이트)를 쓴다. 체크섬 계산에는 포함되지 않는다.</summary>
    public void WriteChecksum()
    {
        Span<byte> tail = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(tail, Checksum);
        stream.Write(tail);
    }
}

/// <summary>읽는 모든 바이트의 체크섬을 함께 계산하는 리틀 엔디언 판독기. 파일이 짧으면 <see cref="MapFormatException"/>.</summary>
internal sealed class ChecksumReader(Stream stream)
{
    public const int MaxStringBytes = 1 << 16;

    private Fnv1a64 _hash = Fnv1a64.Create();

    public ulong Checksum => _hash.Value;

    public void Read(Span<byte> buffer)
    {
        ReadRaw(buffer);
        _hash.Add(buffer);
    }

    public void ReadRaw(Span<byte> buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException e)
        {
            throw new MapFormatException("파일이 예상보다 짧습니다.", e);
        }
    }

    public byte ReadByte()
    {
        Span<byte> b = stackalloc byte[1];
        Read(b);
        return b[0];
    }

    public ushort ReadUInt16()
    {
        Span<byte> b = stackalloc byte[2];
        Read(b);
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    public int ReadInt32()
    {
        Span<byte> b = stackalloc byte[4];
        Read(b);
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    public long ReadInt64()
    {
        Span<byte> b = stackalloc byte[8];
        Read(b);
        return BinaryPrimitives.ReadInt64LittleEndian(b);
    }

    public float ReadSingle()
    {
        Span<byte> b = stackalloc byte[4];
        Read(b);
        return BinaryPrimitives.ReadSingleLittleEndian(b);
    }

    public double ReadDouble()
    {
        Span<byte> b = stackalloc byte[8];
        Read(b);
        return BinaryPrimitives.ReadDoubleLittleEndian(b);
    }

    public string ReadString()
    {
        int length = ReadInt32();
        if (length < 0 || length > MaxStringBytes) throw new MapFormatException($"문자열 길이가 잘못되었습니다: {length}.");
        var bytes = new byte[length];
        Read(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>파일 끝의 체크섬을 읽어 지금까지 계산한 값과 비교한다.</summary>
    public void VerifyChecksum()
    {
        ulong expected = Checksum;
        Span<byte> tail = stackalloc byte[8];
        ReadRaw(tail);
        if (BinaryPrimitives.ReadUInt64LittleEndian(tail) != expected)
            throw new MapFormatException("체크섬이 일치하지 않습니다. 파일이 손상되었습니다.");
    }
}
