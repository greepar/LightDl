namespace LightDl.Tests;

/// <summary>
/// Streams a buffer in small chunks with a delay between them, so a test can tell the difference
/// between a download that was cancelled cooperatively and one that ran to completion.
/// </summary>
public sealed class SlowStream(byte[] buffer, TimeSpan delayPerChunk, int chunkSize = 64 * 1024) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => buffer.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        if (_position >= buffer.Length)
            return 0;

        await Task.Delay(delayPerChunk, ct).ConfigureAwait(false);

        var count = Math.Min(Math.Min(chunkSize, destination.Length), buffer.Length - _position);
        buffer.AsMemory(_position, count).CopyTo(destination);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] destination, int offset, int count, CancellationToken ct)
        => ReadAsync(destination.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] destination, int offset, int count)
        => ReadAsync(destination, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] source, int offset, int count) => throw new NotSupportedException();
}
