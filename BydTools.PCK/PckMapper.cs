using System.Text;

namespace BydTools.PCK;

/// <summary>
/// Reads ESFM-format .map files to resolve Wwise numeric IDs to
/// human-readable file paths (voicelines and music).
/// Port of AnimeWwise mapper.py.
/// </summary>
public class PckMapper
{
    private readonly string[] _languages;
    private readonly byte[] _strings;
    private readonly byte[] _words;
    private readonly byte[] _files;
    private readonly Dictionary<string, int> _keys;
    private readonly Dictionary<string, string> _musicKeys;

    public string GameName { get; }
    public IReadOnlyList<string> Languages => _languages;

    /// <summary>
    /// Loads the built-in beyond.map embedded resource.
    /// Returns <c>null</c> if the resource is not available.
    /// </summary>
    public static PckMapper? LoadBuiltIn()
    {
        var stream = typeof(PckMapper).Assembly
            .GetManifestResourceStream("beyond.map");
        return stream != null ? new PckMapper(stream) : null;
    }

    public PckMapper(string mapFilePath)
        : this(File.OpenRead(mapFilePath))
    { }

    public PckMapper(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        // ── Header ──────────────────────────────────────────────────
        Span<byte> magic = stackalloc byte[4];
        reader.BaseStream.ReadExactly(magic);
        if (!magic.SequenceEqual("ESFM"u8))
            throw new InvalidDataException("Invalid mapping file: expected ESFM magic");

        reader.ReadBytes(2); // reserved
        byte[] version = reader.ReadBytes(2);
        if (version[0] != 0x33 || version[1] != 0x30) // "30"
            throw new InvalidDataException("Incompatible mapping version");
        reader.ReadBytes(2); // reserved

        // ── Game config ─────────────────────────────────────────────
        int gameNameLen = reader.ReadByte();
        GameName = Encoding.UTF8.GetString(reader.ReadBytes(gameNameLen));
        reader.ReadByte(); // game version byte

        // ── Sector table (6 sectors × 2 × int24) ───────────────────
        int ReadInt24BE()
        {
            byte b0 = reader.ReadByte(), b1 = reader.ReadByte(), b2 = reader.ReadByte();
            return (b0 << 16) | (b1 << 8) | b2;
        }

        string[] sectorNames = ["languages", "strings", "words", "files", "keys", "music"];
        var sectors = new Dictionary<string, (int Offset, int Size)>(6);
        foreach (string name in sectorNames)
            sectors[name] = (ReadInt24BE(), ReadInt24BE());

        // ── Languages ───────────────────────────────────────────────
        reader.BaseStream.Position = sectors["languages"].Offset;
        int langCount = reader.ReadByte();
        _languages = new string[langCount];
        for (int i = 0; i < langCount; i++)
        {
            int len = reader.ReadByte();
            _languages[i] = Encoding.UTF8.GetString(reader.ReadBytes(len));
        }

        // ── Raw data sectors ────────────────────────────────────────
        reader.BaseStream.Position = sectors["strings"].Offset;
        _strings = reader.ReadBytes(sectors["strings"].Size);

        reader.BaseStream.Position = sectors["words"].Offset;
        _words = reader.ReadBytes(sectors["words"].Size);

        reader.BaseStream.Position = sectors["files"].Offset;
        _files = reader.ReadBytes(sectors["files"].Size);

        // ── Voiceline keys ──────────────────────────────────────────
        reader.BaseStream.Position = sectors["keys"].Offset;
        int keySize = reader.ReadByte();
        int keyDataLen = sectors["keys"].Size - 1;
        byte[] keysData = reader.ReadBytes(keyDataLen);

        int nKeys = keyDataLen / keySize;
        _keys = new Dictionary<string, int>(nKeys);

        for (int i = 0; i + keySize <= keysData.Length; i += keySize)
        {
            // First 3 bytes = packed value (24-bit BE): 2-bit lang | 22-bit file index
            int value = (keysData[i] << 16) | (keysData[i + 1] << 8) | keysData[i + 2];
            // Remaining bytes = key identifier (big-endian Wwise ID)
            int keyBytes = keySize - 3;
            ulong numericKey = 0;
            for (int b = 0; b < keyBytes; b++)
                numericKey = (numericKey << 8) | keysData[i + 3 + b];
            _keys.TryAdd(numericKey.ToString(), value);
        }

        // ── Music keys ──────────────────────────────────────────────
        _musicKeys = [];
        if (sectors["music"].Size > 0)
        {
            reader.BaseStream.Position = sectors["music"].Offset;
            int rootLen = reader.ReadByte();
            string root = Encoding.UTF8.GetString(reader.ReadBytes(rootLen));
            int nMusic = (reader.ReadByte() << 8) | reader.ReadByte(); // 2-byte BE count

            for (int i = 0; i < nMusic; i++)
            {
                byte[] kb = reader.ReadBytes(4);
                uint key = ((uint)kb[0] << 24) | ((uint)kb[1] << 16) | ((uint)kb[2] << 8) | kb[3];
                int nameLen = reader.ReadByte();
                string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
                _musicKeys.TryAdd(key.ToString(), $@"{root}\{name}");
            }
        }
    }

    /// <summary>
    /// Resolves a Wwise ID key string to a mapped file path.
    /// Returns <c>null</c> if the key has no mapping.
    /// </summary>
    /// <param name="key">
    /// The file name without extension as produced by the PCK parser
    /// (e.g. "305419896" for a sound, or "12345678_9876" for a BNK-embedded WEM).
    /// </param>
    public (string Path, string? Language)? GetMappedPath(string key)
    {
        if (_musicKeys.TryGetValue(key, out string? musicPath))
            return (musicPath, null);

        if (!_keys.TryGetValue(key, out int packed))
            return null;

        int langIndex = (packed >> 22) & 0x03;
        int fileOffset = packed & 0x3FFFFF;

        string path = ResolvePath(fileOffset);
        string? language = langIndex > 0 && langIndex < _languages.Length
            ? _languages[langIndex]
            : null;

        return (path, language);
    }

    /// <summary>
    /// Walks the files → words → strings hierarchy to build a path string.
    /// </summary>
    private string ResolvePath(int fileOffset)
    {
        int partCount = _files[fileOffset];
        var pathSegments = new string[partCount];

        for (int i = 0; i < partCount; i++)
        {
            int idx = fileOffset + 1 + 3 * i;
            int wordOffset = (_files[idx] << 16) | (_files[idx + 1] << 8) | _files[idx + 2];

            int wordPartCount = _words[wordOffset];
            var wordSegments = new string[wordPartCount];

            for (int j = 0; j < wordPartCount; j++)
            {
                int widx = wordOffset + 1 + 2 * j;
                int stringOffset = (_words[widx] << 8) | _words[widx + 1];
                wordSegments[j] = DecodeString(stringOffset);
            }

            pathSegments[i] = string.Join("_", wordSegments);
        }

        return string.Join(@"\", pathSegments);
    }

    private string DecodeString(int offset)
    {
        int sizeOrFlag = _strings[offset];

        if (sizeOrFlag > 128)
        {
            // Numeric value: (sizeOrFlag - 128) bytes, big-endian integer
            int numBytes = sizeOrFlag - 128;
            long value = 0;
            for (int k = 0; k < numBytes; k++)
                value = (value << 8) | _strings[offset + 1 + k];
            return value.ToString();
        }

        return Encoding.UTF8.GetString(_strings, offset + 1, sizeOrFlag);
    }
}
