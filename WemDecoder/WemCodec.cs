namespace WemDecoder;

/// <summary>
/// Audio codec of a WEM stream.
/// </summary>
public enum WemCodec
{
    /// <summary>16-bit PCM (format tags <c>0x0001</c> and <c>0xFFFE</c>).</summary>
    Pcm,

    /// <summary>Wwise Vorbis (format tag <c>0xFFFF</c>).</summary>
    Vorbis,

    /// <summary>Wwise Opus, used since Wwise 2019.2.3 (format tag <c>0x3041</c>).</summary>
    Opus,
}
