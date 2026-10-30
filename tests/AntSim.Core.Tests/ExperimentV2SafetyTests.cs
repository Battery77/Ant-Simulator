using System.Buffers.Binary;
using AntSim.Core.Ants;
using AntSim.Core.Scent;
using AntSim.Core.Serialization;
using AntSim.Core.Simulation;
using AntSim.Core.Util;
using AntSim.Core.Worlds;
using Xunit.Abstractions;

namespace AntSim.Core.Tests;

/// <summary>
/// 실험 파일 v1/v2 호환과 v2 개미 구간의 불러오기 안전성(할당 전 길이 검증, 필드 검증, 레코드 경계).
/// PR #2의 F01/E01/N01 계약이 개미 구간에도 그대로 적용되는지 확인한다.
/// </summary>
public class ExperimentV2SafetyTests(ITestOutputHelper output)
{
    private static readonly AntSettings Ants = AntSettings.Default with { InitialCount = 40, Seed = 5 };

    private static SimulationSession NewSession(int ants = 40) =>
        new(SimulationSessionTests.SampleWorld(), ScentSettings.Default, Ants with { InitialCount = ants });

    private static byte[] Save(SimulationSession session)
    {
        using var stream = new MemoryStream();
        ExperimentSerializer.Save(session, stream);
        return stream.ToArray();
    }

    private static SimulationSession Load(byte[] bytes) => ExperimentSerializer.Load(new MemoryStream(bytes));

    /// <summary>v2 개미 구간의 위치. 파일 끝 = ... count i32, nextId i32, 배열(63n), checksum u64.</summary>
    public readonly record struct AntLayout(int Count, int CountOffset)
    {
        public int Ids => CountOffset + 8;
        public int Caste => Ids + 4 * Count;
        public int Layer => Caste + Count;
        public int Mobility => Layer + Count;
        public int Float(int index) => Mobility + Count + index * 4 * Count; // 0 X, 1 Y, 2 PrevX, 3 PrevY, 4 Heading, 5 Target, 6 BaseSpeed, 7 SpeedFactor, 8 Size, 9 TurnTimer, 10 TurnScale
        public int LastTile => Float(11);
        public int Rng => LastTile + 4 * Count;

        public static AntLayout Of(byte[] bytes, int count) => new(count, bytes.Length - 8 - ExperimentSerializer.BytesPerAnt * count - 8);
    }

    private static void Rechecksum(byte[] bytes)
    {
        var fnv = Fnv1a64.Create();
        fnv.Add(bytes.AsSpan(0, bytes.Length - 8));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8), fnv.Value);
    }

    // ---------------- v1 / v2 호환 ----------------

    [Fact]
    public void BytesPerAnt_IsSixtyThree()
    {
        Assert.Equal(63, ExperimentSerializer.BytesPerAnt);
        var one = Save(NewSession(1));
        var two = Save(NewSession(2));
        var layout = AntLayout.Of(one, 1);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(one.AsSpan(layout.CountOffset)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(two.AsSpan(AntLayout.Of(two, 2).CountOffset)));
    }

    /// <summary>PR #2 형식의 v1 실험(개미 구간 없음)을 불러오면 월드·냄새·틱·시간은 그대로, 개미 0마리. 이후 저장은 v2.</summary>
    [Fact]
    public void V1Record_LoadsWithZeroAnts_AndSavesAsV2()
    {
        var map = AuditFiles.MapBytes(TestWorlds.Create(16, 16));
        var scent = new float[16 * 16 * 2];
        scent[17] = 3.5f;
        var v1 = AuditFiles.Experiment(1, map, map, scent);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(v1.AsSpan(8)));

        var loaded = Load(v1);
        Assert.Equal(0, loaded.Ants.Count);
        Assert.Equal(AntSettings.None, loaded.AntSettings);
        Assert.Equal(3.5f, loaded.Scent.Field(0, LayerId.Surface)[17]);

        var v2 = Save(loaded);
        Assert.Equal(ExperimentSerializer.CurrentVersion, BinaryPrimitives.ReadUInt16LittleEndian(v2.AsSpan(8)));
        Assert.Equal(2, ExperimentSerializer.CurrentVersion);
        Assert.Equal(loaded.ComputeStateHash(), Load(v2).ComputeStateHash());

        // 초기화해도 개미 0마리(v1 → 개미 없음 계약), 개미 설정 적용으로 다시 생성할 수 있다.
        loaded.Reset();
        Assert.Equal(0, loaded.Ants.Count);
        loaded.Reset(Ants with { InitialCount = 3 });
        Assert.Equal(3, loaded.Ants.Count);
    }

    /// <summary>v2: Save → Load → Step → Save → Load. 저장 직후·불러온 직후 상태와 이후 틱이 결정적으로 같다.</summary>
    [Fact]
    public void V2_SaveLoadStepSaveLoad_IsDeterministic()
    {
        var reference = NewSession();
        reference.Step(90);

        var session = NewSession();
        session.Step(30);
        var first = Load(Save(session));
        Assert.Equal(session.ComputeStateHash(), first.ComputeStateHash());
        Assert.Equal(session.Ants.ComputeHash(), first.Ants.ComputeHash()); // ID·RNG·좌표·방향·속도 등 모든 필드
        first.Step(30);
        var second = Load(Save(first));
        Assert.Equal(first.ComputeStateHash(), second.ComputeStateHash());
        second.Step(30);
        Assert.Equal(reference.ComputeStateHash(), second.ComputeStateHash());
    }

    /// <summary>개미 0 / 1 / 지원 최대(100,000)마리 경계 왕복.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(AntSettings.MaxCount)]
    public void AntCountBoundaries_RoundTrip(int count)
    {
        var session = NewSession(count);
        Assert.Equal(count, session.Ants.Count);
        session.Step(2);
        var bytes = Save(session);
        var loaded = Load(bytes);
        Assert.Equal(count, loaded.Ants.Count);
        Assert.Equal(session.ComputeStateHash(), loaded.ComputeStateHash());
        output.WriteLine($"개미 {count}마리: 파일 {bytes.Length}바이트");
    }

    // ---------------- 할당 전 길이 검증 ----------------

    /// <summary>개미 수를 크게(최대 100,000) 선언하고 본문은 1마리뿐: 개미 배열을 만들기 전에 거부한다(작은 할당).</summary>
    [Theory]
    [InlineData(AntSettings.MaxCount, true)]
    [InlineData(50_000, true)]
    [InlineData(2, true)]
    [InlineData(AntSettings.MaxCount, false)]
    public void LargeDeclaredAntCount_WithShortBody_IsRejectedBeforeAllocation(int declared, bool seekable)
    {
        var bytes = Save(NewSession(1));
        var layout = AntLayout.Of(bytes, 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(layout.CountOffset), declared);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(layout.CountOffset + 4), declared);
        Rechecksum(bytes);
        // 기준: 같은 파일의 정상 1마리 버전을 같은 방식으로 불러올 때의 할당. 거짓으로 큰 개미 수는 이보다 거의 더 할당하지 않아야 한다
        // (선할당했다면 최대 100,000 × 63바이트 ≈ 6.3MB가 추가된다).
        var valid = Save(NewSession(1));
        var baseline = AuditFiles.Measure(() => ExperimentSerializer.Load(seekable ? new MemoryStream(valid) : new RecordingStream(valid)));
        Assert.Null(baseline.Error);
        var (allocated, error) = AuditFiles.Measure(() =>
            ExperimentSerializer.Load(seekable ? new MemoryStream(bytes) : new RecordingStream(bytes)));
        output.WriteLine($"선언 {declared}, seek={seekable}: 할당 {allocated}바이트(정상 1마리 {baseline.Allocated}), {error?.Message}");
        Assert.IsType<MapFormatException>(error);
        Assert.Contains("개미 상태", error!.Message);
        Assert.True(allocated <= baseline.Allocated + 64 * 1024,
            $"개미 배열 선할당 의심: {allocated}바이트(정상 1마리 {baseline.Allocated})");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(AntSettings.MaxCount + 1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void OutOfRangeAntCount_IsRejected(int declared)
    {
        var bytes = Save(NewSession(1));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(AntLayout.Of(bytes, 1).CountOffset), declared);
        Rechecksum(bytes);
        var error = Assert.Throws<MapFormatException>(() => Load(bytes));
        Assert.Contains("개미 수", error.Message);
    }

    [Fact]
    public void NextIdBelowCount_IsRejected()
    {
        var bytes = Save(NewSession(3));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(AntLayout.Of(bytes, 3).CountOffset + 4), 2);
        Rechecksum(bytes);
        Assert.Contains("다음 개미 ID", Assert.Throws<MapFormatException>(() => Load(bytes)).Message);
    }

    /// <summary>개미 본문 중간·설정 구간에서 잘린 파일.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(100)]
    [InlineData(63 * 40)]
    [InlineData(63 * 40 + 8 + 20)]
    public void TruncatedAntSection_IsRejected(int cut)
    {
        var bytes = Save(NewSession());
        foreach (var stream in new Func<byte[], Stream>[] { b => new MemoryStream(b), b => new RecordingStream(b) })
            Assert.Throws<MapFormatException>(() => ExperimentSerializer.Load(stream(bytes[..^cut])));
    }

    // ---------------- 개체 필드 검증 ----------------

    public static IEnumerable<object[]> InvalidFields() => new[]
    {
        new object[] { "중복 ID", (Action<byte[], AntLayout>)((b, l) => Int(b, l.Ids + 4, 0)) },
        new object[] { "ID ≥ nextId", (Action<byte[], AntLayout>)((b, l) => Int(b, l.Ids, 1000)) },
        new object[] { "음수 ID", (Action<byte[], AntLayout>)((b, l) => Int(b, l.Ids, -3)) },
        new object[] { "계급", (Action<byte[], AntLayout>)((b, l) => b[l.Caste] = 1) },
        new object[] { "레이어", (Action<byte[], AntLayout>)((b, l) => b[l.Layer] = 2) },
        new object[] { "이동 능력", (Action<byte[], AntLayout>)((b, l) => b[l.Mobility] = 0) },
        new object[] { "X NaN", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(0), float.NaN)) },
        new object[] { "X 맵 밖", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(0), -5)) },
        new object[] { "바위 위", (Action<byte[], AntLayout>)((b, l) => { Float(b, l.Float(0), 35.5f); Float(b, l.Float(1), 10.5f); b[l.Layer] = 0; }) },
        new object[] { "이전 위치 무한대", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(2), float.PositiveInfinity)) },
        new object[] { "이전 위치 맵 밖", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(3), 1e6f)) },
        new object[] { "방향 NaN", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(4), float.NaN)) },
        new object[] { "목표 방향 범위 밖", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(5), 100f)) },
        new object[] { "기본 속도 0", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(6), 0f)) },
        new object[] { "기본 속도 무한대", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(6), float.PositiveInfinity)) },
        new object[] { "속도 배율 2", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(7), 2f)) },
        new object[] { "크기 음수", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(8), -1f)) },
        new object[] { "타이머 NaN", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(9), float.NaN)) },
        new object[] { "간격 배율 0", (Action<byte[], AntLayout>)((b, l) => Float(b, l.Float(10), 0f)) },
        new object[] { "마지막 타일 음수", (Action<byte[], AntLayout>)((b, l) => Int(b, l.LastTile, -1)) },
        new object[] { "마지막 타일 맵 밖", (Action<byte[], AntLayout>)((b, l) => Int(b, l.LastTile, 80 * 60)) },
        new object[] { "난수 상태 0", (Action<byte[], AntLayout>)((b, l) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(l.Rng), 0)) },
    };

    private static void Int(byte[] b, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), v);
    private static void Float(byte[] b, int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), v);

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public void InvalidAntField_IsRejected(string name, Action<byte[], AntLayout> corrupt)
    {
        var bytes = Save(NewSession(3));
        Assert.Equal(Load(bytes).ComputeStateHash(), NewSession(3).ComputeStateHash()); // 손대기 전에는 정상
        corrupt(bytes, AntLayout.Of(bytes, 3));
        Rechecksum(bytes);
        var error = Assert.Throws<MapFormatException>(() => Load(bytes));
        output.WriteLine($"{name}: {error.Message}");
    }

    /// <summary>체크섬 손상(v1·v2, seek·non-seek)은 거부한다. 개미 구간 바이트도 체크섬에 포함된다.</summary>
    [Fact]
    public void ChecksumCorruption_IsRejected_ForV1AndV2()
    {
        var map = AuditFiles.MapBytes(TestWorlds.Create(16, 16));
        var v1 = AuditFiles.Experiment(1, map, map, new float[16 * 16 * 2]);
        var v2 = Save(NewSession(3));
        foreach (var (bytes, at) in new[] { (v1, v1.Length - 12), (v2, AntLayout.Of(v2, 3).Rng + 1), (v2, v2.Length - 1) })
        {
            var corrupted = (byte[])bytes.Clone();
            corrupted[at] ^= 0x10;
            Assert.Throws<MapFormatException>(() => Load(corrupted));
            Assert.Throws<MapFormatException>(() => ExperimentSerializer.Load(new RecordingStream(corrupted)));
        }
    }

    /// <summary>저장할 수 있으면 불러올 수 있다: 불러오기에서 거부될 개미 상태는 저장 전에 거부하고 기존 파일을 보존한다.</summary>
    [Fact]
    public void Save_RefusesInvalidAntState_AndKeepsExistingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"antsim-{Guid.NewGuid():N}.antexp");
        try
        {
            var good = NewSession(3);
            ExperimentSerializer.Save(good, path);
            byte[] original = File.ReadAllBytes(path);

            var bad = NewSession(3);
            bad.Ants.XArray[1] = float.NaN;
            Assert.Throws<InvalidOperationException>(() => ExperimentSerializer.ValidateForSave(bad));
            Assert.Throws<InvalidOperationException>(() => ExperimentSerializer.Save(bad, path));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>불러오기 실패는 실행 중인 세션을 바꾸지 않는다(새 세션은 성공했을 때만 만들어진다).</summary>
    [Fact]
    public async Task FailedLoad_LeavesRunningSessionUntouched()
    {
        using var host = new SimulationHost(NewSession());
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(10));
        ulong before = await host.RunExclusiveAsync(s => s.ComputeStateHash()).WaitAsync(TimeSpan.FromSeconds(10));
        var bytes = Save(NewSession(5));
        Assert.Throws<MapFormatException>(() => Load(bytes[..^10]));
        Assert.Equal(before, await host.RunExclusiveAsync(s => s.ComputeStateHash()).WaitAsync(TimeSpan.FromSeconds(10)));
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, host.Tick);
    }

    // ---------------- 레코드 경계(N01) ----------------

    public static IEnumerable<object[]> ReadModes() => new[]
    {
        new object[] { "seek", 0 }, new object[] { "non-seek", int.MaxValue }, new object[] { "1바이트씩", 1 }, new object[] { "3바이트씩", 3 },
    };

    private static Stream Open(byte[] bytes, int maxPerRead) =>
        maxPerRead == 0 ? new MemoryStream(bytes) : new RecordingStream(bytes, maxPerRead: maxPerRead);

    /// <summary>두 v2 레코드, v1 다음 v2 레코드를 같은 스트림에서 차례로 불러온다. 각 Load는 자기 레코드의 체크섬 직후에서 멈춘다.</summary>
    [Theory]
    [MemberData(nameof(ReadModes))]
    public void ConsecutiveRecords_V2V2_And_V1V2(string mode, int maxPerRead)
    {
        var a = NewSession(7);
        a.Step(3);
        var b = NewSession(12);
        b.Step(9);
        byte[] ra = Save(a), rb = Save(b);
        var map = AuditFiles.MapBytes(TestWorlds.Create(16, 16));
        byte[] v1 = AuditFiles.Experiment(1, map, map, new float[16 * 16 * 2]);
        ulong v1Hash = Load(v1).ComputeStateHash();

        foreach (var (first, firstHash) in new[] { (ra, a.ComputeStateHash()), (v1, v1Hash) })
        {
            using var stream = Open(first.Concat(rb).ToArray(), maxPerRead);
            Assert.Equal(firstHash, ExperimentSerializer.Load(stream).ComputeStateHash());
            if (stream is RecordingStream r1) Assert.Equal(first.Length, r1.Consumed);
            else Assert.Equal(first.Length, stream.Position);
            Assert.Equal(b.ComputeStateHash(), ExperimentSerializer.Load(stream).ComputeStateHash());
            if (stream is RecordingStream r2) Assert.Equal(first.Length + rb.Length, r2.Consumed);
        }
        output.WriteLine($"{mode}: v2 A={ra.Length}, v2 B={rb.Length}, v1={v1.Length}");
    }

    /// <summary>v2 체크섬(개미 구간 뒤)까지 읽으면 바로 반환한다: 레코드 끝 이후 Read 시도 0회.</summary>
    [Fact]
    public void V2_NoReadAfterChecksum()
    {
        var bytes = Save(NewSession(9));
        var stream = new RecordingStream(bytes, boundary: bytes.Length);
        Assert.Equal(9, ExperimentSerializer.Load(stream).Ants.Count);
        Assert.Equal(bytes.Length, stream.Consumed);
        Assert.Equal(0, stream.Overreads);
    }

    /// <summary>잘못된 매직 값은 개미 구간이 있는 긴 입력이어도 첫 8바이트만 읽고 거부한다.</summary>
    [Fact]
    public void BadMagic_IsRejectedAfterEightBytes()
    {
        var bytes = Save(NewSession(9));
        bytes[0] = (byte)'X';
        var stream = new RecordingStream(bytes);
        Assert.Throws<MapFormatException>(() => ExperimentSerializer.Load(stream));
        Assert.Equal(8, stream.Consumed);
    }
}
