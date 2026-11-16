using AntSim.Core.Rendering;
using Godot;

namespace AntSim.App;

/// <summary>
/// 냄새 농도 히트맵 오버레이. 레이어 전체를 텍스처 한 장으로 그린다(필드 값 1개 = 1 픽셀, 화면에서 묶음 크기만큼 확대).
/// 지상은 묶음 격자(4×4칸 = 1픽셀)라 선형 필터로 부드럽게 확대하고, 개미집은 원래 해상도(1칸 = 1픽셀)라 칸 경계가 보이게 최근접 필터를 쓴다.
/// 단일 모드와 복합 모드가 같은 픽셀 버퍼·Image·텍스처를 재사용하며, 색 계산만 다르다.
/// </summary>
public partial class HeatmapView : Node2D
{
    private ImageTexture? _texture;
    private Image? _image;
    private byte[] _pixels = Array.Empty<byte>();
    private int _width, _height, _cellSize = 1;

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        Visible = false;
    }

    /// <summary>마지막 래스터 변환에 걸린 시간(ms). 성능 표시용.</summary>
    public double LastRasterMilliseconds { get; private set; }

    /// <summary>냄새 한 종류(기존 단일 히트맵).</summary>
    public void ShowField(float[] field, int width, int height, int cellSize, float max, HeatmapScale scale)
    {
        EnsureBuffers(width, height, cellSize);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        HeatmapRasterizer.Rasterize(field, max, scale, _pixels);
        LastRasterMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Upload();
    }

    /// <summary>여러 냄새를 색으로 합성한 복합 히트맵.</summary>
    public void ShowComposite(ReadOnlySpan<CompositeChannel> channels, int width, int height, int cellSize, CompositeHeatmapRasterizer rasterizer)
    {
        EnsureBuffers(width, height, cellSize);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        rasterizer.Rasterize(channels, _pixels);
        LastRasterMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Upload();
    }

    private void EnsureBuffers(int width, int height, int cellSize)
    {
        if (cellSize != _cellSize)
        {
            _cellSize = cellSize;
            TextureFilter = cellSize > 1 ? TextureFilterEnum.Linear : TextureFilterEnum.Nearest;
        }
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
        if (_texture is null) return;
        if (_cellSize == 1) DrawTexture(_texture, Vector2.Zero);
        else DrawTextureRect(_texture, new Rect2(Vector2.Zero, new Vector2(_width * _cellSize, _height * _cellSize)), false);
    }
}
