using AntSim.Core.Simulation;
using AntSim.Core.Worlds;
using Godot;

namespace AntSim.App;

/// <summary>
/// 개미 표시. 개미마다 노드를 만들지 않고 MultiMesh 하나에 인스턴스 변환(위치·방향·크기)을 일괄로 넣는다.
/// 위치는 계산 스레드가 게시한 이전 틱 → 현재 틱 좌표 사이를 보간해 틱 사이에도 부드럽게 움직인다.
/// 개미 모양은 앞쪽(머리)이 밝은 화살촉 형태라 이동 방향이 보인다. 크기 = 개체별 외형 크기(타일).
/// </summary>
public partial class AntView : Node2D
{
    private const int FloatsPerInstance = 8; // Transform2D: x.x, y.x, 0, origin.x, x.y, y.y, 0, origin.y

    private static readonly Color SelectionColor = new(1f, 0.95f, 0.2f);

    private MultiMesh _multiMesh = null!;
    private float[] _buffer = Array.Empty<float>();
    private Vector2? _selectedPosition;
    private float _selectedHeading;
    private float _selectedSize;

    /// <summary>
    /// 컬링 범위를 월드 전체로 지정한다. MultiMesh가 자동 계산한 AABB는 Z 두께가 0이고 빈 인스턴스(원점)까지 포함해
    /// 2D 컬링에서 개미 전체가 그려지지 않는 경우가 있었다(Godot 4.4.1, GL Compatibility에서 재현).
    /// </summary>
    public void SetWorldBounds(int width, int height)
    {
        _multiMesh.CustomAabb = new Aabb(new Vector3(-2, -2, -1), new Vector3(width + 4, height + 4, 2));
    }

    /// <summary>현재 레이어에 그린 개미 수.</summary>
    public int VisibleCount { get; private set; }

    public override void _Ready()
    {
        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            Mesh = CreateAntMesh(),
        };
        AddChild(new MultiMeshInstance2D { Multimesh = _multiMesh, Name = "Ants" });
    }

    /// <summary>
    /// 프레임의 개미 중 <paramref name="layer"/>에 있는 개미를 그린다.
    /// <paramref name="alpha"/>: 0 = 이전 틱 위치, 1 = 현재 틱 위치.
    /// </summary>
    public void ShowFrame(AntFrame frame, LayerId layer, float alpha, int selectedId)
    {
        EnsureCapacity(frame.Count);
        int n = 0;
        _selectedPosition = null;
        for (int i = 0; i < frame.Count; i++)
        {
            if (frame.Layer[i] != (byte)layer) continue;
            float x = frame.PrevX[i] + (frame.X[i] - frame.PrevX[i]) * alpha;
            float y = frame.PrevY[i] + (frame.Y[i] - frame.PrevY[i]) * alpha;
            float size = frame.Size[i];
            var (sin, cos) = MathF.SinCos(frame.Heading[i]);
            int o = n * FloatsPerInstance;
            _buffer[o] = cos * size;
            _buffer[o + 1] = -sin * size;
            _buffer[o + 2] = 0;
            _buffer[o + 3] = x;
            _buffer[o + 4] = sin * size;
            _buffer[o + 5] = cos * size;
            _buffer[o + 6] = 0;
            _buffer[o + 7] = y;
            n++;
            if (frame.Id[i] == selectedId)
            {
                _selectedPosition = new Vector2(x, y);
                _selectedHeading = frame.Heading[i];
                _selectedSize = size;
            }
        }
        VisibleCount = n;
        _multiMesh.Buffer = _buffer;
        _multiMesh.VisibleInstanceCount = n;
        QueueRedraw();
    }

    public void Clear()
    {
        VisibleCount = 0;
        _selectedPosition = null;
        if (_multiMesh is not null) _multiMesh.VisibleInstanceCount = 0;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_selectedPosition is not { } p) return;
        // 선택한 개미: 원 + 진행 방향 선
        DrawArc(p, MathF.Max(0.8f, _selectedSize), 0, MathF.Tau, 24, SelectionColor, 0.12f);
        var (sin, cos) = MathF.SinCos(_selectedHeading);
        DrawLine(p, p + new Vector2(cos, sin) * 2.5f, SelectionColor, 0.12f);
    }

    private void EnsureCapacity(int count)
    {
        if (_multiMesh.InstanceCount >= count && _buffer.Length == _multiMesh.InstanceCount * FloatsPerInstance) return;
        int capacity = Math.Max(64, Math.Max(count, _multiMesh.InstanceCount * 2));
        _multiMesh.InstanceCount = capacity;
        _buffer = new float[capacity * FloatsPerInstance];
    }

    /// <summary>
    /// 몸 길이 1(타일)짜리 개미 모양. +X가 앞. 몸통은 어둡고, 머리 삼각형만 밝아서 진행 방향이 보인다(정점 색).
    /// </summary>
    private static ArrayMesh CreateAntMesh()
    {
        var body = new Color(0.10f, 0.06f, 0.04f);
        var head = new Color(1f, 0.78f, 0.30f);
        var vertices = new List<Vector2>();
        var colors = new List<Color>();
        void Triangle(Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            vertices.AddRange(new[] { a, b, c });
            colors.AddRange(new[] { color, color, color });
        }
        // 몸통(배 → 가슴): 뒤가 넓고 앞이 좁은 사다리꼴
        Vector2 tailTop = new(-0.5f, 0.19f), tailBottom = new(-0.5f, -0.19f), neckTop = new(0.22f, 0.13f), neckBottom = new(0.22f, -0.13f);
        Triangle(tailTop, tailBottom, neckBottom, body);
        Triangle(tailTop, neckBottom, neckTop, body);
        // 머리
        Triangle(new Vector2(0.5f, 0f), new Vector2(0.2f, 0.17f), new Vector2(0.2f, -0.17f), head);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
