using System.Text;
using System.Text.Json.Nodes;
using BydTools.Formats.MemoryPack;
using Xunit;

namespace BydTools.Tests;

public class MemoryPackTests
{
    private static string FixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "BydTools.Tests", "Fixtures", "MemoryPack");
            if (Directory.Exists(candidate))
                return candidate;
            candidate = Path.Combine(dir.FullName, "Fixtures", "MemoryPack");
            if (File.Exists(Path.Combine(candidate, "schemas.json")))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("MemoryPack fixtures were not found.");
    }

    private static MemoryPackSchema Schema() =>
        MemoryPackSchema.Load(
            Path.Combine(FixtureRoot(), "schemas.json"),
            Path.Combine(FixtureRoot(), "type_index.json")
        );

    private static byte[] Sample(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureRoot(), "samples", name));

    [Fact]
    public void BambooRaft_decodes_every_byte()
    {
        var decoder = new MemoryPackDecoder(Schema());
        JsonNode? node = decoder.Decode(Sample("BambooRaftTaskTable.json"), "Beyond.Gameplay.BambooRaftTaskTable");
        var table = Assert.IsType<JsonObject>(node);
        var data = Assert.IsType<JsonObject>(table["data"]);
        Assert.Equal(9, data.Count);
        JsonObject first = Assert.IsType<JsonObject>(data.First().Value);
        Assert.True(first.ContainsKey("questConfigs"));
        Assert.True(first.ContainsKey("raftId"));
        Assert.NotNull(first["raftId"]);
    }

    [Fact]
    public void Teleport_table_decodes_three_rows()
    {
        var decoder = new MemoryPackDecoder(Schema());
        JsonNode? node = decoder.Decode(
            Sample("CommonSysTeleportValidationDataTable.json"),
            "Beyond.Gameplay.TeleportValidationDataTable"
        );
        var rows = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(node)["teleportValidationDatas"]);
        Assert.Equal(3, rows.Count);
        JsonObject row = Assert.IsType<JsonObject>(rows.First().Value);
        Assert.Equal(0, row["deviation"]!.GetValue<float>());
        Assert.False(row["keepAnim"]!.GetValue<bool>());
        var position = Assert.IsType<JsonArray>(row["position"]);
        Assert.Equal(3, position.Count);
    }

    [Fact]
    public void NpcAtmospheric_empty_dictionary_is_an_object()
    {
        var decoder = new MemoryPackDecoder(Schema());
        JsonNode? node = decoder.Decode(Sample("sample_NpcAtmospheric.json"), "Beyond.Gameplay.NpcAtmosphericDataTable");
        var table = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(node)["dataTable"]);
        Assert.Empty(table);
    }

    [Fact]
    public void Union_map_loads_nested_tables_and_a_flat_legacy_object()
    {
        var maps = JsonDataMemoryPack.LoadUnionMap(Path.Combine(FixtureRoot(), "union-map.json"));
        Assert.Equal(447, maps["AbilityActionData"].Count);
        Assert.Equal(1364, maps["ActionBase"].Count);
        Assert.Equal("PlayAnimationActionData", maps["AbilityActionData"][296]);

        using var dir = new TempDir();
        string flat = Path.Combine(dir.Path, "flat.json");
        File.WriteAllText(flat, """{ "296": "PlayAnimationActionData" }""");
        var legacy = JsonDataMemoryPack.LoadUnionMap(flat);
        Assert.Single(legacy);
        Assert.Equal("PlayAnimationActionData", legacy["AbilityActionData"][296]);
    }

    [Fact]
    public void Skill_and_buff_fail_with_a_typed_union_error_or_decode()
    {
        var decoder = new MemoryPackDecoder(Schema());
        AssertHardSample(decoder, Sample("sample_SkillData.json"), "Beyond.Gameplay.Core.SkillData");
        AssertHardSample(decoder, Sample("sample_BuffData.json"), "Beyond.Gameplay.Core.BuffData");
    }

    [Fact]
    public void Batch_reports_plain_json_skips_and_folder_counts()
    {
        using var input = new TempDir();
        using var output = new TempDir();
        File.WriteAllBytes(
            Path.Combine(input.Path, "BambooRaftTaskTable.json"),
            Sample("BambooRaftTaskTable.json")
        );
        Directory.CreateDirectory(Path.Combine(input.Path, "AtmosphericNpcData"));
        File.WriteAllBytes(
            Path.Combine(input.Path, "AtmosphericNpcData", "1001.json"),
            Sample("sample_NpcAtmospheric.json")
        );
        Directory.CreateDirectory(Path.Combine(input.Path, "notes"));
        File.WriteAllText(Path.Combine(input.Path, "notes", "hello.json"), "{\n  \"a\": 1\n}");
        Directory.CreateDirectory(Path.Combine(input.Path, "pools"));
        File.WriteAllBytes(Path.Combine(input.Path, "pools", "blob.json"), "USPC"u8.ToArray());

        JsonDataDecodeReport report = JsonDataMemoryPack.DecodeTree(
            Schema(),
            input.Path,
            output.Path,
            jobs: 2
        );
        Assert.Equal(2, report.Ok);
        Assert.Equal(1, report.PlainJson);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Failed);
        Assert.True(report.ByFolder.ContainsKey("AtmosphericNpcData"));
        string text = File.ReadAllText(Path.Combine(output.Path, "notes", "hello.json"));
        Assert.Contains("\"a\"", text, StringComparison.Ordinal);
        Assert.Contains("questConfigs", File.ReadAllText(Path.Combine(output.Path, "BambooRaftTaskTable.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void String_pool_resolves_block_local_ids()
    {
        string[] block0 = ["alpha", "beta"];
        string[] block1 = ["gamma"];
        var pool = new BeyondStringPool([block0, block1]);
        Assert.Equal("alpha", pool.Format(0));
        Assert.Equal("beta", pool.Format(1));
        Assert.Equal("gamma", pool.Format(2));
        Assert.Equal("", pool.Format(-1));
        Assert.Equal("", pool.Format(-2));
        Assert.Equal("#99", pool.Format(99));

        byte[] payload = StringPayload(["hello", "world"]);
        byte[] file = WrapPool(payload, compressed: false);
        Assert.Equal(["hello", "world"], BeyondStringPool.ReadBlock(file));

        byte[] lzFile = WrapPool(payload, compressed: true);
        Assert.Equal(["hello", "world"], BeyondStringPool.ReadBlock(lzFile));

        var prefixed = new byte[4 + file.Length];
        BitConverter.TryWriteBytes(prefixed.AsSpan(0), 27);
        file.CopyTo(prefixed.AsSpan(4));
        Assert.Equal(["hello", "world"], BeyondStringPool.ReadBlock(prefixed));

        using var poolDir = new TempDir();
        File.WriteAllBytes(Path.Combine(poolDir.Path, "StringPool0.byte"), prefixed);
        File.WriteAllBytes(Path.Combine(poolDir.Path, "StringPool1.byte"), file);
        File.WriteAllBytes(Path.Combine(poolDir.Path, "StringPoolHashIndex.byte"), "SHPI"u8.ToArray());
        var loaded = BeyondStringPool.Load(poolDir.Path);
        Assert.Equal(2, loaded.BlockCount);
        Assert.Equal("hello", loaded.Format(0));
        Assert.Equal("world", loaded.Format(1));
        Assert.Equal("hello", loaded.Format(2));
    }


    [Fact]
    public void FAnimationCurve_memberCount_0_yields_empty_object()
    {
        var decoder = new MemoryPackDecoder(Schema());
        // MemoryPack versioning: count 0 means every wire member is default/omitted.
        JsonNode? node = decoder.Decode(new byte[] { 0 }, "Beyond.FAnimationCurve");
        var obj = Assert.IsType<JsonObject>(node);
        Assert.Empty(obj);
    }

    [Fact]
    public void FAnimationCurve_partial_memberCount_reads_wire_prefix()
    {
        var decoder = new MemoryPackDecoder(Schema());
        // wire_order: keys, postWrapMode, preWrapMode — count 1 reads only keys (null array).
        var payload = new byte[] { 1, 0xFF, 0xFF, 0xFF, 0xFF };
        JsonNode? node = decoder.Decode(payload, "Beyond.FAnimationCurve");
        var obj = Assert.IsType<JsonObject>(node);
        Assert.Single(obj);
        Assert.Null(obj["keys"]);
        Assert.False(obj.ContainsKey("postWrapMode"));
        Assert.False(obj.ContainsKey("preWrapMode"));
    }

    private static void AssertHardSample(MemoryPackDecoder decoder, byte[] data, string root)
    {
        var ex = Assert.Throws<MemoryPackUnionException>(() => decoder.Decode(data, root));
        Assert.Equal("AbilityActionData", ex.StaticType);
        Assert.True(ex.Tag is int tag && tag >= 0);
        Assert.True(ex.Offset > 0);
        Assert.NotEmpty(ex.Candidates);
    }

    private static byte[] StringPayload(string[] values)
    {
        byte[][] blobs = values.Select(v => Encoding.UTF8.GetBytes(v)).ToArray();
        int blobBytes = blobs.Sum(b => 4 + b.Length);
        var payload = new byte[4 + values.Length * 4 + blobBytes];
        BitConverter.TryWriteBytes(payload.AsSpan(0), values.Length);
        int cursor = 0;
        for (int i = 0; i < values.Length; i++)
        {
            BitConverter.TryWriteBytes(payload.AsSpan(4 + i * 4), cursor);
            int at = 4 + values.Length * 4 + cursor;
            BitConverter.TryWriteBytes(payload.AsSpan(at), blobs[i].Length);
            blobs[i].CopyTo(payload.AsSpan(at + 4));
            cursor += 4 + blobs[i].Length;
        }

        return payload;
    }

    private static byte[] WrapPool(byte[] payload, bool compressed)
    {
        byte[] stored = compressed ? Lz4Literal(payload) : payload;
        var file = new byte[24 + stored.Length];
        "USPC"u8.CopyTo(file);
        BitConverter.TryWriteBytes(file.AsSpan(4), 1);
        BitConverter.TryWriteBytes(file.AsSpan(8), compressed ? 1 : 0);
        BitConverter.TryWriteBytes(file.AsSpan(12), BitConverter.ToInt32(payload));
        BitConverter.TryWriteBytes(file.AsSpan(16), payload.Length);
        BitConverter.TryWriteBytes(file.AsSpan(20), stored.Length);
        stored.CopyTo(file.AsSpan(24));
        return file;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "byd-mp-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] Lz4Literal(byte[] payload)
    {
        var block = new List<byte> { (byte)(Math.Min(payload.Length, 15) << 4) };
        if (payload.Length >= 15)
        {
            int rest = payload.Length - 15;
            while (rest >= 255)
            {
                block.Add(255);
                rest -= 255;
            }

            block.Add((byte)rest);
        }

        block.AddRange(payload);
        return block.ToArray();
    }

}
