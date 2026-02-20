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
        HelpFormatter.WriteEntry("-m, --mode <mode>", "Extract mode (default: ogg)");
        HelpFormatter.WriteEntryContinuation("raw  Extract wem/bnk/plg without conversion");
        HelpFormatter.WriteEntryContinuation("ogg  Convert to ogg, keep unconvertible as raw");
        HelpFormatter.WriteCommonOptions();
    }

    public void Execute(string[] args)
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

        var mode = parser.GetValue("mode") ?? "ogg";
        if (mode != "raw" && mode != "ogg")
        {
            Console.Error.WriteLine("Error: --mode must be one of: raw, ogg");
            PrintHelp(Program.ExecutableName);
            return;
        }

        try
        {
            var logger = new Logger(parser.GetFlag("verbose"));
            var wemConverter = new BydTools.Wwise.WemConverter();
            var converter = new PckConverter(logger, wemConverter);
            converter.ExtractAndConvert(inputPath, outputDir, mode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: {0}", ex.Message);
            Environment.Exit(1);
        }
    }
}
