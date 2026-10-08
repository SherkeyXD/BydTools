namespace BydTools.Extraction;

public interface IProgressSink
{
    void AddTotal(long amount);
    void Advance(long amount);
}

public sealed class NullProgress : IProgressSink
{
    public static readonly NullProgress Instance = new();

    private NullProgress() { }

    public void AddTotal(long amount) { }

    public void Advance(long amount) { }
}
