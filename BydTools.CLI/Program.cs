using System.Reflection;
using BydTools.PCK;
using BydTools.VFS;

namespace BydTools.CLI;

class Program
{
    private static string GetExecutableName()
    {
        return Path.GetFileNameWithoutExtension(
            Assembly.GetEntryAssembly()?.GetName().Name
                ?? Assembly.GetExecutingAssembly().GetName().Name
                ?? "BydTools.CLI"
        );
    }

    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return;
        }

        var subcommand = args[0].ToLowerInvariant();

        if (subcommand == "vfs")
        {
            HandleVFSCommand(args.Skip(1).ToArray());
        }
        else if (subcommand == "pck")
        {
            HandlePCKCommand(args.Skip(1).ToArray());
        }
        else if (subcommand == "-h" || subcommand == "--help")
        {
            PrintHelp();
        }
        else
        {
            Console.Error.WriteLine("Unknown command: {0}", subcommand);
            PrintHelp();
        }
    }

    static void HandleVFSCommand(string[] args)
    {
        var parser = new ArgParser()
            .AddFlag("help", "h")
            .AddFlag("verbose", "v")
            .AddFlag("debug")
            .AddOption("gamepath")
            .AddOption("blocktype")
            .AddOption("output");

        if (!parser.TryParse(args))
        {
            foreach (var error in parser.Errors)
                Console.Error.WriteLine(error);
            PrintVFSHelp();
            return;
        }

        if (parser.GetFlag("help"))
        {
            PrintVFSHelp();
            return;
        }

        var gamePath = parser.GetValue("gamepath");
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            Console.Error.WriteLine("Error: --gamepath is required.");
            PrintVFSHelp();
            return;
        }

        var outputDir = parser.GetValue("output")
            ?? Path.Combine(AppContext.BaseDirectory, "Assets");

        EVFSBlockType dumpAssetType = EVFSBlockType.All;
        var blockTypeString = parser.GetValue("blocktype");
        if (!string.IsNullOrWhiteSpace(blockTypeString))
        {
            if (
                !Enum.TryParse<EVFSBlockType>(
                    blockTypeString,
                    ignoreCase: true,
                    out dumpAssetType
                )
            )
            {
                if (
                    byte.TryParse(blockTypeString, out var btValue)
                    && Enum.IsDefined(typeof(EVFSBlockType), btValue)
                )
                {
                    dumpAssetType = (EVFSBlockType)btValue;
                }
                else
                {
                    Console.Error.WriteLine(
                        "Error: failed to parse blocktype \"{0}\".",
                        blockTypeString
                    );
                    Console.Error.WriteLine(
                        "Available types: {0}",
                        string.Join(", ", VFSDumper.BlockHashMap.Keys)
                    );
                    return;
                }
            }
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

        var logger = new Logger(parser.GetFlag("verbose"));
        var dumper = new VFSDumper(logger);

        if (parser.GetFlag("debug"))
        {
            dumper.DebugScanBlocks(streamingAssetsPath);
            return;
        }

        if (dumpAssetType == EVFSBlockType.All)
        {
            foreach (var type in VFSDumper.BlockHashMap.Keys)
                dumper.DumpAssetByType(streamingAssetsPath, type, outputDir);
        }
        else
        {
            dumper.DumpAssetByType(streamingAssetsPath, dumpAssetType, outputDir);
        }
    }

    static void HandlePCKCommand(string[] args)
    {
        var parser = new ArgParser()
            .AddFlag("help", "h")
            .AddFlag("verbose", "v")
            .AddOption("input", "i")
            .AddOption("output", "o")
            .AddOption("mode", "m");

        if (!parser.TryParse(args))
        {
            foreach (var error in parser.Errors)
                Console.Error.WriteLine(error);
            PrintPCKHelp();
            return;
        }

        if (parser.GetFlag("help"))
        {
            PrintPCKHelp();
            return;
        }

        var inputPath = parser.GetValue("input");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            Console.Error.WriteLine("Error: --input is required.");
            PrintPCKHelp();
            return;
        }

        var outputDir = parser.GetValue("output");
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            Console.Error.WriteLine("Error: --output is required.");
            PrintPCKHelp();
            return;
        }

        var mode = parser.GetValue("mode") ?? "ogg";
        if (mode != "raw" && mode != "ogg")
        {
            Console.Error.WriteLine("Error: --mode must be one of: raw, ogg");
            PrintPCKHelp();
            return;
        }

        try
        {
            var logger = new Logger(parser.GetFlag("verbose"));
            var converter = new PckConverter(logger);
            converter.ExtractAndConvert(inputPath, outputDir, mode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: {0}", ex.Message);
            Environment.Exit(1);
        }
    }

    static void PrintPCKHelp()
    {
        var exeName = GetExecutableName();
        Console.WriteLine("Usage:");
        Console.WriteLine(
            "  {0} pck --input <pck_file> --output <output_dir> [--mode <mode>] [-h|--help]",
            exeName
        );
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  --input, -i      Input PCK file path");
        Console.WriteLine("  --output, -o     Output directory");
        Console.WriteLine("  --mode, -m       Extract mode: raw, ogg (default: ogg)");
        Console.WriteLine("                   raw: Extract wem/bnk/plg files without conversion");
        Console.WriteLine(
            "                   ogg: Convert files to ogg, keep unconvertible files raw"
        );
        Console.WriteLine("  --verbose, -v    Enable verbose output");
        Console.WriteLine("  -h, --help       Show help information");
    }

    static void PrintHelp()
    {
        var exeName = GetExecutableName();
        Console.WriteLine("Usage:");
        Console.WriteLine("  {0} <command> [options]", exeName);
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  vfs    Dump files from VFS");
        Console.WriteLine("  pck    Extract files from PCK");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -h, --help    Show help information");
    }

    static void PrintVFSHelp()
    {
        var exeName = GetExecutableName();
        Console.WriteLine("Usage:");
        Console.WriteLine(
            "  {0} vfs --gamepath <game_path> [--blocktype <type>] [--output <output_dir>] [--debug] [-h|--help]",
            exeName
        );
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  --gamepath       Game data directory that contains the VFS folder");
        Console.WriteLine(
            "  --blocktype      Block type to dump, supports name or numeric value, default is all"
        );
        Console.WriteLine(
            "                   Available types: {0}",
            string.Join(", ", VFSDumper.BlockHashMap.Keys)
        );
        Console.WriteLine(
            "  --output         Output directory, default is ./Assets next to the executable"
        );
        Console.WriteLine(
            "  --debug          Scan all subfolders and print groupCfgName from each BLC (no extraction)"
        );
        Console.WriteLine("  --verbose, -v    Enable verbose output");
        Console.WriteLine("  -h, --help       Show help information");
    }
}
