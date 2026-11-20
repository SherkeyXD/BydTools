using BydTools.CLI.Commands;
using BydTools.Extraction.Sink;
using BydTools.Extraction.Vfs;
using BydTools.Formats.Bundle;
using BydTools.Formats.Lua;
using BydTools.Tests.TestHelpers;
using BydTools.VFS;
using Xunit;

namespace BydTools.Tests;

public sealed class VfsHandlersTests
{
    [Fact]
    public void BlockRegistry_MapsBundleAndManifestKindsPreservingExistingKinds()
    {
        Assert.True(BlockRegistry.TryGet(EVFSBlockType.InitBundle, out var initBundle));
        Assert.Equal(ContentKind.Bundle, initBundle.Kind);

        Assert.True(BlockRegistry.TryGet(EVFSBlockType.Bundle, out var bundle));
        Assert.Equal(ContentKind.Bundle, bundle.Kind);

        Assert.True(BlockRegistry.TryGet(EVFSBlockType.BundleManifest, out var manifest));
        Assert.Equal(ContentKind.BundleManifest, manifest.Kind);

        Assert.True(BlockRegistry.TryGet(EVFSBlockType.Table, out var table));
        Assert.Equal(ContentKind.Table, table.Kind);

        Assert.True(BlockRegistry.TryGet(EVFSBlockType.Lua, out var lua));
        Assert.Equal(ContentKind.Lua, lua.Kind);

        Assert.True(BlockRegistry.TryGet(EVFSBlockType.Audio, out var audio));
        Assert.Equal(ContentKind.Audio, audio.Kind);

        Assert.True(BlockRegistry.TryGet(EVFSBlockType.JsonData, out var jsonData));
        Assert.Equal(ContentKind.JsonData, jsonData.Kind);
    }

    [Fact]
    public void BundleHandler_PassesThroughStandardUnityFsAndConvertsEndfieldBundles()
    {
        string dir = CreateTempDir();
        try
        {
            var sink = new DirectoryOutputSink(dir);
            var handler = new BundleHandler();

            byte[] standard = EndfieldTestBuilder.BuildSyntheticUnityFs();
            Assert.Equal(HandleStatus.Skipped, handler.Handle("std.ab", standard, sink).Status);
            Assert.False(File.Exists(Path.Combine(dir, "std.ab")));

            byte[] cab = EndfieldTestBuilder.BuildSyntheticSerializedFileHeader("2021.3.34f5", 96);
            byte[] endfield = EndfieldTestBuilder.BuildEndfieldBundle(
                [new EndfieldTestBuilder.TestBlockSpec(cab)],
                [new EndfieldTestBuilder.TestNodeSpec("CAB-1", 0, cab.Length)]);

            Assert.Equal(HandleStatus.Handled, handler.Handle("sub/endfield.ab", endfield, sink).Status);
            byte[] written = File.ReadAllBytes(Path.Combine(dir, "sub/endfield.ab"));
            Assert.True(EndfieldBundle.IsStandardUnityBundle(written));

            byte[] corrupt = [0x01, 0x02, 0x03, 0x04];
            HandleResult failResult = handler.Handle("bad.ab", corrupt, sink);
            Assert.Equal(HandleStatus.Failed, failResult.Status);
            Assert.False(File.Exists(Path.Combine(dir, "bad.ab")));
            Assert.Empty(Directory.GetFiles(dir, "*.partial", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BundleManifestHandler_WritesBinAndLeavesNoPartialOnTruncation()
    {
        string dir = CreateTempDir();
        try
        {
            var sink = new DirectoryOutputSink(dir);
            var handler = new BundleManifestHandler();

            byte[] manifestRaw = EndfieldTestBuilder.BuildValidHgmmapBinary(extraBytes: 512);
            byte[] compressed = EndfieldTestBuilder.CompressBrotli(manifestRaw);

            HandleResult ok = handler.Handle("Data/manifest.hgmmap", compressed, sink);
            Assert.Equal(HandleStatus.Handled, ok.Status);
            Assert.Equal(manifestRaw, File.ReadAllBytes(Path.Combine(dir, "Data/manifest.hgmmap.bin")));

            byte[] truncated = compressed[..(compressed.Length / 2)];
            HandleResult fail = handler.Handle("Data/trunc.hgmmap", truncated, sink);
            Assert.Equal(HandleStatus.Failed, fail.Status);
            Assert.False(File.Exists(Path.Combine(dir, "Data/trunc.hgmmap.bin")));
            Assert.Empty(Directory.GetFiles(dir, "*.partial", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void VfsCommand_EndToEndSupportsRawOptOutPassthroughAndNonRegression()
    {
        string root = CreateTempDir();
        try
        {
            string gameDir = Path.Combine(root, "game");
            string vfsDir = Path.Combine(gameDir, "VFS");
            byte[] key = VFSDefine.ResolveKey("pc", null);

            byte[] cab = EndfieldTestBuilder.BuildSyntheticSerializedFileHeader("2021.3.34f5", 128);
            byte[] endfieldAb = EndfieldTestBuilder.BuildEndfieldBundle(
                [new EndfieldTestBuilder.TestBlockSpec(cab)],
                [new EndfieldTestBuilder.TestNodeSpec("CAB-a", 0, cab.Length)]);
            byte[] stdAb = EndfieldTestBuilder.BuildSyntheticUnityFs();
            byte[] sidecar = "meta"u8.ToArray();

            Assert.True(BlockRegistry.TryGet(EVFSBlockType.InitBundle, out var initBundleDesc));
            EndfieldTestBuilder.WriteSyntheticVfsBlock(
                vfsDir,
                initBundleDesc,
                "InitBundle",
                key,
                [
                    ("Data/Bundles/endfield.ab", endfieldAb),
                    ("Data/Bundles/standard.ab", stdAb),
                    ("Data/Bundles/readme.txt", sidecar),
                ]);

            byte[] manifestBin = EndfieldTestBuilder.BuildValidHgmmapBinary(extraBytes: 256);
            byte[] compressedManifest = EndfieldTestBuilder.CompressBrotli(manifestBin);
            Assert.True(BlockRegistry.TryGet(EVFSBlockType.BundleManifest, out var manifestDesc));
            EndfieldTestBuilder.WriteSyntheticVfsBlock(
                vfsDir,
                manifestDesc,
                "BundleManifest",
                key,
                [("Data/Bundles/Windows/manifest.hgmmap", compressedManifest)]);

            // Preserve plain JSON passthrough in JsonData and plain Lua check
            byte[] plainJson = "{\"ok\":true}\n"u8.ToArray();
            Assert.True(BlockRegistry.TryGet(EVFSBlockType.JsonData, out var jsonDesc));
            EndfieldTestBuilder.WriteSyntheticVfsBlock(
                vfsDir,
                jsonDesc,
                "JsonData",
                key,
                [("Json/sample.json", plainJson)]);

            string decodedOut = Path.Combine(root, "decoded");
            int exitDecoded = new VfsCommand().Execute(
                ["-i", gameDir, "-o", decodedOut, "-t", "InitBundle,BundleManifest,JsonData"],
                CancellationToken.None);
            Assert.Equal(0, exitDecoded);

            byte[] outEndfield = File.ReadAllBytes(Path.Combine(decodedOut, "Data/Bundles/endfield.ab"));
            Assert.True(EndfieldBundle.IsStandardUnityBundle(outEndfield));
            Assert.Equal(stdAb, File.ReadAllBytes(Path.Combine(decodedOut, "Data/Bundles/standard.ab")));
            Assert.Equal(sidecar, File.ReadAllBytes(Path.Combine(decodedOut, "Data/Bundles/readme.txt")));
            Assert.Equal(manifestBin, File.ReadAllBytes(Path.Combine(decodedOut, "Data/Bundles/Windows/manifest.hgmmap.bin")));
            Assert.Equal(plainJson, File.ReadAllBytes(Path.Combine(decodedOut, "Json/sample.json")));

            // --raw skips .ab conversion and .hgmmap decompression
            string rawOut = Path.Combine(root, "raw");
            int exitRaw = new VfsCommand().Execute(
                ["-i", gameDir, "-o", rawOut, "-t", "InitBundle,BundleManifest,JsonData", "--raw"],
                CancellationToken.None);
            Assert.Equal(0, exitRaw);

            Assert.Equal(endfieldAb, File.ReadAllBytes(Path.Combine(rawOut, "Data/Bundles/endfield.ab")));
            Assert.Equal(stdAb, File.ReadAllBytes(Path.Combine(rawOut, "Data/Bundles/standard.ab")));
            Assert.Equal(compressedManifest, File.ReadAllBytes(Path.Combine(rawOut, "Data/Bundles/Windows/manifest.hgmmap")));
            Assert.False(File.Exists(Path.Combine(rawOut, "Data/Bundles/Windows/manifest.hgmmap.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void VfsCommand_ReturnsNonZeroExitCodeWhenManifestIsTruncated()
    {
        string root = CreateTempDir();
        try
        {
            string gameDir = Path.Combine(root, "game");
            string vfsDir = Path.Combine(gameDir, "VFS");
            byte[] key = VFSDefine.ResolveKey("pc", null);

            byte[] manifestBin = EndfieldTestBuilder.BuildValidHgmmapBinary(extraBytes: 256);
            byte[] compressed = EndfieldTestBuilder.CompressBrotli(manifestBin);
            byte[] truncated = compressed[..(compressed.Length / 2)];

            Assert.True(BlockRegistry.TryGet(EVFSBlockType.BundleManifest, out var manifestDesc));
            EndfieldTestBuilder.WriteSyntheticVfsBlock(
                vfsDir,
                manifestDesc,
                "BundleManifest",
                key,
                [("Data/Bundles/Windows/manifest.hgmmap", truncated)]);

            string outDir = Path.Combine(root, "out");
            int exitCode = new VfsCommand().Execute(
                ["-i", gameDir, "-o", outDir, "-t", "BundleManifest"],
                CancellationToken.None);
            Assert.Equal(1, exitCode);
            Assert.False(File.Exists(Path.Combine(outDir, "Data/Bundles/Windows/manifest.hgmmap.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "bydtools-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
