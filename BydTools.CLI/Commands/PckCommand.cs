using BydTools.PCK;

namespace BydTools.CLI.Commands;

sealed class PckCommand : ICommand
{
    public string Name => "pck";
    public string Description => "Extract files from PCK";

    public void PrintHelp(string exeName)
    {
        HelpFormatter.WriteUsage("pck", "--input <file> --output <dir>");

        HelpFormatter.WriteSectionHeader("Required");
        HelpFormatter.WriteEntry("-i, --input <file>", "Input PCK file path");
        HelpFormatter.WriteEntry("-o, --output <dir>", "Output directory");
        HelpFormatter.WriteBlankLine();

        HelpFormatter.WriteSectionHeader("Options");
        HelpFormatter.WriteEntry("-m, --mode <mode>", "Extract mode (default: wav)");
        HelpFormatter.WriteEntryContinuation("raw  Extract wem/bnk/plg without conversion");
        HelpFormatter.WriteEntryContinuation("wav  Convert to wav via vgmstream");
        HelpFormatter.WriteEntry("--json <file>", "JSON mapping file for ID-to-path naming");
        HelpFormatter.WriteEntryContinuation("Format: { \"id\": { \"path\": \"...\", ... }, ... }");
        HelpFormatter.WriteCommonOptions();
    }

    public void Execute(string[] args)
    {
        var parser = new ArgParser()
            .AddFlag("help", "h")
            .AddFlag("verbose", "v")
            .AddOption("input", "i")
            .AddOption("output", "o")
            .AddOption("mode", "m")
            .AddOption("json");

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

        var inputPath = parser.GetValue("input");
        if (string.IsNullOrWhiteSpace(inputPath))
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

        var mode = parser.GetValue("mode") ?? "wav";
        if (mode != "raw" && mode != "wav")
        {
            Console.Error.WriteLine("Error: --mode must be one of: raw, wav");
            PrintHelp(Program.ExecutableName);
            return;
        }

        try
        {
            var logger = new Logger(parser.GetFlag("verbose"));

            BydTools.Wwise.IWemConverter wemConverter;
            if (BydTools.Wwise.LibVgmstreamConverter.IsAvailable)
            {
                wemConverter = new BydTools.Wwise.LibVgmstreamConverter();
                logger.Info("Engine: libvgmstream (DLL)");
            }
            else if (BydTools.Wwise.WemConverter.VgmstreamPath != null)
            {
                wemConverter = new BydTools.Wwise.WemConverter();
                logger.Info("Engine: vgmstream-cli");
            }
            else
            {
                Console.Error.WriteLine(
                    "Error: vgmstream not found. Place libvgmstream.dll (preferred) " +
                    "or vgmstream-cli next to the executable, or add to PATH.");
                return;
            }

            var converter = new PckConverter(logger, wemConverter);

            PckMapper? mapper = null;
            var jsonPath = parser.GetValue("json");
            if (!string.IsNullOrWhiteSpace(jsonPath))
            {
                if (!File.Exists(jsonPath))
                {
                    Console.Error.WriteLine($"Error: JSON file not found: {jsonPath}");
                    return;
                }
                mapper = new PckMapper(jsonPath);
                logger.Info($"JSON:   {jsonPath} ({mapper.Count} entries)");
            }

            converter.ExtractAndConvert(inputPath, outputDir, mode, mapper);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: {0}", ex.Message);
            Environment.Exit(1);
        }
    }
}
