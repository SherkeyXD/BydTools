using BydTools.Core;
using BydTools.Core.IO;

namespace BydTools.VFS;

/// <summary>
/// A game VFS directory. Blocks are discovered by decrypting every .blc,
/// so a catalog entry is not required to dump a block the client just added.
/// </summary>
public sealed class VfsArchive
{
    private VfsArchive(string root, string? persistentRoot, IReadOnlyList<VfsBlock> blocks)
    {
        Root = root;
        PersistentRoot = persistentRoot;
        Blocks = blocks;
    }

    public string Root { get; }
    public string? PersistentRoot { get; }
    public IReadOnlyList<VfsBlock> Blocks { get; }

    /// <summary>
    /// Opens StreamingAssets/VFS with an optional Persistent/VFS overlay.
    /// Each BLC and CHK is resolved independently, preferring Persistent.
    /// </summary>
    public static VfsArchive Open(
        string vfsDirectory,
        byte[] key,
        ILogger logger,
        string? persistentDirectory = null
    )
    {
        if (!Directory.Exists(vfsDirectory))
            throw new DirectoryNotFoundException($"VFS directory not found: {vfsDirectory}");

        string? persistentRoot = string.IsNullOrWhiteSpace(persistentDirectory) ? null : persistentDirectory;
        string[] roots = persistentRoot == null
            || string.Equals(Path.GetFullPath(persistentRoot), Path.GetFullPath(vfsDirectory), PathChecks.FileSystemComparison)
            ? [vfsDirectory]
            : [persistentRoot, vfsDirectory];

        var rootMaps = new List<Dictionary<string, string>>(roots.Length);
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
                map.TryAdd(Path.GetFileName(dir), dir);
            rootMaps.Add(map);
        }

        string[] directoryNames = rootMaps
            .SelectMany(static map => map.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var parsed = new (
            string DirName,
            string[] ChunkDirs,
            string? BlcPath,
            VfsBlockInfo? Info,
            Exception? Error
        )[directoryNames.Length];

        Parallel.For(
            0,
            directoryNames.Length,
            i =>
            {
                string dirName = directoryNames[i];
                string[] chunkDirectories = rootMaps
                    .Select(map => map.TryGetValue(dirName, out string? dir) ? dir : null)
                    .Where(static dir => dir != null)
                    .Cast<string>()
                    .ToArray();
                string? blcPath = chunkDirectories
                    .Select(dir => FindBlcPath(dir, dirName))
                    .FirstOrDefault(static path => path != null);
                if (blcPath == null)
                {
                    parsed[i] = (dirName, chunkDirectories, null, null, null);
                    return;
                }

                try
                {
                    VfsBlockInfo info = BlcReader.Read(blcPath, key);
                    parsed[i] = (dirName, chunkDirectories, blcPath, info, null);
                }
                catch (Exception ex)
                {
                    parsed[i] = (dirName, chunkDirectories, blcPath, null, ex);
                }
            }
        );

        var blocks = new List<VfsBlock>(directoryNames.Length);
        Exception? firstError = null;
        foreach (var item in parsed)
        {
            if (item.BlcPath == null)
            {
                if (logger.IsVerboseEnabled)
                    logger.Verbose("  [{0}] No BLC file found, skipping.", item.DirName);
                continue;
            }

            if (item.Error != null)
            {
                firstError ??= item.Error;
                logger.Error("  [{0}] Failed to parse BLC: {1}", item.DirName, item.Error.Message);
                continue;
            }

            VfsBlockInfo info = item.Info!;
            if (info.NonceVersion != VFSDefine.ProtocolVersion)
            {
                logger.Info(
                    "Warning: VFS version {0} does not match expected {1}.",
                    info.NonceVersion,
                    VFSDefine.ProtocolVersion
                );
            }

            BlockRegistry.TryGet(info.BlockType, out var byType);
            BlockRegistry.TryGetByName(info.GroupCfgName, out var byName);
            var descriptor = byName ?? byType;
            if (
                descriptor != null
                && !descriptor.DirHash.Equals(info.GroupCfgHashName, StringComparison.OrdinalIgnoreCase)
            )
            {
                logger.Info(
                    "Warning: {0} directory hash {1} differs from the catalog hash {2}.",
                    info.GroupCfgName,
                    info.GroupCfgHashName,
                    descriptor.DirHash
                );
            }

            blocks.Add(new VfsBlock(Path.GetDirectoryName(item.BlcPath)!, info, descriptor, key, item.ChunkDirs));
        }

        if (blocks.Count == 0 && firstError != null)
            throw firstError;

        return new VfsArchive(vfsDirectory, persistentRoot, blocks);
    }

    private static string? FindBlcPath(string directory, string dirName)
    {
        string exact = Path.Combine(directory, Path.GetFileName(directory) + ".blc");
        if (File.Exists(exact))
            return exact;

        return Directory
            .EnumerateFiles(directory, "*.blc")
            .FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).Equals(dirName, StringComparison.OrdinalIgnoreCase)
            );
    }

    public VfsBlock? Find(string nameOrType)
    {
        if (BlockRegistry.TryParse(nameOrType, out var descriptor))
        {
            string typeName = descriptor.Type.ToString();
            return Blocks.FirstOrDefault(b =>
                b.Info.BlockType == descriptor.Type
                || b.Info.GroupCfgName.Equals(typeName, StringComparison.OrdinalIgnoreCase)
            );
        }

        return Blocks.FirstOrDefault(b =>
            b.Info.GroupCfgName.Equals(nameOrType, StringComparison.OrdinalIgnoreCase)
        );
    }
}
