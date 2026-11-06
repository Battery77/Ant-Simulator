using AntSim.Core.Ants;
using AntSim.Core.Editing;
using AntSim.Core.Scent;
using AntSim.Core.Serialization;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

/// <summary>단계 A: 개미와 기존 시뮬레이션(세션·초기화·실험 파일·계산 스레드) 연동과 회귀.</summary>
public class AntSimulationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly AntSettings Ants = AntSettings.Default with { InitialCount = 80, Seed = 42 };

    private static SimulationSession NewSession(AntSettings? ants = null) =>
        new(SimulationSessionTests.SampleWorld(), ScentSettings.Default, ants ?? Ants);

    [Fact]
    public void Session_SpawnsAnts_AndAntsMoveEachTick()
    {
        var session = NewSession();
        Assert.Equal(new AntSpawnReport(80, 80), session.LastSpawn);
        var before = session.Ants.X.ToArray();
        session.Step();
        Assert.NotEqual(before, session.Ants.X.ToArray());
    }

    [Fact]
    public void AntsDoNotChangeWorld_AndOriginalIsIsolated()
    {
        var original = SimulationSessionTests.SampleWorld();
        ulong originalHash = original.ComputeContentHash();
        var session = new SimulationSession(original, ScentSettings.Default, Ants);
        ulong simWorld = session.World.ComputeContentHash();
        session.Step(200);
        Assert.Equal(originalHash, original.ComputeContentHash());
        Assert.Equal(simWorld, session.World.ComputeContentHash()); // 단계 A의 개미는 월드를 바꾸지 않는다

        new MapEditor(original).Apply(EditOp.PlaceSolid(LayerId.Surface, 21, 21, SolidBlock.Rock));
        session.Step(10);
        Assert.True(AntMovement.IsPassable(session.World.Surface, 21, 21));
    }

    [Fact]
    public void Reset_RestoresInitialAnts()
    {
        var session = NewSession();
        ulong start = session.ComputeStateHash();
        session.Step(150);
        Assert.NotEqual(start, session.ComputeStateHash());
        session.Reset();
        Assert.Equal(start, session.ComputeStateHash());
    }

    [Fact]
    public void Reset_WithNewAntSettings_Respawns()
    {
        var session = NewSession();
        session.Reset(Ants with { InitialCount = 13 });
        Assert.Equal(13, session.Ants.Count);
        Assert.Equal(13, session.AntSettings.InitialCount);
        session.Reset(AntSettings.None);
        Assert.Equal(0, session.Ants.Count);
        Assert.Throws<ArgumentException>(() => session.Reset(Ants with { BaseSpeed = -1 }));
    }

    [Fact]
    public void AllAntsStayOnPassableTiles_InFullSession()
    {
        var session = NewSession(Ants with { InitialCount = 300, BaseSpeed = 6 });
        for (int t = 0; t < 500; t++)
        {
            session.Step();
            for (int i = 0; i < session.Ants.Count; i++)
            {
                var a = session.Ants.Get(i);
                Assert.True(AntMovement.IsPassable(session.World.GetLayer(a.Layer), a.X, a.Y));
            }
        }
        Assert.True(session.Ants.Layer.ToArray().Any(l => l == (byte)LayerId.Surface), "통로로 지상에 올라온 개미가 있어야 함");
    }

    [Fact]
    public void ScentIsUnaffectedByAnts()
    {
        var withAnts = NewSession();
        var without = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
        withAnts.Step(50);
        without.Step(50);
        Assert.Equal(without.Scent.ComputeHash(), withAnts.Scent.ComputeHash());
    }

    // ---- 실험 파일 ----

    [Fact]
    public void Experiment_RoundTripsAnts_AndResumesIdentically()
    {
        var uninterrupted = NewSession();
        uninterrupted.Step(60);

        var first = NewSession();
        first.Step(25);
        using var stream = new MemoryStream();
        ExperimentSerializer.Save(first, stream);
        stream.Position = 0;
        var loaded = ExperimentSerializer.Load(stream);
        Assert.Equal(first.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Equal(first.AntSettings.Seed, loaded.AntSettings.Seed);
        Assert.Equal(first.AntSettings.QuantitySpeedTiers, loaded.AntSettings.QuantitySpeedTiers);

        loaded.Step(35);
        Assert.Equal(uninterrupted.ComputeStateHash(), loaded.ComputeStateHash());

        loaded.Reset();
        var fresh = NewSession();
        Assert.Equal(fresh.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void Experiment_V1File_StillLoads_WithoutAnts()
    {
        // 개미 추가 전 코드(v1)로 저장한 실제 파일. 기록된 해시는 그 코드에서 계산한 값이다.
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "experiment-v1.antexp");
        var session = ExperimentSerializer.Load(path);
        Assert.Equal(7, session.Tick);
        Assert.Equal(0.7, session.ElapsedSeconds, 9);
        Assert.Equal(1488237037508684556UL, session.World.ComputeContentHash());
        Assert.Equal(1488237037508684556UL, session.InitialWorld.ComputeContentHash());
        Assert.Equal(11466948791231909576UL, session.Scent.ComputeHash());
        Assert.Equal(0, session.Ants.Count);
        Assert.Equal(0, session.AntSettings.InitialCount);
        session.Step(5);
        Assert.Equal(8932635140070662969UL, session.Scent.ComputeHash());
    }

    [Fact]
    public void Experiment_AntOnImpassableTile_IsRejected()
    {
        var session = NewSession(Ants with { InitialCount = 1 });
        using var stream = new MemoryStream();
        ExperimentSerializer.Save(session, stream);
        var bytes = stream.ToArray();

        // 개미가 1마리일 때 파일 끝 구조: ... x f32, (f32 × 10), lastTile i32, rng u64, checksum u64.
        // X 좌표를 맵 밖(−5)으로 바꾸고 체크섬을 다시 계산한다.
        int xOffset = bytes.Length - 8 - 8 - 4 - 4 * 11;
        BitConverter.GetBytes(-5f).CopyTo(bytes, xOffset);
        var fnv = Util.Fnv1a64.Create();
        fnv.Add(bytes.AsSpan(0, bytes.Length - 8));
        BitConverter.GetBytes(fnv.Value).CopyTo(bytes, bytes.Length - 8);
        Assert.Throws<MapFormatException>(() => ExperimentSerializer.Load(new MemoryStream(bytes)));
    }

    // ---- 계산 스레드(Pause / Step / Reset) ----

    [Fact]
    public async Task Host_Step_AdvancesAntsExactlyOneTick_AndPublishesFrame()
    {
        using var host = new SimulationHost(NewSession());
        await host.StepAsync().WaitAsync(Timeout);
        var reference = NewSession();
        reference.Step();

        ScentSnapshot? snapshot = null;
        var until = DateTime.UtcNow + Timeout;
        while (snapshot is null && DateTime.UtcNow < until)
        {
            if (host.TryTakeSnapshot(out var s))
            {
                if (s.Tick == 1) snapshot = s;
                else host.Release(s);
            }
            await Task.Delay(5);
        }
        Assert.NotNull(snapshot);
        var frame = snapshot!.Ants;
        Assert.Equal(reference.Ants.Count, frame.Count);
        Assert.Equal(reference.Ants.X.ToArray(), frame.X.AsSpan(0, frame.Count).ToArray());
        Assert.Equal(reference.Ants.PrevY.ToArray(), frame.PrevY.AsSpan(0, frame.Count).ToArray());
        Assert.Equal(frame.Count, frame.CountOn(LayerId.Surface) + frame.CountOn(LayerId.Nest));
        host.Release(snapshot);
        Assert.Equal(reference.ComputeStateHash(), await host.RunExclusiveAsync(s => s.ComputeStateHash()).WaitAsync(Timeout));
    }

    [Fact]
    public async Task Host_ResetWithAntSettings_RespawnsAndReports()
    {
        using var host = new SimulationHost(NewSession());
        await host.StepAsync().WaitAsync(Timeout);
        var result = await host.ResetAsync(Ants with { InitialCount = 25 }).WaitAsync(Timeout);
        Assert.Equal(new AntSpawnReport(25, 25), result.Ants);
        Assert.Equal(1, result.ResetCount);
        Assert.Equal(0, result.Tick);
        Assert.Equal(0, host.Tick);
        var (count, worldHash) = await host.RunExclusiveAsync(s => (s.Ants.Count, s.World.ComputeContentHash())).WaitAsync(Timeout);
        Assert.Equal(25, count);
        Assert.Equal(worldHash, result.World.ComputeContentHash());
    }

    [Fact]
    public async Task Host_PlayPause_WithAnts_StaysDeterministic()
    {
        using var host = new SimulationHost(NewSession());
        host.Speed = 40;
        host.Play();
        while (host.Tick < 30) await Task.Delay(5);
        host.Pause();
        var (tick, hash) = await host.RunExclusiveAsync(s => (s.Tick, s.ComputeStateHash())).WaitAsync(Timeout);
        var reference = NewSession();
        reference.Step((int)tick);
        Assert.Equal(reference.ComputeStateHash(), hash);
    }
}
