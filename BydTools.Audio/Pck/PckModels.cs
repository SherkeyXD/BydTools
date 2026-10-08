namespace BydTools.Audio.Pck;

public enum PckSectorType
{
    Bank,
    Sound,
    External,
}

public sealed record PckLanguage(uint Id, string Name);

public sealed record PckFileEntry(
    ulong FileId,
    uint Size,
    long Offset,
    uint LanguageId,
    PckSectorType SectorType
);
