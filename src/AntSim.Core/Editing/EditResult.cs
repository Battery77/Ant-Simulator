using AntSim.Core.Worlds;

namespace AntSim.Core.Editing;

/// <summary>편집 결과. 실패 시 <see cref="FailedOpIndex"/>는 배치 안에서 처음 실패한 연산의 위치다.</summary>
public readonly record struct EditResult(WorldError Error, bool Changed, int FailedOpIndex = -1)
{
    public static EditResult Applied => new(WorldError.None, true);
    public static EditResult NoChange => new(WorldError.None, false);
    public static EditResult Fail(WorldError error, int opIndex) => new(error, false, opIndex);

    public bool Succeeded => Error == WorldError.None;
}
