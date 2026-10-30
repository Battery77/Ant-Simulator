using System.Runtime.InteropServices;
using AntSim.Core.Rendering;
using AntSim.Core.Scent;
using AntSim.Core.Simulation;
using AntSim.Core.Util;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

/// <summary>
/// 감사 수정(F01~F07) 전 코드(cc5d416)로 계산해 고정한 기준값. 결함 수정이 정상 범위의 냄새 수학과
/// 히트맵 색상을 바꾸지 않았음을 확인한다. 값이 바뀌면 정상 동작이 달라진 것이므로 원인을 먼저 확인할 것.
/// </summary>
public class AuditGoldenTests
{
    internal static ulong ScentHashAfter50Ticks()
    {
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
        session.Step(50);
        return session.Scent.ComputeHash();
    }

    /// <summary>정상 범위(0, 1e-3 ~ 1e2)의 결정적 농도 필드.</summary>
    internal static float[] NormalField(int n, int salt)
    {
        var field = new float[n];
        for (int i = 0; i < n; i++)
        {
            int k = (i * 7919 + salt * 104729) % 1000;
            field[i] = k < 150 ? 0f : MathF.Pow(10, -3 + 5 * (k / 1000f));
        }
        return field;
    }

    internal static ulong RasterHash(HeatmapScale scale, bool composite)
    {
        var food = NormalField(20000, 1);
        var larva = NormalField(20000, 2);
        var rgba = new byte[food.Length * 4];
        if (composite)
        {
            var palette = CompositeHeatmapPalette.CreateDefault();
            new CompositeHeatmapRasterizer().Rasterize(new[]
            {
                new CompositeChannel(food, HeatmapRasterizer.ComputeMax(food), scale, palette.Get("food")),
                new CompositeChannel(larva, 37.5f, scale, palette.Get("larva")),
            }, rgba);
        }
        else
        {
            HeatmapRasterizer.Rasterize(food, HeatmapRasterizer.ComputeMax(food), scale, rgba);
        }
        var hash = Fnv1a64.Create();
        hash.Add(rgba);
        return hash.Value;
    }

    [Fact]
    public void DefaultScentMath_IsUnchanged()
    {
        Assert.Equal(1813298136883749433UL, ScentHashAfter50Ticks());
    }

    [Theory]
    [InlineData(HeatmapScale.Linear, false, 18134983911294773365UL)]
    [InlineData(HeatmapScale.Log, false, 6404317869119655189UL)]
    [InlineData(HeatmapScale.Linear, true, 17253125131037278429UL)]
    [InlineData(HeatmapScale.Log, true, 10739488175473826197UL)]
    public void NormalRangeHeatmapColors_AreUnchanged(HeatmapScale scale, bool composite, ulong expected)
    {
        Assert.Equal(expected, RasterHash(scale, composite));
    }
}
