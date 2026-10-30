using AntSim.Core.Rendering;
using AntSim.Core.Worlds;
using Godot;

namespace AntSim.App;

/// <summary>
/// 월드를 청크(64×64) 단위 텍스처로 그린다. 타일마다 노드를 만들지 않는다.
/// 매 프레임 두 레이어의 더티 청크만 다시 래스터라이즈해 텍스처를 갱신한다.
/// 1 타일 = 월드 좌표 1 단위.
/// </summary>
public partial class MapView : Node2D
{
    private static readonly Color BorderColor = new(1f, 1f, 1f, 0.6f);
    private static readonly Color HoverColor = new(1f, 1f, 0f, 0.9f);

    private readonly List<Chunk> _dirty = new();
    private World? _world;
    private ImageTexture[][] _textures = Array.Empty<ImageTexture[]>();
    private Image? _image;
    private byte[] _pixels = Array.Empty<byte>();
    private LayerId _layer = LayerId.Surface;
    private Vector2I? _hover;

    public TilePalette Palette { get; set; } = TilePalette.Default;

    /// <summary>지금 그리고 있는 월드(점검용).</summary>
    public World? DisplayedWorld => _world;

    public LayerId Layer
    {
        get => _layer;
        set
        {
            _layer = value;
            QueueRedraw();
        }
    }

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
    }

    /// <summary>새 월드를 표시한다. 모든 청크 텍스처를 다시 만든다.</summary>
    public void SetWorld(World world)
    {
        _world = world;
        int size = world.Config.ChunkSize;
        _pixels = new byte[ChunkRasterizer.BufferSize(size)];
        _image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        _textures = new ImageTexture[World.LayerCount][];
        foreach (var layer in world.Layers)
        {
            var textures = new ImageTexture[layer.Chunks.Count];
            for (int i = 0; i < textures.Length; i++)
            {
                RasterizeToImage(layer.Chunks[i]);
                textures[i] = ImageTexture.CreateFromImage(_image);
            }
            _textures[(int)layer.Id] = textures;
            layer.ClearAllDirty();
        }
        _hover = null;
        QueueRedraw();
    }

    public void SetHover(Vector2I? tile)
    {
        if (_hover == tile) return;
        _hover = tile;
        QueueRedraw();
    }

    /// <summary>마우스 아래 타일 좌표. 맵 밖이면 false.</summary>
    public bool TryGetTileUnderMouse(out Vector2I tile)
    {
        var p = GetLocalMousePosition();
        tile = new Vector2I(Mathf.FloorToInt(p.X), Mathf.FloorToInt(p.Y));
        return _world is not null && _world.Config.InBounds(tile.X, tile.Y);
    }

    /// <summary>입력 이벤트의 화면(뷰포트) 좌표를 타일 좌표로 바꾼다. 맵 밖 좌표도 그대로 반환한다(드래그 보간용).</summary>
    public Vector2I ScreenToTile(Vector2 screenPosition)
    {
        var p = GetGlobalTransformWithCanvas().AffineInverse() * screenPosition;
        return new Vector2I(Mathf.FloorToInt(p.X), Mathf.FloorToInt(p.Y));
    }

    public override void _Process(double delta)
    {
        if (_world is null) return;
        foreach (var layer in _world.Layers)
        {
            _dirty.Clear();
            if (layer.CollectDirtyChunks(_dirty) == 0) continue;
            var textures = _textures[(int)layer.Id];
            foreach (var chunk in _dirty)
            {
                RasterizeToImage(chunk);
                textures[chunk.Coord.Y * _world.Config.ChunksX + chunk.Coord.X].Update(_image);
            }
            if (layer.Id == _layer) QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (_world is null) return;
        var chunks = _world.GetLayer(_layer).Chunks;
        var textures = _textures[(int)_layer];
        for (int i = 0; i < chunks.Count; i++)
            DrawTexture(textures[i], new Vector2(chunks[i].OriginX, chunks[i].OriginY));

        DrawRect(new Rect2(0, 0, _world.Config.Width, _world.Config.Height), BorderColor, filled: false);
        if (_hover is { } h)
            DrawRect(new Rect2(h.X, h.Y, 1, 1), HoverColor, filled: false);
    }

    private void RasterizeToImage(Chunk chunk)
    {
        ChunkRasterizer.Rasterize(chunk, _world!.Passages, _world.Rules.MaxQuantityPerTile, Palette, _pixels);
        _image!.SetData(chunk.Size, chunk.Size, false, Image.Format.Rgba8, _pixels);
    }
}
