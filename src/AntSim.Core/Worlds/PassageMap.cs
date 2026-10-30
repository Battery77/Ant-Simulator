using System.Numerics;
using AntSim.Core.Util;

namespace AntSim.Core.Worlds;

/// <summary>
/// 지상(Layer 0)과 개미집(Layer 1)의 같은 좌표를 양방향으로 잇는 통로 집합.
/// 타일 속성과 분리된 연결 데이터이며, 좌표당 1비트 비트셋으로 저장한다(1024² 기준 128KB).
/// 순회는 항상 선형 인덱스 오름차순이므로 결정적이다.
/// </summary>
public sealed class PassageMap
{
    private readonly ulong[] _bits;

    internal PassageMap(WorldConfig config)
    {
        Config = config;
        _bits = new ulong[(config.TileCount + 63) >> 6];
    }

    public WorldConfig Config { get; }

    public int Count { get; private set; }

    /// <summary>통로가 추가/제거될 때마다 증가한다.</summary>
    public uint Version { get; private set; }

    public bool Contains(int x, int y)
    {
        if (!Config.InBounds(x, y)) return false;
        int i = Config.ToLinearIndex(x, y);
        return (_bits[i >> 6] & (1UL << (i & 63))) != 0;
    }

    /// <summary>검증 없이 통로 유무를 설정한다. 값이 바뀌었으면 true.</summary>
    internal bool Set(int x, int y, bool present)
    {
        int i = Config.ToLinearIndex(x, y);
        ref ulong word = ref _bits[i >> 6];
        ulong mask = 1UL << (i & 63);
        bool current = (word & mask) != 0;
        if (current == present) return false;
        word ^= mask;
        Count += present ? 1 : -1;
        Version++;
        return true;
    }

    internal void CopyFrom(PassageMap source)
    {
        source._bits.CopyTo(_bits, 0);
        Count = source.Count;
        Version = source.Version;
    }

    /// <summary>모든 통로의 선형 인덱스(y * Width + x)를 오름차순으로 추가한다.</summary>
    public void CopyIndicesTo(List<int> output)
    {
        for (int w = 0; w < _bits.Length; w++)
        {
            ulong word = _bits[w];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                output.Add((w << 6) | bit);
                word &= word - 1;
            }
        }
    }

    public IEnumerable<TileCoord> Enumerate()
    {
        var indices = new List<int>(Count);
        CopyIndicesTo(indices);
        foreach (int i in indices) yield return new TileCoord(i % Config.Width, i / Config.Width);
    }

    internal void AddToHash(ref Fnv1a64 hash)
    {
        hash.Add(Count);
        var indices = new List<int>(Count);
        CopyIndicesTo(indices);
        foreach (int i in indices) hash.Add(i);
    }
}
