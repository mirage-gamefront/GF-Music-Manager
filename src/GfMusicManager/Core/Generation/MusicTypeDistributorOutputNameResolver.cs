using GfMusicManager.Core.Diagnostics;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// Chooses an MTD filename that sorts after every enabled, non-generated MTD
/// configuration seen during the MO2 scan.
/// </summary>
public sealed class MusicTypeDistributorOutputNameResolver
{
    public const string DefaultFileName = "zzz_GFMusicProduct_MUS.ini";
    private const string MusicDistributorSuffix = "_MUS.ini";

    public string SelectOutputFileName(IEnumerable<string> existingFileNames)
    {
        ArgumentNullException.ThrowIfNull(existingFileNames);
        var existing = existingFileNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (SortsAfterAll(existing, DefaultFileName))
        {
            return DefaultFileName;
        }

        var lastExisting = existing
            .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
            .First();
        var lastStem = lastExisting.EndsWith(
                MusicDistributorSuffix,
                StringComparison.OrdinalIgnoreCase)
            ? lastExisting[..^MusicDistributorSuffix.Length]
            : Path.GetFileNameWithoutExtension(lastExisting);
        var candidate = $"{lastStem}~GFMusicProduct{MusicDistributorSuffix}";
        if (SortsAfterAll(existing, candidate))
        {
            return candidate;
        }

        throw new MusicGenerationOutputException(
            "既存MTDより後に読み込まれる出力ファイル名を決定できません。");
    }

    private static bool SortsAfterAll(
        IReadOnlyList<string> existing,
        string candidate) =>
        existing.All(name => string.Compare(
            name,
            candidate,
            StringComparison.OrdinalIgnoreCase) < 0);

    public static IReadOnlyList<string> DiscoverExistingFileNames(
        IEnumerable<ModSource> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.Where(mod =>
                     mod.Enabled &&
                     !mod.Name.Equals("GF Music Product", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (!Directory.Exists(mod.Path))
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(
                             mod.Path,
                             "*_MUS.ini",
                             SearchOption.AllDirectories))
                {
                    fileNames.Add(Path.GetFileName(path));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                GfMusicManagerLog.Warning(
                    $"MTD filename discovery skipped {mod.Name}: {exception.Message}");
            }
        }

        return fileNames
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
