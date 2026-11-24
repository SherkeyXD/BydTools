using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace BydTools.Formats.Manifest;

/// <summary>
/// Decompresses Arknights: Endfield Brotli-packed <c>.hgmmap</c> bundle manifests
/// into the underlying binary manifest format (validated by HEAD1/HEAD2 signatures).
/// Note: The output is a binary manifest, not JSON.
/// </summary>
public static class HgmmapManifest
{
    public const uint Head1Magic = 0xFF11FF11u;
    public const uint Head2Magic = 0xF1F2F3F4u;
    public const long DefaultMaxDecompressedBytes = 1024L * 1024 * 1024;

    private const int PrefixProbeBytes = 4096;
    private const int ChunkSize = 64 * 1024;

    public static void DecompressBrotli(
        ReadOnlySpan<byte> compressed,
        Stream output,
        long maxDecompressedBytes = DefaultMaxDecompressedBytes
    )
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecompressedBytes);

        if (compressed.IsEmpty)
            throw new InvalidDataException("Empty .hgmmap input.");

        using var decoder = new BrotliDecoder();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        Span<byte> prefix = stackalloc byte[PrefixProbeBytes];
        int prefixLength = 0;
        bool headerValidated = false;
        long totalWritten = 0;
        int inputOffset = 0;

        try
        {
            while (true)
            {
                ReadOnlySpan<byte> inputSlice = compressed[inputOffset..];
                OperationStatus status = decoder.Decompress(
                    inputSlice,
                    chunk,
                    out int bytesConsumed,
                    out int bytesWritten
                );

                if (status == OperationStatus.InvalidData)
                    throw new InvalidDataException("Invalid Brotli stream in .hgmmap payload.");

                inputOffset += bytesConsumed;

                if (bytesWritten > 0)
                {
                    if (bytesWritten > maxDecompressedBytes - totalWritten)
                    {
                        throw new InvalidDataException(
                            $"Decompressed .hgmmap exceeds safety limit of {maxDecompressedBytes} bytes."
                        );
                    }

                    ReadOnlySpan<byte> produced = chunk.AsSpan(0, bytesWritten);
                    if (!headerValidated)
                    {
                        int toCopy = Math.Min(produced.Length, PrefixProbeBytes - prefixLength);
                        produced[..toCopy].CopyTo(prefix[prefixLength..]);
                        prefixLength += toCopy;

                        if (TryCheckHeader(prefix[..prefixLength], isFinal: false))
                            headerValidated = true;
                    }

                    output.Write(produced);
                    totalWritten += bytesWritten;
                }

                if (status == OperationStatus.Done)
                    break;

                if (status == OperationStatus.NeedMoreData)
                {
                    if (inputOffset >= compressed.Length && bytesWritten == 0)
                    {
                        throw new InvalidDataException(
                            $"Truncated Brotli stream in .hgmmap payload (decoded {totalWritten} bytes before unexpected EOF)."
                        );
                    }

                    continue;
                }

                if (bytesConsumed == 0 && bytesWritten == 0)
                {
                    throw new InvalidDataException(
                        $"Brotli decompression stalled with status {status}."
                    );
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        if (!headerValidated)
            TryCheckHeader(prefix[..prefixLength], isFinal: true);
    }

    private static bool TryCheckHeader(ReadOnlySpan<byte> header, bool isFinal)
    {
        if (header.Length < 8)
        {
            if (isFinal)
            {
                throw new InvalidDataException(
                    $"Decompressed .hgmmap is too short ({header.Length} bytes) for manifest header."
                );
            }

            return false;
        }

        uint head1 = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (head1 != Head1Magic)
        {
            throw new InvalidDataException(
                $"Invalid .hgmmap manifest HEAD1: expected 0x{Head1Magic:X8}, got 0x{head1:X8}."
            );
        }

        uint charCount = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (charCount == 0 || charCount > 1024)
        {
            throw new InvalidDataException(
                $"Invalid .hgmmap manifest version string length: {charCount}."
            );
        }

        int head2Offset = 8 + (int)charCount * 2;
        if (header.Length < head2Offset + 4)
        {
            if (isFinal)
            {
                throw new InvalidDataException(
                    $"Decompressed .hgmmap ({header.Length} bytes) ended before HEAD2 at offset {head2Offset}."
                );
            }

            return false;
        }

        uint head2 = BinaryPrimitives.ReadUInt32LittleEndian(header[head2Offset..]);
        if (head2 != Head2Magic)
        {
            throw new InvalidDataException(
                $"Invalid .hgmmap manifest HEAD2: expected 0x{Head2Magic:X8}, got 0x{head2:X8}."
            );
        }

        return true;
    }
}
