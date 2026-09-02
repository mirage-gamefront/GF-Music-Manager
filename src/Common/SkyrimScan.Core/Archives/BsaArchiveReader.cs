using System.Collections.Concurrent;
using System.IO.Compression;
using K4os.Compression.LZ4.Streams;
using SkyrimScan.Core.Models;

namespace SkyrimScan.Core.Archives;

public sealed class BsaArchiveReader
{
    private const uint BsaMagic = 0x00415342;
    private const uint ArchiveFlagCompressed = 0x0004;
    private const uint ArchiveFlagEmbedFileNames = 0x0100;
    private const uint FileFlagCompressionToggle = 0x40000000;
    private const uint FileSizeMask = 0x3FFFFFFF;
    private readonly ConcurrentDictionary<string, BsaArchiveIndex> _indexes =
        new(StringComparer.OrdinalIgnoreCase);

    public BsaArchive ReadIndex(string archivePath)
    {
        var fullPath = Path.GetFullPath(archivePath);
        return _indexes.GetOrAdd(fullPath, static path => Parse(path)).ToPublic();
    }

    public byte[] ReadEntry(string archivePath, string virtualPath)
    {
        var fullPath = Path.GetFullPath(archivePath);
        var index = _indexes.GetOrAdd(fullPath, static path => Parse(path));
        var normalizedPath = NormalizePath(virtualPath);
        if (!index.Entries.TryGetValue(normalizedPath, out var entry))
        {
            throw new FileNotFoundException(
                $"BSA entry was not found: {virtualPath}",
                $"{fullPath}!{virtualPath}");
        }

        return index.ReadEntry(entry);
    }

    private static BsaArchiveIndex Parse(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != BsaMagic)
        {
            throw new InvalidDataException($"Not a Bethesda BSA archive: {archivePath}");
        }

        var version = reader.ReadUInt32();
        if (version is not 104 and not 105)
        {
            throw new InvalidDataException(
                $"Unsupported BSA version {version} in {archivePath}; Skyrim SE/AE requires 105.");
        }

        _ = reader.ReadUInt32();
        var archiveFlags = reader.ReadUInt32();
        var folderCount = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();

        var folders = new BsaFolder[checked((int)folderCount)];
        for (var index = 0; index < folders.Length; index++)
        {
            _ = reader.ReadUInt64();
            var fileCount = reader.ReadUInt32();
            if (version == 105)
            {
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt64();
            }
            else
            {
                _ = reader.ReadUInt32();
            }

            folders[index] = new BsaFolder(fileCount);
        }

        var fileRecords = new List<(string Folder, BsaEntryInfo Entry)>();
        foreach (var folder in folders)
        {
            var folderNameLength = reader.ReadByte();
            var folderName = ReadNullTerminatedString(reader, folderNameLength);
            for (var index = 0; index < folder.FileCount; index++)
            {
                _ = reader.ReadUInt64();
                var size = reader.ReadUInt32();
                var offset = reader.ReadUInt32();
                fileRecords.Add((
                    folderName,
                    new BsaEntryInfo(
                        new BsaEntry(
                            string.Empty,
                            size & FileSizeMask,
                            offset,
                            (archiveFlags & ArchiveFlagCompressed) != 0 ^
                            (size & FileFlagCompressionToggle) != 0),
                        (archiveFlags & ArchiveFlagEmbedFileNames) != 0)));
            }
        }

        var entries = new Dictionary<string, BsaEntryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in fileRecords)
        {
            var fileName = ReadNullTerminatedString(reader);
            var virtualPath = NormalizePath(Path.Combine(file.Folder, fileName));
            entries[virtualPath] = file.Entry with
            {
                Entry = file.Entry.Entry with { VirtualPath = virtualPath }
            };
        }

        return new BsaArchiveIndex(
            archivePath,
            (archiveFlags & ArchiveFlagEmbedFileNames) != 0,
            entries);
    }

    private static string ReadNullTerminatedString(BinaryReader reader, int? declaredLength = null)
    {
        var bytes = new List<byte>();
        if (declaredLength is { } length)
        {
            for (var index = 0; index < length; index++)
            {
                var value = reader.ReadByte();
                if (value != 0)
                {
                    bytes.Add(value);
                }
            }
        }
        else
        {
            byte value;
            while ((value = reader.ReadByte()) != 0)
            {
                bytes.Add(value);
            }
        }

        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string NormalizePath(string path) => path
        .Trim()
        .Replace('/', '\\')
        .TrimStart('\\')
        .ToLowerInvariant();

    private sealed record BsaFolder(uint FileCount);

    private sealed record BsaEntryInfo(BsaEntry Entry, bool HasEmbeddedFileName)
    {
        public BsaEntryInfo WithPath(string path) => this with { Entry = Entry with { VirtualPath = path } };
    }

    private sealed class BsaArchiveIndex
    {
        public BsaArchiveIndex(
            string archivePath,
            bool hasEmbeddedFileNames,
            IReadOnlyDictionary<string, BsaEntryInfo> entries)
        {
            ArchivePath = archivePath;
            HasEmbeddedFileNames = hasEmbeddedFileNames;
            Entries = entries;
        }

        public string ArchivePath { get; }
        public bool HasEmbeddedFileNames { get; }
        public IReadOnlyDictionary<string, BsaEntryInfo> Entries { get; }

        public BsaArchive ToPublic() => new(
            ArchivePath,
            Entries.Values
                .Select(x => x.Entry)
                .OrderBy(x => x.VirtualPath, StringComparer.OrdinalIgnoreCase)
                .ToArray());

        public byte[] ReadEntry(BsaEntryInfo entry)
        {
            using var stream = File.OpenRead(ArchivePath);
            stream.Position = entry.Entry.Offset;
            using var reader = new BinaryReader(stream);
            var packedSize = entry.Entry.PackedSize;
            if (entry.HasEmbeddedFileName)
            {
                var fileNameLength = reader.ReadByte();
                _ = reader.ReadBytes(fileNameLength);
                packedSize = checked(packedSize - (uint)(fileNameLength + 1));
            }

            var payload = reader.ReadBytes(checked((int)packedSize));
            if (payload.Length != packedSize)
            {
                throw new EndOfStreamException(
                    $"BSA entry is truncated: {ArchivePath} at {entry.Entry.Offset}");
            }

            if (!entry.Entry.IsCompressed)
            {
                return payload;
            }

            if (payload.Length < sizeof(uint))
            {
                throw new InvalidDataException($"Compressed BSA entry has no size header: {ArchivePath}");
            }

            var unpackedSize = BitConverter.ToUInt32(payload, 0);
            var bytes = Decompress(payload, unpackedSize);
            if (bytes.Length != unpackedSize)
            {
                throw new InvalidDataException(
                    $"BSA decompression size mismatch: {ArchivePath}, expected={unpackedSize}, actual={bytes.Length}");
            }

            return bytes;
        }

        private static byte[] Decompress(byte[] payload, uint unpackedSize)
        {
            const uint lz4FrameMagic = 0x184D2204;
            using var packedStream = new MemoryStream(
                payload,
                sizeof(uint),
                payload.Length - sizeof(uint),
                writable: false);
            using Stream decoded = payload.Length >= sizeof(uint) * 2 &&
                BitConverter.ToUInt32(payload, sizeof(uint)) == lz4FrameMagic
                ? LZ4Stream.Decode(packedStream, leaveOpen: false)
                : new ZLibStream(packedStream, CompressionMode.Decompress, leaveOpen: false);
            using var output = new MemoryStream(checked((int)unpackedSize));
            decoded.CopyTo(output);
            return output.ToArray();
        }
    }
}
