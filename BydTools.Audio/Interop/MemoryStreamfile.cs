using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BydTools.Audio.Interop;

/// <summary>
/// In-memory libstreamfile_t (vgmstream API 1.x). The name ends in .wem because
/// vgmstream picks the format from the extension. <c>open</c> returns another
/// view of the same buffer, which is what vgmstream does when it reopens a file.
/// </summary>
internal sealed unsafe class MemoryStreamfile : IDisposable
{
    private State? _state;
    private LibStreamfile* _native;
    private int _disposed;

    private MemoryStreamfile(State state, LibStreamfile* native)
    {
        _state = state;
        _native = native;
    }

    internal nint Native => (nint)_native;

    public static MemoryStreamfile Create(ReadOnlyMemory<byte> data)
    {
        if (!MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) || segment.Array is null)
            throw new NotSupportedException("WEM data must be backed by a byte array.");

        var state = new State(segment.Array, segment.Offset, segment.Count);
        return new MemoryStreamfile(state, Allocate(state));
    }

    internal int ReadAt(long offset, Span<byte> destination)
    {
        var state = _state ?? throw new ObjectDisposedException(nameof(MemoryStreamfile));
        if (offset < 0 || offset >= state.Length || destination.IsEmpty)
            return 0;
        int count = (int)Math.Min(destination.Length, state.Length - offset);
        state.Bytes.AsSpan(state.Offset + (int)offset, count).CopyTo(destination);
        return count;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _state = null;
        if (_native != null)
        {
            Release(_native);
            _native = null;
        }
    }

    private static LibStreamfile* Allocate(State state)
    {
        var native = (LibStreamfile*)NativeMemory.AllocZeroed((nuint)sizeof(LibStreamfile));
        native->UserData = GCHandle.ToIntPtr(state.Self);
        native->Read = &Read;
        native->GetSize = &GetSize;
        native->GetName = &GetName;
        native->Open = &Open;
        native->Close = &Close;
        return native;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(nint user, byte* dst, long offset, int length)
    {
        var state = State.From(user);
        if (state == null || offset < 0 || length <= 0 || offset >= state.Length)
            return 0;
        int count = (int)Math.Min(length, state.Length - offset);
        fixed (byte* src = state.Bytes)
        {
            Buffer.MemoryCopy(src + state.Offset + offset, dst, count, count);
        }

        return count;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long GetSize(nint user)
    {
        var state = State.From(user);
        return state?.Length ?? 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* GetName(nint user)
    {
        var state = State.From(user);
        return state == null ? null : state.Name;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static LibStreamfile* Open(nint user, byte* filename)
    {
        var state = State.From(user);
        if (state == null)
            return null;
        var child = new State(state.Bytes, state.Offset, state.Length);
        return Allocate(child);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Close(LibStreamfile* streamfile) => Release(streamfile);

    private static void Release(LibStreamfile* streamfile)
    {
        if (streamfile == null)
            return;
        var handle = GCHandle.FromIntPtr(streamfile->UserData);
        if (handle.Target is State state)
            state.Release();
        handle.Free();
        NativeMemory.Free(streamfile);
    }

    private sealed class State
    {
        public State(byte[] bytes, int offset, int length)
        {
            Bytes = bytes;
            Offset = offset;
            Length = length;
            Self = GCHandle.Alloc(this);
            Name = (byte*)NativeMemory.Alloc(10);
            "audio.wem\0"u8.CopyTo(new Span<byte>(Name, 10));
        }

        public byte[] Bytes { get; }
        public int Offset { get; }
        public int Length { get; }
        public GCHandle Self { get; }
        public byte* Name { get; private set; }

        public static State? From(nint user)
        {
            if (user == 0)
                return null;
            var handle = GCHandle.FromIntPtr(user);
            return handle.Target as State;
        }

        public void Release()
        {
            if (Name != null)
            {
                NativeMemory.Free(Name);
                Name = null;
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LibStreamfile
{
    public nint UserData;
    public delegate* unmanaged[Cdecl]<nint, byte*, long, int, int> Read;
    public delegate* unmanaged[Cdecl]<nint, long> GetSize;
    public delegate* unmanaged[Cdecl]<nint, byte*> GetName;
    public delegate* unmanaged[Cdecl]<nint, byte*, LibStreamfile*> Open;
    public delegate* unmanaged[Cdecl]<LibStreamfile*, void> Close;
}
