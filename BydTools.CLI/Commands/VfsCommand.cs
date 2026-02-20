using BydTools.VFS;
using BydTools.VFS.PostProcessors;

namespace BydTools.CLI.Commands;

sealed class VfsCommand : ICommand
{
    public string Name => "vfs";
    public string Description => "Dump files from VFS";

    public void PrintHelp(string exeName)
    {
        HelpFormatter.WriteUsage(
            "vfs",
            "--input <path> --output <dir> --blocktype <type>[,type2,...]"
        );

        HelpFormatter.WriteSectionHeader("Required");
        HelpFormatter.WriteEntry(
            "-i, --input <path>",
            "Game data directory that contains the VFS folder"
        );
        HelpFormatter.WriteEntry("-o, --output <dir>", "Output directory");
        HelpFormatter.WriteEntry(
            "-t, --blocktype <type>",
            "Block type to dump (name or numeric value)"
        );
        HelpFormatter.WriteEntryContinuation(
            "Multiple types can be separated by comma, e.g. Bundle,Lua,Table"
        );
        HelpFormatter.WriteBlankLine();

        HelpFormatter.WriteSectionHeader("Options");
        HelpFormatter.WriteEntry(
            "--debug",
            "Scan subfolders and print block info (no extraction)"
        );
        HelpFormatter.WriteEntry(
            "--key <base64>",
            "Custom ChaCha20 key in Base64"
        );
        HelpFormatter.WriteCommonOptions();
        HelpFormatter.WriteBlankLine();

        HelpFormatter.WriteEnumValues("Available block types", VFSDumper.BlockHashMap.Keys);
    }

    public void Execute(string[] args)
    {
        var parser = new ArgParser()
            .AddFlag("help", "h")
            .AddFlag("verbose", "v")
            .AddFlag("debug")
            .AddOption("input", "i")
            .AddOption("output", "o")
            .AddOption("blocktype", "t")
            .AddOption("key");

        if (!parser.TryParse(args))
        {
            foreach (var error in parser.Errors)
                Console.Error.WriteLine(error);
            PrintHelp(Program.ExecutableName);
            return;
        }

        if (parser.GetFlag("help"))
        {
            PrintHelp(Program.ExecutableName);
            return;
        }

        var gamePath = parser.GetValue("input");
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            Console.Error.WriteLine("Error: --input is required.");
            PrintHelp(Program.ExecutableName);
            return;
        }

        var outputDir = parser.GetValue("output");
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            Console.Error.WriteLine("Error: --output is required.");
            PrintHelp(Program.ExecutableName);
            return;
        }

        var streamingAssetsPath = Path.Combine(gamePath, VFSDefine.VFS_DIR);
        if (!Directory.Exists(streamingAssetsPath))
        {
            Console.Error.WriteLine(
                "Error: VFS directory ({1}) not found under \"{0}\".",
                gamePath,
                VFSDefine.VFS_DIR
            );
            return;
        }

        byte[]? customKey = null;
        var keyBase64 = parser.GetValue("key");
        if (!string.IsNullOrWhiteSpace(keyBase64))
        {
            try
            {
                customKey = Convert.FromBase64String(keyBase64);
            }
            catch (FormatException)
            {
                Console.Error.WriteLine("Error: --key must be a valid Base64 string.");
                return;
            }

            if (customKey.Length != VFSDefine.KEY_LEN)
            {
                Console.Error.WriteLine(
                    "Error: --key must decode to {0} bytes (got {1}).",
                    VFSDefine.KEY_LEN,
                    customKey.Length
                );
                return;
            }
        }

        var logger = new Logger(parser.GetFlag("verbose"));
        var postProcessors = PostProcessorFactory.CreateProcessors(logger);
        IVFSDumper dumper = new VFSDumper(logger, postProcessors, customKey);

        if (parser.GetFlag("debug"))
        {
            dumper.DebugScanBlocks(streamingAssetsPath);
            return;
        }

        var blockTypeString = parser.GetValue("blocktype");
        if (string.IsNullOrWhiteSpace(blockTypeString))
        {
            Console.Error.WriteLine("Error: --blocktype is required.");
            HelpFormatter.WriteBlankLine();
            HelpFormatter.WriteEnumValues(
                "Available block types",
                VFSDumper.BlockHashMap.Keys
            );
            return;
        }

        var blockTypes = ParseBlockTypes(blockTypeString);
        if (blockTypes == null)
            return;

        Console.WriteLine("Input:  {0}", streamingAssetsPath);
        Console.WriteLine("Output: {0}", outputDir);

        for (int i = 0; i < blockTypes.Count; i++)
        {
            if (i > 0)
                Console.WriteLine();
            dumper.DumpAssetByType(streamingAssetsPath, blockTypes[i], outputDir);
        }
    }

    private static List<EVFSBlockType>? ParseBlockTypes(string raw)
    {
        var segments = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<EVFSBlockType>(segments.Length);

        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (Enum.TryParse<EVFSBlockType>(trimmed, ignoreCase: true, out var parsed))
            {
                result.Add(parsed);
            }
            else if (
                byte.TryParse(trimmed, out var btValue)
                && Enum.IsDefined(typeof(EVFSBlockType), btValue)
            )
            {
                result.Add((EVFSBlockType)btValue);
            }
            else
            {
                Console.Error.WriteLine(
                    "Error: failed to parse blocktype \"{0}\".",
                    trimmed
                );
                HelpFormatter.WriteBlankLine();
                HelpFormatter.WriteEnumValues(
                    "Available block types",
                    VFSDumper.BlockHashMap.Keys
                );
                return null;
            }
        }

        return result;
    }
}
