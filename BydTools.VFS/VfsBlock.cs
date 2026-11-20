using System.Buffers;
using System.Buffers.Binary;
using BydTools.Core.Crypto;
using Microsoft.Win32.SafeHandles;

namespace BydTools.VFS;

public sealed class VfsBlock
{
    private const int StreamChunkSize = 256 * 1024; // Exact multiple of CSChaCha20.BlockSize (64).
    private readonly byte[] _key;
    private readonly IReadOnlyList<string> _chunkDirectories;

    internal VfsBlock(
        string directoryPath,
        VfsBlockInfo info,
        BlockDescriptor? descriptor,
        byte[] key,
        IReadOnlyList<string> chunkDirectories
    )
    {
        DirectoryPath = directoryPath;
        Info = info;
        Descriptor = descriptor;
        _key = key;
        _chunkDirectories = chunkDirectories;
    }

    public string DirectoryPath { get; }
    public VfsBlockInfo Info { get; }
    public BlockDescriptor? Descriptor { get; }

    public string DisplayName =>
        Descriptor?.Type.ToString()
        ?? (Enum.IsDefined(Info.BlockType) ? Info.BlockType.ToString() : Info.GroupCfgName);

    public string? ResolveChunkPath(VfsChunk chunk)
    {
        foreach (string directory in _chunkDirectories)
        {
            string path = Path.Combine(directory, chunk.FileName);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public VfsChunkReader? OpenChunkReader(VfsChunk chunk)
    {
        string? path = ResolveChunkPath(chunk);
        if (path == null)
            return null;

        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new VfsChunkReader(this, handle);
    }

    internal byte[] ReadFile(SafeFileHandle handle, VfsFile file)
    {
        if (file.Length < 0 || file.Length > int.MaxValue)
            throw new InvalidDataException($"File '{file.Name}' has an invalid length ({file.Length}).");

        int length = (int)file.Length;
        if (length == 0)
            return [];

        byte[] data = new byte[length];
        ReadInto(handle, file, data);
        return data;
    }

    internal byte[] RentAndReadFile(SafeFileHandle handle, VfsFile file, out int length)
    {
        if (file.Length < 0 || file.Length > int.MaxValue)
            throw new InvalidDataException($"File '{file.Name}' has an invalid length ({file.Length}).");

        length = (int)file.Length;
        if (length == 0)
            return [];

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            ReadInto(handle, file, rented.AsSpan(0, length));
            return rented;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    internal int ReadPrefix(SafeFileHandle handle, VfsFile file, Span<byte> destination)
    {
        if (file.Length < 0)
            throw new InvalidDataException($"File '{file.Name}' has an invalid length ({file.Length}).");

        int toRead = (int)Math.Min(file.Length, destination.Length);
        if (toRead == 0)
            return 0;

        Span<byte> slice = destination[..toRead];
        ReadExactly(handle, file.Offset, slice, file.Name);
        if (file.Encrypted)
            DecryptSpan(file.IvSeed, counter: 1, slice);

        return toRead;
    }

    internal void CopyFileTo(SafeFileHandle handle, VfsFile file, Stream destination)
    {
        if (file.Length < 0)
            throw new InvalidDataException($"File '{file.Name}' has an invalid length ({file.Length}).");
        if (file.Length == 0)
            return;

        int bufferSize = (int)Math.Min(StreamChunkSize, file.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            Span<byte> nonce = stackalloc byte[VFSDefine.NonceLength];
            if (file.Encrypted)
            {
                BinaryPrimitives.WriteInt32LittleEndian(nonce, VFSDefine.ProtocolVersion);
                BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], file.IvSeed);
            }

            long remaining = file.Length;
            long fileOffset = 0;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(StreamChunkSize, remaining);
                Span<byte> slice = buffer.AsSpan(0, toRead);
                ReadExactly(handle, file.Offset + fileOffset, slice, file.Name);

                if (file.Encrypted)
                {
                    uint counter = checked((uint)(1 + fileOffset / CSChaCha20.BlockSize));
                    CSChaCha20.DecryptInPlace(_key, nonce, counter, slice);
                }

                destination.Write(slice);
                fileOffset += toRead;
                remaining -= toRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ReadInto(SafeFileHandle handle, VfsFile file, Span<byte> destination)
    {
        ReadExactly(handle, file.Offset, destination, file.Name);
        if (file.Encrypted)
            DecryptSpan(file.IvSeed, counter: 1, destination);
    }

    private void DecryptSpan(long ivSeed, uint counter, Span<byte> data)
    {
        Span<byte> nonce = stackalloc byte[VFSDefine.NonceLength];
        BinaryPrimitives.WriteInt32LittleEndian(nonce, VFSDefine.ProtocolVersion);
        BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], ivSeed);
        CSChaCha20.DecryptInPlace(_key, nonce, counter, data);
    }

    private static void ReadExactly(SafeFileHandle handle, long fileOffset, Span<byte> destination, string fileName)
    {
        int got = 0;
        while (got < destination.Length)
        {
            int n = RandomAccess.Read(handle, destination[got..], fileOffset + got);
            if (n == 0)
            {
                throw new EndOfStreamException(
                    $"Chunk ended before '{fileName}' was fully read ({got}/{destination.Length})."
                );
            }

            got += n;
        }
    }
}
