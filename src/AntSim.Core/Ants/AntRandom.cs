namespace AntSim.Core.Ants;

/// <summary>
/// 개체별 결정적 난수(xorshift64*). 상태는 개미마다 ulong 하나로 SoA 배열에 저장된다.
/// System.Random과 달리 .NET 버전·플랫폼과 무관하게 같은 시드에서 같은 수열을 낸다.
/// </summary>
public static class AntRandom
{
    /// <summary>시드와 스트림 번호(개미 ID 등)에서 0이 아닌 초기 상태를 만든다(SplitMix64).</summary>
    public static ulong CreateState(ulong seed, ulong stream)
    {
        ulong z = seed + 0x9E3779B97F4A7C15UL * (stream + 1);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return z == 0 ? 0x2545F4914F6CDD1DUL : z;
    }

    public static ulong NextULong(ref ulong state)
    {
        ulong x = state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        state = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>[0, 1) 균등 분포.</summary>
    public static float NextFloat(ref ulong state) => (NextULong(ref state) >> 40) * (1f / (1 << 24));

    /// <summary>[min, max) 균등 분포.</summary>
    public static float Range(ref ulong state, float min, float max) => min + (max - min) * NextFloat(ref state);

    /// <summary>[0, count) 정수.</summary>
    public static int NextInt(ref ulong state, int count) => (int)((NextULong(ref state) >> 33) % (ulong)count);
}
