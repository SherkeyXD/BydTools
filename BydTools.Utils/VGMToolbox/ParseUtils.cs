using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BydTools.Utils.VGMToolbox;

/// <summary>
/// Core file parsing utilities for searching, extracting, and analyzing binary data.
/// </summary>
public static class ParseUtils
{
    // Log file names
    public const string LogFileName = "vgmt_extraction_log.txt";
    public const string BatchFileName = "vgmt_extraction_log.bat";
    public const string VfsCutFolder = "vgmt_vfs_cut";

    #region Simple Offset Reading

    /// <summary>
    /// Reads bytes from a byte array at the specified offset.
    /// </summary>
    public static byte[] ReadSimpleOffset(byte[] data, int offset, int length)
    {
        var result = new byte[length];
        Buffer.BlockCopy(data, offset, result, 0, length);
        return result;
    }

    /// <summary>
    /// Reads bytes from a stream at the specified offset without changing stream position.
    /// </summary>
    public static byte[] ReadSimpleOffset(Stream stream, long offset, int length)
    {
        long originalPosition = stream.Position;
        try
        {
            stream.Position = offset;
            var result = new byte[length];
            stream.ReadExactly(result, 0, length);
            return result;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    #endregion

    #region Segment Length

    /// <summary>
    /// Finds the length of a segment by searching for a terminator byte sequence.
    /// </summary>
    public static int GetSegmentLength(byte[] data, int startOffset, byte[] terminator)
    {
        for (int i = startOffset; i <= data.Length - terminator.Length; i++)
        {
            if (data.AsSpan(i, terminator.Length).SequenceEqual(terminator))
            {
                return i - startOffset;
            }
        }
        return 0;
    }

    /// <summary>
    /// Finds the length of a segment by searching for a terminator in a stream.
    /// </summary>
    public static int GetSegmentLength(Stream stream, int startOffset, byte[] terminator)
    {
        stream.Position = startOffset;
        var checkBytes = new byte[terminator.Length];
        int position = startOffset;

        while (position < stream.Length)
        {
            stream.Position = position;
            stream.Read(checkBytes, 0, 1);

            if (checkBytes[0] == terminator[0])
            {
                stream.Position = position;
                stream.Read(checkBytes, 0, terminator.Length);

                if (checkBytes.AsSpan().SequenceEqual(terminator))
                {
                    return position - startOffset;
                }
            }
            position++;
        }

        return 0;
    }

    #endregion

    #region Find Offsets

    /// <summary>
    /// Finds the next occurrence of a byte pattern in a stream.
    /// </summary>
    public static long FindNextOffset(Stream stream, long startOffset, byte[] pattern, bool restorePosition = true)
    {
        long originalPosition = stream.Position;
        long position = startOffset;
        int patternLength = pattern.Length;

        try
        {
            while (position < stream.Length - patternLength + 1)
            {
                stream.Position = position;
                var check = new byte[patternLength];
                int read = stream.Read(check, 0, patternLength);

                if (read == patternLength && check.AsSpan().SequenceEqual(pattern))
                {
                    return position;
                }

                position++;
            }

            return -1;
        }
        finally
        {
            if (restorePosition)
            {
                stream.Position = originalPosition;
            }
        }
    }

    /// <summary>
    /// Finds the previous occurrence of a byte pattern in a stream.
    /// </summary>
    public static long FindPreviousOffset(Stream stream, long startOffset, byte[] pattern)
    {
        long originalPosition = stream.Position;
        int patternLength = pattern.Length;
        long position = startOffset - patternLength;

        try
        {
            while (position >= 0)
            {
                stream.Position = position;
                var check = new byte[patternLength];
                int read = stream.Read(check, 0, patternLength);

                if (read == patternLength && check.AsSpan().SequenceEqual(pattern))
                {
                    if (position == startOffset)
                    {
                        position -= patternLength;
                        continue;
                    }
                    return position;
                }

                position--;
            }

            return -1;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Finds all occurrences of a byte pattern in a stream.
    /// </summary>
    public static long[] FindAllOffsets(
        Stream stream,
        long startOffset,
        byte[] pattern,
        bool useModulo,
        long moduloDivisor,
        long moduloResult,
        bool restorePosition)
    {
        long originalPosition = stream.Position;
        var offsets = new List<long>();
        long position = startOffset;
        int patternLength = pattern.Length;

        try
        {
            while (position < stream.Length - patternLength + 1)
            {
                stream.Position = position;
                var check = new byte[patternLength];
                int read = stream.Read(check, 0, patternLength);

                if (read == patternLength && check.AsSpan().SequenceEqual(pattern))
                {
                    if (!useModulo || position % moduloDivisor == moduloResult)
                    {
                        offsets.Add(position);
                    }
                }

                position++;
            }
        }
        finally
        {
            if (restorePosition)
            {
                stream.Position = originalPosition;
            }
        }

        return offsets.ToArray();
    }

    #endregion

    #region Extraction

    /// <summary>
    /// Extracts a chunk from a stream to a file.
    /// </summary>
    public static void ExtractChunkToFile(
        Stream stream,
        long offset,
        long length,
        string outputPath)
    {
        ExtractChunkToFile(stream, offset, length, outputPath, false, false);
    }

    /// <summary>
    /// Extracts a chunk from a stream to a file with optional logging and batch file generation.
    /// </summary>
    public static void ExtractChunkToFile(
        Stream stream,
        long offset,
        long length,
        string outputPath,
        bool writeLog,
        bool writeBatch)
    {
        string? outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // Handle filename conflicts
        string finalPath = outputPath;
        if (File.Exists(finalPath))
        {
            string dir = outputDir ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(finalPath);
            string ext = Path.GetExtension(finalPath);
            int count = Directory.GetFiles(dir, $"{name}*{ext}", SearchOption.TopDirectoryOnly).Length;
            finalPath = Path.Combine(dir, $"{name}_{count:D3}{ext}");
        }

        // Extract data
        using var output = File.Create(finalPath);
        stream.Position = offset;

        byte[] buffer = new byte[VgmtConstants.FileReadChunkSize];
        long remaining = length;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, buffer.Length);
            int read = stream.Read(buffer, 0, toRead);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            remaining -= read;
        }

        // Write log if requested
        if (writeLog)
        {
            string logEntry = $"Extracted - Offset: 0x{offset:X8}    Length: 0x{length:X8}    File: {Path.GetFileName(finalPath)}";
            string logPath = Path.Combine(outputDir ?? string.Empty, LogFileName);
            File.AppendAllText(logPath, logEntry + Environment.NewLine);
        }
    }

    #endregion

    #region Comparison

    /// <summary>
    /// Compares a segment of a byte array against a pattern.
    /// </summary>
    public static bool MatchesPattern(byte[] data, int offset, byte[] pattern)
    {
        if (data.Length < offset + pattern.Length)
            return false;

        return data.AsSpan(offset, pattern.Length).SequenceEqual(pattern);
    }

    /// <summary>
    /// Compares a segment of a span against a pattern.
    /// </summary>
    public static bool MatchesPattern(ReadOnlySpan<byte> data, int offset, ReadOnlySpan<byte> pattern)
    {
        if (data.Length < offset + pattern.Length)
            return false;

        return data.Slice(offset, pattern.Length).SequenceEqual(pattern);
    }

    #endregion
}
