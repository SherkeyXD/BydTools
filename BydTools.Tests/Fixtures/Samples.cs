using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BydTools.Core.Crypto;
using BydTools.Core.IO;
using BydTools.Formats.SparkBuffer;
using BydTools.VFS;

namespace BydTools.Tests.Fixtures;

internal static class Samples
{
    public static byte[] Key { get; } = Enumerable.Repeat((byte)0x11, 32).ToArray();

    public static byte[] MinimalBean() =>
        Spark(
            static (ms, w) =>
            {
                w.Write(1);
                w.Write((byte)SparkType.Bean);
                Align(ms, 4);
                w.Write(0x12345678);
                CString(w, "Item");
                Align(ms, 4);
                w.Write(1);
                CString(w, "value");
                w.Write((byte)SparkType.Int);
            },
            static (ms, w) =>
            {
                w.Write((byte)SparkType.Bean);
                CString(w, "Root");
                Align(ms, 4);
                w.Write(0x12345678);
            },
            static w => w.Write(42)
        );

    public static byte[] AudioDialogMap() =>
        Spark(
            static (ms, w) =>
            {
                w.Write(1);
                w.Write((byte)SparkType.Bean);
                Align(ms, 4);
                w.Write(0x1111);
                CString(w, "Row");
                Align(ms, 4);
                w.Write(1);
                CString(w, "path");
                w.Write((byte)SparkType.String);
            },
            static (ms, w) =>
            {
                w.Write((byte)SparkType.Map);
                CString(w, "AudioDialog");
                w.Write((byte)SparkType.String);
                w.Write((byte)SparkType.Bean);
                Align(ms, 4);
                w.Write(0x1111);
            },
            static w =>
            {
                w.Write(1);
                w.BaseStream.Position += 8;
                long keyPos = w.BaseStream.Position;
                w.Write(0);
                long valPos = w.BaseStream.Position;
                w.Write(0);
                long keyStr = w.BaseStream.Position;
                CString(w, "100");
                long bean = w.BaseStream.Position;
                long pathField = w.BaseStream.Position;
                w.Write(0);
                long pathStr = w.BaseStream.Position;
                CString(w, "v1d0/a.wem");
                w.BaseStream.Position = keyPos;
                w.Write((int)keyStr);
                w.BaseStream.Position = valPos;
                w.Write((int)bean);
                w.BaseStream.Position = pathField;
                w.Write((int)pathStr);
            }
        );

    public static byte[] PlainPck()
    {
        using var lang = new MemoryStream();
        using (var writer = new BinaryWriter(lang))
        {
            writer.Write(1u);
            writer.Write(12u);
            writer.Write(1u);
            writer.Write(Encoding.UTF8.GetBytes("sfx\0"));
        }

        byte[] fileData = Encoding.ASCII.GetBytes("RIFF-demo-data");
        using var sounds = new MemoryStream();
        using (var writer = new BinaryWriter(sounds))
        {
            writer.Write(1u);
            writer.Write(99u);
            writer.Write(0u);
            writer.Write((uint)fileData.Length);
            writer.Write(0u);
            writer.Write(1u);
        }

        byte[] langBytes = lang.ToArray();
        byte[] soundBytes = sounds.ToArray();
        int headerLength = 16 + langBytes.Length + soundBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(soundBytes.AsSpan(16, 4), (uint)(8 + headerLength));

        using var file = new MemoryStream();
        using var output = new BinaryWriter(file);
        output.Write(0x4B504B41u);
        output.Write((uint)headerLength);
        output.Write(1u);
        output.Write((uint)langBytes.Length);
        output.Write(0u);
        output.Write((uint)soundBytes.Length);
        output.Write(langBytes);
        output.Write(soundBytes);
        output.Write(fileData);
        return file.ToArray();
    }

    public const string EncryptedPckBase64 =
        "AAAAADgAAAABAAAA8Q3+vBBY1TmbeU/Ds8MmQCnloMlVL3hGtDaKz/eayUzajfzS6NfTT1X5TdnKQyVW/GSf3zjASLfwzu0XY9g7lgt3";

    public static byte[] Bnk()
    {
        byte[] wem = Encoding.ASCII.GetBytes("RIFF");
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(Encoding.ASCII.GetBytes("BKHD"));
        writer.Write(4);
        writer.Write(1);
        writer.Write(Encoding.ASCII.GetBytes("DIDX"));
        writer.Write(12);
        writer.Write(7u);
        writer.Write(0u);
        writer.Write((uint)wem.Length);
        writer.Write(Encoding.ASCII.GetBytes("DATA"));
        writer.Write(wem.Length);
        writer.Write(wem);
        return ms.ToArray();
    }

    public static byte[] Usm()
    {
        using var ms = new MemoryStream();
        Packet(ms, "CRID", "HEAD"u8, 0);
        Packet(ms, "@SFV", "VIDEODATA"u8, 0);
        Packet(ms, "@SFA", new byte[] { 0x48, 0x43, 0x41, 0x00 }.Concat(Encoding.ASCII.GetBytes("AUDIODATA")).ToArray(), 1);
        Packet(ms, "@SFA", new byte[] { 0x48, 0x43, 0x41, 0x00 }.Concat(Encoding.ASCII.GetBytes("MORE")).ToArray(), 2);
        return ms.ToArray();
    }

    public static string CreateGame(string root, params VfsFileSpec[] files)
    {
        var groups = files.GroupBy(static file => file.DirectoryHash);
        foreach (var group in groups)
        {
            var first = group.First();
            string dir = Path.Combine(root, VFSDefine.VfsDirectoryName, first.DirectoryHash);
            Directory.CreateDirectory(dir);
            byte[] payload = BlockPayload(first, group.ToArray());
            byte[] blc = Encrypt(payload);
            File.WriteAllBytes(Path.Combine(dir, first.DirectoryHash + ".blc"), blc);
            var chunk = new byte[group.Sum(static file => file.Stored.Length)];
            int offset = 0;
            foreach (var file in group)
            {
                file.Stored.CopyTo(chunk, offset);
                offset += file.Stored.Length;
            }

            UInt128 md5 = 1;
            File.WriteAllBytes(Path.Combine(dir, md5.ToHexLittleEndian() + ".chk"), chunk);
        }

        return root;
    }

    public static VfsFileSpec Entry(
        string directoryHash,
        string groupName,
        EVFSBlockType type,
        string name,
        byte[] plain,
        bool encrypt = false,
        int codeVersion = 3
    )
    {
        byte[] stored = plain;
        long iv = 0;
        if (encrypt)
        {
            iv = 7;
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(nonce, VFSDefine.ProtocolVersion);
            BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], iv);
            using var chacha = new CSChaCha20(Key, nonce.ToArray(), 1);
            stored = chacha.EncryptBytes(plain);
        }

        return new VfsFileSpec(directoryHash, groupName, type, name, plain, stored, encrypt, iv, codeVersion);
    }

    private static byte[] BlockPayload(VfsFileSpec header, VfsFileSpec[] files)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(header.CodeVersion);
        if (header.CodeVersion <= 10)
            writer.Write(VFSDefine.ProtocolVersion);
        byte[] name = Encoding.UTF8.GetBytes(header.GroupName);
        writer.Write((ushort)name.Length);
        writer.Write(name);
        uint hash = uint.Parse(header.DirectoryHash, NumberStyles.HexNumber);
        Span<byte> hashBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(hashBytes, hash);
        writer.Write(hashBytes);
        writer.Write(0);
        writer.Write(files.Length);
        writer.Write(files.Sum(static file => (long)file.Stored.Length));
        writer.Write((byte)header.Type);
        writer.Write(1);
        Span<byte> md5 = stackalloc byte[16];
        md5[0] = 1;
        writer.Write(md5);
        writer.Write(new byte[16]);
        writer.Write(files.Sum(static file => (long)file.Stored.Length));
        writer.Write((byte)header.Type);
        writer.Write(files.Length);
        long offset = 0;
        foreach (var file in files)
        {
            byte[] fileName = Encoding.UTF8.GetBytes(file.Name);
            writer.Write((ushort)fileName.Length);
            writer.Write(fileName);
            writer.Write(0L);
            writer.Write(new byte[16]);
            writer.Write(new byte[16]);
            writer.Write(offset);
            writer.Write((long)file.Stored.Length);
            writer.Write((byte)file.Type);
            writer.Write((byte)(file.Encrypt ? 1 : 0));
            if (file.Encrypt)
                writer.Write(file.IvSeed);
            offset += file.Stored.Length;
        }

        return ms.ToArray();
    }

    private static byte[] Encrypt(byte[] payload)
    {
        byte[] nonce = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(nonce, VFSDefine.ProtocolVersion);
        using var chacha = new CSChaCha20(Key, nonce, 1);
        byte[] cipher = chacha.EncryptBytes(payload);
        byte[] file = new byte[nonce.Length + cipher.Length];
        nonce.CopyTo(file, 0);
        cipher.CopyTo(file, nonce.Length);
        return file;
    }

    private static void Packet(Stream stream, string signature, ReadOnlySpan<byte> payload, byte streamId)
    {
        const ushort headerSkip = 0x18;
        uint blockSize = headerSkip + (uint)payload.Length;
        stream.Write(Encoding.ASCII.GetBytes(signature));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, blockSize);
        stream.Write(size);
        Span<byte> header = stackalloc byte[headerSkip];
        BinaryPrimitives.WriteUInt16BigEndian(header, headerSkip);
        header[4] = streamId;
        stream.Write(header);
        stream.Write(payload);
    }

    private static byte[] Spark(
        Action<Stream, BinaryWriter> typeDef,
        Action<Stream, BinaryWriter> rootDef,
        Action<BinaryWriter> data
    )
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        long typeDefAt = ms.Position;
        typeDef(ms, writer);
        long rootAt = ms.Position;
        rootDef(ms, writer);
        long dataAt = ms.Position;
        data(writer);
        ms.Position = 0;
        writer.Write((int)typeDefAt);
        writer.Write((int)rootAt);
        writer.Write((int)dataAt);
        return ms.ToArray();
    }

    private static void Align(Stream stream, int boundary)
    {
        long aligned = (stream.Position - 1) + (boundary - ((stream.Position - 1) % boundary));
        while (stream.Position < aligned)
            stream.WriteByte(0);
    }

    private static void CString(BinaryWriter writer, string text)
    {
        writer.Write(Encoding.UTF8.GetBytes(text));
        writer.Write((byte)0);
    }
}

internal sealed record VfsFileSpec(
    string DirectoryHash,
    string GroupName,
    EVFSBlockType Type,
    string Name,
    byte[] Plain,
    byte[] Stored,
    bool Encrypt,
    long IvSeed,
    int CodeVersion
);
