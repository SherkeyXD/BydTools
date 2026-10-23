using System.Buffers.Binary;
using System.Text;

namespace WemDecoder.Tests.Support;

/// <summary>Assembles RIFF (little-endian) or RIFX (big-endian) WEM files chunk by chunk.</summary>
internal sealed class WemBuilder(bool bigEndian = false)
{
    private readonly MemoryStream _chunks = new();

    public bool BigEndian => bigEndian;

    /// <summary>Offset of the next chunk's body from the start of the file.</summary>
    public int NextBodyOffset => 12 + (int)_chunks.Length + 8;

    public WemBuilder Chunk(string id, ReadOnlySpan<byte> body, uint? declaredSize = null)
    {
        _chunks.Write(Encoding.ASCII.GetBytes(id));
        _chunks.Write(U32(declaredSize ?? (uint)body.Length));
        _chunks.Write(body);
        return this;
    }

    public byte[] Build()
    {
        var file = new MemoryStream();
        file.Write(bigEndian ? "RIFX"u8 : "RIFF"u8);
        file.Write(U32((uint)(4 + _chunks.Length)));
        file.Write("WAVE"u8);
        _chunks.WriteTo(file);
        return file.ToArray();
    }

    /// <summary>A fmt chunk body: WAVEFORMATEX fields, then <paramref name="extra"/> preceded by its size.</summary>
    public byte[] Fmt(ushort format, int channels, int sampleRate, ushort blockAlign, ushort bitsPerSample, ReadOnlySpan<byte> extra)
    {
        var body = new MemoryStream();
        body.Write(U16(format));
        body.Write(U16((ushort)channels));
        body.Write(U32((uint)sampleRate));
        body.Write(U32((uint)(sampleRate * Math.Max(1, (int)blockAlign))));
        body.Write(U16(blockAlign));
        body.Write(U16(bitsPerSample));
        body.Write(U16((ushort)extra.Length));
        body.Write(extra);
        return body.ToArray();
    }

    /// <summary>Wwise's channel config: count, config type 1 (standard speakers) and speaker mask.</summary>
    public static uint ChannelConfig(int channels, uint speakerMask) => (uint)channels | (1u << 8) | (speakerMask << 12);

    public byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    public byte[] U32(uint value)
    {
        var bytes = new byte[4];
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    public void Put16(Span<byte> target, ushort value) => U16(value).CopyTo(target);

    public void Put32(Span<byte> target, uint value) => U32(value).CopyTo(target);
}
