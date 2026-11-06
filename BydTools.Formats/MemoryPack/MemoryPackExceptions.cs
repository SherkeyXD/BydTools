namespace BydTools.Formats.MemoryPack;

public class MemoryPackTypeException : Exception
{
    public MemoryPackTypeException(string message) : base(message) { }
}

public sealed class MemoryPackUnionException : MemoryPackTypeException
{
    public MemoryPackUnionException(
        string message,
        string staticType,
        int offset,
        int? tag,
        int? memberCount,
        IReadOnlyList<string> candidates
    ) : base(message)
    {
        StaticType = staticType;
        Offset = offset;
        Tag = tag;
        MemberCount = memberCount;
        Candidates = candidates;
    }

    public string StaticType { get; }
    public int Offset { get; }
    public int? Tag { get; }
    public int? MemberCount { get; }
    public IReadOnlyList<string> Candidates { get; }
}
