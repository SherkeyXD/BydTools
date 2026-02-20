using BydTools.PCK;

namespace BydTools.CLI.Commands;

sealed class PckCommand : ICommand
{
    public string Name => "pck";
    public string Description => "Extract files from PCK";

    public void PrintHelp(string exeName)
    {
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
        Console.WriteLine("                   ogg: Convert files to ogg, keep unconvertible files raw");
        Console.WriteLine("  --verbose, -v    Enable verbose output");
        Console.WriteLine("  -h, --help       Show help information");
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
