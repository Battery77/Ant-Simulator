using AntSim.Core.Ants;
using AntSim.Core.Editing;
using AntSim.Core.Scent;
using AntSim.Core.Serialization;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

// PR #3(개미 단계 A)을 PR #2 병합 후 main에 통합하면서 맞춘 계약의 회귀 테스트:
// 초기화 결과(표시 월드·초기화 세대·개미 생성 결과)와 스냅샷(냄새 + AntFrame)의 세대 일치.

public class StageAResetIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly AntSettings Ants = AntSettings.Default with { InitialCount = 80, Seed = 42 };

    private static SimulationSession NewSession(AntSettings? ants = null) =>
        new(SimulationSessionTests.SampleWorld(), ScentSettings.Default, ants ?? Ants);

    /// <summary>조건을 만족하는 스냅샷을 기다려 가져온다(조건에 맞지 않는 스냅샷은 반납). 시간 제한이 있다.</summary>
    internal static async Task<ScentSnapshot> TakeSnapshot(SimulationHost host, Func<ScentSnapshot, bool> accept, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (host.TryTakeSnapshot(out var snapshot))
            {
                if (accept(snapshot)) return snapshot;
                host.Release(snapshot);
            }
            await Task.Delay(2);
        }
        throw new TimeoutException($"스냅샷을 받지 못함: {what}");
    }

    /// <summary>초기화 결과의 월드·세대·틱·개미 생성 결과가 같은 명령에서 나온 계산 상태와 일치하고, 상태 전체가 새 실험과 같다.</summary>
    [Fact]
    public async Task ResetResult_MatchesComputedState_FromTheSameCommand()
    {
        using var host = new SimulationHost(NewSession());
        await host.RunExclusiveAsync(s =>
        {
            s.Step(40);
            new MapEditor(s.World).Apply(EditOp.SetTerrain(LayerId.Surface, 5, 5, Terrain.Water)); // 현재 월드를 시작 월드와 다르게
            return true;
        }).WaitAsync(Timeout);

        var result = await host.ResetAsync().WaitAsync(Timeout);
        Assert.Equal(1, result.ResetCount);
        Assert.Equal(0, result.Tick);
        Assert.Equal(new AntSpawnReport(80, 80), result.Ants);

        var state = await host.RunExclusiveAsync(s => (
            World: s.World.ComputeContentHash(), Initial: s.InitialWorld.ComputeContentHash(), Same: ReferenceEquals(s.World, result.World),
            s.Ants.Count, s.Tick, s.ElapsedSeconds, s.ResetCount, Hash: s.ComputeStateHash())).WaitAsync(Timeout);
        Assert.Equal(state.Initial, state.World);
        Assert.Equal(state.World, result.World.ComputeContentHash());
        Assert.False(state.Same, "표시용 월드는 계산 월드의 복제본이어야 한다");
        Assert.Equal(80, state.Count);
        Assert.Equal(0, state.Tick);
        Assert.Equal(0, state.ElapsedSeconds);
        Assert.Equal(1, state.ResetCount);
        Assert.Equal(NewSession().ComputeStateHash(), state.Hash); // 월드·냄새·틱·개미 전부 새 실험과 같다
    }

    /// <summary>개미 설정 적용 경로(ResetAsync(ants)): 새 설정으로 재생성, 세대 증가, 월드 복원, 프레임 개수 일치.</summary>
    [Fact]
    public async Task ResetWithAntSettings_RespawnsWithNewSettings_AndAdvancesGeneration()
    {
        using var host = new SimulationHost(NewSession());
        await host.StepAsync().WaitAsync(Timeout);
        var changed = Ants with { InitialCount = 33, Seed = 7 };
        var result = await host.ResetAsync(changed).WaitAsync(Timeout);

        Assert.Equal(new AntSpawnReport(33, 33), result.Ants);
        Assert.Equal(1, result.ResetCount);
        var (settings, count) = await host.RunExclusiveAsync(s => (s.AntSettings, s.Ants.Count)).WaitAsync(Timeout);
        Assert.Equal(changed, settings);
        Assert.Equal(33, count);

        host.SelectHeatmap(0, LayerId.Surface);
        var snapshot = await TakeSnapshot(host, s => s.ResetCount == 1, "초기화 후 스냅샷");
        Assert.Equal(33, snapshot.Ants.Count);
        Assert.Equal(NewSession(changed).Ants.X.ToArray(), snapshot.Ants.X.AsSpan(0, 33).ToArray());
        host.Release(snapshot);
    }

    /// <summary>잘못된 개미 설정으로 초기화하면 Task가 실패하고 상태(틱·세대·월드·개미)는 하나도 바뀌지 않는다.</summary>
    [Fact]
    public async Task ResetWithInvalidAntSettings_FailsWithoutChangingAnything()
    {
        using var host = new SimulationHost(NewSession());
        await host.RunExclusiveAsync(s => { s.Step(5); return true; }).WaitAsync(Timeout);
        var before = await host.RunExclusiveAsync(s => (s.ComputeStateHash(), s.ResetCount, s.AntSettings)).WaitAsync(Timeout);

        await Assert.ThrowsAsync<ArgumentException>(() => host.ResetAsync(Ants with { InitialCount = -1 }).WaitAsync(Timeout));
        var after = await host.RunExclusiveAsync(s => (s.ComputeStateHash(), s.ResetCount, s.AntSettings)).WaitAsync(Timeout);
        Assert.Equal(before, after);
        Assert.Equal(5, host.Tick);
    }

    /// <summary>
    /// 스냅샷의 냄새와 개미 프레임은 같은 세대·같은 틱에서 함께 만들어진다. 초기화 직후 프레임은 이전 위치 = 현재 위치(보간 없음)이고,
    /// 화면이 들고 있는 이전 세대 스냅샷은 계산 스레드가 덮어쓰지 않는다(배열 소유권). 개미 ID는 초기화 뒤 0부터 다시 쓰이지만 세대로 구분된다.
    /// </summary>
    [Fact]
    public async Task Snapshots_CarryGeneration_FrameIsConsistent_AndHeldSnapshotsAreNotOverwritten()
    {
        using var host = new SimulationHost(NewSession());
        host.SelectHeatmap(new[] { 0, 1 }, LayerId.Surface);
        await host.RunExclusiveAsync(s => { s.Step(30); return true; }).WaitAsync(Timeout);
        var old = await TakeSnapshot(host, s => s.Tick == 30, "틱 30");
        Assert.Equal(0, old.ResetCount);
        var oldX = old.Ants.X.AsSpan(0, old.Ants.Count).ToArray();
        var oldIds = old.Ants.Id.AsSpan(0, old.Ants.Count).ToArray();
        var oldFood = old.Field(0).ToArray();

        var result = await host.ResetAsync().WaitAsync(Timeout);
        var fresh = await TakeSnapshot(host, s => s.ResetCount == result.ResetCount, "초기화 후");
        Assert.True(old.ResetCount < result.ResetCount, "이전 세대 스냅샷은 화면 필터(ResetCount < 최소 세대)로 버려진다");
        Assert.Equal(0, fresh.Tick);

        var frame = fresh.Ants;
        var reference = NewSession();
        Assert.Equal(reference.Ants.Count, frame.Count);
        Assert.Equal(reference.Ants.X.ToArray(), frame.X.AsSpan(0, frame.Count).ToArray());
        Assert.Equal(frame.X.AsSpan(0, frame.Count).ToArray(), frame.PrevX.AsSpan(0, frame.Count).ToArray()); // 초기화 전 위치에서 보간하지 않음
        Assert.Equal(frame.Y.AsSpan(0, frame.Count).ToArray(), frame.PrevY.AsSpan(0, frame.Count).ToArray());
        Assert.All(fresh.Field(0), v => Assert.Equal(0f, v)); // 냄새도 같은 세대(초기화 직후 0)
        Assert.Equal(oldIds[0], frame.Id[0]);                 // ID는 재사용되지만
        Assert.NotEqual(old.ResetCount, fresh.ResetCount);    // 세대가 다르다

        // 계산 스레드가 계속 게시해도 화면이 들고 있는 스냅샷은 그대로다.
        for (int i = 0; i < 5; i++)
        {
            await host.StepAsync().WaitAsync(Timeout);
            if (host.TryTakeSnapshot(out var extra)) host.Release(extra);
        }
        Assert.Equal(oldX, old.Ants.X.AsSpan(0, old.Ants.Count).ToArray());
        Assert.Equal(oldFood, old.Field(0).ToArray());
        host.Release(old);
        host.Release(fresh);
    }

    /// <summary>
    /// 늦게 도착한 이전 세대 스냅샷: 초기화 직전에 게시된 스냅샷을 초기화 결과를 받은 뒤에 가져와도 세대가 낮아 구분된다.
    /// (게시 지점을 동기화 훅으로 붙잡아 순서를 결정적으로 만든다.)
    /// </summary>
    [Fact]
    public async Task SnapshotPublishedBeforeReset_ButTakenAfter_HasOlderGeneration()
    {
        using var host = new SimulationHost(NewSession());
        using var hold = new ManualResetEventSlim();
        using var atPublish = new ManualResetEventSlim();
        using var stored = new ManualResetEventSlim();
        try
        {
            await host.StepAsync().WaitAsync(Timeout);
            var first = await TakeSnapshot(host, s => s.Tick == 1, "틱 1");
            host.Release(first);

            // 게시 1번째(틱 2, 세대 0)는 그대로 통과시키고, 그 게시가 끝난 뒤(다음 루프의 before-dequeue) 알린다.
            // 게시 2번째(초기화 뒤, 세대 1)는 시작 지점에서 붙잡는다.
            int publishes = 0;
            host.TestHook = point =>
            {
                if (point == "publish")
                {
                    if (Interlocked.Increment(ref publishes) == 2)
                    {
                        atPublish.Set();
                        hold.Wait(Timeout);
                    }
                }
                else if (point == "before-dequeue" && Volatile.Read(ref publishes) == 1)
                {
                    stored.Set();
                }
            };
            await host.StepAsync().WaitAsync(Timeout);
            Assert.True(stored.Wait(Timeout), "틱 2 스냅샷 게시가 끝나지 않음");

            var result = await host.ResetAsync().WaitAsync(Timeout);
            Assert.True(atPublish.Wait(Timeout), "초기화 뒤 게시 지점에 도달하지 않음");

            // 초기화 결과를 받은 뒤 가져온 스냅샷이 초기화 이전에 게시된 것: 세대로 구분된다.
            Assert.True(host.TryTakeSnapshot(out var late));
            Assert.Equal(2, late.Tick);
            Assert.Equal(0, late.ResetCount);
            Assert.True(late.ResetCount < result.ResetCount);
            host.Release(late);

            hold.Set();
            var current = await TakeSnapshot(host, s => s.ResetCount == result.ResetCount, "새 세대");
            Assert.Equal(0, current.Tick);
            host.Release(current);
        }
        finally
        {
            hold.Set();
            host.TestHook = null;
        }
    }

    /// <summary>Play → Pause → Reset → Play: 초기화 뒤 다시 재생해도 새 실험과 같은 결정적 결과.</summary>
    [Fact]
    public async Task PlayPauseResetPlay_IsDeterministic()
    {
        using var host = new SimulationHost(NewSession()) { Speed = 40 };
        host.Play();
        await WaitTick(host, 15);
        host.Pause();
        var result = await host.ResetAsync().WaitAsync(Timeout);
        Assert.Equal(0, host.Tick);
        Assert.False(host.IsPlaying);
        Assert.Equal(1, result.ResetCount);

        host.Play();
        await WaitTick(host, 10);
        host.Pause();
        var (tick, hash) = await host.RunExclusiveAsync(s => (s.Tick, s.ComputeStateHash())).WaitAsync(Timeout);
        var reference = NewSession();
        reference.Step((int)tick);
        Assert.Equal(reference.ComputeStateHash(), hash);
    }

    /// <summary>재생 중 초기화: 일시정지 상태로 틱 0, 상태 = 새 실험.</summary>
    [Fact]
    public async Task ResetWhilePlaying_StopsAndRestoresStart()
    {
        using var host = new SimulationHost(NewSession()) { Speed = 40 };
        host.Play();
        await WaitTick(host, 5);
        var result = await host.ResetAsync().WaitAsync(Timeout);
        Assert.False(host.IsPlaying);
        var (tick, hash) = await host.RunExclusiveAsync(s => (s.Tick, s.ComputeStateHash())).WaitAsync(Timeout);
        Assert.Equal(0, tick);
        Assert.Equal(0, result.Tick);
        Assert.Equal(NewSession().ComputeStateHash(), hash);
    }

    /// <summary>연속 초기화: 결과가 순서대로 세대 1, 2, 3이고 각 표시 월드는 서로 독립된 복제본이다.</summary>
    [Fact]
    public async Task ConsecutiveResets_AdvanceGenerationInOrder()
    {
        using var host = new SimulationHost(NewSession());
        var tasks = new[] { host.ResetAsync(), host.ResetAsync(Ants with { InitialCount = 10 }), host.ResetAsync() };
        var results = await Task.WhenAll(tasks).WaitAsync(Timeout);
        Assert.Equal(new long[] { 1, 2, 3 }, results.Select(r => r.ResetCount));
        Assert.Equal(new[] { 80, 10, 10 }, results.Select(r => r.Ants.Spawned)); // 설정 없는 초기화는 마지막 적용 설정을 유지
        Assert.False(ReferenceEquals(results[0].World, results[2].World));
        Assert.Equal(results[0].World.ComputeContentHash(), results[2].World.ComputeContentHash());
    }

    /// <summary>초기화와 종료의 경합: 종료가 먼저 실행권을 정하면 초기화 콜백은 실행되지 않고 취소된다(F02와 같은 계약).</summary>
    [Fact]
    public async Task ResetQueuedWhenDisposed_IsCanceled_AndSessionUntouched()
    {
        var session = NewSession();
        var host = new SimulationHost(session);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? dispose = null;
        try
        {
            var blocker = host.RunExclusiveAsync(_ => { entered.Set(); release.Wait(Timeout); return 0; });
            Assert.True(entered.Wait(Timeout));
            var reset = host.ResetAsync(Ants with { InitialCount = 5 });
            dispose = Task.Run(host.Dispose);
            Assert.True(SpinWait.SpinUntil(() => host.DisposeRequested, Timeout));
            release.Set();
            await dispose.WaitAsync(Timeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset.WaitAsync(Timeout));
            Assert.Equal(0, session.ResetCount); // 계산 스레드가 끝났으므로 직접 읽어도 안전
            Assert.Equal(80, session.Ants.Count);
        }
        finally
        {
            release.Set();
            host.Dispose();
        }
    }

    /// <summary>Load → Reset → Step: 시작·현재 월드가 다른 v2 실험을 불러와 초기화하면 시작 월드·저장된 개미 설정으로 재생성되고 이후 결정적.</summary>
    [Fact]
    public async Task LoadResetStep_UsesInitialWorldAndStoredAntSettings()
    {
        var original = NewSession(Ants with { InitialCount = 50, Seed = 9 });
        original.Step(60);
        new MapEditor(original.World).Apply(EditOp.PlaceSolid(LayerId.Surface, 2, 2, SolidBlock.Rock));
        using var file = new MemoryStream();
        ExperimentSerializer.Save(original, file);
        file.Position = 0;
        var loaded = ExperimentSerializer.Load(file);
        Assert.Equal(original.ComputeStateHash(), loaded.ComputeStateHash());

        using var host = new SimulationHost(loaded);
        var result = await host.ResetAsync().WaitAsync(Timeout);
        Assert.Equal(new AntSpawnReport(50, 50), result.Ants);
        var reference = NewSession(Ants with { InitialCount = 50, Seed = 9 });
        Assert.Equal(reference.World.ComputeContentHash(), result.World.ComputeContentHash());
        await host.RunExclusiveAsync(s => { s.Step(25); return true; }).WaitAsync(Timeout);
        reference.Step(25);
        Assert.Equal(reference.ComputeStateHash(), await host.RunExclusiveAsync(s => s.ComputeStateHash()).WaitAsync(Timeout));
    }

    /// <summary>일시정지 중 단일 틱: 스냅샷의 틱·개미 프레임·레이어별 집계가 계산 상태와 일치한다.</summary>
    [Fact]
    public async Task PausedStep_FrameMatchesSession_AndLayerCountsAddUp()
    {
        using var host = new SimulationHost(NewSession());
        host.SelectHeatmap(0, LayerId.Nest);
        await host.RunExclusiveAsync(s => { s.Step(200); return true; }).WaitAsync(Timeout);
        await host.StepAsync().WaitAsync(Timeout);
        var snapshot = await TakeSnapshot(host, s => s.Tick == 201, "틱 201");
        var (x, layers) = await host.RunExclusiveAsync(s => (s.Ants.X.ToArray(), s.Ants.Layer.ToArray())).WaitAsync(Timeout);
        var frame = snapshot.Ants;
        Assert.Equal(x, frame.X.AsSpan(0, frame.Count).ToArray());
        int surface = layers.Count(l => l == (byte)LayerId.Surface);
        Assert.Equal(surface, frame.CountOn(LayerId.Surface));
        Assert.Equal(layers.Length - surface, frame.CountOn(LayerId.Nest));
        Assert.True(surface > 0 && surface < layers.Length, $"두 레이어 모두에 개미가 있어야 시험이 의미 있음 (지상 {surface})");
        host.Release(snapshot);
    }

    private static async Task WaitTick(SimulationHost host, long tick)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (host.Tick < tick)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"틱 {tick}에 도달하지 않음");
            await Task.Delay(2);
        }
    }
}

/// <summary>개미 수가 많을 때의 성능 특성: 틱마다 개미 수에 비례하는 관리 힙 할당이 없어야 한다(GC 압력).</summary>
public class StageAAllocationTests
{
    [Fact]
    public void AntTick_DoesNotAllocatePerAnt()
    {
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default,
            AntSettings.Default with { InitialCount = 5000, Seed = 3 });
        var system = new AntSystem();
        for (int i = 0; i < 20; i++) system.Tick(session.Ants, session.World, session.AntSettings, 0.1f); // JIT·지상 확산
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) system.Tick(session.Ants, session.World, session.AntSettings, 0.1f);
        long perTick = (GC.GetAllocatedBytesForCurrentThread() - before) / 10;
        Assert.True(perTick < 1024, $"개미 5,000마리 1틱 할당 {perTick}바이트(개미 수에 비례하는 할당 의심)");
    }
}
