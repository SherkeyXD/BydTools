using BydTools.Extraction.Vfs;
using BydTools.Formats.MemoryPack;
using BydTools.VFS;

namespace BydTools.CLI.Commands;

public sealed class VfsCommand : ICommand
{
    public string Name => "vfs";
    public string Description => "Parse VFS block declarations and dump assets";

    public int Execute(string[] args, CancellationToken cancellationToken)
    {
        var parser = CreateParser();
        if (!parser.Parse(args))
            return CliHelpers.Fail(2, parser.Errors[0]);
        if (parser.HasFlag("help"))
        {
            PrintHelp(parser);
            return 0;
        }

        bool debug = parser.HasFlag("debug");
        if (!parser.ValidateRequired(opt => debug ? opt.LongName == "input" : opt.Required))
            return CliHelpers.Fail(2, parser.Errors[0]);

        if (!CliHelpers.TryResolveVfsDirectory(parser.Get("input")!, out string vfsPath, out string? vfsError))
            return CliHelpers.Fail(1, vfsError!);

        byte[] key;
        int jobs;
        try
        {
            key = VFSDefine.ResolveKey(parser.Get("platform"), parser.Get("key"));
            jobs = CliHelpers.ResolveJobs(parser.Get("jobs"));
        }
        catch (ArgumentException ex)
        {
            return CliHelpers.Fail(2, ex.Message);
        }

        bool rawMode = parser.HasFlag("raw");
        bool verbose = parser.HasFlag("verbose");
        var logger = new Logger(verbose);
        VfsArchive archive;
        try
        {
            string? persistentVfs = CliHelpers.ResolvePersistentVfs(parser.Get("persistent"), logger);
            archive = VfsArchive.Open(vfsPath, key, logger, persistentVfs);
        }
        catch (Exception ex)
        {
            return CliHelpers.Fail(1, ex.Message);
        }

        if (debug)
        {
            new VfsExtractor(logger).WriteDebugReport(archive);
            return 0;
        }

        var blocks = new List<VfsBlock>();
        foreach (string token in parser.Get("blocktype")!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var block = archive.Find(token);
            if (block == null)
            {
                bool known = BlockRegistry.TryParse(token, out _);
                return known
                    ? CliHelpers.Fail(1, "Block \"{0}\" was not found in this VFS.", token)
                    : CliHelpers.Fail(2, "Unknown block type: {0}", token);
            }

            blocks.Add(block);
        }

        MemoryPackSchema? jsonSchema = null;
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? unionMaps = null;
        if (!rawMode)
        {
            string? schemaDir = parser.Get("schema-dir");
            if (!string.IsNullOrWhiteSpace(schemaDir))
            {
                try
                {
                    MemoryPackExport.Paths paths = MemoryPackExport.Resolve(schemaDir);
                    jsonSchema = MemoryPackSchema.Load(paths.Schema, paths.TypeIndex);
                    unionMaps = string.IsNullOrWhiteSpace(paths.UnionMap)
                        ? MemoryPackResources.UnionMaps
                        : JsonDataMemoryPack.LoadUnionMap(paths.UnionMap);
                }
                catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException or IOException)
                {
                    return CliHelpers.Fail(2, ex.Message);
                }
            }
        }

        var extractor = new VfsExtractor(
            logger,
            rawJsonData: rawMode,
            jsonSchema: jsonSchema,
            unionMaps: unionMaps,
            rawBundles: rawMode,
            rawManifests: rawMode);

        string outputPath = Path.GetFullPath(parser.Get("output")!);
        int totalExtracted = 0;
        int totalFailed = 0;
        try
        {
            CliHelpers.RunWithProgress(logger, verbose, createProgress =>
            {
                foreach (var block in blocks)
                {
                    var result = extractor.Dump(
                        block,
                        outputPath,
                        jobs,
                        createProgress(block.DisplayName),
                        cancellationToken
                    );
                    totalExtracted += result.Extracted;
                    totalFailed += result.Failed;
                }
            });
        }
        catch (Exception ex) when (CliHelpers.IsCancel(ex))
        {
            return CliHelpers.Fail(1, "Cancelled.");
        }
        catch (Exception ex)
        {
            return CliHelpers.Fail(1, ex.Message);
        }

        return totalFailed > 0 ? 1 : 0;
    }

    internal static ArgParser CreateParser() =>
        new ArgParser()
            .AddInputAndOutput("Output directory for extracted files")
            .Add(
                "blocktype",
                "t",
                "Block type to extract",
                "type",
                required: true,
                "Separate multiple types with commas."
            )
            .AddCryptoAndJobs("How many chunks to read at once")
            .Add("raw", null, "Write JsonData, Bundle (.ab), and BundleManifest (.hgmmap) without post-VFS decoding")
            .Add("schema-dir", null, "beyond-mp-schema export directory (overrides embedded)", "dir",
                continuation: "Loads schemas/type_index/union-map/unmanaged/enums as .msgpack.lz4 or .json. Ignored with --raw.")
            .Add("debug", "d", "Scan block declarations without extracting")
            .Add("verbose", "v", "Print per-chunk and per-file details")
            .Add("help", "h", "Show help information");

    private static void PrintHelp(ArgParser parser)
    {
        HelpFormatter.WriteCommandHelp(
            "VFS asset dumper",
            "vfs --input <path> --output <dir> --blocktype <type> [--persistent <path>]",
            parser,
            () =>
            {
                HelpFormatter.WriteEnumValues("Available block types", BlockRegistry.All.Select(static d => d.Type));
                HelpFormatter.WriteSection("Notes");
                Console.WriteLine("  JsonData .json files are decoded to plaintext using the embedded MemoryPack schema.");
                Console.WriteLine("  InitBundle/Bundle .ab files are decrypted and repacked as standard UnityFS bundles");
                Console.WriteLine("  (standard UnityFS/UnityWeb/UnityRaw/UnityArchive inputs pass through unchanged).");
                Console.WriteLine("  BundleManifest .hgmmap files are Brotli-decompressed to <name>.hgmmap.bin (binary");
                Console.WriteLine("  manifest, not JSON). --raw copies JsonData, .ab, and .hgmmap files unchanged.");
                Console.WriteLine("  --schema-dir overrides the embed. StringPool*.byte in the same block is used for");
                Console.WriteLine("  string ids, then copied as-is. Lua .lua files are decrypted in place. Table .bytes");
                Console.WriteLine("  become SparkBuffer JSON.");
                Console.WriteLine();
            }
        );
    }
}
