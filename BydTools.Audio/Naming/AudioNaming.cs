using BydTools.Audio.Pck;

namespace BydTools.Audio.Naming;

public static class AudioNaming
{
    public static string ResolveWem(
        string blockFolder,
        string? blockLanguage,
        ulong fileId,
        PckMapper? mapper,
        string extension,
        IReadOnlyList<PckLanguage>? languages,
        uint languageId
    )
    {
        string? mapped = mapper?.GetMappedPath(fileId);
        if (mapped != null)
            return BuildMapped(blockFolder, blockLanguage, mapped, extension);

        return Unmapped(blockFolder, LanguageFolder(blockLanguage, languages, languageId), $"{fileId}{extension}");
    }

    public static string ResolveBnkWem(
        string blockFolder,
        string? blockLanguage,
        ulong bankFileId,
        uint wemId,
        PckMapper? mapper,
        string extension,
        IReadOnlyList<PckLanguage>? languages,
        uint languageId
    )
    {
        string? mapped = mapper?.GetMappedPath(wemId);
        if (mapped != null)
            return BuildMapped(blockFolder, blockLanguage, mapped, extension);

        string language = LanguageFolder(blockLanguage, languages, languageId);
        return Unmapped(blockFolder, language, $"{bankFileId}_{wemId}{extension}");
    }

    private static string LanguageFolder(
        string? blockLanguage,
        IReadOnlyList<PckLanguage>? languages,
        uint languageId
    )
    {
        if (languageId != 0 && languages != null)
        {
            foreach (var language in languages)
            {
                if (language.Id == languageId)
                    return Normalize(language.Name);
            }
        }

        return Normalize(blockLanguage);
    }

    private static string BuildMapped(
        string blockFolder,
        string? blockLanguage,
        string mappedPath,
        string extension
    )
    {
        string withExtension = Path.ChangeExtension(mappedPath, extension);
        if (!TryParseMapped(withExtension, out var audioType, out var language, out var realPath))
        {
            return Unmapped(blockFolder, Normalize(blockLanguage), Path.GetFileName(withExtension));
        }

        return Path.Combine(blockFolder, Normalize(language), audioType, realPath);
    }

    private static bool TryParseMapped(
        string mappedPath,
        out string audioType,
        out string language,
        out string realPath
    )
    {
        string[] parts = mappedPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            audioType = "unknown";
            language = "Unknown";
            realPath = Path.GetFileName(mappedPath);
            return false;
        }

        audioType = parts[0];
        language = parts[1];
        realPath = Path.Combine(parts[2..]);
        return true;
    }

    private static string Unmapped(string blockFolder, string language, string fileName) =>
        Path.Combine(blockFolder, "unmapped", language, fileName);

    private static string Normalize(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
            return "Unknown";
        string trimmed = languageName.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }
}
