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
        if (!CliHelpers.TryParseAndValidate(
            parser,
            args,
            PrintHelp,
            out int validationExit,
            opt =>
            {
                if (parser.HasFlag("debug"))
                    return opt.LongName == "input";
                if (parser.HasFlag("check"))
                    return opt.LongName is "input" or "output";
                return opt.Required;
            }))
        {
            return validationExit;
        }

        bool debug = parser.HasFlag("debug");
        bool check = parser.HasFlag("check");
        if (debug && check)
            return CliHelpers.Fail(2, "--check cannot be combined with --debug.");
        if (check && parser.Get("blocktype") != null)
            return CliHelpers.Fail(2, "--check cannot be combined with --blocktype.");

        if (!VfsSession.TryOpen(parser, requireOutput: !debug, out VfsSession? session, out int openExit))
            return openExit;

        if (debug)
        {
            new VfsExtractor(session.Logger).WriteDebugReport(session.Archive);
            return 0;
        }

        var blocks = new List<VfsBlock>();
        if (!check)
        {
            foreach (string token in parser.Get("blocktype")!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!session.TryFindBlock(token, out VfsBlock? block, out int findExit))
                    return findExit;
                blocks.Add(block);
            }
        }

        bool rawMode = parser.HasFlag("raw");
        MemoryPackSchema? jsonSchema = null;
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? unionMaps = null;
        if (!rawMode)
        {
            string? schemaDir = parser.Get("schema-dir");
            if (schemaDir != null)
            {
                try
                {
                    MemoryPackExport.Paths paths = MemoryPackExport.Resolve(schemaDir);
                    jsonSchema = MemoryPackSchema.Load(paths.Schema, paths.TypeIndex);
                    unionMaps = string.IsNullOrWhiteSpace(paths.UnionMap)
                        ? MemoryPackResources.UnionMaps
                        : JsonDataMemoryPack.LoadUnionMap(paths.UnionMap);
                }
                catch (ArgumentException ex)
                {
                    return CliHelpers.Fail(
                        2,
                        string.IsNullOrWhiteSpace(schemaDir) ? "--schema-dir cannot be empty." : ex.Message);
                }
                catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException)
                {
                    return CliHelpers.Fail(2, ex.Message);
                }
            }
        }

        var extractor = new VfsExtractor(
            session.Logger,
            rawJsonData: rawMode,
            jsonSchema: jsonSchema,
            unionMaps: unionMaps,
            rawBundles: rawMode,
            rawManifests: rawMode);

        string outputPath = session.OutputPath!;
        if (check)
        {
            return session.RunWithProgress(createProgress =>
            {
                VfsCheckResult result = extractor.Check(
                    session.Archive,
                    outputPath,
                    createProgress("Checking"),
                    cancellationToken
                );
                return result.Failed > 0 || result.Passed == 0 ? 1 : 0;
            });
        }

        return session.RunWithProgress(createProgress =>
        {
            int totalFailed = 0;
            foreach (var block in blocks)
            {
                var result = extractor.Dump(
                    block,
                    outputPath,
                    session.Jobs,
                    createProgress(block.DisplayName),
                    cancellationToken
                );
                totalFailed += result.Failed;
            }

            return totalFailed > 0 ? 1 : 0;
        });
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
            .Add("check", "c", "Extract one file per available block type to check the pipeline")
            .Add("debug", "d", "Scan block declarations without extracting")
            .Add("verbose", "v", "Print per-chunk and per-file details")
            .Add("help", "h", "Show help information");

    private static void PrintHelp(ArgParser parser)
    {
        HelpFormatter.WriteCommandHelp(
            "VFS asset dumper",
            "vfs --input <path> --output <dir> (--blocktype <type> | --check) [--persistent <path>]",
            parser,
            () =>
            {
                HelpFormatter.WriteEnumValues("Available block types", BlockRegistry.All.Select(static d => d.Type));
                HelpFormatter.WriteSection("Notes");
                Console.WriteLine("  --check scans all locally available blocks and exports one file per block under");
                Console.WriteLine("  <output>/<block>/ to test decryption and decoding after a game update.");
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
