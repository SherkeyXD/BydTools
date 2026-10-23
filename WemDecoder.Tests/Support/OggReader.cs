namespace WemDecoder.Tests.Support;

/// <summary>Splits a single-stream Ogg file into packets.</summary>
internal static class OggReader
{
    /// <param name="ogg">The Ogg file.</param>
    /// <param name="lastGranule">Granule position of the last page (the sample count, for Vorbis).</param>
    public static List<byte[]> ReadPackets(ReadOnlySpan<byte> ogg, out long lastGranule)
    {
        var packets = new List<byte[]>();
        var packet = new MemoryStream();
        int offset = 0;
        lastGranule = 0;
        while (offset + 27 <= ogg.Length)
        {
            if (!ogg.Slice(offset, 4).SequenceEqual("OggS"u8))
                throw new InvalidDataException($"Missing Ogg page at 0x{offset:X}.");

            lastGranule = BitConverter.ToInt64(ogg.Slice(offset + 6, 8));
            int segments = ogg[offset + 26];
            int body = offset + 27 + segments;
            for (int i = 0; i < segments; i++)
            {
                int lacing = ogg[offset + 27 + i];
                packet.Write(ogg.Slice(body, lacing));
                body += lacing;
                if (lacing < 255)
                {
                    packets.Add(packet.ToArray());
                    packet.SetLength(0);
                }
            }

            offset = body;
        }

        return packets;
    }
}
