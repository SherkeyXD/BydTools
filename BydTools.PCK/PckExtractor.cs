using BydTools.Utils;
using BydTools.Wwise;

namespace BydTools.PCK;

/// <summary>
/// Extracts audio files from PCK archives, optionally mapping numeric IDs
/// to human-readable paths via <see cref="PckMapper"/>.
/// </summary>
public class PckExtractor
{
    private readonly ILogger _logger;

    public PckExtractor(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extracts all files from a PCK archive to <paramref name="outputDir"/>.
    /// BNK entries are expanded to their embedded WEM files.
    /// </summary>
    public void ExtractFiles(
        string pckPath,
        string outputDir,
        PckMapper? mapper = null,
        bool extractWem = true,
        bool extractBnk = true,
        bool extractPlg = true
    )
    {
        if (!File.Exists(pckPath))
            throw new FileNotFoundException("PCK file not found", pckPath);

        Directory.CreateDirectory(outputDir);

        using var fileStream = File.OpenRead(pckPath);
        var parser = new PckParser(fileStream);
        var content = parser.Parse();

        _logger.Info(
            $"Parsed {content.Entries.Count} entries, {content.Languages.Count} languages"
        );

        int savedCount = 0;

        foreach (var entry in content.Entries)
        {
            byte[] fileData = parser.GetFileData(entry);
            if (fileData.Length < 4)
                continue;

            ReadOnlySpan<byte> magic = fileData.AsSpan(0, 4);

            if (magic.SequenceEqual("BKHD"u8))
            {
                if (!extractBnk)
                    continue;

                savedCount += ExtractBnkWems(
                    fileData,
                    entry,
                    outputDir,
                    mapper,
                    content.Languages
                );
            }
            else if (magic.SequenceEqual("RIFF"u8) || magic.SequenceEqual("RIFX"u8))
            {
                if (!extractWem)
                    continue;

                string name = ResolveOutputName(entry.FileId, mapper, ".wem", content.Languages, entry.LanguageId);
                SaveFile(outputDir, name, fileData);
                savedCount++;
            }
            else if (magic.SequenceEqual("PLUG"u8))
            {
                if (!extractPlg)
                    continue;

                string name = ResolveOutputName(entry.FileId, mapper, ".plg", content.Languages, entry.LanguageId);
                SaveFile(outputDir, name, fileData);
                savedCount++;
            }
        }

        _logger.Info($"Extracted {savedCount} files");
    }

    private int ExtractBnkWems(
        byte[] bnkData,
        PckFileEntry bankEntry,
        string outputDir,
        PckMapper? mapper,
        List<PckLanguage> languages
    )
    {
        var wemEntries = BnkParser.Parse(bnkData);
        if (wemEntries.Count == 0)
            return 0;

        int count = 0;
        foreach (var wem in wemEntries)
        {
            byte[] wemData = new byte[wem.Size];
            Array.Copy(bnkData, wem.Offset, wemData, 0, wem.Size);

            string name = ResolveBnkWemName(bankEntry.FileId, wem.Id, mapper, ".wem");
            SaveFile(outputDir, name, wemData);
            count++;
        }

        return count;
    }

    internal static string ResolveOutputName(
        ulong fileId,
        PckMapper? mapper,
        string extension,
        List<PckLanguage>? languages = null,
        uint languageId = 0
    )
    {
        if (mapper != null)
        {
            string key = fileId.ToString();
            var mapped = mapper.GetMappedPath(key);
            if (mapped.HasValue)
            {
                string path = mapped.Value.Path;
                if (mapped.Value.Language != null)
                    path = Path.Combine(mapped.Value.Language, path);
                return Path.ChangeExtension(path, extension);
            }
        }

        if (languageId != 0 && languages != null)
        {
            var lang = languages.Find(l => l.Id == languageId);
            if (lang != null)
                return Path.Combine("unmapped", lang.Name, $"{fileId}{extension}");
        }

        return Path.Combine("unmapped", $"{fileId}{extension}");
    }

    internal static string ResolveBnkWemName(
        ulong bankFileId,
        uint wemId,
        PckMapper? mapper,
        string extension
    )
    {
        if (mapper != null)
        {
            string key = wemId.ToString();
            var mapped = mapper.GetMappedPath(key);
            if (mapped.HasValue)
            {
                string path = mapped.Value.Path;
                if (mapped.Value.Language != null)
                    path = Path.Combine(mapped.Value.Language, path);
                return Path.ChangeExtension(path, extension);
            }
        }

        return Path.Combine("unmapped", $"{bankFileId}_{wemId}{extension}");
    }

    private static void SaveFile(string baseDir, string relativePath, byte[] data)
    {
        string fullPath = Path.Combine(baseDir, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir != null)
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(fullPath, data);
    }
}
