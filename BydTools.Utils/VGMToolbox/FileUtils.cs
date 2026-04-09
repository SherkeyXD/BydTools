using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BydTools.Utils.VGMToolbox;

/// <summary>
/// Provides file I/O utilities including reading, writing, chunking,
/// and file manipulation operations used by VGMToolbox.
/// </summary>
public static class FileUtils
{
    #region Stream Reading

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes from the stream into the buffer,
    /// throwing if EOF is reached prematurely.
    /// </summary>
    public static void ReadExact(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        int remaining = count;

        while (remaining > 0)
        {
            int read = stream.Read(buffer, offset, remaining);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"End of stream reached with {remaining} bytes left to read");
            }
            remaining -= read;
            offset += read;
        }
    }

    /// <summary>
    /// Reads exactly the full length of the buffer from the stream.
    /// </summary>
    public static void ReadExact(Stream stream, byte[] buffer) =>
        ReadExact(stream, buffer, buffer.Length);

    #endregion

    #region File Statistics

    /// <summary>
    /// Counts total files in the provided paths (files and directories).
    /// </summary>
    public static int CountFiles(string[] paths, bool includeSubdirectories = true)
    {
        int total = 0;
        var option = includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                total++;
            }
            else if (Directory.Exists(path))
            {
                total += Directory.GetFiles(path, "*.*", option).Length;
            }
        }

        return total;
    }

    #endregion

    #region File Name Sanitization

    /// <summary>
    /// Replaces invalid filename characters with underscores.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(c, '_');
        }
        return fileName;
    }

    #endregion

    #region Chunk Replacement

    /// <summary>
    /// Replaces a chunk in the destination file with data from the source file.
    /// </summary>
    public static void ReplaceChunk(
        string sourcePath, long sourceOffset, long length,
        string destPath, long destOffset)
    {
        byte[] buffer = new byte[VgmtConstants.FileReadChunkSize];

        using var source = File.OpenRead(sourcePath);
        using var dest = File.Open(destPath, FileMode.Open, FileAccess.Write);

        source.Position = sourceOffset;
        dest.Position = destOffset;

        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, buffer.Length);
            int read = source.Read(buffer, 0, toRead);
            if (read == 0) break;
            dest.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    #endregion

    #region Zero Out

    /// <summary>
    /// Writes zeros to a file chunk at the specified offset.
    /// </summary>
    public static void ZeroOutChunk(string path, long offset, int length)
    {
        byte[] buffer = new byte[Math.Min(length, VgmtConstants.FileReadChunkSize)];
        int remaining = length;

        using var stream = File.OpenWrite(path);
        stream.Position = offset;

        while (remaining > 0)
        {
            int toWrite = Math.Min(remaining, buffer.Length);
            stream.Write(buffer, 0, toWrite);
            remaining -= toWrite;
        }
    }

    #endregion

    #region Interleave Files

    /// <summary>
    /// Interleaves multiple source files into a single destination file.
    /// </summary>
    public static void InterleaveFiles(
        string[] sourceFiles,
        uint interleaveSize,
        long startOffset,
        byte[] padding,
        string destFile)
    {
        using var dest = File.Create(destFile);
        var sources = sourceFiles.Select(f => File.OpenRead(f)).ToArray();

        try
        {
            long maxLength = sources.Max(s => s.Length);
            long currentOffset = startOffset;

            while (currentOffset < maxLength)
            {
                foreach (var source in sources)
                {
                    if (currentOffset < source.Length)
                    {
                        source.Position = currentOffset;
                        long remainingInSource = source.Length - currentOffset;
                        long toCopy = Math.Min(remainingInSource, interleaveSize);

                        CopyStream(source, dest, toCopy);

                        // Pad if necessary
                        long paddingNeeded = interleaveSize - toCopy;
                        if (paddingNeeded > 0)
                        {
                            WritePadding(dest, padding, (int)paddingNeeded);
                        }
                    }
                    else
                    {
                        WritePadding(dest, padding, (int)interleaveSize);
                    }
                }
                currentOffset += interleaveSize;
            }
        }
        finally
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
        }
    }

    private static void CopyStream(Stream source, Stream dest, long count)
    {
        byte[] buffer = new byte[8192];
        long remaining = count;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, buffer.Length);
            int read = source.Read(buffer, 0, toRead);
            if (read == 0) break;
            dest.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void WritePadding(Stream stream, byte[] padding, int count)
    {
        int written = 0;
        while (written < count)
        {
            int toWrite = Math.Min(padding.Length, count - written);
            stream.Write(padding, 0, toWrite);
            written += toWrite;
        }
    }

    #endregion

    #region File Splitting

    /// <summary>
    /// Splits a file into chunks of the specified size.
    /// </summary>
    public static string[] SplitFile(string sourceFile, long startOffset, ulong chunkSize, string outputFolder)
    {
        var files = new List<string>();

        using var source = File.OpenRead(sourceFile);
        long fileLength = source.Length;
        long currentOffset = startOffset;
        int chunkNumber = 1;

        while (currentOffset < fileLength)
        {
            string outputFile = Path.Combine(outputFolder, $"{Path.GetFileName(sourceFile)}.{chunkNumber:D3}");
            long remaining = fileLength - currentOffset;
            long toExtract = Math.Min((long)chunkSize, remaining);
            ParseUtils.ExtractChunkToFile(source, currentOffset, toExtract, outputFile);
            files.Add(outputFile);
            currentOffset += toExtract;
            chunkNumber++;
        }

        return files.ToArray();
    }

    #endregion

    #region File Properties

    /// <summary>
    /// Gets the size of a file, or null if the file doesn't exist or is inaccessible.
    /// </summary>
    public static long? GetFileSize(string path)
    {
        try
        {
            var info = new FileInfo(Path.GetFullPath(path));
            return info.Length;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region File Creation

    /// <summary>
    /// Creates a file from a byte array.
    /// </summary>
    public static void CreateFileFromBytes(string path, byte[] data)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// Creates a file from a text string using ASCII encoding.
    /// </summary>
    public static void CreateFileFromText(string path, string text)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, text, Encoding.ASCII);
    }

    #endregion

    #region Execute External Program

    /// <summary>
    /// Executes an external program and captures its output.
    /// </summary>
    public static bool ExecuteExternal(
        string executablePath,
        string arguments,
        string workingDirectory,
        out string stdout,
        out string stderr)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executablePath, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        stdout = string.Empty;
        stderr = string.Empty;

        bool started = process.Start();
        stdout = process.StandardOutput.ReadToEnd();
        stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return started;
    }

    #endregion

    #region Header Operations

    /// <summary>
    /// Prepends a header to a file.
    /// </summary>
    public static void PrependHeader(byte[] header, string sourceFile, string destFile)
    {
        byte[] buffer = new byte[8192];

        using var dest = File.Create(destFile);
        dest.Write(header, 0, header.Length);

        using var source = File.OpenRead(sourceFile);
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            dest.Write(buffer, 0, read);
        }
    }

    #endregion

    #region Rename by Internal Name

    /// <summary>
    /// Renames a file based on an internal name stored at a specific offset.
    /// </summary>
    public static void RenameByInternalName(
        string path,
        long nameOffset,
        int maxLength,
        byte[]? terminator,
        bool preserveExtension)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("Cannot determine directory for file");

        string originalExtension = Path.GetExtension(path);
        string internalName;

        using (var fs = File.OpenRead(path))
        {
            int nameLength;
            if (terminator != null)
            {
                nameLength = ParseUtils.GetSegmentLength(fs, (int)nameOffset, terminator);
            }
            else
            {
                nameLength = maxLength;
            }

            if (nameLength < 1)
                throw new ArgumentOutOfRangeException(nameof(nameLength), "Name length is less than 1");

            byte[] nameBytes = ParseUtils.ReadSimpleOffset(fs, nameOffset, nameLength);

            // Replace nulls with spaces and decode as ASCII
            for (int i = 0; i < nameBytes.Length; i++)
            {
                if (nameBytes[i] == 0x00) nameBytes[i] = 0x20;
            }
            internalName = Encoding.ASCII.GetString(nameBytes).Trim();
        }

        string newPath = Path.Combine(directory, internalName);
        if (preserveExtension)
        {
            newPath = Path.ChangeExtension(newPath, originalExtension);
        }

        if (path.Equals(newPath, StringComparison.OrdinalIgnoreCase))
            return;

        // Handle conflicts
        if (File.Exists(newPath))
        {
            string baseName = Path.GetFileNameWithoutExtension(newPath);
            string ext = Path.GetExtension(newPath);
            int count = Directory.GetFiles(directory, $"{baseName}*{ext}").Length;
            newPath = Path.Combine(directory, $"{baseName}_{count:D4}{ext}");
        }

        File.Move(path, newPath);
    }

    #endregion

    #region Remove Chunks

    /// <summary>
    /// Removes a chunk from a file and returns the path to the new file.
    /// </summary>
    public static string RemoveChunk(string path, long offset, long length)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory == null)
            throw new InvalidOperationException("Cannot determine directory");

        string tempPath = Path.ChangeExtension(fullPath, ".cut");

        using (var source = File.OpenRead(fullPath))
        {
            // Copy before the chunk
            ParseUtils.ExtractChunkToFile(source, 0, offset, tempPath);

            // Copy after the chunk
            using (var dest = File.Open(tempPath, FileMode.Append, FileAccess.Write))
            {
                source.Position = offset + length;
                byte[] buffer = new byte[1024];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dest.Write(buffer, 0, read);
                }
            }
        }

        return tempPath;
    }

    /// <summary>
    /// Removes all occurrences of a byte pattern from a file.
    /// </summary>
    public static string RemoveAllChunks(string path, byte[] pattern)
    {
        using var fs = File.OpenRead(path);
        return RemoveAllChunks(fs, pattern);
    }

    /// <summary>
    /// Removes all occurrences of a byte pattern from a file stream.
    /// </summary>
    public static string RemoveAllChunks(FileStream fs, byte[] pattern)
    {
        long[] offsets = ParseUtils.FindAllOffsets(fs, 0, pattern, false, -1, -1, true);
        string destPath = Path.ChangeExtension(fs.Name, ".cut");

        byte[] buffer = new byte[VgmtConstants.FileReadChunkSize];
        long readOffset = 0;

        using var dest = File.Create(destPath);

        foreach (long offset in offsets)
        {
            fs.Position = readOffset;
            long chunkSize = offset - readOffset;
            CopyStreamChunk(fs, dest, chunkSize, buffer);
            readOffset = offset + pattern.Length;
        }

        // Copy remaining data after last pattern
        fs.Position = readOffset;
        CopyStreamChunk(fs, dest, fs.Length - readOffset, buffer);

        return destPath;
    }

    private static void CopyStreamChunk(Stream source, Stream dest, long count, byte[] buffer)
    {
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, buffer.Length);
            int read = source.Read(buffer, 0, toRead);
            if (read == 0) break;
            dest.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    #endregion

    #region External Process Execution

    /// <summary>
    /// Executes an external program and captures its output.
    /// </summary>
    public static bool Execute(
        string executable,
        string arguments,
        string workingDirectory,
        out string stdout,
        out string stderr)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        bool started = process.Start();
        stdout = process.StandardOutput.ReadToEnd();
        stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return started;
    }

    #endregion
}
