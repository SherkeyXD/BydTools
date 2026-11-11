using BydTools.Formats.MemoryPack;

namespace BydTools.CLI.Commands;

public sealed class DecodeJsonDataCommand : ICommand
{
    public string Name => "decode-jsondata";
    public string Description => "Re-decode a raw JsonData dump using the embedded schema";

    public int Execute(string[] args, CancellationToken cancellationToken)
    {
        var parser = CreateParser();
        if (!parser.Parse(args))
            return VfsCommand.Fail(2, parser.Errors[0]);
        if (parser.HasFlag("help"))
        {
            HelpFormatter.WriteCommandHelp(
                "JsonData MemoryPack decoder",
                "decode-jsondata --input <dir> --output <dir>",
                parser,
                () =>
                {
                    HelpFormatter.WriteSection("Notes");
                    Console.WriteLine("  vfs --blocktype JsonData already decodes MMP using the embedded schema.");
                    Console.WriteLine("  Use this command to re-decode a raw dump, or to override the schema.");
                    Console.WriteLine("  Embedded schemas / type_index / union-map / unmanaged / enums are used by default.");
                    Console.WriteLine("  --schema-dir is a beyond-schema-dumper export (.msgpack.lz4 preferred, else .json).");
                    Console.WriteLine("  --schema / --index / --union-map override individual files.");
                    Console.WriteLine("  A leading { or [ is copied as UTF-8 JSON. USPC and SHPI files are skipped.");
                    Console.WriteLine("  String pool ids -1 and -2 are \"\" and null. Other ids resolve from StringPool*.byte");
                    Console.WriteLine("  under --string-pool, or <input>/Data/Json/StringPool when that folder exists.");
                    Console.WriteLine();
                }
            );
            return 0;
        }

        string? input = parser.Get("input");
        string? output = parser.Get("output");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            return VfsCommand.Fail(2, "--input and --output are required.");

        bool useEmbedded = string.IsNullOrWhiteSpace(parser.Get("schema-dir"))
            && string.IsNullOrWhiteSpace(parser.Get("schema"))
            && string.IsNullOrWhiteSpace(parser.Get("index"));
        MemoryPackExport.Paths? export = null;
        if (!useEmbedded)
        {
            try
            {
                export = MemoryPackExport.Resolve(
                    parser.Get("schema-dir"),
                    parser.Get("schema"),
                    parser.Get("index"),
                    parser.Get("union-map")
                );
            }
            catch (ArgumentException ex)
            {
                return VfsCommand.Fail(2, ex.Message);
            }
        }

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
            MemoryPackSchema loaded;
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? unions;
            if (export is { } paths)
            {
                loaded = MemoryPackSchema.Load(paths.Schema, paths.TypeIndex);
                unions = string.IsNullOrWhiteSpace(paths.UnionMap)
                    ? null
                    : JsonDataMemoryPack.LoadUnionMap(paths.UnionMap);
            }
            else
            {
                loaded = MemoryPackResources.Schema;
                string? unionOverride = parser.Get("union-map");
                unions = string.IsNullOrWhiteSpace(unionOverride)
                    ? MemoryPackResources.UnionMaps
                    : JsonDataMemoryPack.LoadUnionMap(Path.GetFullPath(unionOverride));
            }

            BeyondStringPool? pool = null;
            string? poolDir = parser.Get("string-pool");
            if (string.IsNullOrWhiteSpace(poolDir))
            {
                string guess = Path.Combine(Path.GetFullPath(input), "Data", "Json", "StringPool");
                if (Directory.Exists(guess)
                    && Directory.EnumerateFiles(guess, "StringPool*.byte").Any())
                    poolDir = guess;
            }
            if (!string.IsNullOrWhiteSpace(poolDir))
                pool = BeyondStringPool.Load(Path.GetFullPath(poolDir));

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
            .Add("input", "i", "JsonData dump directory (raw MMP, not already decoded)", "dir", required: true)
            .Add("output", "o", "Directory for plaintext JSON", "dir", required: true)
            .Add("schema-dir", null, "beyond-schema-dumper export directory (overrides embedded)", "dir",
                continuation: "Loads schemas/type_index/union-map/unmanaged/enums as .msgpack.lz4 or .json.")
            .Add("schema", null, "schemas.json or schemas.msgpack.lz4 (overrides --schema-dir)", "file")
            .Add("index", null, "type_index.json or type_index.msgpack.lz4 (overrides --schema-dir)", "file")
            .Add("string-pool", null, "Directory of StringPool*.byte (USPC)", "dir",
                continuation: "Default: <input>/Data/Json/StringPool when that folder exists.")
            .Add("union-map", null, "union-map.json or union-map.msgpack.lz4 (overrides --schema-dir)", "file")
            .Add("report", null, "Coverage report path", "file", continuation: "Default: <output>/memorypack-report.json.")
            .Add("jobs", null, "Files decoded at once", "n", continuation: "Default: processor count.")
            .Add("help", "h", "Show help information");
}
