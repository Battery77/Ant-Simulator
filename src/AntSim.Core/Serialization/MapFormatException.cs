namespace AntSim.Core.Serialization;

/// <summary>맵 파일이 손상되었거나 지원하지 않는 형식일 때 발생한다.</summary>
public sealed class MapFormatException : Exception
{
    public MapFormatException(string message, Exception? inner = null) : base(message, inner) { }
}
