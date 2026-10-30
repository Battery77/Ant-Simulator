using AntSim.Core.Rendering;
using AntSim.Core.Scent;
using AntSim.Core.Simulation;
using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class CompositeHeatmapTests
{
    private static readonly CompositeHeatmapPalette Palette = CompositeHeatmapPalette.CreateDefault();
    private static readonly HeatmapRamp Food = Palette.Get("food");
    private static readonly HeatmapRamp Larva = Palette.Get("larva");

    private static (byte R, byte G, byte B, byte A) Split(uint c) => ((byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24));

    /// <summary>음식·유충 두 채널로 한 픽셀을 합성한다.</summary>
    private static uint Pixel(float food, float larva, float foodMax = 10, float larvaMax = 10,
        HeatmapScale scale = HeatmapScale.Linear, CompositeBlend? blend = null)
    {
        var rasterizer = new CompositeHeatmapRasterizer(blend);
        var channels = new[]
        {
            new CompositeChannel(new[] { food }, foodMax, scale, Food),
            new CompositeChannel(new[] { larva }, larvaMax, scale, Larva),
        };
        var rgba = new byte[4];
        rasterizer.Rasterize(channels, rgba);
        return BitConverter.ToUInt32(rgba);
    }

    // 1. 음식 냄새만
    [Fact]
    public void FoodOnly_IsOrange()
    {
        var (r, g, b, a) = Split(Pixel(10, 0));
        Assert.Equal(Food.ColorAt(255), Pixel(10, 0));
        Assert.True(Split(Pixel(0.5f, 0)).A < Split(Pixel(5, 0)).A, "불투명도는 농도에 따라 커진다"); // 단일 채널이면 그 채널 램프 색 그대로
        Assert.True(r > g && g > b, $"주황 계열이어야 함: {r},{g},{b}");
        Assert.True(a > 200);
    }

    // 2. 유충 냄새만
    [Fact]
    public void LarvaOnly_IsPurple()
    {
        var (r, g, b, _) = Split(Pixel(0, 10));
        Assert.Equal(Larva.ColorAt(255), Pixel(0, 10));
        Assert.True(b > r && r > g, $"보라 계열이어야 함: {r},{g},{b}");
    }

    // 3. 같은 타일에 둘 다
    [Fact]
    public void Overlap_MixesBothColors_WithoutOverflow()
    {
        var (fr, fg, fb, fa) = Split(Pixel(10, 0));
        var (lr, lg, lb, la) = Split(Pixel(0, 10));
        var (r, g, b, a) = Split(Pixel(10, 10));

        // 같은 강도면 두 색의 정확한 중간(가중 평균) — RGB는 두 색 범위 안에 있어 넘치지 않는다.
        Assert.True(Math.Abs(r - (fr + lr) / 2.0) <= 0.5);
        Assert.True(Math.Abs(g - (fg + lg) / 2.0) <= 0.5);
        Assert.True(Math.Abs(b - (fb + lb) / 2.0) <= 0.5);
        Assert.InRange(r, Math.Min(fr, lr), Math.Max(fr, lr));
        Assert.InRange(b, Math.Min(fb, lb), Math.Max(fb, lb));
        // 겹치면 더 불투명해진다: 1 − (1−αf)(1−αl)
        Assert.True(a > fa && a > la);
        Assert.True(Math.Abs(a - 255 * (1 - (1 - fa / 255.0) * (1 - la / 255.0))) <= 1);
    }

    [Fact]
    public void Overlap_ColorFollowsRelativeIntensity()
    {
        var (r1, _, b1, _) = Split(Pixel(10, 3));
        var (r2, _, b2, _) = Split(Pixel(3, 10));
        Assert.True(r1 > r2, "음식이 강하면 더 주황");
        Assert.True(b2 > b1, "유충이 강하면 더 보라");
    }

    // 4. 농도 차이가 매우 클 때
    [Fact]
    public void HugeConcentrationDifference_IndependentAutoMax_ShowsBothEqually()
    {
        // 실제 농도는 10만 배 차이지만 종류별 자동 최대값으로 각각 정규화하면 둘 다 최대 강도로 보인다.
        uint independent = Pixel(1000f, 0.01f, foodMax: 1000f, larvaMax: 0.01f);
        Assert.Equal(Pixel(10, 10), independent);
    }

    [Fact]
    public void HugeConcentrationDifference_SharedMax_WeakScentIsStillVisible()
    {
        // 같은 고정 최대값(1000)을 쓰면 유충 강도는 최소 단계지만 0이 아니므로 색에 남는다(완전히 가려지지 않음).
        uint foodOnly = Pixel(1000f, 0, foodMax: 1000f, larvaMax: 1000f);
        uint both = Pixel(1000f, 0.01f, foodMax: 1000f, larvaMax: 1000f);
        Assert.NotEqual(foodOnly, both);
        Assert.True(Split(both).B > Split(foodOnly).B);
    }

    [Fact]
    public void WeightExponent_KeepsWeakChannelVisibleBeyondItsLinearShare()
    {
        // 강도 1 : 0.1 → γ=1이면 유충 비중 1/11, γ=0.5면 약 0.24.
        var linear = Split(Pixel(10, 1, blend: new CompositeBlend(WeightExponent: 1f)));
        var soft = Split(Pixel(10, 1));
        Assert.True(soft.B > linear.B, "γ=0.5에서 약한 유충 색이 더 드러나야 함");
        Assert.True(soft.R < linear.R);
    }

    // 5. 모두 0
    [Theory]
    [InlineData(0f, 0f, 10f, 10f)]
    [InlineData(-1f, 0f, 10f, 10f)]
    [InlineData(5f, 5f, 0f, 0f)] // 최대값 0(냄새 없음) → 기여 없음
    public void NoScent_IsTransparent(float food, float larva, float foodMax, float larvaMax)
    {
        Assert.Equal(0u, Pixel(food, larva, foodMax, larvaMax));
    }

    [Fact]
    public void ZeroChannel_DoesNotContribute()
    {
        Assert.Equal(Pixel(7, 0), Pixel(7, 0, larvaMax: 0));
        Assert.Equal(Food.ColorAt(HeatmapRasterizer.Quantize(7, 10, HeatmapScale.Linear)), Pixel(7, 0));
    }

    [Fact]
    public void LogScale_AppliesPerChannel()
    {
        var linear = Split(Pixel(1, 0, foodMax: 100));
        var log = Split(Pixel(1, 0, foodMax: 100, scale: HeatmapScale.Log));
        Assert.True(log.A > linear.A, "로그 스케일은 작은 농도를 더 진하게 보인다");
        Assert.Equal(Food.ColorAt(HeatmapRasterizer.Quantize(1, 100, HeatmapScale.Log)), Pixel(1, 0, foodMax: 100, scale: HeatmapScale.Log));
    }

    [Fact]
    public void Quantizer_MatchesNormalizeWithinOneStep_AndSingleAndCompositeAgree()
    {
        foreach (var scale in new[] { HeatmapScale.Linear, HeatmapScale.Log })
        {
            var quantizer = new HeatmapQuantizer(37.5f, scale);
            for (float v = 0.001f; v < 60; v *= 1.37f)
            {
                int q = quantizer.Quantize(v);
                int reference = HeatmapRasterizer.QuantizeNormalized(HeatmapRasterizer.Normalize(v, 37.5f, scale));
                Assert.InRange(q, reference - 1, reference + 1);
                Assert.InRange(q, 1, 255);
            }
            Assert.Equal(255, quantizer.Quantize(1e9f));
            Assert.Equal(0, quantizer.Quantize(0));
            Assert.True(new HeatmapQuantizer(0, scale).IsEmpty);
        }

        // 단일 히트맵과 복합(한 채널만 있는 칸)은 같은 양자화 단계를 쓴다.
        var field = new[] { 0.3f, 2f, 9.9f, 15f };
        var single = new byte[16];
        HeatmapRasterizer.Rasterize(field, 10, HeatmapScale.Log, single);
        var composite = new byte[16];
        new CompositeHeatmapRasterizer().Rasterize(new[] { new CompositeChannel(field, 10, HeatmapScale.Log, Food) }, composite);
        for (int i = 0; i < field.Length; i++)
        {
            int q = HeatmapRasterizer.Quantize(field[i], 10, HeatmapScale.Log);
            Assert.Equal(HeatmapRasterizer.LutColor(q / 255f), BitConverter.ToUInt32(single, i * 4));
            Assert.Equal(Food.ColorAt(q), BitConverter.ToUInt32(composite, i * 4));
        }
    }

    [Fact]
    public void BulkQuantize_MatchesScalarExactly_IncludingEdgeValues()
    {
        var values = new float[1000];
        for (int i = 0; i < values.Length; i++) values[i] = (i % 13) switch
        {
            0 => 0f,
            1 => -3f,
            2 => float.NaN,
            3 => float.PositiveInfinity,
            4 => 1e-30f,
            5 => 37.5f,          // = 최대값
            6 => 1000f,          // 최대값 초과
            _ => i * 0.0471f,
        };
        foreach (var scale in new[] { HeatmapScale.Linear, HeatmapScale.Log })
        {
            var quantizer = new HeatmapQuantizer(37.5f, scale);
            var bulk = new byte[values.Length];
            quantizer.QuantizeTo(values, bulk);
            for (int i = 0; i < values.Length; i++) Assert.Equal(quantizer.Quantize(values[i]), bulk[i]);
        }
    }

    [Fact]
    public void LargeField_CompositeMatchesPerPixelComposition()
    {
        // 블록 경계(4096)와 SIMD 꼬리 처리를 넘는 크기로, 전체 래스터 결과가 칸별 계산과 같은지 확인한다.
        int n = 4096 * 2 + 37;
        var food = new float[n];
        var larva = new float[n];
        for (int i = 0; i < n; i++)
        {
            food[i] = i % 3 == 0 ? 0 : (i % 101) * 0.1f;
            larva[i] = i % 5 == 0 ? 0 : (i % 37) * 0.27f;
        }
        var channels = new[]
        {
            new CompositeChannel(food, HeatmapRasterizer.ComputeMax(food), HeatmapScale.Linear, Food),
            new CompositeChannel(larva, 5f, HeatmapScale.Log, Larva),
        };
        var rasterizer = new CompositeHeatmapRasterizer();
        var rgba = new byte[n * 4];
        rasterizer.Rasterize(channels, rgba);
        for (int i = 0; i < n; i += 97)
            Assert.Equal(rasterizer.ComposePixel(new[] { food[i], larva[i] }, channels), BitConverter.ToUInt32(rgba, i * 4));
    }

    [Fact]
    public void AdditiveMode_ClampsInsteadOfOverflowing()
    {
        var blend = new CompositeBlend(CompositeBlendMode.AdditiveClamped);
        var (r, g, b, a) = Split(Pixel(10, 10, blend: blend));
        Assert.True(r <= 255 && g <= 255 && b <= 255 && a <= 255);
        Assert.Equal(255, r); // 주황 R + 보라 R → 포화(가중 평균을 기본으로 쓰는 이유)
    }

    [Fact]
    public void Rasterize_DoesNotModifyConcentrationData()
    {
        var food = new[] { 0f, 1f, 5f, 10f, 0.001f, 3f };
        var larva = new[] { 2f, 0f, 5f, 0.5f, 9f, 3f };
        var foodCopy = (float[])food.Clone();
        var larvaCopy = (float[])larva.Clone();
        var rgba = new byte[food.Length * 4];
        new CompositeHeatmapRasterizer().Rasterize(new[]
        {
            new CompositeChannel(food, HeatmapRasterizer.ComputeMax(food), HeatmapScale.Log, Food),
            new CompositeChannel(larva, HeatmapRasterizer.ComputeMax(larva), HeatmapScale.Log, Larva),
        }, rgba);
        HeatmapRasterizer.Rasterize(food, 10, HeatmapScale.Linear, rgba);
        Assert.Equal(foodCopy, food);
        Assert.Equal(larvaCopy, larva);
    }

    [Fact]
    public void InvalidInputs_AreRejected()
    {
        var r = new CompositeHeatmapRasterizer();
        Assert.Throws<ArgumentException>(() => r.Rasterize(ReadOnlySpan<CompositeChannel>.Empty, new byte[4]));
        Assert.Throws<ArgumentException>(() => r.Rasterize(new[]
        {
            new CompositeChannel(new float[2], 1, HeatmapScale.Linear, Food),
            new CompositeChannel(new float[3], 1, HeatmapScale.Linear, Larva),
        }, new byte[12]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompositeHeatmapRasterizer(new CompositeBlend(WeightExponent: 0)));
    }

    [Fact]
    public void Palette_DefaultsAndExtension()
    {
        Assert.Equal(CompositeHeatmapPalette.FoodOrange, Food.Color);
        Assert.Equal(CompositeHeatmapPalette.LarvaPurple, Larva.Color);
        var palette = CompositeHeatmapPalette.CreateDefault();
        var alarm = palette.Get("alarm");   // 새 페로몬: 예비 색 자동 배정
        var trail = palette.Get("trail");
        Assert.NotEqual(alarm.Color, trail.Color);
        Assert.Same(alarm, palette.Get("alarm"));
        palette.Set("food", TilePalette.Rgba(0, 255, 0));
        Assert.Equal(TilePalette.Rgba(0, 255, 0), palette.Get("food").Color);
    }

    [Fact]
    public void ThreeChannels_AreSupported()
    {
        var palette = CompositeHeatmapPalette.CreateDefault();
        var channels = new[]
        {
            new CompositeChannel(new[] { 5f }, 10, HeatmapScale.Linear, palette.Get("food")),
            new CompositeChannel(new[] { 5f }, 10, HeatmapScale.Linear, palette.Get("larva")),
            new CompositeChannel(new[] { 5f }, 10, HeatmapScale.Linear, palette.Get("alarm")),
        };
        var rgba = new byte[4];
        new CompositeHeatmapRasterizer().Rasterize(channels, rgba);
        Assert.True(rgba[3] > 0);
    }
}

/// <summary>호스트 스냅샷: 단일/복합/숨김 전환, 데이터 불변, 레이어 전환.</summary>
public class HeatmapSelectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<ScentSnapshot> WaitSnapshot(SimulationHost host, Func<ScentSnapshot, bool> match)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (host.TryTakeSnapshot(out var s))
            {
                if (match(s)) return s;
                host.Release(s);
            }
            await Task.Delay(5);
        }
        throw new TimeoutException("스냅샷을 받지 못했습니다.");
    }

    private static SimulationHost NewHost(int steps)
    {
        var session = new SimulationSession(SimulationSessionTests.SampleWorld(), ScentSettings.Default);
        session.Step(steps);
        return new SimulationHost(session);
    }

    // 6. 단일 ↔ 복합 ↔ 숨김 전환 + 7. 실제 농도 불변
    [Fact]
    public async Task SwitchingModes_ChangesOnlyPublishedFields_NotSimulationState()
    {
        using var host = NewHost(20);
        ulong before = await host.RunExclusiveAsync(s => s.ComputeStateHash()).WaitAsync(Timeout);

        host.SelectHeatmap(0, LayerId.Surface);
        var single = await WaitSnapshot(host, s => s.Types.Count == 1);
        Assert.Equal(new[] { 0 }, single.Types);
        Assert.False(single.Contains(1));
        host.Release(single);

        host.SelectHeatmap(new[] { 0, 1 }, LayerId.Surface);
        var composite = await WaitSnapshot(host, s => s.Types.Count == 2);
        Assert.Equal(new[] { 0, 1 }, composite.Types);

        // 스냅샷은 복사본이다: 렌더러가 값을 바꿔도 시뮬레이션은 그대로.
        Array.Fill(composite.Field(0), 12345f);
        host.Release(composite);

        host.SelectHeatmap(Array.Empty<int>(), LayerId.Surface);
        var hidden = await WaitSnapshot(host, s => s.Types.Count == 0);
        Assert.Equal(-1, hidden.TypeIndex);
        host.Release(hidden);

        Assert.Equal(before, await host.RunExclusiveAsync(s => s.ComputeStateHash()).WaitAsync(Timeout));
        Assert.Equal(20, host.Tick);
    }

    [Fact]
    public async Task CompositeSnapshot_MatchesActualFieldsAndMaxes()
    {
        using var host = NewHost(30);
        host.SelectHeatmap(new[] { 0, 1 }, LayerId.Nest);
        var snapshot = await WaitSnapshot(host, s => s.Types.Count == 2 && s.Layer == LayerId.Nest);
        var (food, larva) = await host.RunExclusiveAsync(s =>
            (s.Scent.Field(0, LayerId.Nest).ToArray(), s.Scent.Field(1, LayerId.Nest).ToArray())).WaitAsync(Timeout);
        Assert.Equal(food, snapshot.Field(0));
        Assert.Equal(larva, snapshot.Field(1));
        Assert.Equal(food.Max(), snapshot.MaxOf(0));
        Assert.Equal(larva.Max(), snapshot.MaxOf(1));
        Assert.True(snapshot.MaxOf(1) > 0, "개미집 유충 발생원이 있어야 함");
        host.Release(snapshot);
    }

    // 8. 레이어 전환
    [Fact]
    public async Task LayerSwitch_PublishesTheRequestedLayer()
    {
        using var host = NewHost(30);
        host.SelectHeatmap(new[] { 0, 1 }, LayerId.Surface);
        var surface = await WaitSnapshot(host, s => s.Layer == LayerId.Surface && s.Types.Count == 2);
        float[] surfaceLarva = (float[])surface.Field(1).Clone();
        host.Release(surface);

        host.SelectHeatmap(new[] { 0, 1 }, LayerId.Nest);
        var nest = await WaitSnapshot(host, s => s.Layer == LayerId.Nest && s.Types.Count == 2);
        var expected = await host.RunExclusiveAsync(s => s.Scent.Field(1, LayerId.Nest).ToArray()).WaitAsync(Timeout);
        Assert.Equal(expected, nest.Field(1));
        Assert.NotEqual(surfaceLarva, nest.Field(1));
        host.Release(nest);
    }

    [Fact]
    public async Task OutOfRangeTypes_AreIgnored()
    {
        using var host = NewHost(1);
        host.SelectHeatmap(new[] { 1, 7, -1 }, LayerId.Surface);
        var snapshot = await WaitSnapshot(host, s => s.Types.Count == 1 && s.TypeIndex == 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Field(0));
        host.Release(snapshot);
    }

    [Fact]
    public async Task Playing_ThrottlesPublishing_ButPausePublishesFinalState()
    {
        using var host = NewHost(0);
        host.SelectHeatmap(new[] { 0, 1 }, LayerId.Surface);
        host.Speed = 50;
        host.Play();
        int published = 0;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(0.5);
        while (DateTime.UtcNow < until)
        {
            if (host.TryTakeSnapshot(out var s))
            {
                published++;
                host.Release(s);
            }
            await Task.Delay(1);
        }
        host.Pause();
        long ticks = await host.RunExclusiveAsync(s => s.Tick).WaitAsync(Timeout);
        var last = await WaitSnapshot(host, s => s.Tick == ticks);
        host.Release(last);
        // 0.5초 동안 약 30Hz 이하로 게시(여유를 둔 상한)
        Assert.InRange(published, 1, 30);
    }
}
