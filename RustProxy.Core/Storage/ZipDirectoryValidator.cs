using System.Buffers.Binary;
using RustProxy.Core.Package;

namespace RustProxy.Core.Storage;

/// <summary>Bounds ZIP directory parsing before ZipArchive allocates entry names, comments and metadata.</summary>
internal static class ZipDirectoryValidator
{
    public static async Task ValidateAsync(Stream input, PackageLimits limits, CancellationToken cancellationToken)
    {
        try { await ValidateDirectoryAsync(input, limits, cancellationToken); }
        catch (EndOfStreamException exception) { throw new InvalidDataException("Truncated ZIP directory.", exception); }
        finally { input.Position = 0; }
    }

    private static async Task ValidateDirectoryAsync(Stream input, PackageLimits limits, CancellationToken cancellationToken)
    {
        var tail = new byte[(int)Math.Min(input.Length, 65535 + 22)];
        input.Position = input.Length - tail.Length;
        await input.ReadExactlyAsync(tail, cancellationToken);
        var end = -1;
        for (var offset = tail.Length - 22; offset >= 0; offset--)
        {
            if (U32(tail, offset) != 0x06054b50) continue;
            // Use the same last signature as ZipArchive; never fall back to an earlier trailer in a crafted comment.
            if (offset + 22 + U16(tail, offset + 20) != tail.Length)
                throw new InvalidDataException("Ambiguous ZIP trailer or trailing data.");
            end = offset;
            break;
        }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10))
            throw new InvalidDataException("Invalid or multi-disk ZIP directory.");
        var endPosition = input.Length - tail.Length + end;
        ulong count = U16(tail, end + 10);
        ulong size = U32(tail, end + 12);
        ulong start = U32(tail, end + 16);
        if (count != ushort.MaxValue && start != uint.MaxValue && endPosition >= 20)
        {
            var signature = new byte[4];
            input.Position = endPosition - 20;
            await input.ReadExactlyAsync(signature, cancellationToken);
            if (U32(signature, 0) == 0x07064b50) throw new InvalidDataException("Unexpected ZIP64 directory locator.");
        }
        if (count == ushort.MaxValue || start == uint.MaxValue)
        {
            if (endPosition < 20) throw new InvalidDataException("Missing ZIP64 directory locator.");
            var locator = new byte[20];
            input.Position = endPosition - locator.Length;
            await input.ReadExactlyAsync(locator, cancellationToken);
            if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1)
                throw new InvalidDataException("Invalid ZIP64 directory locator.");
            var zip64Position = U64(locator, 8);
            if (zip64Position > (ulong)(endPosition - 20) || (ulong)(endPosition - 20) - zip64Position < 56)
                throw new InvalidDataException("Invalid ZIP64 directory offset.");
            var record = new byte[56];
            input.Position = (long)zip64Position;
            await input.ReadExactlyAsync(record, cancellationToken);
            if (U32(record, 0) != 0x06064b50 || U64(record, 4) < 44 || U64(record, 4) != (ulong)(endPosition - 20) - zip64Position - 12 ||
                U32(record, 16) != 0 || U32(record, 20) != 0 || U64(record, 24) != U64(record, 32))
                throw new InvalidDataException("Invalid or multi-disk ZIP64 directory.");
            count = U64(record, 32);
            size = U64(record, 40);
            start = U64(record, 48);
            endPosition = (long)zip64Position;
        }
        if (count > (ulong)limits.MaxFiles + 1 || size > (ulong)limits.MaxDirectoryBytes ||
            start > (ulong)endPosition || size != (ulong)endPosition - start)
            throw new InvalidDataException("ZIP directory exceeds its quota or has invalid bounds.");

        input.Position = (long)start;
        var header = new byte[46];
        ulong actualCount = 0;
        while (input.Position < endPosition)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = endPosition - input.Position;
            if (remaining < 4) throw new InvalidDataException("Truncated ZIP directory entry.");
            await input.ReadExactlyAsync(header.AsMemory(0, 4), cancellationToken);
            if (U32(header, 0) == 0x05054b50)
            {
                if (remaining < 6) throw new InvalidDataException("Truncated ZIP directory signature.");
                await input.ReadExactlyAsync(header.AsMemory(4, 2), cancellationToken);
                if (remaining != 6 + U16(header, 4)) throw new InvalidDataException("Invalid ZIP directory signature size.");
                input.Position = endPosition;
                break;
            }
            if (remaining < 46 || U32(header, 0) != 0x02014b50 || ++actualCount > count)
                throw new InvalidDataException("Invalid ZIP directory entry count.");
            await input.ReadExactlyAsync(header.AsMemory(4, 42), cancellationToken);
            var nameLength = U16(header, 28);
            var variableLength = nameLength + U16(header, 30) + U16(header, 32);
            if (nameLength is 0 or > 4096 || variableLength > remaining - header.Length)
                throw new InvalidDataException("Invalid ZIP directory entry lengths.");
            input.Position += variableLength;
        }
        if (actualCount != count) throw new InvalidDataException("ZIP directory entry count does not match its trailer.");
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
}
