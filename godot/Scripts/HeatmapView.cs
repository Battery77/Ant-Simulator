using AntSim.Core.Rendering;
using Godot;

namespace AntSim.App;

/// <summary>냄새 농도 히트맵 오버레이. 레이어 전체를 텍스처 한 장으로 그린다(1 타일 = 1 픽셀).</summary>
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

    public void ShowField(float[] field, int width, int height, float max, HeatmapScale scale)
    {
        if (width != _width || height != _height || _image is null)
        {
            _width = width;
            _height = height;
            _pixels = new byte[width * height * 4];
            _image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
            _texture = null;
        }
        HeatmapRasterizer.Rasterize(field, max, scale, _pixels);
        _image.SetData(width, height, false, Image.Format.Rgba8, _pixels);
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
