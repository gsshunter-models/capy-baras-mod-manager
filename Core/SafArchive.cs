using System.Globalization;
using System.Text;

namespace MXSimModManager.Core;

public sealed record SafEntry(string Path, long Size, long Offset);

/// <summary>Reads MX Simulator's text-directory SAF format without extracting files.</summary>
public static class SafArchive
{
    private const int MaximumHeaderBytes = 16 * 1024 * 1024;
    private const int MaximumLineBytes = 64 * 1024;
    private const int MaximumEntries = 200_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<SafEntry> ReadIndex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var directory = new List<(string Path, long Size)>();
        long payloadBytes = 0;
        while (true)
        {
            var line = ReadHeaderLine(stream);
            if (line == "-") break;
            var space = line.IndexOf(' ');
            if (space <= 0 || space == line.Length - 1 ||
                !long.TryParse(line.AsSpan(0, space), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var size))
                throw new InvalidDataException("Invalid SAF directory record.");

            var entryPath = line[(space + 1)..];
            if (entryPath.IndexOf('\0') >= 0)
                throw new InvalidDataException("SAF entry contains an invalid filename.");
            try { payloadBytes = checked(payloadBytes + size); }
            catch (OverflowException ex) { throw new InvalidDataException("SAF size overflow.", ex); }
            if (payloadBytes > stream.Length)
                throw new InvalidDataException("SAF directory exceeds the archive size.");
            directory.Add((entryPath, size));
            if (directory.Count > MaximumEntries)
                throw new InvalidDataException("SAF contains too many entries.");
        }

        var offset = stream.Position;
        if (payloadBytes != stream.Length - offset)
            throw new InvalidDataException("SAF file sizes do not match the archive length.");
        var entries = new List<SafEntry>(directory.Count);
        foreach (var item in directory)
        {
            entries.Add(new SafEntry(item.Path, item.Size, offset));
            offset += item.Size;
        }
        return entries;
    }

    public static string? ReadSmallText(string archive, SafEntry entry, int maxBytes = 16384)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (entry.Path.EndsWith(".scram", StringComparison.OrdinalIgnoreCase)) return null;
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (entry.Size < 0 || entry.Offset < 0 || entry.Offset > stream.Length ||
            entry.Size > stream.Length - entry.Offset)
            throw new InvalidDataException("SAF text entry lies outside the archive.");
        var count = (int)Math.Min(entry.Size, Math.Min(maxBytes, 1024 * 1024));
        var bytes = new byte[count];
        stream.Position = entry.Offset;
        stream.ReadExactly(bytes);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return null;
        return Decode(bytes).TrimStart('\uFEFF');
    }

    private static string ReadHeaderLine(FileStream stream)
    {
        var bytes = new List<byte>(128);
        while (true)
        {
            if (stream.Position >= MaximumHeaderBytes)
                throw new InvalidDataException("SAF directory exceeds the size limit.");
            var next = stream.ReadByte();
            if (next < 0) throw new InvalidDataException("SAF directory is incomplete.");
            if (next == '\n') break;
            bytes.Add((byte)next);
            if (bytes.Count > MaximumLineBytes)
                throw new InvalidDataException("SAF directory line exceeds the size limit.");
        }
        if (bytes.Count > 0 && bytes[^1] == '\r') bytes.RemoveAt(bytes.Count - 1);
        return Decode(bytes.ToArray());
    }

    private static string Decode(byte[] bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }
}
