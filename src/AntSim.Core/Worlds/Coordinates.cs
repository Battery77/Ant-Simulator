namespace AntSim.Core.Worlds;

/// <summary>월드 레이어 식별자. 현재 지상과 개미집 두 레이어만 존재한다.</summary>
public enum LayerId : byte
{
    Surface = 0,
    Nest = 1,
}

/// <summary>월드 전역 타일 좌표 (원점은 좌상단, X는 오른쪽, Y는 아래쪽).</summary>
public readonly record struct TileCoord(int X, int Y);

/// <summary>청크 격자 좌표. 타일 좌표를 청크 크기로 나눈 값이다.</summary>
public readonly record struct ChunkCoord(int X, int Y);
