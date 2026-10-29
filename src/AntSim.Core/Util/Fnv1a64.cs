namespace AntSim.Core.Util;

/// <summary>
/// 결정적 64비트 FNV-1a 해시. 플랫폼/프로세스와 무관하게 같은 입력에 같은 값을 낸다
/// (string.GetHashCode와 달리 실행마다 바뀌지 않는다). 재현성 검증용이며 암호학적 용도가 아니다.
/// </summary>
public struct Fnv1a64
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public ulong Value { get; private set; }

    public static Fnv1a64 Create() => new() { Value = OffsetBasis };

    public void Add(ReadOnlySpan<byte> bytes)
    {
        ulong h = Value;
        foreach (byte b in bytes)
        {
            h ^= b;
            h *= Prime;
        }
        Value = h;
    }

    public void Add(byte value) => Add([value]);

    public void Add(int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        Add(buffer);
    }

    public void Add(ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Add(buffer);
    }
}
