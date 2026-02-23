using BydTools.VFS;
using BydTools.VFS.PostProcessors;
using Spectre.Console;

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
        HelpFormatter.WriteEntry("--debug", "Scan subfolders and print block info (no extraction)");
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
                Logger.WriteError(error);
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
            Logger.WriteError("--input is required.");
            PrintHelp(Program.ExecutableName);
            return;
        }

        var streamingAssetsPath = Path.Combine(gamePath, VFSDefine.VFS_DIR);
        if (!Directory.Exists(streamingAssetsPath))
        {
            Logger.WriteError(
                $"VFS directory ({VFSDefine.VFS_DIR}) not found under \"{gamePath}\"."
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
                Logger.WriteError("--key must be a valid Base64 string.");
                return;
            }

            if (customKey.Length != VFSDefine.KEY_LEN)
            {
                Logger.WriteError(
                    $"--key must decode to {VFSDefine.KEY_LEN} bytes (got {customKey.Length})."
                );
                return;
            }
        }

        var logger = new Logger(parser.GetFlag("verbose"));
        var postProcessors = PostProcessorFactory.CreateProcessors(logger);
        IVFSDumper dumper = new VFSDumper(logger, postProcessors, customKey);

        if (parser.GetFlag("debug"))
        {
            try
            {
                dumper.DebugScanBlocks(streamingAssetsPath);
            }
            catch (Exception ex)
            {
                Logger.WriteError(ex.Message);
                Environment.Exit(1);
            }
            return;
        }

        var outputDir = parser.GetValue("output");
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            Logger.WriteError("--output is required.");
            PrintHelp(Program.ExecutableName);
            return;
        }

        var blockTypeString = parser.GetValue("blocktype");
        if (string.IsNullOrWhiteSpace(blockTypeString))
        {
            Logger.WriteError("--blocktype is required.");
            HelpFormatter.WriteBlankLine();
            HelpFormatter.WriteEnumValues("Available block types", VFSDumper.BlockHashMap.Keys);
            return;
        }

        var blockTypes = ParseBlockTypes(blockTypeString);
        if (blockTypes == null)
            return;

        try
        {
            AnsiConsole.MarkupLine($"[bold]Input:[/]  {Markup.Escape(streamingAssetsPath)}");
            AnsiConsole.MarkupLine($"[bold]Output:[/] {Markup.Escape(outputDir)}");

            for (int i = 0; i < blockTypes.Count; i++)
            {
                if (i > 0)
                    AnsiConsole.WriteLine();
                dumper.DumpAssetByType(streamingAssetsPath, blockTypes[i], outputDir);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteError(ex.Message);
            Environment.Exit(1);
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
                Logger.WriteError($"failed to parse blocktype \"{trimmed}\".");
                HelpFormatter.WriteBlankLine();
                HelpFormatter.WriteEnumValues("Available block types", VFSDumper.BlockHashMap.Keys);
                return null;
            }
        }

        return result;
    }
}
