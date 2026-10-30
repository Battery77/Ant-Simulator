using System.Buffers.Binary;
using System.Text;
using AntSim.Core.Rendering;
using AntSim.Core.Scent;
using AntSim.Core.Serialization;
using AntSim.Core.Simulation;
using AntSim.Core.Util;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

/// <summary>테스트용 스트림·파일 조립 도우미(PR #2 감사 재현).</summary>
internal static class AuditFiles
{
    /// <summary>읽기만 되고 탐색(seek)이 안 되는 스트림(네트워크·파이프 흉내).</summary>
    internal sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static byte[] MapBytes(World world)
    {
        using var stream = new MemoryStream();
        MapSerializer.Save(world, stream);
        return stream.ToArray();
    }

    /// <summary>
    /// 실험 파일(v1 형식)을 직접 조립한다. 체크섬은 마지막에 붙인다(checksum=false면 생략).
    /// <paramref name="initialLength"/>로 첫 월드 길이 필드를 임의 값으로 바꿀 수 있다.
    /// </summary>
    internal static byte[] Experiment(int typeCount, byte[]? initialMap, byte[]? currentMap, float[]? scent,
        int? initialLength = null, bool checksum = true, string? key = null)
    {
        using var ms = new MemoryStream();
        void W(ReadOnlySpan<byte> b) => ms.Write(b);
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); W(b); }
        void I64(long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); W(b); }
        void F64(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, v); W(b); }
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); I32(b.Length); W(b); }

        W("ANTEXP\0\0"u8);
        W(new byte[] { 1, 0, 0, 0 }); // version 1, flags 0
        I64(0); F64(0);
        F64(0.1); I32(typeCount);
        for (int t = 0; t < typeCount; t++)
        {
            Str(key ?? $"t{t}"); Str($"type {t}");
            W(new byte[] { 0, 0 });
            F64(0.6); F64(0.6); F64(0.025); F64(1.0);
        }
        if (initialMap is not null)
        {
            I32(initialLength ?? initialMap.Length);
            W(initialMap);
        }
        else if (initialLength is { } length)
        {
            I32(length);
        }
        if (currentMap is not null) { I32(currentMap.Length); W(currentMap); }
        if (scent is not null)
        {
            var b = new byte[4];
            foreach (float v in scent) { BinaryPrimitives.WriteSingleLittleEndian(b, v); W(b); }
        }
        if (checksum)
        {
            var fnv = Fnv1a64.Create();
            fnv.Add(ms.GetBuffer().AsSpan(0, (int)ms.Length));
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, fnv.Value);
            W(b);
        }
        return ms.ToArray();
    }

    /// <summary>현재 스레드에서 <paramref name="action"/>이 할당한 관리 메모리(바이트)와 던진 예외.</summary>
    internal static (long Allocated, Exception? Error) Measure(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Exception? error = null;
        try
        {
            action();
        }
        catch (Exception e)
        {
            error = e;
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before, error);
    }

    internal const long MiB = 1024 * 1024;
}

// ======================= F01 / E01: 할당 이전 입력 검증 =======================

public class AuditF01ExperimentAllocationTests
{
    private static readonly byte[] SmallMap = AuditFiles.MapBytes(TestWorlds.Create(16, 16));

    private static void AssertRejectedCheaply(byte[] bytes, long limitBytes)
    {
        foreach (bool seekable in new[] { true, false })
        {
            Stream stream = new MemoryStream(bytes, writable: false);
            if (!seekable) stream = new AuditFiles.NonSeekableStream(stream);
            var (allocated, error) = AuditFiles.Measure(() => ExperimentSerializer.Load(stream));
            Assert.IsType<MapFormatException>(error);
            Assert.True(allocated < limitBytes,
                $"{(seekable ? "seek 가능" : "seek 불가")} 스트림: 거부 전에 {allocated / AuditFiles.MiB} MiB 할당 (한도 {limitBytes / AuditFiles.MiB} MiB)");
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(200_000_000)]            // 수정 전 코드에서도 안전하게 실행 가능한 크기
    public void DeclaredWorldLength_LargerThanFile_IsRejectedBeforeAllocation(int declared)
    {
        // 정상 헤더·설정 뒤에 첫 월드 길이만 적고 파일을 끝낸다.
        var bytes = AuditFiles.Experiment(2, null, null, null, initialLength: declared, checksum: false);
        AssertRejectedCheaply(bytes, 16 * AuditFiles.MiB);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1_500_000_000)]
    public void Large_DeclaredWorldLength_IsRejectedBeforeAllocation(int declared)
    {
        var bytes = AuditFiles.Experiment(2, null, null, null, initialLength: declared, checksum: false);
        AssertRejectedCheaply(bytes, 16 * AuditFiles.MiB);
    }

    [Fact]
    public void DeclaredWorldLength_LongerThanEmbeddedMap_IsRejected()
    {
        // 길이 필드가 실제 맵보다 길면(뒤에 다른 데이터가 섞임) 거부한다.
        var bytes = AuditFiles.Experiment(2, SmallMap, SmallMap, new float[2 * 2 * 256], initialLength: SmallMap.Length + 4);
        Assert.Throws<MapFormatException>(() => ExperimentSerializer.Load(new MemoryStream(bytes)));
    }

    [Fact]
    public void TruncatedScent_WithManyTypes_IsRejectedBeforeScentAllocation()
    {
        // 1024² 두 월드 + 16종, 농도 본문 없음. 수정 전: 냄새 배열 16×2×2×4MiB = 256MiB를 먼저 할당.
        var map = AuditFiles.MapBytes(TestWorlds.Create(1024, 1024));
        var bytes = AuditFiles.Experiment(16, map, map, null, checksum: false);
        AssertRejectedCheaply(bytes, 100 * AuditFiles.MiB);
    }

    [Fact]
    public void Large_TruncatedScent_With256Types_IsRejectedBeforeScentAllocation()
    {
        // 수정 전에는 냄새 배열만 4GiB를 먼저 요구하던 사례.
        var map = AuditFiles.MapBytes(TestWorlds.Create(1024, 1024));
        var bytes = AuditFiles.Experiment(256, map, map, null, checksum: false);
        AssertRejectedCheaply(bytes, 100 * AuditFiles.MiB);
    }

    [Fact]
    public void ValidExperiment_StillLoads_FromSeekableAndNonSeekableStreams()
    {
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
        session.Step(9);
        using var ms = new MemoryStream();
        ExperimentSerializer.Save(session, ms);
        var bytes = ms.ToArray();
        Assert.Equal(session.ComputeStateHash(), ExperimentSerializer.Load(new MemoryStream(bytes)).ComputeStateHash());
        Assert.Equal(session.ComputeStateHash(),
            ExperimentSerializer.Load(new AuditFiles.NonSeekableStream(new MemoryStream(bytes))).ComputeStateHash());
    }
}

public class AuditE01MapAllocationTests
{
    /// <summary>정상 맵 헤더(지정 크기)만 있고 본문이 없는 .antmap.</summary>
    private static byte[] HeaderOnlyMap(int width, int height)
    {
        var full = AuditFiles.MapBytes(TestWorlds.Create(8, 8));
        var header = full[..25].ToArray(); // magic 8 + ver 2 + flags 2 + w 4 + h 4 + maxQ 4 + layers 1
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), height);
        return header;
    }

    private static void AssertRejectedCheaply(byte[] bytes, long limitBytes)
    {
        foreach (bool seekable in new[] { true, false })
        {
            Stream stream = new MemoryStream(bytes, writable: false);
            if (!seekable) stream = new AuditFiles.NonSeekableStream(stream);
            var (allocated, error) = AuditFiles.Measure(() => MapSerializer.Load(stream));
            Assert.IsType<MapFormatException>(error);
            Assert.True(allocated < limitBytes, $"{(seekable ? "seek 가능" : "seek 불가")}: 거부 전에 {allocated / AuditFiles.MiB} MiB 할당");
        }
    }

    [Fact]
    public void HeaderOnly4096Map_IsRejectedBeforeWorldAllocation()
    {
        // 수정 전 코드에서도 안전한 크기(월드 128MiB + 임시 64MiB).
        AssertRejectedCheaply(HeaderOnlyMap(4096, 4096), 16 * AuditFiles.MiB);
    }

    [Fact]
    public void Large_HeaderOnly16384Map_IsRejectedBeforeWorldAllocation()
    {
        AssertRejectedCheaply(HeaderOnlyMap(16384, 16384), 16 * AuditFiles.MiB);
    }

    [Fact]
    public void TruncatedPassageList_IsRejected()
    {
        var world = TestWorlds.Create(16, 16);
        world.TrySetPassage(3, 3, true);
        var bytes = AuditFiles.MapBytes(world);
        // 통로 수를 크게 바꾼다(본문은 그대로) → 남은 길이로 즉시 거부.
        int passageCountOffset = 25 + 2 * 4 * 256;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(passageCountOffset), 200);
        Assert.Throws<MapFormatException>(() => MapSerializer.Load(new MemoryStream(bytes)));
    }

    [Fact]
    public void ValidMap_StillLoads_FromNonSeekableStream()
    {
        var world = TestWorlds.Create(100, 70);
        TestWorlds.Paint(world);
        var bytes = AuditFiles.MapBytes(world);
        Assert.Equal(world.ComputeContentHash(),
            MapSerializer.Load(new AuditFiles.NonSeekableStream(new MemoryStream(bytes))).ComputeContentHash());
    }
}

// ======================= F02: 종료·실패 시 명령 종결 =======================

public class AuditF02HostLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static SimulationHost NewHost() =>
        new(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));

    /// <summary>계산 스레드를 "publish" 지점에서 붙잡는 동기화 장치.</summary>
    private sealed class PublishGate : IDisposable
    {
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public Exception? ThrowOnRelease;
        private int _armed = 1;

        public void Hook(string point)
        {
            if (point != "publish" || Interlocked.Exchange(ref _armed, 0) == 0) return;
            Entered.Set();
            Release.Wait();
            if (ThrowOnRelease is { } e) throw e;
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
        }
    }

    private static async Task AssertTerminates(Task task, string name)
    {
        var finished = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.True(finished == task, $"{name} Task가 {Timeout.TotalSeconds}초 안에 끝나지 않음(영원히 대기)");
    }

    /// <summary>
    /// 계산 스레드가 스냅샷 게시 중일 때 명령 4종을 큐에 넣고, 그 사이 Dispose를 시작한다.
    /// 게시가 끝나면 모든 Task는 성공·취소·실패 중 하나로 끝나야 한다.
    /// </summary>
    [Fact]
    public async Task CommandsQueuedDuringPublish_TerminateWhenDisposed()
    {
        var host = NewHost();
        using var gate = new PublishGate();
        host.TestHook = gate.Hook;
        host.SelectHeatmap(0, LayerId.Surface);          // 게시 요청 → 계산 스레드가 훅에서 멈춤
        Assert.True(gate.Entered.Wait(Timeout), "계산 스레드가 게시 지점에 도달하지 않음");

        var save = host.RunExclusiveAsync(s => { using var ms = new MemoryStream(); ExperimentSerializer.Save(s, ms); return ms.Length; });
        var step = host.StepAsync();
        var reset = host.ResetAsync();
        var settings = host.SetSettingsAsync(ScentSettings.Default.WithTick(0.05));

        var dispose = Task.Run(host.Dispose);
        Assert.True(SpinWait.SpinUntil(() => host.DisposeRequested, Timeout), "Dispose 요청이 반영되지 않음");
        gate.Release.Set();                               // 게시를 끝내게 한다
        await AssertTerminates(dispose, "Dispose");

        await AssertTerminates(save, "Save");
        await AssertTerminates(step, "Step");
        await AssertTerminates(reset, "Reset");
        await AssertTerminates(settings, "설정 변경");
        foreach (var task in new Task[] { save, step, reset, settings })
            Assert.True(task.IsCanceled || task.IsFaulted || task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WorkerFailure_FailsQueuedCommands_AndRejectsNewOnes()
    {
        var host = NewHost();
        using var gate = new PublishGate { ThrowOnRelease = new InvalidOperationException("계산 스레드 시험 예외") };
        host.TestHook = gate.Hook;
        host.SelectHeatmap(0, LayerId.Surface);
        Assert.True(gate.Entered.Wait(Timeout));

        var step = host.StepAsync();
        var save = host.RunExclusiveAsync(s => s.Tick);
        gate.Release.Set();                               // 게시 중 예외 → 계산 스레드 실패

        await AssertTerminates(step, "Step");
        await AssertTerminates(save, "Save");
        Assert.True(step.IsFaulted || step.IsCanceled);
        Assert.NotNull(host.Failure);

        // 실패한 호스트에 새 요청: 무기한 대기하지 않고 즉시 실패해야 한다.
        Task late;
        try
        {
            late = host.StepAsync();
        }
        catch (Exception e)
        {
            late = Task.FromException(e);
        }
        await AssertTerminates(late, "실패 후 새 요청");
        Assert.True(late.IsFaulted || late.IsCanceled);
        host.Dispose();
    }

    [Fact]
    public async Task CommandsAfterDispose_AreRejectedImmediately()
    {
        var host = NewHost();
        host.Dispose();
        Task late;
        try
        {
            late = host.RunExclusiveAsync(s => s.Tick);
        }
        catch (ObjectDisposedException e)
        {
            late = Task.FromException(e);
        }
        await AssertTerminates(late, "Dispose 후 요청");
        Assert.True(late.IsFaulted || late.IsCanceled);
        host.Dispose(); // 두 번 호출해도 안전
    }

    [Fact]
    public async Task PlayingHost_DisposeDuringTick_TerminatesQueuedCommands()
    {
        var host = NewHost();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        int armed = 1;
        host.TestHook = point =>
        {
            if (point != "tick" || Interlocked.Exchange(ref armed, 0) == 0) return;
            entered.Set();
            release.Wait();
        };
        host.Speed = 50;
        host.Play();
        Assert.True(entered.Wait(Timeout), "재생 틱에 도달하지 않음");
        var reset = host.ResetAsync();
        var step = host.StepAsync();
        var dispose = Task.Run(host.Dispose);
        Assert.True(SpinWait.SpinUntil(() => host.DisposeRequested, Timeout));
        release.Set();
        await AssertTerminates(dispose, "Dispose");
        await AssertTerminates(reset, "Reset");
        await AssertTerminates(step, "Step");
    }
}

// ======================= F03: 수치 범위 =======================

public class AuditF03NumericRangeTests
{
    [Fact]
    public void HugeEmission_IsRejectedByValidation()
    {
        var settings = new ScentSettings
        {
            TickSeconds = 0.1,
            Types = new[] { new ScentTypeSettings("food", "음식", QuantityKind.Food, 0, 0, 0, 1e40) },
        };
        Assert.NotEmpty(settings.Validate());
    }

    [Fact]
    public void MaxSupportedEmission_StaysFinite()
    {
        var settings = new ScentSettings
        {
            TickSeconds = ScentSettings.MaxTickSeconds / 30, // CFL 안에서 큰 틱
            Types = new[] { new ScentTypeSettings("food", "음식", QuantityKind.Food, 0.6, 0.6, 0, 1e6) },
        };
        Assert.Empty(settings.Validate());
        var world = TestWorlds.Create(16, 16, rules: new WorldRules { MaxQuantityPerTile = 255 });
        world.Surface.TrySetState(8, 8, new TileState(Terrain.Dirt, SolidBlock.None, 255, 0));
        var system = new ScentSystem(world);
        var state = new ScentState(world.Config, 1);
        for (int i = 0; i < 200; i++) system.Tick(state, settings);
        foreach (var layer in new[] { LayerId.Surface, LayerId.Nest })
            Assert.All(state.Field(0, layer).ToArray(), v => Assert.True(float.IsFinite(v) && v >= 0));
    }

    [Fact]
    public void ZeroDiffusion_KeepsHugeFiniteConcentrationUnchanged()
    {
        // 확산을 꺼도 네 이웃 차이의 합(−4e38)이 넘쳐 c + 0×(−∞) = NaN이 되던 반례.
        var settings = new ScentSettings { Types = new[] { new ScentTypeSettings("x", "x", null, 0, 0, 0, 0) } };
        var world = TestWorlds.Create(5, 5);
        var system = new ScentSystem(world);
        var state = new ScentState(world.Config, 1);
        state.MutableField(0, LayerId.Surface)[world.Config.ToLinearIndex(2, 2)] = 1e38f;
        system.Tick(state, settings);
        Assert.Equal(1e38f, state.Get(0, LayerId.Surface, 2, 2));
        Assert.Equal(0f, state.Get(0, LayerId.Surface, 1, 2));
    }

    [Fact]
    public void ConcentrationAboveSupportedRange_IsRejectedOnLoad()
    {
        var map = AuditFiles.MapBytes(TestWorlds.Create(4, 4));
        var scent = new float[1 * 2 * 16];
        scent[5] = 1e38f;  // 유한하지만 지원 범위(ScentState.MaxConcentration) 밖
        var bytes = AuditFiles.Experiment(1, map, map, scent);
        Assert.Throws<MapFormatException>(() => ExperimentSerializer.Load(new MemoryStream(bytes)));
    }

    [Fact]
    public void Save_RefusesNonFiniteOrOutOfRangeState_AndKeepsExistingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.antexp");
        try
        {
            var good = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
            good.Step(3);
            ExperimentSerializer.Save(good, path);
            var original = File.ReadAllBytes(path);

            foreach (float bad in new[] { float.NaN, float.PositiveInfinity, -1f, 1e38f })
            {
                var broken = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
                broken.Scent.MutableField(0, LayerId.Surface)[7] = bad;
                Assert.ThrowsAny<Exception>(() => ExperimentSerializer.Save(broken, path));
                Assert.Equal(original, File.ReadAllBytes(path)); // 기존 정상 파일 유지
                Assert.False(File.Exists(path + ".tmp"));
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}

// ======================= F04: 작은 양수의 로그 정규화 =======================

public class AuditF04SmallLogTests
{
    public static IEnumerable<object[]> Maxima => new[]
    {
        new object[] { 1e-4f }, new object[] { 1e-8f }, new object[] { 1e-30f },
        new object[] { float.Epsilon * 8 }, new object[] { float.Epsilon },
    };

    [Theory]
    [MemberData(nameof(Maxima))]
    public void Normalize_IsFiniteAndOne_AtMax(float max)
    {
        float t = HeatmapRasterizer.Normalize(max, max, HeatmapScale.Log);
        Assert.False(float.IsNaN(t));
        Assert.Equal(1f, t);
        Assert.Equal(0f, HeatmapRasterizer.Normalize(0, max, HeatmapScale.Log));
        float half = HeatmapRasterizer.Normalize(max / 2, max, HeatmapScale.Log);
        Assert.InRange(half, 0f, 1f);
    }

    [Theory]
    [MemberData(nameof(Maxima))]
    public void Quantize_ScalarAndBulk_GiveValidLevels(float max)
    {
        var q = new HeatmapQuantizer(max, HeatmapScale.Log);
        Assert.Equal(255, q.Quantize(max));
        Assert.Equal(0, q.Quantize(0));
        float half = max / 2; // max = float.Epsilon이면 0으로 반올림된다 → 투명이 맞다
        if (half > 0) Assert.InRange(q.Quantize(half), 1, 255);
        else Assert.Equal(0, q.Quantize(half));
        Assert.InRange(q.Quantize(float.Epsilon), 1, 255);

        var values = new[] { 0f, max, max / 2, float.Epsilon, max * 4 };
        var bulk = new byte[values.Length];
        q.QuantizeTo(values, bulk);
        Assert.Equal(new byte[] { 0, 255, (byte)q.Quantize(max / 2), (byte)q.Quantize(float.Epsilon), 255 }, bulk);
    }

    [Theory]
    [MemberData(nameof(Maxima))]
    public void SingleAndCompositeLogHeatmaps_ShowMaxAsBrightest(float max)
    {
        var field = new[] { 0f, max };
        var rgba = new byte[8];
        HeatmapRasterizer.Rasterize(field, max, HeatmapScale.Log, rgba);
        Assert.Equal(0u, BitConverter.ToUInt32(rgba, 0));
        Assert.Equal(HeatmapRasterizer.LutColor(1f), BitConverter.ToUInt32(rgba, 4));

        var ramp = CompositeHeatmapPalette.CreateDefault().Get("food");
        new CompositeHeatmapRasterizer().Rasterize(new[] { new CompositeChannel(field, max, HeatmapScale.Log, ramp) }, rgba);
        Assert.Equal(0u, BitConverter.ToUInt32(rgba, 0));
        Assert.Equal(ramp.ColorAt(255), BitConverter.ToUInt32(rgba, 4));
    }

    [Fact]
    public void UiScenario_HighDecay_ProducesDrawableMaximum()
    {
        // 감사 재현: D=Dp=0, k=100, dt=0.1로 한 번 발생(0.1) 후 e=0으로 두 틱 → 최대 약 2.06e-10.
        var world = TestWorlds.Create(8, 8);
        world.Surface.TrySetState(3, 3, new TileState(Terrain.Dirt, SolidBlock.None, 1, 0));
        var emit = new ScentSettings { Types = new[] { new ScentTypeSettings("food", "음식", QuantityKind.Food, 0, 0, 100, 1) } };
        var quiet = emit.WithType(0, emit.Types[0] with { Emission = 0 });
        var system = new ScentSystem(world);
        var state = new ScentState(world.Config, 1);
        system.Tick(state, emit);
        system.Tick(state, quiet);
        system.Tick(state, quiet);
        float max = HeatmapRasterizer.ComputeMax(state.Field(0, LayerId.Surface));
        Assert.InRange(max, 1e-10f, 1e-9f);
        Assert.Equal(255, new HeatmapQuantizer(max, HeatmapScale.Log).Quantize(max));
    }
}

// ======================= F06: 저장·불러오기 계약 =======================

public class AuditF06SaveLoadContractTests
{
    private static ScentSettings WithTypes(int count, Func<int, string>? key = null, Func<int, string>? name = null) => new()
    {
        Types = Enumerable.Range(0, count)
            .Select(i => new ScentTypeSettings(key?.Invoke(i) ?? $"t{i}", name?.Invoke(i) ?? $"종류 {i}", null))
            .ToArray(),
    };

    private static void AssertRoundTrips(ScentSettings settings)
    {
        Assert.Empty(settings.Validate());
        var session = new SimulationSession(TestWorlds.Create(8, 8), settings);
        session.Step(2);
        using var ms = new MemoryStream();
        ExperimentSerializer.Save(session, ms);
        ms.Position = 0;
        var loaded = ExperimentSerializer.Load(ms);
        Assert.Equal(session.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Equal(settings.Types.Select(t => t.Key), loaded.Settings.Types.Select(t => t.Key));
        Assert.Equal(settings.Types.Select(t => t.DisplayName), loaded.Settings.Types.Select(t => t.DisplayName));
    }

    [Fact]
    public void TypeCount256_RoundTrips()
    {
        AssertRoundTrips(WithTypes(256));
    }

    [Fact]
    public void TypeCount257_IsRejectedByTheModel_SoItCanNeverBeSavedUnloadable()
    {
        var settings = WithTypes(257);
        Assert.NotEmpty(settings.Validate());
        Assert.Throws<ArgumentException>(() => new SimulationSession(TestWorlds.Create(8, 8), settings));
    }

    [Theory]
    [InlineData(65_536, true)]
    [InlineData(65_537, false)]
    public void AsciiStringLength_LimitMatchesLoader(int bytes, bool allowed)
    {
        var key = new string('k', bytes);
        var name = new string('n', bytes);
        var byKey = WithTypes(1, key: _ => key);
        var byName = WithTypes(1, name: _ => name);
        if (allowed)
        {
            AssertRoundTrips(byKey);
            AssertRoundTrips(byName);
        }
        else
        {
            Assert.NotEmpty(byKey.Validate());
            Assert.NotEmpty(byName.Validate());
        }
    }

    [Theory]
    [InlineData(21_845, true)]   // '가' = UTF-8 3바이트 → 65,535바이트
    [InlineData(21_846, false)]  // 65,538바이트
    public void KoreanStringLength_IsCountedInUtf8Bytes(int chars, bool allowed)
    {
        var settings = WithTypes(1, name: _ => new string('가', chars));
        if (allowed) AssertRoundTrips(settings);
        else Assert.NotEmpty(settings.Validate());
    }
}

// ======================= F07: 예외 이후 공개 Tick =======================

public class AuditF07TickSyncTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StepThenThrow_PublicTickMatchesSessionImmediately()
    {
        using var host = new SimulationHost(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));
        var failing = host.RunExclusiveAsync<bool>(s =>
        {
            s.Step();
            throw new InvalidOperationException("callback 실패");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(Timeout));
        Assert.Equal(1, host.Tick); // 다른 명령을 보내지 않고 바로 확인
    }

    [Fact]
    public async Task ResetThenThrow_PublicTickMatchesSessionImmediately()
    {
        using var host = new SimulationHost(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));
        for (int i = 0; i < 3; i++) await host.StepAsync().WaitAsync(Timeout);
        Assert.Equal(3, host.Tick);
        var failing = host.RunExclusiveAsync<bool>(s =>
        {
            s.Reset();
            throw new InvalidOperationException("callback 실패");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(Timeout));
        Assert.Equal(0, host.Tick);
    }
}

// ======================= F05: Reset 후 계산 월드·표시 월드 =======================

public class AuditF05ResetWorldTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>시작 월드와 현재 월드의 지형·고체·수량·통로가 다른 정상 실험 파일.</summary>
    internal static byte[] DivergedExperiment(out ulong initialHash, out ulong currentHash)
    {
        var session = new SimulationSession(WorldPresets.StandardNest(new WorldConfig(40, 30, 16)), ScentSettings.Default);
        var editor = new Editing.MapEditor(session.World); // 현재 월드만 바꾼다(시작 월드는 그대로)
        editor.Apply(Editing.EditOp.SetTerrain(LayerId.Surface, 5, 5, Terrain.Water));
        editor.Apply(Editing.EditOp.PlaceSolid(LayerId.Surface, 6, 5, SolidBlock.Rock));
        editor.Apply(Editing.EditOp.SetQuantity(LayerId.Surface, 20, 10, QuantityKind.Food, 9));
        editor.Apply(Editing.EditOp.AddPassage(30, 20));
        session.Step(4);
        initialHash = session.InitialWorld.ComputeContentHash();
        currentHash = session.World.ComputeContentHash();
        Assert.NotEqual(initialHash, currentHash);
        using var ms = new MemoryStream();
        ExperimentSerializer.Save(session, ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task LoadResetStep_DisplayWorldMatchesComputedWorld_AndUsesInitialSources()
    {
        var bytes = DivergedExperiment(out ulong initialHash, out ulong currentHash);
        var loaded = ExperimentSerializer.Load(new MemoryStream(bytes));
        Assert.Equal(currentHash, loaded.World.ComputeContentHash());
        using var host = new SimulationHost(loaded);

        var reset = await host.ResetAsync().WaitAsync(Timeout);
        Assert.Equal(0, reset.Tick);
        Assert.Equal(1, reset.ResetCount);
        Assert.Equal(initialHash, reset.World.ComputeContentHash());                       // 표시용 복제본
        Assert.Equal(initialHash, await host.RunExclusiveAsync(s => s.World.ComputeContentHash()).WaitAsync(Timeout));
        Assert.NotSame(reset.World, await host.RunExclusiveAsync(s => s.World).WaitAsync(Timeout)); // 화면 소유 복제본

        await host.StepAsync().WaitAsync(Timeout);
        var (foodAtCurrentOnly, passageCurrentOnly) = await host.RunExclusiveAsync(s =>
            (s.Scent.Get(0, LayerId.Surface, 20, 10), s.World.Passages.Contains(30, 20))).WaitAsync(Timeout);
        Assert.Equal(0f, foodAtCurrentOnly);   // 현재 월드에만 있던 먹이 발생원은 초기화 뒤 없음
        Assert.False(passageCurrentOnly);      // 현재 월드에만 있던 통로도 없음
    }

    [Fact]
    public async Task SnapshotsCarryResetGeneration()
    {
        using var host = new SimulationHost(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));
        await host.StepAsync().WaitAsync(Timeout);
        var reset = await host.ResetAsync().WaitAsync(Timeout);
        var until = DateTime.UtcNow + Timeout;
        ScentSnapshot? fresh = null;
        while (fresh is null && DateTime.UtcNow < until)
        {
            if (host.TryTakeSnapshot(out var s))
            {
                if (s.ResetCount == reset.ResetCount) fresh = s;
                else
                {
                    Assert.True(s.ResetCount < reset.ResetCount); // 이전 세대는 화면에서 버릴 대상
                    host.Release(s);
                }
            }
            await Task.Delay(5);
        }
        Assert.NotNull(fresh);
        Assert.Equal(0, fresh!.Tick);
        host.Release(fresh);
    }
}

public class AuditMiscTests
{
    [Fact]
    public void MapEncodedLength_MatchesSavedBytes()
    {
        var world = TestWorlds.Create(70, 50, 16);
        TestWorlds.Paint(world);
        Assert.Equal(AuditFiles.MapBytes(world).Length, MapSerializer.EncodedLength(world));
    }

    [Fact]
    public void ControlCallsAfterDispose_DoNotThrow()
    {
        var host = new SimulationHost(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));
        host.Dispose();
        host.Play();
        host.Pause();
        host.SelectHeatmap(0, LayerId.Surface);
        host.TryTakeSnapshot(out _);
    }
}
