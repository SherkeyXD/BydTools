using System.Text;
using BydTools.CLI;
using BydTools.Core;
using BydTools.Extraction.Audio;
using BydTools.Extraction.Vfs;
using BydTools.Tests.Fixtures;
using BydTools.VFS;
using Xunit;

namespace BydTools.Tests;

public class PipelineTests
{
    [Fact]
    public void Reads_a_block_and_rejects_a_wrong_key()
    {
        using var root = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "hello.txt", "hi"u8.ToArray(), encrypt: true)
        );
        var archive = VfsArchive.Open(Vfs(root.Path), Samples.Key, new QuietLogger());
        var block = archive.Find("Table");
        Assert.NotNull(block);
        Assert.Equal("Table", block.Info.GroupCfgName);
        byte[] data = block.ReadFile(block.ResolveChunkPath(block.Info.Chunks[0])!, block.Info.Chunks[0].Files[0]);
        Assert.Equal("hi", Encoding.ASCII.GetString(data));

        var blc = Directory.GetFiles(Vfs(root.Path), "*.blc", SearchOption.AllDirectories).Single();
        var error = Assert.Throws<InvalidDataException>(() =>
            BlcReader.Parse(File.ReadAllBytes(blc), Enumerable.Repeat((byte)0x22, 32).ToArray(), "42A8FCA6")
        );
        Assert.Contains("key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Code_version_above_ten_does_not_consume_a_second_version()
    {
        using var root = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "a.txt", "z"u8.ToArray(), codeVersion: 11)
        );
        var block = VfsArchive.Open(Vfs(root.Path), Samples.Key, new QuietLogger()).Find("Table");
        Assert.NotNull(block);
        Assert.Equal(11, block.Info.Version);
        Assert.Equal(3, block.Info.CodeVersion);
        Assert.Equal("a.txt", block.Info.Chunks[0].Files[0].Name);
    }

    [Fact]
    public void Dump_converts_spark_and_refuses_paths_that_escape()
    {
        using var root = new TempDir();
        using var output = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "row.bytes", Samples.MinimalBean()),
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "../outside.txt", "no"u8.ToArray())
        );
        var block = VfsArchive.Open(Vfs(root.Path), Samples.Key, new QuietLogger()).Find("Table")!;
        var result = new VfsExtractor(new QuietLogger()).Dump(block, output.Path, jobs: 2);
        Assert.Contains("\"value\": 42", File.ReadAllText(Path.Combine(output.Path, "row.json")));
        Assert.False(File.Exists(Path.Combine(output.Path, "row.bytes")));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(output.Path)!.FullName, "outside.txt")));
        Assert.True(result.Failed >= 1);
    }

    [Fact]
    public void Usm_names_stay_stable_when_extracted_twice()
    {
        using var root = new TempDir();
        using var output = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("55FC21C6", "Video", EVFSBlockType.Video, "clip.usm", Samples.Usm())
        );
        var block = VfsArchive.Open(Vfs(root.Path), Samples.Key, new QuietLogger()).Find("Video")!;
        var extractor = new VfsExtractor(new QuietLogger());
        extractor.Dump(block, output.Path, jobs: 1);
        extractor.Dump(block, output.Path, jobs: 1);
        string[] names = Directory
            .GetFiles(output.Path, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
        Assert.Equal(["clip.hca", "clip.m2v", "clip_1.hca"], names);
        Assert.Equal("VIDEODATA", File.ReadAllText(Path.Combine(output.Path, "clip.m2v")));
    }

    [Fact]
    public void Audio_export_writes_one_pck_without_copying_the_whole_block_first()
    {
        using var root = new TempDir();
        using var output = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("24ED34CF", "Audio", EVFSBlockType.Audio, "bank.pck", Samples.PlainPck())
        );
        var archive = VfsArchive.Open(Vfs(root.Path), Samples.Key, new QuietLogger());
        var result = new AudioExportService(new QuietLogger()).Export(
            archive,
            archive.Find("Audio")!,
            output.Path,
            AudioExportMode.Raw,
            mapNames: true,
            decoder: null,
            jobs: 1
        );
        Assert.Equal(1, result.Extracted);
        Assert.Equal(
            "RIFF-demo-data",
            File.ReadAllText(Path.Combine(output.Path, "Audio", "unmapped", "Sfx", "99.wem"))
        );
    }

    [Fact]
    public void Cli_rejects_bad_arguments_and_dumps_a_table()
    {
        Assert.Equal(0, Program.Main([]));
        Assert.Equal(2, Program.Main(["nope"]));
        Assert.Equal(2, Program.Main(["vfs"]));

        using var root = new TempDir();
        using var output = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "row.bytes", Samples.MinimalBean())
        );
        string key = Convert.ToBase64String(Samples.Key);
        Assert.Equal(2, Program.Main(["vfs", "-i", root.Path, "-o", output.Path, "-t", "50", "--key", key]));
        Assert.Equal(2, Program.Main(["vfs", "-i", root.Path, "-o", output.Path, "-t", "All", "--key", key]));
        Assert.Equal(
            0,
            Program.Main(["vfs", "-i", root.Path, "-o", output.Path, "-t", "Table", "--key", key, "--jobs", "1"])
        );
        Assert.True(File.Exists(Path.Combine(output.Path, "row.json")));
    }

    private static string Vfs(string root) => Path.Combine(root, VFSDefine.VfsDirectoryName);

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = Directory.CreateTempSubdirectory("bydtools-").FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class QuietLogger : ILogger
    {
        public void Info(string message) { }

        public void Info(string format, params object[] args) { }

        public void Verbose(string message) { }

        public void Verbose(string format, params object[] args) { }

        public void Error(string message) { }

        public void Error(string format, params object[] args) { }
    }
}
