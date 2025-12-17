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

        // Check for subcommand
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
        string? gamePath = null;
        string? blockTypeString = null;
        string? outputDir = null;
        bool showHelp = false;
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;

                case "--verbose":
                case "-v":
                    verbose = true;
                    break;

                case "--gamepath":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: --gamepath requires a value.");
                        return;
                    }
                    gamePath = args[++i];
                    break;

                case "--blocktype":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: --blocktype requires a value.");
                        return;
                    }
                    blockTypeString = args[++i];
                    break;

                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: --output requires a value.");
                        return;
                    }
                    outputDir = args[++i];
                    break;

                default:
                    Console.Error.WriteLine("Unknown argument: {0}", arg);
                    PrintVFSHelp();
                    return;
            }
        }

        if (showHelp)
        {
            PrintVFSHelp();
            return;
        }

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            Console.Error.WriteLine("Error: --gamepath is required.");
            PrintVFSHelp();
            return;
        }

        // Default output directory
        outputDir ??= Path.Combine(AppContext.BaseDirectory, "Assets");

        // Parse blocktype, support name or numeric value, default is All
        EVFSBlockType dumpAssetType = EVFSBlockType.All;
        if (!string.IsNullOrWhiteSpace(blockTypeString))
        {
            if (!Enum.TryParse<EVFSBlockType>(blockTypeString, ignoreCase: true, out dumpAssetType))
            {
                // Try parse as numeric value
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

        var logger = new Logger(verbose);
        var dumper = new VFSDumper(logger);
        if (dumpAssetType == EVFSBlockType.All)
        {
            foreach (var type in VFSDumper.BlockHashMap.Keys)
            {
                dumper.DumpAssetByType(streamingAssetsPath, type, outputDir);
            }
        }
        else
        {
            dumper.DumpAssetByType(streamingAssetsPath, dumpAssetType, outputDir);
        }
    }

    static void HandlePCKCommand(string[] args)
    {
        string? inputPath = null;
        string? outputDir = null;
        string format = "ogg";
        bool showHelp = false;
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;

                case "--verbose":
                case "-v":
                    verbose = true;
                    break;

                case "--input":
                case "-i":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: --input requires a value.");
                        return;
                    }
                    inputPath = args[++i];
                    break;

                case "--output":
                case "-o":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: --output requires a value.");
                        return;
                    }
                    outputDir = args[++i];
                    break;

                case "--format":
                case "-f":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: --format requires a value.");
                        return;
                    }
                    format = args[++i];
                    break;

                default:
                    Console.Error.WriteLine("Unknown argument: {0}", arg);
                    PrintPCKHelp();
                    return;
            }
        }

        if (showHelp)
        {
            PrintPCKHelp();
            return;
        }

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            Console.Error.WriteLine("Error: --input is required.");
            PrintPCKHelp();
            return;
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            Console.Error.WriteLine("Error: --output is required.");
            PrintPCKHelp();
            return;
        }

        if (format != "bnk" && format != "wem" && format != "ogg")
        {
            Console.Error.WriteLine("Error: --format must be one of: bnk, wem, ogg");
            PrintPCKHelp();
            return;
        }

        try
        {
            var logger = new Logger(verbose);
            var converter = new PckConverter(logger);
            converter.ExtractAndConvert(inputPath, outputDir, format);
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
            "  {0} pck --input <pck_file> --output <output_dir> [--format <format>] [-h|--help]",
            exeName
        );
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  --input, -i      Input PCK file path");
        Console.WriteLine("  --output, -o     Output directory");
        Console.WriteLine("  --format, -f     Output format: bnk, wem, ogg (default: ogg)");
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
            "  {0} vfs --gamepath <game_path> [--blocktype <type>] [--output <output_dir>] [-h|--help]",
            exeName
        );
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  --gamepath   Game data directory that contains the VFS folder");
        Console.WriteLine(
            "  --blocktype  Block type to dump, supports name or numeric value, default is all"
        );
        Console.WriteLine(
            "               Available types: {0}",
            string.Join(", ", VFSDumper.BlockHashMap.Keys)
        );
        Console.WriteLine(
            "  --output     Output directory, default is ./Assets next to the executable"
        );
        Console.WriteLine("  --verbose, -v   Enable verbose output");
        Console.WriteLine("  -h, --help   Show help information");
    }
}
