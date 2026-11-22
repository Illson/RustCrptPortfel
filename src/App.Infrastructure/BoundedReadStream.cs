namespace Portfel.Infrastructure;

internal sealed class BoundedReadStream(Stream source, int remaining) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("This stream only supports async reads.");

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int count = await source.ReadAsync(buffer[..Math.Min(remaining + 1, buffer.Length)], cancellationToken);
        remaining -= count;
        if (remaining < 0) throw new System.Text.Json.JsonException("Response exceeds the allowed size.");
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override ValueTask DisposeAsync() => source.DisposeAsync();
}
