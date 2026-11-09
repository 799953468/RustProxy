namespace RustProxy.Core.Storage;

/// <summary>Limits streamed output without taking ownership of the destination stream.</summary>
internal sealed class BoundedWriteStream(Stream destination, long maximumBytes) : Stream
{
    private long _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => destination.CanWrite;
    public override long Length => _written;
    public override long Position { get => _written; set => throw new NotSupportedException(); }
    public override void Flush() => destination.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckCapacity(buffer.Length);
        destination.Write(buffer);
        _written += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckCapacity(buffer.Length);
        await destination.WriteAsync(buffer, cancellationToken);
        _written += buffer.Length;
    }

    private void CheckCapacity(int count)
    {
        if (count > maximumBytes - _written) throw new InvalidDataException("Package output exceeds its byte quota.");
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
