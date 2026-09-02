using GfMusicManager.Core.Generation;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicTypeDistributorOutputNameResolverTests
{
    [Fact]
    public void SelectOutputFileName_UsesDefaultWhenItAlreadySortsLast()
    {
        var result = new MusicTypeDistributorOutputNameResolver()
            .SelectOutputFileName(new[] { "A_MUS.ini", "Personalized_MUS.ini" });

        Assert.Equal("zzz_GFMusicProduct_MUS.ini", result);
    }

    [Fact]
    public void SelectOutputFileName_AddsSuffixAfterTheLastExistingFile()
    {
        var result = new MusicTypeDistributorOutputNameResolver()
            .SelectOutputFileName(new[] { "zzzz_Other_MUS.ini" });

        Assert.True(string.Compare(
            result,
            "zzzz_Other_MUS.ini",
            StringComparison.OrdinalIgnoreCase) > 0);
    }

    [Fact]
    public void SelectOutputFileName_HandlesExistingNamesThatFollowTheZPrefix()
    {
        var result = new MusicTypeDistributorOutputNameResolver()
            .SelectOutputFileName(new[] { "zzz~_MUS.ini" });

        Assert.Equal("zzz~~GFMusicProduct_MUS.ini", result);
        Assert.True(string.Compare(
            result,
            "zzz~_MUS.ini",
            StringComparison.OrdinalIgnoreCase) > 0);
    }

    [Fact]
    public void DiscoverExistingFileNames_UsesEnabledModsAndExcludesGeneratedOutput()
    {
        var root = Directory.CreateTempSubdirectory("gf-mtd-discovery-");
        try
        {
            var enabledPath = Path.Combine(root.FullName, "Enabled");
            var disabledPath = Path.Combine(root.FullName, "Disabled");
            var generatedPath = Path.Combine(root.FullName, "GF Music Product");
            Directory.CreateDirectory(enabledPath);
            Directory.CreateDirectory(disabledPath);
            Directory.CreateDirectory(generatedPath);
            File.WriteAllText(Path.Combine(enabledPath, "Enabled_MUS.ini"), string.Empty);
            File.WriteAllText(Path.Combine(disabledPath, "Disabled_MUS.ini"), string.Empty);
            File.WriteAllText(Path.Combine(generatedPath, "zzz_GFMusicProduct_MUS.ini"), string.Empty);

            var result = MusicTypeDistributorOutputNameResolver.DiscoverExistingFileNames(
                new[]
                {
                    Mod("Enabled", enabledPath, true),
                    Mod("Disabled", disabledPath, false),
                    Mod("GF Music Product", generatedPath, true)
                });

            Assert.Equal(new[] { "Enabled_MUS.ini" }, result);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
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
