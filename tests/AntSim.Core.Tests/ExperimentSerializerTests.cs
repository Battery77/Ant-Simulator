using AntSim.Core.Scent;
using AntSim.Core.Serialization;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class ExperimentSerializerTests
{
    private static byte[] Save(SimulationSession session)
    {
        using var stream = new MemoryStream();
        ExperimentSerializer.Save(session, stream);
        return stream.ToArray();
    }

    private static SimulationSession Load(byte[] bytes) => ExperimentSerializer.Load(new MemoryStream(bytes));

    [Fact]
    public void RoundTrip_PreservesEverythingBitExactly()
    {
        var settings = ScentSettings.Default.WithType(1, ScentSettings.Default.Types[1] with { Decay = 0.1, Emission = 2.5 });
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), settings);
        session.Step(23);

        var loaded = Load(Save(session));
        Assert.Equal(session.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Equal(23, loaded.Tick);
        Assert.Equal(session.ElapsedSeconds, loaded.ElapsedSeconds);
        Assert.Equal(session.Settings.Types, loaded.Settings.Types);
        Assert.Equal(session.InitialWorld.ComputeContentHash(), loaded.InitialWorld.ComputeContentHash());
    }

    [Fact]
    public void ResumedExperiment_MatchesUninterruptedRun()
    {
        var uninterrupted = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
        uninterrupted.Step(40);

        var first = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
        first.Step(17);
        var resumed = Load(Save(first));
        resumed.Step(23);

        Assert.Equal(uninterrupted.ComputeStateHash(), resumed.ComputeStateHash());
    }

    [Fact]
    public void LoadedExperiment_CanResetToItsStart()
    {
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
        ulong start = session.ComputeStateHash();
        session.Step(10);
        var loaded = Load(Save(session));
        loaded.Reset();
        Assert.Equal(start, loaded.ComputeStateHash());
    }

    [Fact]
    public void MapFile_IsRejectedWithClearMessage_AndViceVersa()
    {
        using var map = new MemoryStream();
        MapSerializer.Save(SimulationSessionTests.SampleWorld(), map);
        var e = Assert.Throws<MapFormatException>(() => Load(map.ToArray()));
        Assert.Contains("맵 파일", e.Message);

        var experiment = Save(new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default));
        Assert.Throws<MapFormatException>(() => MapSerializer.Load(new MemoryStream(experiment)));
    }

    [Fact]
    public void Corruption_TruncationAndNewerVersion_AreRejected()
    {
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
        session.Step(3);
        var bytes = Save(session);

        var corrupted = (byte[])bytes.Clone();
        corrupted[^20] ^= 0x40; // 냄새 데이터 영역
        Assert.Throws<MapFormatException>(() => Load(corrupted));

        Assert.Throws<MapFormatException>(() => Load(bytes[..^9]));

        var newer = (byte[])bytes.Clone();
        newer[8] = (byte)(ExperimentSerializer.CurrentVersion + 1);
        Assert.Contains("버전", Assert.Throws<MapFormatException>(() => Load(newer)).Message);
    }

    [Fact]
    public void FileRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"exp-{Guid.NewGuid():N}.antexp");
        try
        {
            var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
            session.Step(5);
            ExperimentSerializer.Save(session, path);
            Assert.Equal(session.ComputeStateHash(), ExperimentSerializer.Load(path).ComputeStateHash());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
