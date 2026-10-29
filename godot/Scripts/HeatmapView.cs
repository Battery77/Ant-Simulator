using AntSim.Core.Rendering;
using Godot;

namespace AntSim.App;

/// <summary>
/// 냄새 농도 히트맵 오버레이. 레이어 전체를 텍스처 한 장으로 그린다(1 타일 = 1 픽셀).
/// 단일 모드와 복합 모드가 같은 픽셀 버퍼·Image·텍스처를 재사용하며, 색 계산만 다르다.
/// </summary>
public partial class HeatmapView : Node2D
{
    private ImageTexture? _texture;
    private Image? _image;
    private byte[] _pixels = Array.Empty<byte>();
    private int _width, _height;

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        Visible = false;
    }

    /// <summary>마지막 래스터 변환에 걸린 시간(ms). 성능 표시용.</summary>
    public double LastRasterMilliseconds { get; private set; }

    /// <summary>냄새 한 종류(기존 단일 히트맵).</summary>
    public void ShowField(float[] field, int width, int height, float max, HeatmapScale scale)
    {
        EnsureBuffers(width, height);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        HeatmapRasterizer.Rasterize(field, max, scale, _pixels);
        LastRasterMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Upload();
    }

    /// <summary>여러 냄새를 색으로 합성한 복합 히트맵.</summary>
    public void ShowComposite(ReadOnlySpan<CompositeChannel> channels, int width, int height, CompositeHeatmapRasterizer rasterizer)
    {
        EnsureBuffers(width, height);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        rasterizer.Rasterize(channels, _pixels);
        LastRasterMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Upload();
    }

    private void EnsureBuffers(int width, int height)
    {
        if (width == _width && height == _height && _image is not null) return;
        _width = width;
        _height = height;
        _pixels = new byte[width * height * 4];
        _image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        _texture = null;
    }

    private void Upload()
    {
        _image!.SetData(_width, _height, false, Image.Format.Rgba8, _pixels);
        if (_texture is null) _texture = ImageTexture.CreateFromImage(_image);
        else _texture.Update(_image);
        Visible = true;
        QueueRedraw();
    }

    public void HideHeatmap() => Visible = false;

    public override void _Draw()
    {
        if (_texture is not null) DrawTexture(_texture, Vector2.Zero);
    }
}
