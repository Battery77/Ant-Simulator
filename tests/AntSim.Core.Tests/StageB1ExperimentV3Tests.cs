using System.Buffers.Binary;
using System.Text;
using AntSim.Core.Ants;
using AntSim.Core.Scent;
using AntSim.Core.Serialization;
using AntSim.Core.Simulation;
using AntSim.Core.Util;
using AntSim.Core.Worlds;
using Xunit.Abstractions;

namespace AntSim.Core.Tests;

/// <summary>
/// 단계 B1 .antexp v3(docs/STAGE_B_DESIGN.md E): 저장·불러오기 결정성, v1/v2 초기값, 손상 파일 방어, 연속 레코드, 선택 세대 보호.
/// </summary>
public class StageB1ExperimentV3Tests(ITestOutputHelper output)
{
    private static readonly AntSettings Ants = AntSettings.Default with { InitialCount = 40, Seed = 5 };

    private static SimulationSession NewSession(int ants = 40, AntSettings? settings = null, ScentSettings? scent = null) =>
        new(SimulationSessionTests.SampleWorld(), scent ?? ScentSettings.Default, (settings ?? Ants) with { InitialCount = ants });

    private static byte[] Save(SimulationSession session) => ExperimentV2SafetyTests.SaveCurrent(session);

    private static SimulationSession Load(byte[] bytes) => ExperimentSerializer.Load(new MemoryStream(bytes));

    private static void Rechecksum(byte[] bytes)
    {
        var fnv = Fnv1a64.Create();
        fnv.Add(bytes.AsSpan(0, bytes.Length - 8));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8), fnv.Value);
    }

    /// <summary>v3 단계 B 구간의 위치(체크섬 바로 앞). 설정 블록 → 개미별 배열.</summary>
    public readonly record struct Layout(int Start, int Count, int K, int KeyBytes)
    {
        public int Period => Start;
        public int Distance => Start + 4;
        public int Angle => Start + 8;
        public int Sigma => Start + 12;
        public int KeyMode => Start + 16;
        public int KeyCount => Start + 17;
        public int FirstKey => Start + 21;
        public int Arrays => Start + 21 + KeyBytes;
        public int Timer => Arrays;
        public int SenseTick => Timer + 4 * Count;
        public int SenseHeading => SenseTick + 8 * Count;
        public int Flags => SenseHeading + 4 * Count;
        public int Values => Flags + Count;
        public int HomeX => Values + 12 * K * Count;
        public int HomeY => HomeX + 4 * Count;
        public int HomeFlags => HomeY + 4 * Count;
        public int NavRng => HomeFlags + Count;
        public int ExitX => NavRng + 8 * Count;
        public int ExitY => ExitX + 4 * Count;

        public static Layout Of(byte[] bytes, SimulationSession session)
        {
            int k = session.Sensing.TypeCount, n = session.Ants.Count;
            int keyBytes = session.Sensing.Keys.Sum(key => 4 + Encoding.UTF8.GetByteCount(key));
            int block = 21 + keyBytes + n * (ExperimentSerializer.StageBBytesPerAnt + 12 * k);
            return new Layout(bytes.Length - 8 - block, n, k, keyBytes);
        }
    }

    // ---------- 19·21. v3 왕복과 이어 실행 ----------

    [Fact]
    public void V3_SaveLoadContinue_EqualsUninterruptedRun_IncludingSensingSchedule()
    {
        var straight = NewSession(120);
        var session = NewSession(120);
        // 감지 주기 중간(37틱)에 저장한다.
        straight.Step(37);
        session.Step(37);
        var bytes = Save(session);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)));
        var loaded = Load(bytes);
        Assert.Equal(session.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Equal(session.Ants.ComputeStageBHash(), loaded.Ants.ComputeStageBHash());
        for (int t = 0; t < 40; t++)
        {
            straight.Step();
            loaded.Step();
            Assert.Equal(straight.Ants.ComputeStageBHash(), loaded.Ants.ComputeStageBHash()); // 감지 틱·타이머·값이 틱마다 같다
            Assert.Equal(straight.Ants.ComputeHash(), loaded.Ants.ComputeHash());              // 이동 상태·이동 난수
        }
        Assert.Equal(straight.ComputeStateHash(), loaded.ComputeStateHash());
        // 다시 저장 → 불러오기도 같다.
        var again = Load(Save(loaded));
        Assert.Equal(loaded.ComputeStateHash(), again.ComputeStateHash());
    }

    [Fact]
    public void V3_RoundTrips_SettingsKeysAndKeyMode()
    {
        var scent = new ScentSettings
        {
            Types = new[]
            {
                new ScentTypeSettings("larva", "유충", QuantityKind.Larva),
                new ScentTypeSettings("trail", "길", null),
                new ScentTypeSettings("food", "음식", QuantityKind.Food),
            },
        };
        var explicitKeys = Ants with
        {
            SensedScentKeys = new[] { "trail", "food" }, SensorDistance = 2f, SensorAngleDegrees = 30f, SensePeriodSeconds = 0.5f, PathErrorSigma = 0.2f,
        };
        var a = NewSession(20, explicitKeys, scent);
        a.Step(9);
        var loaded = Load(Save(a));
        Assert.Equal(new[] { "trail", "food" }, loaded.AntSettings.SensedScentKeys);
        Assert.Equal(new[] { "trail", "food" }, loaded.Sensing.Keys);
        Assert.Equal(new[] { 1, 2 }, loaded.Sensing.TypeIndices);
        Assert.Equal((2f, 30f, 0.5f, 0.2f), (loaded.AntSettings.SensorDistance, loaded.AntSettings.SensorAngleDegrees,
            loaded.AntSettings.SensePeriodSeconds, loaded.AntSettings.PathErrorSigma));
        Assert.Equal(a.ComputeStateHash(), loaded.ComputeStateHash());

        // 기본 규칙(null)은 불러온 뒤에도 기본 규칙이다(명시 목록으로 바뀌지 않는다).
        var b = NewSession(20, Ants, scent);
        var loadedDefault = Load(Save(b));
        Assert.Null(loadedDefault.AntSettings.SensedScentKeys);
        Assert.Equal(new[] { "food", "larva" }, loadedDefault.Sensing.Keys);
        Assert.Equal(new[] { 2, 0 }, loadedDefault.Sensing.TypeIndices);
    }

    [Fact]
    public void V3_WithZeroAnts_AndKZero_RoundTrips()
    {
        var none = NewSession(0);
        Assert.Equal(none.ComputeStateHash(), Load(Save(none)).ComputeStateHash());
        var scent = new ScentSettings { Types = new[] { new ScentTypeSettings("x", "x", null) } };
        var k0 = NewSession(15, Ants, scent);
        Assert.Equal(0, k0.Sensing.TypeCount);
        k0.Step(5);
        var loaded = Load(Save(k0));
        Assert.Equal(0, loaded.Ants.SensedTypeCount);
        Assert.Equal(k0.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void B2Fields_StayAtInitialValues_InB1()
    {
        var session = NewSession(80);
        session.Step(200);
        for (int i = 0; i < session.Ants.Count; i++)
        {
            Assert.Equal(0f, session.Ants.HomeXArray[i]);
            Assert.Equal(0f, session.Ants.HomeYArray[i]);
            Assert.Equal(0, session.Ants.HomeFlagsArray[i]);
            Assert.Equal(0f, session.Ants.DebugExitXArray[i]);
            Assert.Equal(0f, session.Ants.DebugExitYArray[i]);
            Assert.Equal(AntRandom.CreateState(Ants.Seed, AntSpawner.NavStream(session.Ants.Ids[i])), session.Ants.NavRngArray[i]);
        }
    }

    // ---------- v1/v2 → 결정적 초기값 ----------

    [Fact]
    public void V2Fixture_LoadsWithDeterministicStageBInitialValues()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "experiment-v2-stagea.antexp"));
        var a = Load(bytes);
        var b = Load(bytes);
        Assert.Equal(a.Ants.ComputeStageBHash(), b.Ants.ComputeStageBHash());
        Assert.Null(a.AntSettings.SensedScentKeys);
        Assert.Equal(new[] { "food", "larva" }, a.Sensing.Keys);
        var values = new float[6];
        for (int i = 0; i < a.Ants.Count; i++)
        {
            var info = a.Ants.GetSense(i, values);
            Assert.False(info.HasSensed);
            Assert.Equal(AntSensing.InitialPhase(a.Ants.Ids[i], 0.3f), info.SenseTimer);
            Assert.All(values, v => Assert.Equal(0f, v));
            Assert.Equal(AntRandom.CreateState(a.AntSettings.Seed, AntSpawner.NavStream(a.Ants.Ids[i])), a.Ants.NavRngArray[i]);
            Assert.Equal(0, a.Ants.HomeFlagsArray[i]);
        }
        // 불러온 v2 상태는 v3로 저장되고, 다시 불러오면 같은 상태다.
        var v3 = Save(a);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(v3.AsSpan(8)));
        Assert.Equal(a.ComputeStateHash(), Load(v3).ComputeStateHash());
    }

    [Fact]
    public void V1Fixture_LoadsWithNoAnts_AndDefaultKeys()
    {
        var loaded = Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "experiment-v1.antexp")));
        Assert.Equal(0, loaded.Ants.Count);
        Assert.Equal(loaded.Sensing.TypeCount, loaded.Ants.SensedTypeCount);
        Assert.Equal(loaded.ComputeStateHash(), Load(Save(loaded)).ComputeStateHash());
    }

    // ---------- 20. 손상 파일 ----------

    private void Rejected(byte[] bytes, string contains)
    {
        var e = Assert.Throws<MapFormatException>(() => Load(bytes));
        output.WriteLine($"{contains}: {e.Message}");
        Assert.Contains(contains, e.Message);
    }

    public static IEnumerable<object[]> Corruptions() => new (string Name, string Message, Action<byte[], Layout> Corrupt)[]
    {
        ("거리 2.5", "센서 거리", (b, l) => WriteF(b, l.Distance, 2.5f)),
        ("거리 NaN", "센서 거리", (b, l) => WriteF(b, l.Distance, float.NaN)),
        ("각도 91", "센서 각도", (b, l) => WriteF(b, l.Angle, 91f)),
        ("주기 0", "감지 주기", (b, l) => WriteF(b, l.Period, 0f)),
        ("주기 ∞", "감지 주기", (b, l) => WriteF(b, l.Period, float.PositiveInfinity)),
        ("σ 음수", "σ", (b, l) => WriteF(b, l.Sigma, -1f)),
        ("키 방식 2", "감지 키 방식", (b, l) => b[l.KeyMode] = 2),
        ("K = 9", "감지 냄새 종류 수", (b, l) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(l.KeyCount), 9)),
        ("K 음수", "감지 냄새 종류 수", (b, l) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(l.KeyCount), -1)),
        ("타이머 0", "감지 타이머", (b, l) => WriteF(b, l.Timer, 0f)),
        ("타이머 음수", "감지 타이머", (b, l) => WriteF(b, l.Timer, -0.1f)),
        ("타이머 P 초과", "감지 타이머", (b, l) => WriteF(b, l.Timer, 0.31f)),
        ("타이머 NaN", "감지 타이머", (b, l) => WriteF(b, l.Timer, float.NaN)),
        ("감지 틱 미래", "마지막 감지 틱", (b, l) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(l.SenseTick), 1_000_000)),
        ("감지 틱 0", "마지막 감지 틱", (b, l) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(l.SenseTick), 0)),
        ("감지 틱 -2", "마지막 감지 틱", (b, l) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(l.SenseTick), -2)),
        ("감지 방향 NaN", "감지 방향", (b, l) => WriteF(b, l.SenseHeading, float.NaN)),
        ("차폐 비트 3", "차폐 비트", (b, l) => b[l.Flags] = 0b1000),
        ("감지 값 NaN", "감지 값", (b, l) => WriteF(b, l.Values, float.NaN)),
        ("감지 값 음수", "감지 값", (b, l) => WriteF(b, l.Values, -1f)),
        ("감지 값 1e31", "감지 값", (b, l) => WriteF(b, l.Values, 1e31f)),
        ("귀환 플래그 2", "귀환 기억 플래그", (b, l) => b[l.HomeFlags] = 2),
        ("무효 귀환에 값", "무효인 귀환 기억", (b, l) => WriteF(b, l.HomeX, 1f)),
        ("유효 귀환 상한 초과", "귀환 벡터", (b, l) => { b[l.HomeFlags] = 1; WriteF(b, l.HomeX, 2e7f); WriteF(b, l.ExitX, 1f); WriteF(b, l.ExitY, 1f); }),
        ("유효 귀환 출구 맵 밖", "기준 출구", (b, l) => { b[l.HomeFlags] = 1; WriteF(b, l.ExitX, -1f); }),
        ("Nav 난수 0", "경로 적분 난수", (b, l) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(l.NavRng), 0)),
    }.Select(c => new object[] { c.Name, c.Message, c.Corrupt });

    private static void WriteF(byte[] b, int offset, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(offset), v);

    /// <summary>키 "food"(4바이트)와 "larva"(5바이트)의 순서를 바꾼다(길이가 달라 바이트 수는 같게 유지).</summary>
    private static void SwapKeys(byte[] b, Layout l)
    {
        var swapped = new List<byte>();
        foreach (string key in new[] { "larva", "food" })
        {
            var bytes = Encoding.UTF8.GetBytes(key);
            swapped.AddRange(BitConverter.GetBytes(bytes.Length));
            swapped.AddRange(bytes);
        }
        swapped.ToArray().CopyTo(b, l.FirstKey);
    }

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void CorruptedV3Fields_AreRejected(string name, string message, Action<byte[], Layout> corrupt)
    {
        var session = NewSession(3);
        session.Step(6); // 모두 한 번 이상 감지한 상태(감지 틱 ≥ 1)
        var bytes = Save(session);
        var layout = Layout.Of(bytes, session);
        Assert.Equal(session.ComputeStateHash(), Load(bytes).ComputeStateHash()); // 손대기 전에는 정상
        corrupt(bytes, layout);
        Rechecksum(bytes);
        output.WriteLine(name);
        Rejected(bytes, message);
    }

    /// <summary>
    /// 키 손상: 명시 목록(keyMode 1)에서 냄새 설정에 없는 키, 중복 키. 기본 규칙(keyMode 0)에서 해석 결과와 다른 키 순서.
    /// 같은 길이의 키("keyA", "keyB")를 써서 바이트 수를 유지한 채 바꾼다.
    /// </summary>
    [Fact]
    public void CorruptedKeys_AreRejected()
    {
        var scent = new ScentSettings
        {
            Types = new[] { new ScentTypeSettings("keyA", "A", null), new ScentTypeSettings("keyB", "B", null), new ScentTypeSettings("food", "음식", QuantityKind.Food) },
        };
        var session = NewSession(3, Ants with { SensedScentKeys = new[] { "keyA", "keyB" } }, scent);
        session.Step(6);
        var clean = Save(session);
        var layout = Layout.Of(clean, session);
        Assert.Equal(1, clean[layout.KeyMode]);
        Assert.Equal(session.ComputeStateHash(), Load(clean).ComputeStateHash());

        var missing = clean.ToArray();
        missing[layout.FirstKey + 4 + 3] = (byte)'C'; // "keyA" → "keyC"
        Rechecksum(missing);
        Rejected(missing, "냄새 설정에 없습니다");

        var duplicate = clean.ToArray();
        duplicate[layout.FirstKey + 8 + 4 + 3] = (byte)'A'; // 두 번째 "keyB" → "keyA"
        Rechecksum(duplicate);
        Rejected(duplicate, "중복");

        var defaults = NewSession(3);
        defaults.Step(6);
        var swapped = Save(defaults);
        var defaultLayout = Layout.Of(swapped, defaults);
        Assert.Equal(0, swapped[defaultLayout.KeyMode]);
        SwapKeys(swapped, defaultLayout);
        Rechecksum(swapped);
        Rejected(swapped, "기본 규칙");
    }

    [Fact]
    public void ValueOnABlockedSensor_OrBeforeFirstSense_IsRejected()
    {
        var session = NewSession(3);
        var bytes = Save(session); // 틱 0: 모두 감지 전
        var layout = Layout.Of(bytes, session);
        WriteF(bytes, layout.Values, 0.5f);
        Rechecksum(bytes);
        Rejected(bytes, "감지 전이거나 차폐된 센서");

        session.Step(6);
        bytes = Save(session);
        layout = Layout.Of(bytes, session);
        bytes[layout.Flags] = 0b111;          // 모든 센서 차폐로 표시
        WriteF(bytes, layout.Values + 4, 0.5f); // 종류 0의 정면 값
        Rechecksum(bytes);
        Rejected(bytes, "감지 전이거나 차폐된 센서");
    }

    [Fact]
    public void ChecksumAndTruncation_AreRejected()
    {
        var session = NewSession(5);
        session.Step(4);
        var bytes = Save(session);
        var layout = Layout.Of(bytes, session);
        var flipped = bytes.ToArray();
        flipped[layout.Values + 1] ^= 0x40;
        Assert.Throws<MapFormatException>(() => Load(flipped));
        foreach (int cut in new[] { layout.Start + 3, layout.FirstKey + 2, layout.Values + 5, bytes.Length - 9, bytes.Length - 1 })
            Assert.Throws<MapFormatException>(() => Load(bytes.AsSpan(0, cut).ToArray()));
    }

    /// <summary>단계 B 본문이 짧으면 감지 값 배열을 만들기 전에 거부한다(할당 전 길이 확인).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShortStageBBody_IsRejectedBeforeAllocatingSenseValues(bool seekable)
    {
        var scent = new ScentSettings { Types = Enumerable.Range(0, 8).Select(i => new ScentTypeSettings($"t{i}", $"t{i}", null)).ToArray() };
        var session = NewSession(AntSettings.MaxCount, Ants with { SensedScentKeys = scent.Types.Select(t => t.Key).ToArray() }, scent);
        var bytes = Save(session);
        var layout = Layout.Of(bytes, session);
        var cut = bytes.AsSpan(0, layout.Arrays + 16).ToArray(); // 개미별 배열이 거의 없다
        Stream Open(byte[] data) => seekable ? new MemoryStream(data) : new RecordingStream(data);
        var (allocated, error) = AuditFiles.Measure(() => ExperimentSerializer.Load(Open(cut)));
        output.WriteLine($"seek={seekable}: 할당 {allocated}바이트, {error?.Message}");
        Assert.IsType<MapFormatException>(error);
        Assert.Contains("개미 감각 상태", error!.Message);
        long senseValues = 12L * 8 * AntSettings.MaxCount; // 9.6MB
        var (fullAllocated, fullError) = AuditFiles.Measure(() => ExperimentSerializer.Load(Open(bytes))); // 같은 스트림 방식의 정상 파일
        Assert.Null(fullError);
        output.WriteLine($"정상 파일 할당 {fullAllocated}바이트");
        Assert.True(allocated <= fullAllocated - senseValues, $"감지 값 배열 선할당 의심: {allocated} (정상 {fullAllocated})");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    [InlineData(3)]
    public void ConsecutiveV3Records_KeepRecordBoundaries(int maxPerRead)
    {
        var a = NewSession(7);
        a.Step(3);
        var b = NewSession(12);
        b.Step(9);
        byte[] ra = Save(a), rb = Save(b);
        var data = ra.Concat(rb).ToArray();
        using Stream stream = maxPerRead == 0 ? new MemoryStream(data) : new RecordingStream(data, maxPerRead: maxPerRead);
        Assert.Equal(a.ComputeStateHash(), ExperimentSerializer.Load(stream).ComputeStateHash());
        if (stream is RecordingStream r1) Assert.Equal(ra.Length, r1.Consumed);
        else Assert.Equal(ra.Length, stream.Position);
        Assert.Equal(b.ComputeStateHash(), ExperimentSerializer.Load(stream).ComputeStateHash());
    }

    [Fact]
    public void SaveRefusesInvalidStageBState_AndKeepsExistingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"antsim-b1-{Guid.NewGuid():N}.antexp");
        try
        {
            var good = NewSession(4);
            ExperimentSerializer.Save(good, path);
            var before = File.ReadAllBytes(path);
            var bad = NewSession(4);
            bad.Ants.SenseTimerArray[0] = float.NaN;
            Assert.Throws<InvalidOperationException>(() => ExperimentSerializer.Save(bad, path));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------- 23. 선택 세대 보호 ----------

    [Fact]
    public async Task HostSelection_IsGenerationProtected()
    {
        using var host = new SimulationHost(NewSession(30));
        await host.StepAsync();
        var (rc, id) = await host.RunExclusiveAsync(s => (s.ResetCount, s.Ants.Ids[3]));
        host.SelectAnt(id, rc);
        var snapshot = await StageAResetIntegrationTests.TakeSnapshot(host, s => s.SelectedAnt.Requested && s.SelectedAnt.Id == id, "선택 상세");
        Assert.True(snapshot.SelectedAnt.HasValue);
        Assert.Equal(rc, snapshot.SelectedAnt.ResetCount);
        var expected = await host.RunExclusiveAsync(s =>
        {
            var values = new float[6];
            var info = s.Ants.GetSense(s.Ants.IndexOfId(id), values);
            return (info, values, s.Tick);
        });
        Assert.Equal(expected.Tick, snapshot.Tick);
        Assert.Equal(expected.info.SenseTimer, snapshot.SelectedAnt.SenseTimer);
        Assert.Equal(expected.info.SenseTick, snapshot.SelectedAnt.SenseTick);
        Assert.Equal(expected.values, snapshot.SelectedAnt.Values.Take(6));
        host.Release(snapshot);

        var reset = await host.ResetAsync();
        Assert.Equal(rc + 1, reset.ResetCount);
        // 초기화하면 호스트의 선택도 지워진다.
        snapshot = await StageAResetIntegrationTests.TakeSnapshot(host, s => s.ResetCount == reset.ResetCount, "초기화 뒤 스냅샷");
        Assert.False(snapshot.SelectedAnt.Requested);
        host.Release(snapshot);
        // 초기화 뒤 늦게 도착한 이전 세대 요청: 같은 id의 새 개미 상세를 채우지 않는다.
        host.SelectAnt(id, rc);
        snapshot = await StageAResetIntegrationTests.TakeSnapshot(host, s => s.SelectedAnt.Requested && s.SelectedAnt.ResetCount == rc, "이전 세대 요청");
        Assert.False(snapshot.SelectedAnt.HasValue);
        host.Release(snapshot);
        host.SelectAnt(id, reset.ResetCount);
        snapshot = await StageAResetIntegrationTests.TakeSnapshot(host, s => s.SelectedAnt.HasValue && s.SelectedAnt.ResetCount == reset.ResetCount, "새 세대 요청");
        Assert.Equal(id, snapshot.SelectedAnt.Id);
        host.Release(snapshot);
        host.ClearAntSelection();
        snapshot = await StageAResetIntegrationTests.TakeSnapshot(host, s => !s.SelectedAnt.Requested, "선택 해제");
        Assert.False(snapshot.SelectedAnt.HasValue);
        host.Release(snapshot);
    }
}
