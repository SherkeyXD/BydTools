namespace BydTools.PCK;

/// <summary>
/// Sector type in a PCK (AKPK) archive header.
/// </summary>
public enum PckSectorType
{
    Bank,
    Sound,
    External
}

/// <summary>
/// Language entry parsed from a PCK header.
/// </summary>
public record PckLanguage(uint Id, string Name);

/// <summary>
/// A file entry parsed from a PCK archive.
/// </summary>
/// <param name="FileId">Wwise file/sound ID (uint32 or uint64 for externals).</param>
/// <param name="Size">File data size in bytes.</param>
/// <param name="Offset">Absolute byte offset within the PCK file.</param>
/// <param name="LanguageId">Associated language ID (0 = SFX / language-independent).</param>
/// <param name="SectorType">Which sector this entry belongs to.</param>
public record PckFileEntry(
    ulong FileId,
    uint Size,
    long Offset,
    uint LanguageId,
    PckSectorType SectorType
);

/// <summary>
/// Complete parsed result of a PCK archive header.
/// </summary>
public record PckContent(
    List<PckLanguage> Languages,
    List<PckFileEntry> Entries
);
