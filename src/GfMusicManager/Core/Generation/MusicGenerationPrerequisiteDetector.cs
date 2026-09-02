using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// Detects the runtime DLLs required by the files emitted by GF Music Manager.
/// Detection is based on enabled MO2 mod contents rather than mod display names.
/// </summary>
public sealed record MusicGenerationPrerequisiteStatus(
    bool MusicTypeDistributorFound,
    string? MusicTypeDistributorModName,
    bool SkyPatcherFound,
    string? SkyPatcherModName);

public sealed class MusicGenerationPrerequisiteDetector
{
    private const string MusicTypeDistributorDll = @"SKSE\Plugins\MusicTypeDistributor.dll";
    private const string SkyPatcherDll = @"SKSE\Plugins\SkyPatcher.dll";

    public MusicGenerationPrerequisiteStatus Detect(
        IReadOnlyList<ModSource> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var musicTypeDistributor = FindEnabledModContaining(mods, MusicTypeDistributorDll);
        var skyPatcher = FindEnabledModContaining(mods, SkyPatcherDll);
        return new MusicGenerationPrerequisiteStatus(
            musicTypeDistributor is not null,
            musicTypeDistributor?.Name,
            skyPatcher is not null,
            skyPatcher?.Name);
    }

    private static ModSource? FindEnabledModContaining(
        IEnumerable<ModSource> mods,
        string relativePath)
    {
        foreach (var mod in mods)
        {
            if (!mod.Enabled || !Directory.Exists(mod.Path))
            {
                continue;
            }

            var path = Path.Combine(
                mod.Path,
                relativePath.Replace('\\', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                return mod;
            }
        }

        return null;
    }
}
