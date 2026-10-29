using AntSim.Core.Editing;
using AntSim.Core.Scent;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class SimulationSessionTests
{
    internal static World SampleWorld()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(80, 60, 32));
        var editor = new MapEditor(world);
        editor.Apply(EditOp.AddPassage(20, 20));
        editor.Apply(EditOp.AddPassage(50, 30));
        editor.Apply(EditOp.SetQuantity(LayerId.Surface, 10, 10, QuantityKind.Food, 8));
        editor.Apply(EditOp.SetQuantity(LayerId.Nest, 20, 21, QuantityKind.Larva, 5));
        for (int y = 0; y < 60; y++) editor.Apply(EditOp.PlaceSolid(LayerId.Surface, 35, y, SolidBlock.Rock));
        return world;
    }

    [Fact]
    public void Start_ClonesWorld_AndNeverChangesOriginal()
    {
        var world = SampleWorld();
        ulong original = world.ComputeContentHash();
        var session = new SimulationSession(world, ScentSettings.Default);
        session.Step(50);
        Assert.Equal(original, world.ComputeContentHash());
        Assert.NotSame(world, session.World);
    }

    [Fact]
    public void EditsToOriginal_AfterStart_DoNotAffectSession()
    {
        var world = SampleWorld();
        var session = new SimulationSession(world, ScentSettings.Default);
        ulong simWorld = session.World.ComputeContentHash();
        new MapEditor(world).Apply(EditOp.SetQuantity(LayerId.Surface, 1, 1, QuantityKind.Food, 9));
        Assert.Equal(simWorld, session.World.ComputeContentHash());
    }

    [Fact]
    public void SameInputs_ProduceSameState()
    {
        var a = new SimulationSession(SampleWorld(), ScentSettings.Default);
        var b = new SimulationSession(SampleWorld(), ScentSettings.Default);
        a.Step(40);
        b.Step(25);
        b.Step(15);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(40, a.Tick);
        Assert.Equal(4.0, a.ElapsedSeconds, 9);
    }

    [Fact]
    public void Reset_ReturnsToStartState()
    {
        var session = new SimulationSession(SampleWorld(), ScentSettings.Default);
        ulong start = session.ComputeStateHash();
        session.Step(30);
        Assert.True(session.Scent.Total(0) > 0);
        session.Reset();
        Assert.Equal(0, session.Tick);
        Assert.Equal(0, session.ElapsedSeconds);
        Assert.Equal(start, session.ComputeStateHash());
    }

    [Fact]
    public void Settings_CanChangeCoefficientsButNotTypes()
    {
        var session = new SimulationSession(SampleWorld(), ScentSettings.Default);
        var faster = ScentSettings.Default.WithType(0, ScentSettings.Default.Types[0] with { Decay = 0.5 });
        Assert.Empty(session.TrySetSettings(faster));
        Assert.Equal(0.5, session.Settings.Types[0].Decay);

        Assert.NotEmpty(session.TrySetSettings(ScentSettings.Default.WithTick(0.5)));       // 불안정
        Assert.NotEmpty(session.TrySetSettings(ScentSettings.Default with { Types = ScentSettings.Default.Types.Take(1).ToArray() }));
        Assert.Equal(0.5, session.Settings.Types[0].Decay); // 실패한 변경은 반영되지 않음
    }

    [Fact]
    public void InvalidStartSettings_Throw()
    {
        Assert.Throws<ArgumentException>(() => new SimulationSession(SampleWorld(), ScentSettings.Default.WithTick(2)));
    }
}

public class SimulationHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Step_RunsExactlyOneTick()
    {
        using var host = new SimulationHost(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));
        await host.StepAsync().WaitAsync(Timeout);
        await host.StepAsync().WaitAsync(Timeout);
        Assert.Equal(2, host.Tick);
        Assert.Equal(2, await host.RunExclusiveAsync(s => s.Tick).WaitAsync(Timeout));
    }

    [Fact]
    public async Task Play_AdvancesAndPause_Stops()
    {
        using var host = new SimulationHost(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));
        host.Speed = 50; // 실제 0.1초에 50틱 정도
        host.Play();
        var until = DateTime.UtcNow + Timeout;
        while (host.Tick < 20 && DateTime.UtcNow < until) await Task.Delay(10);
        host.Pause();
        long paused = await host.RunExclusiveAsync(s => s.Tick).WaitAsync(Timeout);
        Assert.True(paused >= 20);
        await Task.Delay(200);
        Assert.Equal(paused, await host.RunExclusiveAsync(s => s.Tick).WaitAsync(Timeout));
        Assert.Null(host.Failure);
    }

    [Fact]
    public async Task ResultsDoNotDependOnPlaybackTiming()
    {
        using var host = new SimulationHost(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));
        host.Speed = 37;
        host.Play();
        while (host.Tick < 30) await Task.Delay(5);
        host.Pause();
        var (tick, hash) = await host.RunExclusiveAsync(s => (s.Tick, s.ComputeStateHash())).WaitAsync(Timeout);

        var reference = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
        reference.Step((int)tick);
        Assert.Equal(reference.ComputeStateHash(), hash);
    }

    [Fact]
    public async Task Reset_GoesBackToTickZero()
    {
        using var host = new SimulationHost(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));
        for (int i = 0; i < 5; i++) await host.StepAsync().WaitAsync(Timeout);
        await host.ResetAsync().WaitAsync(Timeout);
        Assert.Equal(0, host.Tick);
        Assert.Equal(0, await host.RunExclusiveAsync(s => s.Scent.Total(0)).WaitAsync(Timeout));
    }

    [Fact]
    public async Task Snapshot_IsPublishedForSelectedField()
    {
        using var host = new SimulationHost(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));
        host.SelectHeatmap(1, LayerId.Nest);
        for (int i = 0; i < 3; i++) await host.StepAsync().WaitAsync(Timeout);

        ScentSnapshot? snapshot = null;
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (host.TryTakeSnapshot(out var s))
            {
                if (s.Tick == 3) { snapshot = s; break; }
                host.Release(s);
            }
            await Task.Delay(5);
        }
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.TypeIndex);
        Assert.Equal(LayerId.Nest, snapshot.Layer);
        var expected = await host.RunExclusiveAsync(s => s.Scent.Field(1, LayerId.Nest).ToArray()).WaitAsync(Timeout);
        Assert.Equal(expected, snapshot.Data);
        Assert.Equal(expected.Max(), snapshot.Max);
        host.Release(snapshot);
    }

    [Fact]
    public async Task InvalidSettings_AreRejected_ValidOnesApplied()
    {
        using var host = new SimulationHost(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));
        Assert.NotEmpty(await host.SetSettingsAsync(ScentSettings.Default.WithTick(1)).WaitAsync(Timeout));
        Assert.Empty(await host.SetSettingsAsync(ScentSettings.Default.WithTick(0.05)).WaitAsync(Timeout));
        await host.StepAsync().WaitAsync(Timeout);
        Assert.Equal(0.05, await host.RunExclusiveAsync(s => s.ElapsedSeconds).WaitAsync(Timeout), 9);
    }
}
