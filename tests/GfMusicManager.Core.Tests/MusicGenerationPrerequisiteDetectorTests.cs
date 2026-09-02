using GfMusicManager.Core.Generation;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicGenerationPrerequisiteDetectorTests
{
    [Fact]
    public void Detect_FindsEnabledPrerequisiteDllsAndReturnsOwningModNames()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-prerequisites-");
        try
        {
            var mtdPath = CreateMod(root.FullName, "Custom MTD");
            var skyPatcherPath = CreateMod(root.FullName, "Custom SkyPatcher");
            File.WriteAllText(
                Path.Combine(mtdPath, "SKSE", "Plugins", "MusicTypeDistributor.dll"),
                string.Empty);
            File.WriteAllText(
                Path.Combine(skyPatcherPath, "SKSE", "Plugins", "SkyPatcher.dll"),
                string.Empty);

            var result = new MusicGenerationPrerequisiteDetector().Detect(
                new[]
                {
                    Mod("Custom MTD", mtdPath, enabled: true),
                    Mod("Custom SkyPatcher", skyPatcherPath, enabled: true)
                });

            Assert.True(result.MusicTypeDistributorFound);
            Assert.Equal("Custom MTD", result.MusicTypeDistributorModName);
            Assert.True(result.SkyPatcherFound);
            Assert.Equal("Custom SkyPatcher", result.SkyPatcherModName);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void Detect_IgnoresDllsFromDisabledMods()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-prerequisites-disabled-");
        try
        {
            var mtdPath = CreateMod(root.FullName, "Disabled MTD");
            var skyPatcherPath = CreateMod(root.FullName, "Disabled SkyPatcher");
            File.WriteAllText(
                Path.Combine(mtdPath, "SKSE", "Plugins", "MusicTypeDistributor.dll"),
                string.Empty);
            File.WriteAllText(
                Path.Combine(skyPatcherPath, "SKSE", "Plugins", "SkyPatcher.dll"),
                string.Empty);

            var result = new MusicGenerationPrerequisiteDetector().Detect(
                new[]
                {
                    Mod("Disabled MTD", mtdPath, enabled: false),
                    Mod("Disabled SkyPatcher", skyPatcherPath, enabled: false)
                });

            Assert.False(result.MusicTypeDistributorFound);
            Assert.Null(result.MusicTypeDistributorModName);
            Assert.False(result.SkyPatcherFound);
            Assert.Null(result.SkyPatcherModName);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    private static string CreateMod(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(path, "SKSE", "Plugins"));
        return path;
    }

    private static ModSource Mod(string name, string path, bool enabled) =>
        new(
            name,
            path,
            enabled,
            1,
            Array.Empty<string>(),
            Array.Empty<string>());
}
