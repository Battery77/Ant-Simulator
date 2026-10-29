using System.Buffers.Binary;
using AntSim.Core.Scent;
using AntSim.Core.Serialization;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;
using Xunit.Abstractions;

namespace AntSim.Core.Tests;

// PR #2 독립 재검증(da05632)에서 재현된 결함 F02(종료/실행권 경합), F03(농도 상한 경계), N01(non-seek 과잉 읽기)의 회귀 테스트.
// 각 테스트는 올바른 계약을 기대한다(결함 관찰값을 확인하는 재현 하네스와 반대).

public class VerificationF02DisposeRaceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static SimulationHost NewHost() =>
        new(new SimulationSession(TestWorlds.Create(5, 5), ScentSettings.Default));

    private static async Task AssertTerminates(Task task, string name)
    {
        var finished = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.True(finished == task, $"{name}이(가) {Timeout.TotalSeconds}초 안에 끝나지 않음");
    }

    /// <summary>
    /// 테스트 A — 종료가 먼저 실행권을 얻는 순서(재검증 T1): 계산 스레드가 종료 검사를 통과하고 다음 명령을 꺼내기 직전에 멈춘 사이
    /// Dispose가 Stopping으로 바꾼다. 그 명령은 실행되지 않고 취소되어야 한다.
    /// </summary>
    [Fact]
    public async Task DisposeBeforeDequeue_PendingCallbackNeverRuns_AndIsCanceled()
    {
        var host = NewHost();
        using var entered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var atBoundary = new ManualResetEventSlim();
        using var releaseBoundary = new ManualResetEventSlim();
        Task? dispose = null;
        try
        {
            var first = host.RunExclusiveAsync(_ => { entered.Set(); releaseFirst.Wait(Timeout); return 7; });
            Assert.True(entered.Wait(Timeout), "첫 명령이 시작되지 않음");

            int armed = 1;
            host.TestHook = point =>
            {
                if (point != "before-dequeue" || Interlocked.Exchange(ref armed, 0) == 0) return;
                atBoundary.Set();
                releaseBoundary.Wait(Timeout);
            };
            int calls = 0;
            var target = host.RunExclusiveAsync(_ => Interlocked.Increment(ref calls));

            releaseFirst.Set();
            Assert.True(atBoundary.Wait(Timeout), "계산 스레드가 명령을 꺼내기 직전 지점에 도달하지 않음");
            Assert.Equal(0, calls);
            Assert.False(target.IsCompleted);

            dispose = Task.Run(host.Dispose);
            Assert.True(SpinWait.SpinUntil(() => host.DisposeRequested, Timeout), "Dispose가 Stopping으로 바꾸지 않음");
            releaseBoundary.Set();

            await AssertTerminates(dispose, "Dispose");
            await AssertTerminates(target, "대기 명령");
            Assert.Equal(7, await first);
            Assert.Equal(0, Volatile.Read(ref calls));
            Assert.True(target.IsCanceled, $"대기 명령 상태: {target.Status}");
            Assert.Null(host.Failure);
        }
        finally
        {
            releaseFirst.Set();
            releaseBoundary.Set();
            host.TestHook = null;
            dispose ??= Task.Run(host.Dispose);
            Assert.True(dispose.Wait(Timeout), "정리 중 Dispose가 끝나지 않음");
        }
    }

    /// <summary>
    /// 테스트 B — 명령이 먼저 실행권을 얻는 순서: 실행 중인 명령은 Dispose와 무관하게 정확히 한 번 끝까지 실행되고
    /// (성공 또는 실제 예외), 그 뒤 대기 명령은 취소된다. 예외여도 공개 Tick은 세션과 맞는다(F07).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningCallback_CompletesExactlyOnce_QueuedAreCanceled(bool throws)
    {
        var host = NewHost();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? dispose = null;
        try
        {
            int runs = 0, queuedRuns = 0;
            var running = host.RunExclusiveAsync(s =>
            {
                Interlocked.Increment(ref runs);
                entered.Set();
                release.Wait(Timeout);
                s.Step();
                if (throws) throw new InvalidOperationException("명령 예외");
                return s.Tick;
            });
            Assert.True(entered.Wait(Timeout));
            var queued = new[]
            {
                host.RunExclusiveAsync(_ => Interlocked.Increment(ref queuedRuns)),
                host.RunExclusiveAsync(_ => Interlocked.Increment(ref queuedRuns)),
            };

            dispose = Task.Run(host.Dispose);
            Assert.True(SpinWait.SpinUntil(() => host.DisposeRequested, Timeout));
            release.Set();

            await AssertTerminates(dispose, "Dispose");
            await AssertTerminates(running, "실행 중 명령");
            foreach (var q in queued) await AssertTerminates(q, "대기 명령");

            Assert.Equal(1, runs);
            if (throws)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => running);
                Assert.Equal("명령 예외", error.Message);
            }
            else
            {
                Assert.Equal(1L, await running);
            }
            Assert.Equal(1, host.Tick);
            Assert.Equal(0, queuedRuns);
            Assert.All(queued, q => Assert.True(q.IsCanceled, $"대기 명령 상태: {q.Status}"));
        }
        finally
        {
            release.Set();
            dispose ??= Task.Run(host.Dispose);
            Assert.True(dispose.Wait(Timeout));
        }
    }

    /// <summary>
    /// 테스트 C-1 — 여러 스레드가 동시에 명령을 넣는 중 Dispose: 접수된 명령은 모두 끝나고, 실행된 콜백 수 = 성공 Task 수,
    /// Dispose 반환 후에는 어떤 콜백도 실행되지 않는다. 접수 거부는 ObjectDisposedException뿐이다.
    /// </summary>
    [Fact]
    public async Task ConcurrentEnqueueDuringDispose_EveryAcceptedTaskTerminates_AndNoCallbackRunsAfterDispose()
    {
        for (int round = 0; round < 20; round++)
        {
            var host = NewHost();
            int callbacks = 0;
            const int producers = 4, perProducer = 50;
            var accepted = new System.Collections.Concurrent.ConcurrentBag<Task<int>>();
            using var start = new Barrier(producers + 1);
            var threads = Enumerable.Range(0, producers).Select(_ => Task.Run(() =>
            {
                start.SignalAndWait(Timeout);
                for (int i = 0; i < perProducer; i++)
                {
                    try { accepted.Add(host.RunExclusiveAsync(_ => Interlocked.Increment(ref callbacks))); }
                    catch (ObjectDisposedException) { }
                }
            })).ToArray();

            start.SignalAndWait(Timeout);
            host.Dispose();
            int callbacksAtDispose = Volatile.Read(ref callbacks);
            await AssertTerminates(Task.WhenAll(threads), "생산자");

            foreach (var task in accepted) await AssertTerminates(task, "접수된 명령");
            Assert.Equal(callbacksAtDispose, Volatile.Read(ref callbacks));
            Assert.Equal(accepted.Count(t => t.IsCompletedSuccessfully), callbacksAtDispose);
            Assert.All(accepted, t => Assert.True(t.IsCompletedSuccessfully || t.IsCanceled, $"상태: {t.Status}"));
        }
    }

    /// <summary>테스트 C-2 — 계산 스레드 자신이 명령 안에서 Dispose: self-join 없이 끝나고, 뒤 명령은 취소, 중복 Dispose와 새 명령 거부.</summary>
    [Fact]
    public async Task DisposeFromWorkerCallback_DoesNotSelfJoin_AndCancelsTheRest()
    {
        var host = NewHost();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var blocker = host.RunExclusiveAsync(_ => { entered.Set(); release.Wait(Timeout); return 0; });
            Assert.True(entered.Wait(Timeout));
            var selfDispose = host.RunExclusiveAsync(_ => { host.Dispose(); return 1; });
            int laterRuns = 0;
            var later = host.RunExclusiveAsync(_ => Interlocked.Increment(ref laterRuns));
            release.Set();

            await AssertTerminates(selfDispose, "명령 안 Dispose");
            await AssertTerminates(later, "뒤 명령");
            Assert.Equal(1, await selfDispose);
            Assert.True(later.IsCanceled);
            Assert.Equal(0, laterRuns);

            var second = Task.Run(host.Dispose);
            await AssertTerminates(second, "중복 Dispose");
            Assert.Throws<ObjectDisposedException>(() => { _ = host.StepAsync(); });
        }
        finally
        {
            release.Set();
            host.Dispose();
        }
    }

    /// <summary>테스트 C-3 — 계산 스레드 실패 직전에 꺼낼 수 있었던 명령도 실행되지 않고 실패로 끝나며 새 명령은 거부된다.</summary>
    [Fact]
    public async Task WorkerFailureAtDequeueBoundary_FailsPendingWithoutRunning()
    {
        var host = NewHost();
        using var entered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        try
        {
            var first = host.RunExclusiveAsync(_ => { entered.Set(); releaseFirst.Wait(Timeout); return 0; });
            Assert.True(entered.Wait(Timeout));
            int armed = 1;
            host.TestHook = point =>
            {
                if (point == "before-dequeue" && Interlocked.Exchange(ref armed, 0) == 1)
                    throw new InvalidOperationException("계산 스레드 시험 예외");
            };
            int calls = 0;
            var pending = host.RunExclusiveAsync(_ => Interlocked.Increment(ref calls));
            releaseFirst.Set();

            await AssertTerminates(pending, "대기 명령");
            Assert.Equal(0, await first);
            Assert.Equal(0, calls);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.Equal("계산 스레드 시험 예외", error.InnerException?.Message);
            Assert.Throws<InvalidOperationException>(() => { _ = host.StepAsync(); });
        }
        finally
        {
            releaseFirst.Set();
            host.TestHook = null;
            host.Dispose();
        }
    }
}

public class VerificationF03ConcentrationBoundaryTests(ITestOutputHelper output)
{
    private static readonly int MaxBits = BitConverter.SingleToInt32Bits(ScentState.MaxConcentration);

    /// <summary>재검증 T2 입력: 양쪽 레이어가 열린 5×5, (2,2) 통로(선택), 1종, 지상 중심 0, 지상 4이웃·지하 중심 = c.</summary>
    private static SimulationSession BoundarySession(double dt, double d, double dp, float c, bool passage = true)
    {
        var world = TestWorlds.Create(5, 5);
        if (passage) Assert.Equal(WorldError.None, world.TrySetPassage(2, 2, true));
        var settings = new ScentSettings
        {
            TickSeconds = dt,
            Types = new[] { new ScentTypeSettings("x", "X", null, d, dp, 0, 0) },
        };
        Assert.Empty(settings.Validate());
        var session = new SimulationSession(world, settings);
        var surface = session.Scent.MutableField(0, LayerId.Surface);
        foreach (var (x, y) in new[] { (1, 2), (3, 2), (2, 1), (2, 3) }) surface[world.Config.ToLinearIndex(x, y)] = c;
        session.Scent.MutableField(0, LayerId.Nest)[world.Config.ToLinearIndex(2, 2)] = c;
        return session;
    }

    private static byte[] Save(SimulationSession session)
    {
        using var stream = new MemoryStream();
        ExperimentSerializer.Save(session, stream);
        return stream.ToArray();
    }

    /// <summary>Validate → Save → Load → Step → Save → Load 전체 왕복과 범위·해시를 확인하고 중심 값을 돌려준다.</summary>
    private float RoundTripStepRoundTrip(SimulationSession session, string label)
    {
        Assert.True(session.Scent.AllValuesInSupportedRange(), "초기 농도가 지원 범위 밖");
        var loaded = ExperimentSerializer.Load(new MemoryStream(Save(session)));
        Assert.Equal(session.ComputeStateHash(), loaded.ComputeStateHash());

        loaded.Step();
        Assert.Equal(1, loaded.Tick);
        float center = loaded.Scent.Get(0, LayerId.Surface, 2, 2);
        foreach (var layer in new[] { LayerId.Surface, LayerId.Nest })
            Assert.All(loaded.Scent.Field(0, layer).ToArray(), v => Assert.True(float.IsFinite(v) && v >= 0 && v <= ScentState.MaxConcentration,
                $"범위 밖 값 {v:R} (0x{BitConverter.SingleToInt32Bits(v):X8})"));
        Assert.True(loaded.Scent.AllValuesInSupportedRange());

        var second = Save(loaded);
        var reloaded = ExperimentSerializer.Load(new MemoryStream(second));
        Assert.Equal(loaded.ComputeStateHash(), reloaded.ComputeStateHash());
        Assert.Equal(center, reloaded.Scent.Get(0, LayerId.Surface, 2, 2));
        output.WriteLine($"{label}: center={center:R} bits=0x{BitConverter.SingleToInt32Bits(center):X8} " +
                         $"(max 0x{MaxBits:X8}, diff {BitConverter.SingleToInt32Bits(center) - MaxBits} ULP), second save {second.Length} bytes OK");
        return center;
    }

    /// <summary>재검증 T2 반례 그대로: CFL = 1, 상한 농도. 수정 전에는 0x7149F2CB(+1 ULP)가 되어 두 번째 Save가 실패했다.</summary>
    [Fact]
    public void BoundaryInput_StaysSaveableAfterOneTick()
    {
        var session = BoundarySession(1, 0.012, 0.952, ScentState.MaxConcentration);
        Assert.Equal(1.0, session.Settings.Types[0].MaxOutflowFraction(1));
        Assert.Equal(0x7149F2CA, MaxBits);
        float center = RoundTripStepRoundTrip(session, "boundary");
        Assert.Equal(ScentState.MaxConcentration, center);
    }

    [Theory]
    [InlineData("Dp=0.951999", 1.0, 0.012, 0.951999, true)]
    [InlineData("통로 없음", 1.0, 0.012, 0.952, false)]
    [InlineData("dt=0.1", 0.1, 0.12, 9.52, true)]
    [InlineData("dt=10", 10.0, 0.0012, 0.0952, true)]
    [InlineData("4D=1, Dp=0", 1.0, 0.25, 0.0, true)]
    [InlineData("D=0, Dp=1", 1.0, 0.0, 1.0, true)]
    [InlineData("D=0.1, Dp=0.6", 1.0, 0.1, 0.6, true)]
    public void BoundaryControls_StaySaveable(string label, double dt, double d, double dp, bool passage)
    {
        RoundTripStepRoundTrip(BoundarySession(dt, d, dp, ScentState.MaxConcentration, passage), label);
        RoundTripStepRoundTrip(BoundarySession(dt, d, dp, MathF.BitDecrement(ScentState.MaxConcentration), passage), label + " 상한-1ULP");
    }

    /// <summary>같은 구조에서 농도 1: 경계 보정이 일어나지 않는 정상 범위이며 계산 결과가 그대로 유지된다.</summary>
    [Fact]
    public void SameStructure_UnitConcentration_IsUnaffected()
    {
        float center = RoundTripStepRoundTrip(BoundarySession(1, 0.012, 0.952, 1f), "농도 1");
        // 수정 전후 동일(재검증 하네스 관찰값: 1 = 0x3F800000).
        Assert.Equal(1f, center);
    }

    /// <summary>
    /// CFL 경계 근처 계수와 상한 농도 배치를 여러 개 돌려도(여러 틱) 모든 값이 지원 범위에 남는다.
    /// 보정은 반올림 여유 안에서만 일어나므로, 여유를 넘는 오차가 있었다면 범위 밖 값이 남아 실패한다.
    /// </summary>
    [Fact]
    public void BoundarySweep_ManyCoefficientsAndTicks_StayInRange()
    {
        var rng = new Random(20261029);
        var world = TestWorlds.Create(7, 7);
        Assert.Equal(WorldError.None, world.TrySetPassage(3, 3, true));
        Assert.Equal(WorldError.None, world.TrySetPassage(1, 5, true));
        for (int trial = 0; trial < 400; trial++)
        {
            double dt = new[] { 0.05, 0.1, 1, 3, 10 }[trial % 5];
            double share = rng.NextDouble();
            double d = share / 4 / dt, dp = (1 - share) / dt; // (4D + Dp)·dt = 1 (double 반올림 포함, Validate가 판정)
            var settings = new ScentSettings { TickSeconds = dt, Types = new[] { new ScentTypeSettings("x", "X", null, d, dp, 0, 0) } };
            if (settings.Validate().Count > 0) continue;
            var state = new ScentState(world.Config, 1);
            foreach (var layer in new[] { LayerId.Surface, LayerId.Nest })
            {
                var field = state.MutableField(0, layer);
                for (int i = 0; i < field.Length; i++)
                    field[i] = rng.Next(3) switch { 0 => 0, 1 => ScentState.MaxConcentration, _ => MathF.BitDecrement(ScentState.MaxConcentration) };
            }
            var system = new ScentSystem(world);
            for (int tick = 0; tick < 5; tick++)
            {
                system.Tick(state, settings);
                Assert.True(state.AllValuesInSupportedRange(), $"trial {trial} tick {tick}: D={d:R} Dp={dp:R} dt={dt}");
            }
        }
    }

    /// <summary>보정 범위: 상한을 반올림 여유 이내로 넘은 값만 상한으로 내리고, 그보다 큰 값(지원 범위 밖 입력·결함)은 그대로 두어 저장이 거부되게 한다.</summary>
    [Fact]
    public void RoundingCorrection_OnlyAppliesWithinRoundingSlack()
    {
        float max = ScentState.MaxConcentration;
        Assert.Equal(max, ScentSystem.CorrectRoundingOvershoot(BitConverter.Int32BitsToSingle(MaxBits + 1)));
        Assert.Equal(max, ScentSystem.CorrectRoundingOvershoot(BitConverter.Int32BitsToSingle(MaxBits + ScentSystem.RoundingSlackUlps)));
        float beyond = BitConverter.Int32BitsToSingle(MaxBits + ScentSystem.RoundingSlackUlps + 1);
        Assert.Equal(beyond, ScentSystem.CorrectRoundingOvershoot(beyond));
        Assert.Equal(2e30f, ScentSystem.CorrectRoundingOvershoot(2e30f));
        Assert.Equal(max, ScentSystem.CorrectRoundingOvershoot(max));
        Assert.Equal(123f, ScentSystem.CorrectRoundingOvershoot(123f));

        // 지원 범위 밖 상태(2e30)는 한 틱 뒤에도 상한으로 숨겨지지 않는다 → 저장 거부 유지.
        var session = BoundarySession(1, 0.012, 0.952, 1f);
        session.Scent.MutableField(0, LayerId.Nest)[session.World.Config.ToLinearIndex(0, 0)] = 2e30f;
        session.Step();
        Assert.False(session.Scent.AllValuesInSupportedRange());
        Assert.Throws<InvalidOperationException>(() => ExperimentSerializer.Save(session, new MemoryStream()));
    }

    /// <summary>
    /// 감쇠·발생·범위 정리(벡터 경로)는 스칼라 기준식과 비트 단위로 같다: NaN·−0·음수·상한 근처·여유 밖 값, 벡터 폭의 배수가 아닌 길이 포함.
    /// </summary>
    [Theory]
    [InlineData(1f, 0f, false)]
    [InlineData(0.99f, 0f, false)]
    [InlineData(0.97f, 0.35f, true)]
    [InlineData(1f, 1e6f, true)]
    [InlineData(0.5f, 0f, true)]
    public void DecayAndEmit_VectorPath_MatchesScalarReferenceBitForBit(float decay, float emit, bool withSource)
    {
        var rng = new Random(7);
        float max = ScentState.MaxConcentration;
        float[] specials =
        {
            0f, -0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, max, MathF.BitDecrement(max),
            BitConverter.Int32BitsToSingle(MaxBits + 1), BitConverter.Int32BitsToSingle(MaxBits + ScentSystem.RoundingSlackUlps),
            BitConverter.Int32BitsToSingle(MaxBits + ScentSystem.RoundingSlackUlps + 1), 2e30f, float.Epsilon, 1e-30f,
        };
        foreach (int length in new[] { 1, 7, 31, 32, 33, 1000, 4099 })
        {
            var input = new float[length];
            var source = new byte[length];
            for (int i = 0; i < length; i++)
            {
                input[i] = rng.Next(3) == 0 ? specials[rng.Next(specials.Length)] : (float)(rng.NextDouble() * Math.Pow(10, rng.Next(-20, 31)));
                source[i] = (byte)rng.Next(256);
            }
            var actual = (float[])input.Clone();
            ScentSystem.DecayAndEmit(actual, decay, withSource ? source : null, emit);
            for (int i = 0; i < length; i++)
            {
                float raw = withSource && emit != 0 ? input[i] * decay + emit * source[i] : input[i] * decay;
                float expected = ScentSystem.CorrectRoundingOvershoot(MathF.Max(0f, raw));
                Assert.True(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual[i]),
                    $"len {length} i {i}: input {input[i]:R} → expected {expected:R}, actual {actual[i]:R}");
            }
        }
    }

    /// <summary>발생·감쇠가 없으면 정상 범위에서 총량이 보존된다(수정 전후 동일, 보정은 상한 근처에서만 동작).</summary>
    [Fact]
    public void NormalRange_WithoutDecayOrEmission_ConservesTotal()
    {
        var world = TestWorlds.Create(32, 24);
        TestWorlds.Paint(world);
        var settings = new ScentSettings { Types = new[] { new ScentTypeSettings("x", "X", null, 0.5, 2, 0, 0) } };
        Assert.Empty(settings.Validate());
        var state = new ScentState(world.Config, 1);
        var system = new ScentSystem(world);
        system.Tick(state, settings); // 고체 칸 0 정리
        var surface = state.MutableField(0, LayerId.Surface);
        for (int i = 0; i < surface.Length; i += 7) surface[i] = 1000f;
        system.Tick(state, settings);
        double before = Total(state);
        for (int i = 0; i < 100; i++) system.Tick(state, settings);
        Assert.Equal(before, Total(state), before * 1e-5);

        static double Total(ScentState s) =>
            s.Field(0, LayerId.Surface).ToArray().Sum(v => (double)v) + s.Field(0, LayerId.Nest).ToArray().Sum(v => (double)v);
    }
}
