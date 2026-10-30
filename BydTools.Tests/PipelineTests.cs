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
    public void Code_version_five_reads_the_extra_fields()
    {
        using var root = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "a.txt", "z"u8.ToArray(), codeVersion: 5)
        );
        var block = VfsArchive.Open(Vfs(root.Path), Samples.Key, new QuietLogger()).Find("Table");
        Assert.NotNull(block);
        Assert.Equal(5, block.Info.CodeVersion);
        Assert.Equal("a.txt", block.Info.Chunks[0].Files[0].Name);
        Assert.Equal("z", Encoding.ASCII.GetString(block.ReadFile(block.ResolveChunkPath(block.Info.Chunks[0])!, block.Info.Chunks[0].Files[0])));
    }

    [Fact]
    public void Lua_block_copies_files_that_are_not_lua()
    {
        byte[] markdown = "# notes\n"u8.ToArray();
        using var root = new TempDir();
        using var output = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("19E3AE45", "Lua", EVFSBlockType.Lua, "Data/notes.md", markdown)
        );
        var block = VfsArchive.Open(Vfs(root.Path), Samples.Key, new QuietLogger()).Find("Lua")!;
        var result = new VfsExtractor(new QuietLogger()).Dump(block, output.Path, jobs: 1);
        Assert.Equal(0, result.Failed);
        Assert.Equal(markdown, File.ReadAllBytes(Path.Combine(output.Path, "Data", "notes.md")));
        Assert.False(File.Exists(Path.Combine(output.Path, "Data", "notes.lua")));
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

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Persistent_blc_and_chunks_are_resolved_independently(bool hasBlc, bool hasChunk)
    {
        using var root = new TempDir();
        using var persistent = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "row.txt", "base"u8.ToArray(), encrypt: true)
        );
        Samples.CreateGame(
            persistent.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "row.txt", "edit"u8.ToArray(), encrypt: true, codeVersion: 5)
        );
        string persistentBlock = Path.Combine(Vfs(persistent.Path), "42A8FCA6");
        if (!hasBlc)
            File.Delete(Path.Combine(persistentBlock, "42A8FCA6.blc"));
        if (!hasChunk)
            File.Delete(Directory.GetFiles(persistentBlock, "*.chk").Single());

        var archive = VfsArchive.Open(Vfs(root.Path), Samples.Key, new QuietLogger(), Vfs(persistent.Path));
        var block = Assert.Single(archive.Blocks);
        Assert.Equal(hasBlc ? 5 : 3, block.Info.CodeVersion);
        var chunk = Assert.Single(block.Info.Chunks);
        string chunkPath = block.ResolveChunkPath(chunk)!;
        Assert.Equal(
            Path.Combine(hasChunk ? persistentBlock : Path.Combine(Vfs(root.Path), "42A8FCA6"), chunk.FileName),
            chunkPath
        );
        Assert.Equal(
            hasChunk ? "edit" : "base",
            Encoding.ASCII.GetString(block.ReadFile(chunkPath, Assert.Single(chunk.Files)))
        );

        // A second CHK in this block falls back independently of the first.
        var baseOnlyChunk = chunk with { Md5Name = 2 };
        string baseOnlyPath = Path.Combine(Vfs(root.Path), "42A8FCA6", baseOnlyChunk.FileName);
        File.Copy(Path.Combine(Vfs(root.Path), "42A8FCA6", chunk.FileName), baseOnlyPath);
        Assert.Equal(baseOnlyPath, block.ResolveChunkPath(baseOnlyChunk));
        Assert.Null(block.ResolveChunkPath(chunk with { Md5Name = 3 }));
    }

    [Fact]
    public void Persistent_and_streaming_only_blocks_are_both_discovered()
    {
        using var root = new TempDir();
        using var persistent = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "base.txt", "base"u8.ToArray()),
            Samples.Entry("24ED34CF", "Audio", EVFSBlockType.Audio, "bank.pck", Samples.PlainPck())
        );
        Samples.CreateGame(
            persistent.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "updated.txt", "edit"u8.ToArray()),
            Samples.Entry("19E3AE45", "Lua", EVFSBlockType.Lua, "patch.txt", "lua"u8.ToArray())
        );
        var archive = VfsArchive.Open(Vfs(root.Path), Samples.Key, new QuietLogger(), Vfs(persistent.Path));
        Assert.Equal(3, archive.Blocks.Count);
        Assert.NotNull(archive.Find("Audio"));
        Assert.NotNull(archive.Find("Lua"));
        Assert.Equal("updated.txt", archive.Find("Table")!.Info.Chunks[0].Files[0].Name);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("empty-vfs")]
    [InlineData("missing")]
    public void Cli_falls_back_when_persistent_has_no_files(string state)
    {
        using var root = new TempDir();
        using var persistent = new TempDir();
        using var output = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "row.bytes", Samples.MinimalBean())
        );
        string persistentPath = persistent.Path;
        if (state == "empty-vfs")
            Directory.CreateDirectory(Vfs(persistent.Path));
        else if (state == "missing")
            persistentPath = Path.Combine(persistent.Path, "missing");

        Assert.Equal(
            0,
            Program.Main([
                "vfs", "-i", root.Path, "-p", persistentPath, "-o", output.Path,
                "-t", "Table", "--key", Convert.ToBase64String(Samples.Key), "--jobs", "1",
            ])
        );
        Assert.Contains("\"value\": 42", File.ReadAllText(Path.Combine(output.Path, "row.json")));
    }

    [Theory]
    [InlineData("-p")]
    [InlineData("--persistent")]
    public void Cli_dumps_the_persistent_declaration_and_data(string option)
    {
        using var root = new TempDir();
        using var persistent = new TempDir();
        using var output = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "old.txt", "base"u8.ToArray())
        );
        Samples.CreateGame(
            persistent.Path,
            Samples.Entry("42A8FCA6", "Table", EVFSBlockType.Table, "new.txt", "edit"u8.ToArray(), encrypt: true)
        );
        Assert.Equal(
            0,
            Program.Main([
                "vfs", "-i", root.Path, option, persistent.Path, "-o", output.Path,
                "-t", "Table", "--key", Convert.ToBase64String(Samples.Key), "--jobs", "1",
            ])
        );
        Assert.Equal("edit", File.ReadAllText(Path.Combine(output.Path, "new.txt")));
        Assert.False(File.Exists(Path.Combine(output.Path, "old.txt")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Pck_command_uses_persistent_and_falls_back_to_streaming(bool hasPersistentAudio)
    {
        using var root = new TempDir();
        using var persistent = new TempDir();
        using var output = new TempDir();
        Samples.CreateGame(
            root.Path,
            Samples.Entry("24ED34CF", "Audio", EVFSBlockType.Audio, "bank.pck", Samples.PlainPck())
        );
        if (hasPersistentAudio)
        {
            byte[] patched = Samples.PlainPck();
            "RIFF-edit-data"u8.CopyTo(patched.AsSpan(patched.Length - 14));
            Samples.CreateGame(
                persistent.Path,
                Samples.Entry("24ED34CF", "Audio", EVFSBlockType.Audio, "bank.pck", patched, encrypt: true)
            );
        }

        Assert.Equal(
            0,
            Program.Main([
                "pck", "-i", root.Path, "--persistent", persistent.Path, "-o", output.Path,
                "-t", "Audio", "-m", "raw", "--no-map", "--key", Convert.ToBase64String(Samples.Key),
                "--jobs", "1",
            ])
        );
        Assert.Equal(
            hasPersistentAudio ? "RIFF-edit-data" : "RIFF-demo-data",
            File.ReadAllText(Path.Combine(output.Path, "Audio", "unmapped", "Sfx", "99.wem"))
        );
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
