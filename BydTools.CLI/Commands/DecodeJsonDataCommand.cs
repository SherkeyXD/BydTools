using BydTools.Formats.MemoryPack;

namespace BydTools.CLI.Commands;

public sealed class DecodeJsonDataCommand : ICommand
{
    public string Name => "decode-jsondata";
    public string Description => "Decode JsonData MemoryPack files to plaintext JSON";

    public int Execute(string[] args, CancellationToken cancellationToken)
    {
        var parser = CreateParser();
        if (!parser.Parse(args))
            return VfsCommand.Fail(2, parser.Errors[0]);
        if (parser.HasFlag("help"))
        {
            HelpFormatter.WriteCommandHelp(
                "JsonData MemoryPack decoder",
                "decode-jsondata --input <dir> --output <dir> --schema <schemas.json> --index <type_index.json>",
                parser,
                () =>
                {
                    HelpFormatter.WriteSection("Notes");
                    Console.WriteLine("  Dump JsonData first: BydTools vfs --blocktype JsonData --input <game> --output <dump>");
                    Console.WriteLine("  A leading { or [ is copied as UTF-8 JSON. USPC and SHPI files are skipped.");
                    Console.WriteLine("  String pool ids -1 and -2 are \"\" and null. Other ids are resolved from StringPool*.byte");
                    Console.WriteLine("  when --string-pool is set, otherwise written as \"#<id>\".");
                    Console.WriteLine("  --union-map is a JSON object { \"AbilityActionData\": { \"<tag>\": \"<TypeName>\" } }.");
                    Console.WriteLine();
                }
            );
            return 0;
        }

        string? input = parser.Get("input");
        string? output = parser.Get("output");
        string? schema = parser.Get("schema");
        string? index = parser.Get("index");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output)
            || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(index))
            return VfsCommand.Fail(2, "--input, --output, --schema, and --index are required.");

        int jobs;
        try
        {
            jobs = VfsCommand.ResolveJobs(parser.Get("jobs"));
        }
        catch (ArgumentException ex)
        {
            return VfsCommand.Fail(2, ex.Message);
        }

        try
        {
            var loaded = MemoryPackSchema.Load(Path.GetFullPath(schema), Path.GetFullPath(index));
            BeyondStringPool? pool = null;
            string? poolDir = parser.Get("string-pool");
            if (!string.IsNullOrWhiteSpace(poolDir))
                pool = BeyondStringPool.Load(Path.GetFullPath(poolDir));
            IReadOnlyDictionary<int, string>? unions = null;
            string? unionMap = parser.Get("union-map");
            if (!string.IsNullOrWhiteSpace(unionMap))
                unions = JsonDataMemoryPack.LoadUnionMap(Path.GetFullPath(unionMap));

            string outputPath = Path.GetFullPath(output);
            JsonDataDecodeReport report = JsonDataMemoryPack.DecodeTree(
                loaded,
                Path.GetFullPath(input),
                outputPath,
                pool,
                unions,
                jobs,
                cancellationToken
            );
            string? reportPath = parser.Get("report");
            if (string.IsNullOrWhiteSpace(reportPath))
                reportPath = Path.Combine(outputPath, "memorypack-report.json");
            else
                reportPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonDataMemoryPack.ReportJson(report).ToJsonString(JsonDataMemoryPack.JsonOptions));

            var logger = new Logger(verbose: false);
            logger.Info(
                "JsonData decode: {0} ok, {1} plain JSON, {2} skipped, {3} failed ({4} files).",
                report.Ok,
                report.PlainJson,
                report.Skipped,
                report.Failed,
                report.Total
            );
            logger.Info("Report: {0}", reportPath);
            foreach (var folder in report.ByFolder.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                logger.Info(
                    "  {0}: ok {1}, plain {2}, skipped {3}, failed {4}",
                    folder.Key,
                    folder.Value.Ok,
                    folder.Value.PlainJson,
                    folder.Value.Skipped,
                    folder.Value.Failed
                );
            }

            return report.Failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            return VfsCommand.Fail(1, "Cancelled.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            return VfsCommand.Fail(1, ex.Message);
        }
    }

    private static ArgParser CreateParser() =>
        new ArgParser()
            .Add("input", "i", "JsonData dump directory (vfs --blocktype JsonData)", "dir", required: true)
            .Add("output", "o", "Directory for plaintext JSON", "dir", required: true)
            .Add("schema", null, "beyond-mp-schema schemas.json", "file", required: true)
            .Add("index", null, "beyond-mp-schema type_index.json", "file", required: true)
            .Add("string-pool", null, "Directory of StringPool*.byte (USPC)", "dir")
            .Add("union-map", null, "JSON map of AbilityActionData union tags to type names", "file")
            .Add("report", null, "Coverage report path", "file", continuation: "Default: <output>/memorypack-report.json.")
            .Add("jobs", null, "Files decoded at once", "n", continuation: "Default: processor count.")
            .Add("help", "h", "Show help information");
}
