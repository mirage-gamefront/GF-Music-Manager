using System.Security.Cryptography;
using System.Text;
using GfMusicManager.Core.Analysis;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Planning;

/// <summary>
/// Resolves the virtual path that a generated Music Track and generated MOD
/// must use for each adopted asset.  A VFS winner can keep its source path;
/// every other source in the same adopted path group is copied to a stable
/// GF Music Manager-owned path.
/// </summary>
public sealed class MusicGenerationAssetPathMap
{
    public const string ManagedDuplicatePathRoot =
        @"music\GF Music Manager\duplicates";

    private const int MaximumManagedPathLength = 240;

    private readonly IReadOnlyDictionary<string, string> _pathsByAssetKey;

    private MusicGenerationAssetPathMap(
        IReadOnlyDictionary<string, string> pathsByAssetKey)
    {
        _pathsByAssetKey = pathsByAssetKey;
    }

    public IReadOnlyDictionary<string, string> PathsByAssetKey => _pathsByAssetKey;

    public static MusicGenerationAssetPathMap Create(
        IEnumerable<MusicGenerationPlanEntry> adoptedEntries)
    {
        ArgumentNullException.ThrowIfNull(adoptedEntries);

        var sources = adoptedEntries
            .Where(entry => entry.IsAdopted && entry.Asset is not null)
            .SelectMany(entry => new[] { entry.Asset! }
                .Concat(entry.Tracks
                    .Select(track => track.FinaleAsset)
                    .Where(asset => asset is not null)
                    .Cast<AssetSource>()))
            .DistinctBy(
                MusicGenerationPlanEntry.CreateAssetKey,
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(
                MusicGenerationPlanEntry.CreateAssetKey,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var groups = sources
            .GroupBy(
                asset => NormalizeVirtualPath(asset.VirtualPath),
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Reserve every unchanged path before assigning managed paths.  This
        // also protects against a source whose original path happens to look
        // like one of the generated management paths.
        foreach (var group in groups)
        {
            var source = SelectOriginalPathSource(group);
            var assetKey = MusicGenerationPlanEntry.CreateAssetKey(source);
            var originalPath = group.Key;
            paths.Add(assetKey, originalPath);
            usedOutputPaths.Add(originalPath);
        }

        foreach (var group in groups.Where(group => group.Count() > 1))
        {
            var originalPathSource = SelectOriginalPathSource(group);
            foreach (var source in group
                         .Where(candidate => !ReferenceEquals(candidate, originalPathSource))
                         .OrderBy(
                             MusicGenerationPlanEntry.CreateAssetKey,
                             StringComparer.OrdinalIgnoreCase))
            {
                var assetKey = MusicGenerationPlanEntry.CreateAssetKey(source);
                var managedPath = CreateManagedPath(source, suffix: 0);
                var suffix = 2;
                while (!usedOutputPaths.Add(managedPath))
                {
                    managedPath = CreateManagedPath(source, suffix++);
                }

                paths[assetKey] = managedPath;
            }
        }

        return new MusicGenerationAssetPathMap(paths);
    }

    public string GetOutputPath(AssetSource asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var assetKey = MusicGenerationPlanEntry.CreateAssetKey(asset);
        return _pathsByAssetKey.TryGetValue(assetKey, out var outputPath)
            ? outputPath
            : NormalizeVirtualPath(asset.VirtualPath);
    }

    public bool IsRemapped(AssetSource asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return !GetOutputPath(asset).Equals(
            NormalizeVirtualPath(asset.VirtualPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static AssetSource SelectOriginalPathSource(
        IEnumerable<AssetSource> candidates) => candidates
        .OrderByDescending(asset => asset.IsVfsWinner && asset.ModEnabled)
        .ThenByDescending(asset => asset.ModEnabled)
        .ThenBy(
            MusicGenerationPlanEntry.CreateAssetKey,
            StringComparer.OrdinalIgnoreCase)
        .First();

    private static string CreateManagedPath(AssetSource asset, int suffix)
    {
        var assetKey = MusicGenerationPlanEntry.CreateAssetKey(asset);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(assetKey)))
            .ToLowerInvariant();
        var extension = CreateSafeExtension(asset.VirtualPath);
        var fileName = $"asset-{hash}{extension}";
        if (suffix > 1)
        {
            fileName = $"asset-{hash}_{suffix}{extension}";
        }

        var directory = ManagedDuplicatePathRoot + '\\';
        var maximumFileNameLength = Math.Max(
            1,
            MaximumManagedPathLength - directory.Length);
        if (fileName.Length > maximumFileNameLength)
        {
            fileName = fileName[..maximumFileNameLength];
        }

        return directory + fileName;
    }

    private static string CreateSafeExtension(string virtualPath)
    {
        var extension = Path.GetExtension(NormalizeVirtualPath(virtualPath));
        if (string.IsNullOrWhiteSpace(extension) || extension.Length == 1)
        {
            return string.Empty;
        }

        var safeCharacters = extension[1..]
            .Where(char.IsLetterOrDigit)
            .Take(8)
            .ToArray();
        return safeCharacters.Length == 0
            ? string.Empty
            : "." + new string(safeCharacters).ToLowerInvariant();
    }

    private static string NormalizeVirtualPath(string path) =>
        MusicAudioPathIdentity.NormalizeAssetPath(path);
}
