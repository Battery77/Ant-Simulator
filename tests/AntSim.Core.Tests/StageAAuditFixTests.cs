using System.Buffers.Binary;
using AntSim.Core.Ants;
using AntSim.Core.Editing;
using AntSim.Core.Scent;
using AntSim.Core.Serialization;
using AntSim.Core.Simulation;
using AntSim.Core.Util;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

// PR #3 단계 A 독립 감사(6b10ef0) 결함 SA01·SA02·SA03의 회귀 테스트. 모두 올바른 계약을 기대한다.

internal static class AuditStageA
{
    public static World Open(int size = 32) => new(new WorldConfig(size, size));

    public static World Nest()
    {
        var world = WorldPresets.StandardNest(new WorldConfig(64, 64));
        new MapEditor(world).Apply(EditOp.AddPassage(20, 20));
        return world;
    }

    public static byte[] Save(SimulationSession session)
    {
        using var stream = new MemoryStream();
        ExperimentSerializer.Save(session, stream);
        return stream.ToArray();
    }

    public static void Rechecksum(byte[] bytes)
    {
        var fnv = Fnv1a64.Create();
        fnv.Add(bytes.AsSpan(0, bytes.Length - 8));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8), fnv.Value);
    }

    public static string TempFile() => Path.Combine(Path.GetTempPath(), $"antsim-audit-{Guid.NewGuid():N}.antexp");
}

/// <summary>SA01: 속도 구간 수 상한이 검증·저장·불러오기에서 같다(저장할 수 있으면 불러올 수 있다).</summary>
public class AuditSA01SpeedTierLimitTests
{
    private static SpeedTier[] Tiers(int count) => Enumerable.Range(0, count).Select(i => new SpeedTier(i, 1f)).ToArray();

    [Fact]
    public void Tiers256_ValidateAndRoundTrip()
    {
        var settings = AntSettings.Default with { InitialCount = 0, QuantitySpeedTiers = Tiers(256) };
        Assert.Empty(settings.Validate());
        var session = new SimulationSession(AuditStageA.Open(), ScentSettings.Default, settings);
        var loaded = ExperimentSerializer.Load(new MemoryStream(AuditStageA.Save(session)));
        Assert.Equal(256, loaded.AntSettings.QuantitySpeedTiers.Count);
        Assert.Equal(settings.QuantitySpeedTiers, loaded.AntSettings.QuantitySpeedTiers);
        Assert.Equal(session.ComputeStateHash(), loaded.ComputeStateHash());
    }

    /// <summary>감사 반례: 257개 구간. 검증에서 거부되어 세션을 만들 수 없고, 따라서 기존 정상 파일을 덮어쓸 수 없다.</summary>
    [Fact]
    public void Tiers257_RejectedBeforeAnyWrite_ExistingFileKept()
    {
        string path = AuditStageA.TempFile();
        try
        {
            var good = new SimulationSession(AuditStageA.Open(), ScentSettings.Default, AntSettings.Default with { InitialCount = 3 });
            ExperimentSerializer.Save(good, path);
            byte[] before = File.ReadAllBytes(path);

            var settings = AntSettings.Default with { InitialCount = 0, QuantitySpeedTiers = Tiers(257) };
            Assert.Contains(settings.Validate(), e => e.Contains("속도 구간"));
            Assert.Throws<ArgumentException>(() =>
            {
                var bad = new SimulationSession(AuditStageA.Open(), ScentSettings.Default, settings);
                ExperimentSerializer.Save(bad, path);
            });
            // 정상 세션에 257개 구간으로 초기화하려 해도 거부되고 상태는 그대로다.
            ulong hash = good.ComputeStateHash();
            Assert.Throws<ArgumentException>(() => good.Reset(settings));
            Assert.Equal(hash, good.ComputeStateHash());

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(3, ExperimentSerializer.Load(path).Ants.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultSettings_StillRoundTrip()
    {
        var session = new SimulationSession(AuditStageA.Nest(), ScentSettings.Default, AntSettings.Default with { InitialCount = 20 });
        session.Step(5);
        var loaded = ExperimentSerializer.Load(new MemoryStream(AuditStageA.Save(session)));
        Assert.Equal(session.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Equal(AntSettings.DefaultQuantitySpeedTiers, loaded.AntSettings.QuantitySpeedTiers);
    }
}

/// <summary>SA02: 속도 배율 갱신이 float 반올림으로 0(또는 구간 밖)이 되지 않는다.</summary>
public class AuditSA02SpeedFactorTests
{
    /// <summary>
    /// 감사 반례(평활화 0) + 자체 검증에서 찾은 같은 원인의 경우(평활화 0.001초: 1 − exp(−dt/T)가 float로 정확히 1이 됨).
    /// 1 → 1e-8로 바뀔 때 결과는 정확히 1e-8이어야 하고, 저장·불러오기가 되어야 한다.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    public void ImmediateSlowdown_ToTinyFactor_IsExact_AndSaveable(float smoothingSeconds)
    {
        var settings = AntSettings.Default with
        {
            InitialCount = 0, SpeedFactorSmoothingSeconds = smoothingSeconds,
            QuantitySpeedTiers = new[] { new SpeedTier(0, 1f), new SpeedTier(1, 1e-8f) },
        };
        Assert.Empty(settings.Validate());
        var session = new SimulationSession(AuditStageA.Open(16), ScentSettings.Default, settings);
        Assert.True(AntSpawner.TrySpawn(session.Ants, session.World, settings, LayerId.Surface, 5.5f, 5.5f, out _, heading: 0));
        Assert.Equal(1f, session.Ants.SpeedFactor[0]);
        new MapEditor(session.World).Apply(EditOp.SetQuantity(LayerId.Surface, 5, 5, QuantityKind.Food, 1));

        session.Step();
        Assert.Equal(1e-8f, session.Ants.SpeedFactor[0]);
        var loaded = ExperimentSerializer.Load(new MemoryStream(AuditStageA.Save(session)));
        Assert.Equal(session.ComputeStateHash(), loaded.ComputeStateHash());

        // 다시 수량 0 타일로 돌아가면 1로 복귀(즉시 경로는 목표값 그대로).
        new MapEditor(session.World).Apply(EditOp.SetQuantity(LayerId.Surface, 5, 5, QuantityKind.Food, 0));
        session.Step();
        if (smoothingSeconds == 0) Assert.Equal(1f, session.Ants.SpeedFactor[0]);
        Assert.True(session.Ants.SpeedFactor[0] > 0 && session.Ants.SpeedFactor[0] <= 1);
    }

    /// <summary>일반 평활화: 여러 평활화 시간·틱·구간 배율에서 배율은 항상 직전 값과 목표 사이(양수, 1 이하)이고 저장 가능하다.</summary>
    [Fact]
    public void Smoothing_StaysBetweenPreviousAndTarget()
    {
        var rng = new Random(11);
        foreach (float smoothing in new[] { 0f, 1e-30f, 1e-6f, 0.001f, 0.05f, 0.15f, 1f, 100f })
        foreach (double dt in new[] { 0.001, 0.1, 1, 10 })
        {
            float low = new[] { 1e-8f, 1e-3f, 0.5f, 0.85f }[rng.Next(4)];
            var settings = AntSettings.Default with
            {
                InitialCount = 0, SpeedFactorSmoothingSeconds = smoothing,
                QuantitySpeedTiers = new[] { new SpeedTier(0, 1f), new SpeedTier(1, low) },
            };
            var scent = ScentSettings.Default with { TickSeconds = dt, Types = ScentSettings.Default.Types.Select(t => t with { Diffusion = 0.01, PassageDiffusion = 0.01 }).ToArray() };
            var session = new SimulationSession(AuditStageA.Open(16), scent, settings);
            AntSpawner.TrySpawn(session.Ants, session.World, settings, LayerId.Surface, 5.5f, 5.5f, out _, heading: 0, baseSpeed: 1e-6f);
            for (int tick = 0; tick < 6; tick++)
            {
                int quantity = tick % 3 == 2 ? 0 : 1;
                new MapEditor(session.World).Apply(EditOp.SetQuantity(LayerId.Surface, 5, 5, QuantityKind.Food, quantity));
                float before = session.Ants.SpeedFactor[0], target = settings.SpeedFactorFor(quantity);
                session.Step();
                float after = session.Ants.SpeedFactor[0];
                Assert.True(after >= MathF.Min(before, target) && after <= MathF.Max(before, target),
                    $"T={smoothing} dt={dt} {before:R} → {after:R} (목표 {target:R})");
                Assert.True(after > 0);
            }
            ExperimentSerializer.ValidateForSave(session);
        }
    }
}

/// <summary>SA03: 검증을 통과한 설정은 생성·틱 계산에서 비정상 값·예외·부분 틱을 만들지 않는다.</summary>
public class AuditSA03SupportedRangeTests
{
    /// <summary>A: 무한대 방향 변경 간격은 상태 변경 전에 거부된다(생성·초기화 모두).</summary>
    [Fact]
    public void InfiniteTurnInterval_IsRejectedBeforeStateChange()
    {
        var bad = AntSettings.Default with { InitialCount = 5, TurnIntervalMax = float.PositiveInfinity };
        Assert.NotEmpty(bad.Validate());
        Assert.Throws<ArgumentException>(() => new SimulationSession(AuditStageA.Nest(), ScentSettings.Default, bad));

        var session = new SimulationSession(AuditStageA.Nest(), ScentSettings.Default, AntSettings.Default with { InitialCount = 5 });
        session.Step(3);
        ulong hash = session.ComputeStateHash();
        Assert.Throws<ArgumentException>(() => session.Reset(bad));
        Assert.Equal(hash, session.ComputeStateHash());
        Assert.Equal(3, session.Tick);
    }

    /// <summary>
    /// B: 계산 중 넘침(1e38 타일/초 × 10초 = ∞)이 나는 속도는 검증에서 거부된다.
    /// 자체 검증: 수정 전에는 방향이 정확히 0이면 Math.Sign(NaN) 예외(냄새 갱신 뒤), 그 외에는 개미가 전혀 움직이지 않았다.
    /// </summary>
    [Fact]
    public void OverflowingSpeed_IsRejected_AndSupportedMaximumStaysFinite()
    {
        Assert.NotEmpty((AntSettings.Default with { BaseSpeed = 1e38f }).Validate());
        Assert.NotEmpty((AntSettings.Default with { BaseSize = 1e38f }).Validate());
        Assert.NotEmpty((AntSettings.Default with { TurnIntervalMin = 1e38f, TurnIntervalMax = 3e38f }).Validate());

        // 개별 지정(TrySpawn)도 같은 한도: 넘침이 나는 속도·크기는 생성하지 않는다.
        var world = AuditStageA.Open(64);
        var ants = new AntPopulation();
        Assert.False(AntSpawner.TrySpawn(ants, world, AntSettings.Default, LayerId.Surface, 30.5f, 30.5f, out _, baseSpeed: 1e38f));
        Assert.False(AntSpawner.TrySpawn(ants, world, AntSettings.Default, LayerId.Surface, 30.5f, 30.5f, out _, size: float.PositiveInfinity));
        Assert.Equal(0, ants.Count);
    }

    /// <summary>B 대조: 지원하는 최대 속도·최대 틱 간격·최대 편차에서 방향 0 포함 여러 방향으로 진행해도 상태가 유한하고 저장 가능하다.</summary>
    [Fact]
    public void FastestSupportedAnts_AtLongestTick_StayFiniteAndSaveable()
    {
        var settings = AntSettings.Default with
        {
            InitialCount = 0, BaseSpeed = 1000f, SpeedVariation = 0.999f, BaseSize = 1000f, SizeVariation = 0.999f,
            TurnIntervalMin = 1e6f, TurnIntervalMax = 1e6f, TurnIntervalVariation = 0.999f,
        };
        Assert.Empty(settings.Validate());
        var scent = ScentSettings.Default with { TickSeconds = 10, Types = ScentSettings.Default.Types.Select(t => t with { Diffusion = 0.01, PassageDiffusion = 0.01 }).ToArray() };
        var session = new SimulationSession(AuditStageA.Open(64), scent, settings);
        foreach (float heading in new[] { 0f, MathF.PI / 2, MathF.PI, -MathF.PI / 2, 0.7f })
            Assert.True(AntSpawner.TrySpawn(session.Ants, session.World, settings, LayerId.Surface, 30.5f, 30.5f, out _, heading: heading,
                baseSpeed: 1999f, size: 1999f));
        session.Step(5);
        Assert.Equal(5, session.Tick);
        foreach (float v in session.Ants.X.ToArray().Concat(session.Ants.Y.ToArray())) Assert.True(float.IsFinite(v));
        var loaded = ExperimentSerializer.Load(new MemoryStream(AuditStageA.Save(session)));
        Assert.Equal(session.ComputeStateHash(), loaded.ComputeStateHash());
    }

    /// <summary>
    /// C: double 틱 간격이 float로 0이 되면(1e-50) 개미가 있는 실험은 상태 변경 전에 거부된다. 개미 없는 냄새 실험(PR #2)은 그대로 허용.
    /// 자체 검증: 수정 전에는 발생원이 있고 e·dt가 float로 0이 아니면 냄새만 바뀐 채 예외가 났다(틱 그대로).
    /// </summary>
    [Fact]
    public void TickTooSmallForAnts_RejectedBeforeAnyChange_ScentOnlyStillAllowed()
    {
        var tiny = ScentSettings.Default with { TickSeconds = 1e-50, Types = ScentSettings.Default.Types.Select(t => t with { Emission = 1e6 }).ToArray() };
        Assert.Empty(tiny.Validate()); // 냄새 설정 자체의 지원 범위는 그대로

        var world = AuditStageA.Nest();
        new MapEditor(world).Apply(EditOp.SetQuantity(LayerId.Surface, 10, 10, QuantityKind.Food, 10));

        // 개미 없는 냄새 실험: 기존과 같이 동작
        var scentOnly = new SimulationSession(world, tiny);
        scentOnly.Step();
        Assert.Equal(1, scentOnly.Tick);
        Assert.True(scentOnly.Scent.Get(0, LayerId.Surface, 10, 10) > 0);

        // 개미가 있는 실험: 만들 때 거부
        Assert.Throws<ArgumentException>(() => new SimulationSession(world, tiny, AntSettings.Default with { InitialCount = 5 }));

        // 실행 중 설정 변경: 개미가 있으면 거부, 상태·설정 그대로
        var session = new SimulationSession(world, ScentSettings.Default with { Types = tiny.Types }, AntSettings.Default with { InitialCount = 5 });
        session.Step(2);
        ulong hash = session.ComputeStateHash();
        Assert.NotEmpty(session.TrySetSettings(tiny));
        Assert.Equal(0.1, session.Settings.TickSeconds);
        session.Step();
        Assert.Equal(3, session.Tick);

        // 개미 없는 실험을 그 틱 간격으로 실행 중 개미 설정으로 초기화: 거부, 상태 그대로
        hash = scentOnly.ComputeStateHash();
        Assert.Throws<ArgumentException>(() => scentOnly.Reset(AntSettings.Default with { InitialCount = 5 }));
        Assert.Equal(hash, scentOnly.ComputeStateHash());
        Assert.Equal(1, scentOnly.Tick);
        scentOnly.Reset(AntSettings.None); // 개미 0이면 허용
        Assert.Equal(0, scentOnly.Ants.Count);
    }

    /// <summary>C: 개미가 있는 v2 파일의 틱 간격이 개미 계산 범위 밖이면 불러오기에서 거부한다(냄새만 바뀌는 부분 틱 방지).</summary>
    [Fact]
    public void V2FileWithAnts_AndTickTooSmall_IsRejectedOnLoad()
    {
        var session = new SimulationSession(AuditStageA.Nest(), ScentSettings.Default, AntSettings.Default with { InitialCount = 4 });
        var bytes = AuditStageA.Save(session);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(28), 1e-50); // magic 8 + version 2 + flags 2 + tick 8 + elapsed 8 = 28
        AuditStageA.Rechecksum(bytes);
        Assert.Throws<MapFormatException>(() => ExperimentSerializer.Load(new MemoryStream(bytes)));

        var noAnts = new SimulationSession(AuditStageA.Nest(), ScentSettings.Default, AntSettings.None);
        bytes = AuditStageA.Save(noAnts);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(28), 1e-50);
        AuditStageA.Rechecksum(bytes);
        Assert.Equal(1e-50, ExperimentSerializer.Load(new MemoryStream(bytes)).Settings.TickSeconds); // 개미 없으면 허용
    }

    /// <summary>계산 스레드 경로: 거부된 설정 변경 뒤에도 호스트는 정상 동작한다.</summary>
    [Fact]
    public async Task Host_RejectsTinyTickWithAnts_AndKeepsRunning()
    {
        using var host = new SimulationHost(new SimulationSession(AuditStageA.Nest(), ScentSettings.Default, AntSettings.Default with { InitialCount = 5 }));
        var errors = await host.SetSettingsAsync(ScentSettings.Default.WithTick(1e-50)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(errors);
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, host.Tick);
        Assert.Null(host.Failure);
    }
}
