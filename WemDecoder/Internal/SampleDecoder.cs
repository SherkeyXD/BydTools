namespace WemDecoder.Internal;

/// <summary>
/// Codec-specific source of interleaved PCM16 frames.
/// </summary>
internal abstract class SampleDecoder : IDisposable
{
    /// <summary>
    /// Decodes up to <c>destination.Length / channels</c> frames.
    /// </summary>
    /// <returns>Frames written; 0 once the codec has no more data.</returns>
    public abstract int Read(Span<short> destination);

    public virtual void Dispose()
    {
    }

    /// <summary>
    /// Float to PCM16 the way vgmstream converts (scale by 32767, truncate, clamp).
    /// </summary>
    public static void FloatToPcm16(ReadOnlySpan<float> source, Span<short> destination)
    {
        for (int i = 0; i < source.Length; i++)
            destination[i] = (short)Math.Clamp((int)(source[i] * 32767.0f), short.MinValue, short.MaxValue);
    }
}
