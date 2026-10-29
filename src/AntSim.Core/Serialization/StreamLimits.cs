namespace AntSim.Core.Serialization;

/// <summary>남은 길이를 아는 읽기 전용 스트림(파일 안의 구간 등).</summary>
internal interface IKnownRemainingStream
{
    long Remaining { get; }
}

/// <summary>
/// 불러오기 전 크기 검증 도우미. 파일에 적힌 길이·크기를 믿고 큰 배열을 먼저 만들지 않도록,
/// 실제로 남은 바이트 수와 비교해 부족하면 할당 전에 <see cref="MapFormatException"/>으로 거부한다.
/// </summary>
internal static class StreamLimits
{
    /// <summary>seek 불가 스트림을 버퍼링할 때의 상한(.NET 배열 최대 길이). 버퍼는 실제로 들어온 데이터만큼만 커진다.</summary>
    public static readonly long MaxBufferedBytes = Array.MaxLength;

    /// <summary>남은 바이트 수. 알 수 없으면 null.</summary>
    public static long? Remaining(Stream stream) => stream switch
    {
        IKnownRemainingStream known => known.Remaining,
        { CanSeek: true } => Math.Max(0, stream.Length - stream.Position),
        _ => null,
    };

    /// <summary>
    /// 남은 길이를 알 수 없는 스트림(seek 불가)이면 끝까지 읽어 메모리 스트림으로 바꾼다.
    /// 버퍼는 실제로 받은 데이터만큼만 커지므로, 잘린 파일이 큰 크기를 선언해도 그만큼 할당되지 않는다.
    /// </summary>
    public static Stream EnsureKnownLength(Stream stream)
    {
        if (Remaining(stream) is not null) return stream;
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxBufferedBytes)
                throw new MapFormatException($"파일이 너무 큽니다(최대 {MaxBufferedBytes}바이트).");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>앞으로 최소 <paramref name="needed"/>바이트를 읽어야 하는데 남은 데이터가 부족하면 거부한다.</summary>
    public static void Require(Stream stream, long needed, string what)
    {
        if (needed < 0) throw new MapFormatException($"{what}: 크기가 잘못되었습니다.");
        if (Remaining(stream) is { } remaining && needed > remaining)
            throw new MapFormatException($"{what}: 파일이 예상보다 짧습니다(필요 {needed}바이트, 남은 {remaining}바이트).");
    }
}

/// <summary>
/// 바깥 파일 안의 길이가 정해진 구간을 읽는 스트림(실험 파일 안의 내장 맵). 읽은 바이트는 바깥 체크섬에 포함된다.
/// 구간 길이는 만들기 전에 바깥 파일의 남은 길이로 검증한다.
/// </summary>
internal sealed class ChecksumSectionStream(ChecksumReader outer, long length) : Stream, IKnownRemainingStream
{
    private long _remaining = length;

    public long Remaining => _remaining;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = (int)Math.Min(buffer.Length, _remaining);
        if (n == 0) return 0;
        outer.Read(buffer[..n]);
        _remaining -= n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
