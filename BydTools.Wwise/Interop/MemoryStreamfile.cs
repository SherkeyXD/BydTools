using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BydTools.Wwise.Interop;

/// <summary>
/// Creates a native libstreamfile_t that reads from a pinned byte[] in memory,
/// eliminating temp-file I/O for libvgmstream conversions.
/// <para>
/// The pinned data must remain alive (i.e. <see cref="Dispose"/> must not be called)
/// until the owning <see cref="VgmstreamHandle"/> is fully done decoding.
/// </para>
/// </summary>
internal sealed class MemoryStreamfile : IDisposable
{
    private GCHandle _dataHandle;
    private GCHandle _nameHandle;
    private nint _ptr;

    public nint Ptr =>
        _ptr != 0
            ? _ptr
            : throw new ObjectDisposedException(nameof(MemoryStreamfile));

    private MemoryStreamfile() { }

    public static MemoryStreamfile Create(byte[] data, string name)
    {
        var sf = new MemoryStreamfile();
        sf._dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);

        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name + '\0');
        sf._nameHandle = GCHandle.Alloc(nameBytes, GCHandleType.Pinned);

        unsafe
        {
            sf._ptr = BuildStruct(
                (byte*)sf._dataHandle.AddrOfPinnedObject(),
                data.Length,
                (byte*)sf._nameHandle.AddrOfPinnedObject()
            );
        }

        return sf;
    }

    /// <summary>
    /// Frees the primary struct and its state, but keeps the pinned data alive
    /// so that copies created by libvgmstream's internal <c>open</c> can continue reading.
    /// Call after <see cref="VgmstreamHandle.OpenStream"/> returns.
    /// </summary>
    public void ReleaseStruct()
    {
        if (_ptr != 0)
        {
            FreeStreamfile(_ptr);
            _ptr = 0;
        }
    }

    /// <summary>
    /// Releases ownership of the native struct pointer without freeing it.
    /// Use when handing the struct to <c>libstreamfile_open_buffered</c>,
    /// which takes ownership and will call <c>close</c> itself.
    /// </summary>
    public void DetachStruct()
    {
        _ptr = 0;
    }

    public void Dispose()
    {
        ReleaseStruct();
        if (_dataHandle.IsAllocated)
            _dataHandle.Free();
        if (_nameHandle.IsAllocated)
            _nameHandle.Free();
    }

    // ── native struct helpers ────────────────────────────────────────

    private static unsafe nint BuildStruct(byte* dataPtr, int dataLen, byte* namePtr)
    {
        nint statePtr = Marshal.AllocHGlobal(sizeof(State));
        *(State*)statePtr = new State
        {
            DataPtr = dataPtr,
            DataLen = dataLen,
            NamePtr = namePtr,
        };

        nint structPtr = Marshal.AllocHGlobal(nint.Size * 6);
        nint* f = (nint*)structPtr;
        f[0] = statePtr;
        f[1] = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, int, long, int>)&ReadCb;
        f[2] = (nint)(delegate* unmanaged[Cdecl]<nint, long>)&GetSizeCb;
        f[3] = (nint)(delegate* unmanaged[Cdecl]<nint, nint>)&GetNameCb;
        f[4] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&OpenCb;
        f[5] = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&CloseCb;
        return structPtr;
    }

    private static void FreeStreamfile(nint structPtr)
    {
        nint statePtr = Marshal.ReadIntPtr(structPtr);
        if (statePtr != 0)
            Marshal.FreeHGlobal(statePtr);
        Marshal.FreeHGlobal(structPtr);
    }

    // ── callbacks (called from native code) ──────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ReadCb(nint userData, byte* dst, int dstSize, long offset)
    {
        var s = (State*)userData;

        if (offset < 0 || offset >= s->DataLen || dstSize <= 0)
        {
            NativeMemory.Clear(dst, (nuint)dstSize);
            return 0;
        }

        int available = (int)Math.Min(dstSize, s->DataLen - offset);
        Buffer.MemoryCopy(s->DataPtr + offset, dst, dstSize, available);

        if (available < dstSize)
            NativeMemory.Clear(dst + available, (nuint)(dstSize - available));

        return available;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe long GetSizeCb(nint userData)
    {
        return ((State*)userData)->DataLen;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe nint GetNameCb(nint userData)
    {
        return (nint)((State*)userData)->NamePtr;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe nint OpenCb(nint userData, nint _filename)
    {
        var s = (State*)userData;
        return BuildStruct(s->DataPtr, s->DataLen, s->NamePtr);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CloseCb(nint libsf)
    {
        FreeStreamfile(libsf);
    }

    // ── state ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct State
    {
        public byte* DataPtr;
        public int DataLen;
        public byte* NamePtr;
    }
}
