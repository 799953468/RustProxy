using System.Buffers;
using System.Security.Cryptography;

namespace RustProxy.Core.Storage;

/// <summary>Copies bounded streams while hashing the exact bytes written.</summary>
public static class StreamTransfer
{
    /// <summary>Copies at most the specified bytes and returns length and lowercase SHA-256.</summary>
    public static async Task<(long Length, string Sha256)> CopyAsync(Stream input, Stream output, long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) != 0)
            {
                if (read > maximumBytes - length) throw new InvalidDataException("Stream exceeds its allowed length.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                length += read;
            }
            return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
