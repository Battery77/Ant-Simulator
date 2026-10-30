namespace AntSim.Core.Serialization;

/// <summary>남은 길이를 아는 읽기 전용 스트림(파일 안의 구간 등).</summary>
internal interface IKnownRemainingStream
{
    long Remaining { get; }
}

/// <summary>
/// 불러오기 전 크기 검증 도우미. 파일에 적힌 길이·크기를 믿고 큰 배열을 먼저 만들지 않도록,
/// 실제로 남은 바이트 수와 비교해 부족하면 할당 전에 <see cref="MapFormatException"/>으로 거부한다.
/// 로더는 레코드(맵·실험 파일 하나)만 읽고 체크섬 직후에서 멈추므로, 같은 스트림 뒤의 데이터는 그대로 남는다.
/// </summary>
internal static class StreamLimits
{
    /// <summary>남은 바이트 수. 알 수 없으면 null.</summary>
    public static long? Remaining(Stream stream) => stream switch
    {
        IKnownRemainingStream known => known.Remaining,
        { CanSeek: true } => Math.Max(0, stream.Length - stream.Position),
        _ => null,
    };

    /// <summary>
    /// 남은 길이를 알 수 없는 스트림(seek 불가)은 <see cref="LookaheadStream"/>으로 감싼다. 끝까지 미리 읽지 않고,
    /// <see cref="Require"/>가 요구한 바이트(레코드에 반드시 들어 있어야 하는 최소 길이)만 실제로 받아 확인한다.
    /// </summary>
    public static Stream ForRecord(Stream stream) => Remaining(stream) is null ? new LookaheadStream(stream) : stream;

    /// <summary>
    /// 앞으로 최소 <paramref name="needed"/>바이트를 읽어야 하는데 남은 데이터가 부족하면 거부한다.
    /// <paramref name="needed"/>는 이 레코드의 남은 부분에 반드시 포함되는 길이여야 한다(다음 레코드까지 읽지 않도록).
    /// </summary>
    public static void Require(Stream stream, long needed, string what)
    {
        if (needed < 0) throw new MapFormatException($"{what}: 크기가 잘못되었습니다.");
        long? remaining = stream is LookaheadStream lookahead ? lookahead.Prefetch(needed) : Remaining(stream);
        if (remaining is { } r && needed > r)
            throw new MapFormatException($"{what}: 파일이 예상보다 짧습니다(필요 {needed}바이트, 남은 {r}바이트).");
    }
}

/// <summary>
/// seek 불가 스트림의 미리 읽기 버퍼. <see cref="Prefetch"/>는 필요한 만큼만(요청 크기를 넘지 않게) 원본에서 받아 두고,
/// 읽기는 버퍼 → 원본 순으로 요청한 바이트만 가져간다. 따라서 원본에서 소비하는 바이트는 로더가 실제로 읽은 레코드 바이트와 같다.
/// 버퍼는 4KiB부터 받은 양만큼 두 배씩(최대 1MiB 조각) 늘어나므로, 잘린 입력이 큰 크기를 선언해도 할당은 실제로 받은 데이터의
/// 약 2배 + 4KiB를 넘지 않는다. 읽어 간 조각은 바로 놓아 준다.
/// </summary>
internal sealed class LookaheadStream(Stream inner) : Stream
{
    private const int FirstChunk = 4096;
    private const int MaxChunk = 1 << 20;
    private readonly Queue<(byte[] Data, int Count)> _chunks = new();
    private int _headOffset;
    private long _buffered;

    /// <summary>버퍼에 <paramref name="needed"/>바이트가 모일 때까지 원본에서 읽고, 모인(또는 끝까지 받은) 바이트 수를 돌려준다.</summary>
    public long Prefetch(long needed)
    {
        while (_buffered < needed)
        {
            int size = (int)Math.Min(needed - _buffered, Math.Clamp(_buffered, FirstChunk, MaxChunk));
            var chunk = new byte[size];
            int filled = 0;
            while (filled < size)
            {
                int n = inner.Read(chunk, filled, size - filled);
                if (n <= 0) break;
                filled += n;
            }
            if (filled > 0)
            {
                _chunks.Enqueue((chunk, filled));
                _buffered += filled;
            }
            if (filled < size) break; // 원본 끝
        }
        return _buffered;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        if (_chunks.Count == 0) return inner.Read(buffer);
        var (data, count) = _chunks.Peek();
        int n = Math.Min(buffer.Length, count - _headOffset);
        data.AsSpan(_headOffset, n).CopyTo(buffer);
        _headOffset += n;
        _buffered -= n;
        if (_headOffset == count)
        {
            _chunks.Dequeue();
            _headOffset = 0;
        }
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
