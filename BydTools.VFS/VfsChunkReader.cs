using Microsoft.Win32.SafeHandles;

namespace BydTools.VFS;

public sealed class VfsChunkReader : IDisposable
{
    private readonly VfsBlock _block;
    private readonly SafeFileHandle _handle;

    internal VfsChunkReader(VfsBlock block, SafeFileHandle handle)
    {
        _block = block;
        _handle = handle;
    }

    public byte[] ReadFile(VfsFile file) => _block.ReadFile(_handle, file);

    public byte[] RentAndReadFile(VfsFile file, out int length) =>
        _block.RentAndReadFile(_handle, file, out length);

    public int ReadPrefix(VfsFile file, Span<byte> destination) =>
        _block.ReadPrefix(_handle, file, destination);

    public void CopyFileTo(VfsFile file, Stream destination) =>
        _block.CopyFileTo(_handle, file, destination);

    public void Dispose() => _handle.Dispose();
}
