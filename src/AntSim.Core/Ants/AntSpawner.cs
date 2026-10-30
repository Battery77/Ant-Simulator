using AntSim.Core.Worlds;

namespace AntSim.Core.Ants;

/// <summary>초기 생성 결과. 열린 공간이 부족하면 요청보다 적게 생성될 수 있다.</summary>
public readonly record struct AntSpawnReport(int Requested, int Spawned)
{
    public int Failed => Requested - Spawned;
}

/// <summary>
/// 개미 생성. 이동할 수 없는 위치(맵 밖, 물, 토양·바위)에는 만들지 않는다.
/// 초기 배치: 통로가 있으면 각 통로의 개미집 3×3 굴착 공간에 차례로 나눠 놓고, 없으면 지상 맵 중앙 주변에 놓는다.
/// 후보 위치를 무작위로 몇 번만 시도하며 맵 전체를 훑지 않는다.
/// </summary>
public static class AntSpawner
{
    public const int AttemptsPerAnt = 24;
    public const int SurfaceFallbackRadius = 12;

    public static AntSpawnReport SpawnInitial(AntPopulation ants, World world, AntSettings settings)
    {
        ArgumentNullException.ThrowIfNull(ants);
        ArgumentNullException.ThrowIfNull(world);
        var errors = settings.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(settings));

        ulong placement = AntRandom.CreateState(settings.Seed, ulong.MaxValue);
        var anchors = new List<(LayerId Layer, int X, int Y, int Radius)>();
        var passages = new List<int>(world.Passages.Count);
        world.Passages.CopyIndicesTo(passages);
        foreach (int index in passages)
            anchors.Add((LayerId.Nest, index % world.Config.Width, index / world.Config.Width, 1));
        if (anchors.Count == 0)
            anchors.Add((LayerId.Surface, world.Config.Width / 2, world.Config.Height / 2, SurfaceFallbackRadius));

        ants.Grow(ants.Count + settings.InitialCount);
        int spawned = 0;
        for (int n = 0; n < settings.InitialCount; n++)
        {
            var anchor = anchors[n % anchors.Count];
            for (int attempt = 0; attempt < AttemptsPerAnt; attempt++)
            {
                int tx = anchor.X + AntRandom.NextInt(ref placement, 2 * anchor.Radius + 1) - anchor.Radius;
                int ty = anchor.Y + AntRandom.NextInt(ref placement, 2 * anchor.Radius + 1) - anchor.Radius;
                float x = tx + AntRandom.Range(ref placement, 0.1f, 0.9f);
                float y = ty + AntRandom.Range(ref placement, 0.1f, 0.9f);
                if (TrySpawn(ants, world, settings, anchor.Layer, x, y, out _))
                {
                    spawned++;
                    break;
                }
            }
        }
        return new AntSpawnReport(settings.InitialCount, spawned);
    }

    /// <summary>
    /// 지정한 위치에 일개미 한 마리를 만든다. 이동할 수 없는 위치면 false.
    /// 속도·크기·방향을 지정하지 않으면 설정의 편차 안에서 개체별 난수로 정한다(속도와 크기는 서로 독립).
    /// </summary>
    public static bool TrySpawn(AntPopulation ants, World world, AntSettings settings, LayerId layer, float x, float y,
        out int index, float? baseSpeed = null, float? size = null, float? heading = null)
    {
        index = -1;
        if ((uint)layer >= World.LayerCount || !float.IsFinite(x) || !float.IsFinite(y)) return false;
        var worldLayer = world.GetLayer(layer);
        int tx = (int)MathF.Floor(x), ty = (int)MathF.Floor(y);
        if (!AntMovement.IsPassable(worldLayer, tx, ty)) return false;

        int id = ants.NextId;
        ulong rng = AntRandom.CreateState(settings.Seed, (ulong)id);
        float speed = baseSpeed ?? settings.BaseSpeed * (1 + settings.SpeedVariation * AntRandom.Range(ref rng, -1f, 1f));
        float bodySize = size ?? settings.BaseSize * (1 + settings.SizeVariation * AntRandom.Range(ref rng, -1f, 1f));
        float direction = heading ?? AntRandom.Range(ref rng, -MathF.PI, MathF.PI);
        float intervalScale = 1 + settings.TurnIntervalVariation * AntRandom.Range(ref rng, -1f, 1f);
        // 첫 방향 결정 시점을 개체마다 흩어 놓아 갱신 부하와 움직임이 한 틱에 몰리지 않게 한다.
        float firstTurn = AntRandom.Range(ref rng, 0f, settings.TurnIntervalMax * intervalScale);
        float factor = settings.SpeedFactorFor(worldLayer.GetState(tx, ty).TotalQuantity);

        index = ants.Add(id, AntCaste.Worker, AntMobility.Walk, layer, x, y, direction,
            speed, factor, bodySize, firstTurn, intervalScale, ty * world.Config.Width + tx, rng);
        return true;
    }
}
