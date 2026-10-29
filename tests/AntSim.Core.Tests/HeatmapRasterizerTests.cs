using AntSim.Core.Rendering;

namespace AntSim.Core.Tests;

public class HeatmapRasterizerTests
{
    [Theory]
    [InlineData(0f, 10f, 0f)]
    [InlineData(-1f, 10f, 0f)]
    [InlineData(5f, 10f, 0.5f)]
    [InlineData(10f, 10f, 1f)]
    [InlineData(20f, 10f, 1f)] // 고정 최대값 초과는 1로 고정
    [InlineData(5f, 0f, 0f)]
    public void Linear_Normalization(float value, float max, float expected)
    {
        Near.Equal(expected, HeatmapRasterizer.Normalize(value, max, HeatmapScale.Linear), 6);
    }

    [Fact]
    public void Log_Normalization_EmphasizesSmallValues()
    {
        float linear = HeatmapRasterizer.Normalize(1, 100, HeatmapScale.Linear);
        float log = HeatmapRasterizer.Normalize(1, 100, HeatmapScale.Log);
        Near.Equal(MathF.Log(2) / MathF.Log(101), log, 6);
        Assert.True(log > linear);
        Near.Equal(1f, HeatmapRasterizer.Normalize(100, 100, HeatmapScale.Log), 6);
    }

    [Fact]
    public void AutoMax_AndPixels()
    {
        var field = new[] { 0f, 2f, 8f, 4f };
        float max = HeatmapRasterizer.ComputeMax(field);
        Assert.Equal(8f, max);
        var rgba = new byte[16];
        HeatmapRasterizer.Rasterize(field, max, HeatmapScale.Linear, rgba);
        Assert.Equal(0, rgba[3]);                 // 0 → 투명
        Assert.True(rgba[11] > rgba[7]);          // 클수록 불투명
        Assert.Equal(HeatmapRasterizer.Color(1f), BitConverter.ToUInt32(rgba, 8));
        Assert.Equal(HeatmapRasterizer.LutColor(0.5f), BitConverter.ToUInt32(rgba, 12));
    }

    [Fact]
    public void TinyPositiveValues_AreVisible_AndLogPathMatchesLut()
    {
        var field = new[] { 1e-6f, 100f };
        var rgba = new byte[8];
        HeatmapRasterizer.Rasterize(field, 100, HeatmapScale.Linear, rgba);
        Assert.NotEqual(0, rgba[3]);
        HeatmapRasterizer.Rasterize(field, 100, HeatmapScale.Log, rgba);
        Assert.Equal(HeatmapRasterizer.LutColor(HeatmapRasterizer.Normalize(1e-6f, 100, HeatmapScale.Log)), BitConverter.ToUInt32(rgba, 0));
    }
}
