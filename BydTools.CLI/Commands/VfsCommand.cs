using BydTools.VFS;
using BydTools.VFS.PostProcessors;

namespace BydTools.CLI.Commands;

sealed class VfsCommand : ICommand
{
    public string Name => "vfs";
    public string Description => "Dump files from VFS";

    public void PrintHelp(string exeName)
    {
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

    public void Execute(string[] args)
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
            PrintHelp(Program.ExecutableName);
            return;
        }

        if (parser.GetFlag("help"))
        {
            PrintHelp(Program.ExecutableName);
            return;
        }

        var gamePath = parser.GetValue("gamepath");
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            Console.Error.WriteLine("Error: --gamepath is required.");
            PrintHelp(Program.ExecutableName);
            return;
        }

        var outputDir = parser.GetValue("output")
            ?? Path.Combine(AppContext.BaseDirectory, "Assets");

        EVFSBlockType dumpAssetType = EVFSBlockType.All;
        var blockTypeString = parser.GetValue("blocktype");
        if (!string.IsNullOrWhiteSpace(blockTypeString))
        {
            if (!Enum.TryParse<EVFSBlockType>(blockTypeString, ignoreCase: true, out dumpAssetType))
            {
                if (byte.TryParse(blockTypeString, out var btValue)
                    && Enum.IsDefined(typeof(EVFSBlockType), btValue))
                {
                    dumpAssetType = (EVFSBlockType)btValue;
                }
                else
                {
                    Console.Error.WriteLine("Error: failed to parse blocktype \"{0}\".", blockTypeString);
                    Console.Error.WriteLine("Available types: {0}", string.Join(", ", VFSDumper.BlockHashMap.Keys));
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
        var postProcessors = PostProcessorFactory.CreateProcessors(logger);
        IVFSDumper dumper = new VFSDumper(logger, postProcessors);

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
}
