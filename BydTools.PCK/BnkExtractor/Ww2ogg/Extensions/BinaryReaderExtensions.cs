using System;
using System.IO;

namespace BnkExtractor.Ww2ogg.Extensions;

internal static class BinaryReaderExtensions
{
    internal static void seekg(this BinaryReader reader, int v, StreamPosition streamPosition)
    {
        switch (streamPosition)
        {
            case StreamPosition.Beginning:
                reader.BaseStream.Position = v;
                return;
            case StreamPosition.Current:
                reader.BaseStream.Position += v;
                return;
            case StreamPosition.End:
                reader.BaseStream.Position = v + reader.BaseStream.Length;
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(streamPosition));
        }
    }

    internal static void seekg(this BinaryReader reader, int position) =>
        reader.seekg(position, StreamPosition.Beginning);

    internal static int tellg(this BinaryReader reader)
    {
        return (int)reader.BaseStream.Position;
    }
}
