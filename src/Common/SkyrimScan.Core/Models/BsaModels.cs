namespace SkyrimScan.Core.Models;

public sealed record BsaEntry(
    string VirtualPath,
    uint PackedSize,
    uint Offset,
    bool IsCompressed);

public sealed record BsaArchive(
    string Path,
    IReadOnlyList<BsaEntry> Entries);
