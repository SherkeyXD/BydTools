using System.Text;
using System.Text.Json.Nodes;
using BydTools.Audio.Bnk;
using BydTools.Audio.Interop;
using BydTools.Audio.Naming;
using BydTools.Audio.Pck;
using BydTools.Formats.Lua;
using BydTools.Formats.SparkBuffer;
using BydTools.Formats.Usm;
using BydTools.Tests.Fixtures;
using BydTools.VFS;
using Xunit;

namespace BydTools.Tests;

public class SparkBufferTests
{
    [Fact]
    public void Bean_matches_legacy_json()
    {
        string json = SparkBuffer.ToJson(Samples.MinimalBean());
        Assert.Equal("{\n  \"value\": 42\n}", json.Replace("\r\n", "\n"));
        Assert.Equal("Root", SparkBuffer.GetRootName(Samples.MinimalBean()));
    }

    [Fact]
    public void Map_matches_legacy_audio_dialog()
    {
        byte[] data = Samples.AudioDialogMap();
        Assert.Equal("AudioDialog", SparkBuffer.GetRootName(data));
        string json = SparkBuffer.ToJson(data).Replace("\r\n", "\n");
        Assert.Equal("{\n  \"100\": {\n    \"path\": \"v1d0/a.wem\"\n  }\n}", json);
    }

    [Fact]
    public void Conversions_do_not_share_type_tables()
    {
        byte[] bean = Samples.MinimalBean();
        byte[] map = Samples.AudioDialogMap();
        Parallel.For(0, 8, _ =>
        {
            Assert.Equal("Root", SparkBuffer.GetRootName(bean));
            Assert.Contains("\"path\"", SparkBuffer.ToJson(map));
        });
    }
}

public class PckTests
{
    [Fact]
    public void Plain_archive_reads_the_file_entry()
    {
        var archive = PckArchive.Parse(Samples.PlainPck());
        Assert.False(archive.IsVfsEncrypted);
        Assert.Equal("sfx", archive.Languages[0].Name);
        Assert.Equal(99ul, archive.Entries[0].FileId);
        Assert.Equal(14u, archive.Entries[0].Size);
    }

    [Fact]
    public void Encrypted_archive_matches_the_legacy_parser()
    {
        byte[] file = Convert.FromBase64String(Samples.EncryptedPckBase64);
        var archive = PckArchive.Parse(file);
        Assert.True(archive.IsVfsEncrypted);
        archive.DecipherPayloads(file);
        Assert.True(PckArchive.TrySlice(file, archive.Entries[0], out var slice));
        Assert.Equal("RIFF-demo-data", Encoding.ASCII.GetString(slice));

        byte[] plain = archive.ToPlainPck(Convert.FromBase64String(Samples.EncryptedPckBase64));
        Assert.Equal(0x4B504B41u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(plain));
        var again = PckArchive.Parse(plain);
        Assert.False(again.IsVfsEncrypted);
    }
}

public class PckMapperTests
{
    [Fact]
    public void Hashes_match_the_legacy_mapper()
    {
        var root = (JsonObject)JsonNode.Parse("{\"100\":{\"path\":\"v1d0/a.wem\"}}")!;
        var mapper = new PckMapper(root, "Chinese");
        Assert.Equal(3, mapper.Count);
        Assert.Equal("voice/chinese/v1d0/a", mapper.GetMappedPath(6563861493395949416));
        Assert.Equal("music/chinese/v1d0/a", mapper.GetMappedPath(3014147426767775513));
        Assert.Equal("sfx/chinese/v1d0/a", mapper.GetMappedPath(9659578313000333649));
    }
}

public class AudioNamingTests
{
    [Fact]
    public void Mapped_and_unmapped_paths_use_the_block_language()
    {
        var root = (JsonObject)JsonNode.Parse("{\"100\":{\"path\":\"v1d0/a.wem\"}}")!;
        var mapper = new PckMapper(root, "Chinese");
        Assert.Equal(
            Path.Combine("Audio", "Chinese", "voice", "v1d0", "a.wav"),
            AudioNaming.ResolveWem("Audio", "Chinese", 6563861493395949416, mapper, ".wav", null, 0)
        );
        Assert.Equal(
            Path.Combine("Audio", "unmapped", "Chinese", "99.wav"),
            AudioNaming.ResolveWem("Audio", "Chinese", 99, null, ".wav", null, 0)
        );
        Assert.Equal(
            Path.Combine("Audio", "unmapped", "Chinese", "5_7.wav"),
            AudioNaming.ResolveBnkWem("Audio", "Chinese", 5, 7, null, ".wav", null, 0)
        );
    }
}

public class BnkTests
{
    [Fact]
    public void Parses_a_single_wem()
    {
        byte[] bnk = Samples.Bnk();
        var entries = BnkParser.Parse(bnk);
        Assert.Equal(7u, entries[0].Id);
        Assert.Equal(4, entries[0].Size);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bnk.AsSpan(entries[0].Offset, 4)));
    }
}

public class UsmTests
{
    [Fact]
    public void Demux_keeps_video_and_both_audio_streams()
    {
        var streams = CriUsmDemuxer.Demux(Samples.Usm());
        Assert.Equal(".m2v", streams[0].Extension);
        Assert.Equal("VIDEODATA", Encoding.ASCII.GetString(streams[0].Data));
        Assert.Equal(".hca", streams[1].Extension);
        Assert.StartsWith("HCA", Encoding.ASCII.GetString(streams[1].Data));
        Assert.Equal(".hca", streams[2].Extension);
    }
}

public class LuaTests
{
    [Fact]
    public void Rejects_bytes_that_are_only_ascii_noise()
    {
        Assert.False(LuaDecipher.IsValid("     "u8));
        Assert.True(LuaDecipher.IsValid("\u001bLua"u8));
        Assert.True(LuaDecipher.IsValid("local x = 1"u8));
    }
}

public class RegistryTests
{
    [Theory]
    [InlineData("Audio", true)]
    [InlineData("12", true)]
    [InlineData("50", false)]
    [InlineData("0", false)]
    [InlineData("All", false)]
    [InlineData("Raw", false)]
    [InlineData("100", false)]
    public void Only_catalogued_block_types_parse(string text, bool expected) =>
        Assert.Equal(expected, BlockRegistry.TryParse(text, out _));
}

public class MemoryStreamfileTests
{
    [Fact]
    public void Callbacks_read_the_pinned_buffer()
    {
        byte[] data = [1, 2, 3, 4, 5];
        using var stream = MemoryStreamfile.Create(data);
        Assert.Equal(3, stream.ReadAt(1, stackalloc byte[3]));
        unsafe
        {
            var native = (LibStreamfile*)stream.Native;
            byte* dst = stackalloc byte[2];
            Assert.Equal(2, native->Read(native->UserData, dst, 0, 2));
            Assert.Equal(1, dst[0]);
            Assert.Equal(5, native->GetSize(native->UserData));
            LibStreamfile* child = native->Open(native->UserData, null);
            Assert.Equal(1, child->Read(child->UserData, dst, 4, 1));
            Assert.Equal(5, dst[0]);
            child->Close(child);
        }
    }
}
