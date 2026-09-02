using System.Text.Json;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class ExistingMusicProductLoaderTests
{
    [Fact]
    public void LoadDirectory_ExistingFolderWithoutManifest_IsDetectedAndWarned()
    {
        var directory = Directory.CreateTempSubdirectory("gf-existing-product-");
        try
        {
            var result = new ExistingMusicProductLoader().LoadDirectory(directory.FullName);

            Assert.True(result.IsDetected);
            Assert.False(result.IsComplete);
            Assert.Null(result.Manifest);
            Assert.Contains(result.Warnings, warning => warning.Contains("GFMusicProduct.json"));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void Restorer_RestoresAdoptionDestinationsConditionsAndVanillaPolicy()
    {
        var root = Directory.CreateTempSubdirectory("gf-existing-product-restore-");
        try
        {
            var sourcePath = Path.Combine(root.FullName, "source.xwm");
            File.WriteAllBytes(sourcePath, [1, 2, 3]);
            var asset = new AssetSource(
                "music\\combat\\source.xwm",
                AssetSourceKind.Loose,
                "Source Mod",
                Path.Combine(root.FullName, "Source Mod"),
                true,
                sourcePath,
                null,
                3);
            var plugin = new PluginSource(
                "Skyrim.esm",
                Path.Combine(root.FullName, "Skyrim.esm"),
                "Game Data",
                root.FullName,
                true,
                true,
                0,
                0);
            var musicTypeRecord = new PluginRecordSource(
                "000800:Skyrim.esm",
                "MusicType",
                "MUSCombat",
                false,
                plugin,
                true);
            var setting = new MusicSettingSource(
                MusicSettingScope.MusicType,
                musicTypeRecord.FormKey,
                musicTypeRecord.EditorId,
                musicTypeRecord.FormKey,
                musicTypeRecord.EditorId,
                musicTypeRecord,
                musicTypeRecord,
                Array.Empty<MusicTrackSource>());
            var originalCondition = MusicConditionSource.CreateCurrentTime(
                6,
                "GreaterThanOrEqualTo");
            var plan = new MusicGenerationPlan();
            var entry = plan.GetOrCreate(asset, new[] { setting }, new[] { originalCondition });
            var destinationKey = string.Join(
                "\u001f",
                setting.Scope,
                setting.ScopeFormKey,
                setting.MusicTypeFormKey);
            var restoredCondition = MusicConditionSource.CreateCurrentTime(
                18,
                "LessThan");
            var manifest = new MusicGenerationManifest(
                5,
                DateTimeOffset.UtcNow,
                root.FullName,
                "zzz_GFMusicProduct_MUS.ini",
                true,
                false,
                Array.Empty<GeneratedPluginOutput>(),
                Array.Empty<GeneratedMusicTrackOutput>(),
                Array.Empty<GeneratedWorldSpaceOutput>(),
                Array.Empty<GeneratedCellOutput>(),
                Array.Empty<GeneratedMusicTypeOutput>(),
                null,
                Array.Empty<GeneratedAssetOutput>(),
                new[]
                {
                    new MusicGenerationPlanEntryOutput(
                        entry.AssetKey,
                        asset.VirtualPath,
                        false,
                        new[] { destinationKey },
                        new[] { restoredCondition })
                },
                0,
                4096);

            var restore = new MusicGenerationPlanRestorer().Restore(
                plan,
                manifest,
                new[] { setting });

            Assert.True(restore.IsRestored);
            Assert.True(restore.HasCompleteEntryState);
            Assert.False(restore.UsedLegacyTrackFallback);
            Assert.Equal(false, plan.KeepVanillaMusic);
            Assert.False(entry.IsAdopted);
            Assert.Single(entry.DestinationKeys);
            Assert.Equal(setting.MusicTypeFormKey, entry.DestinationKeys[0].MusicTypeFormKey);
            Assert.Single(entry.Conditions);
            Assert.Equal(18, entry.Conditions[0].ComparisonValue);
            Assert.Empty(restore.Warnings);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void ManifestRoundTrip_PersistsExcludedPlanEntries()
    {
        var manifest = new MusicGenerationManifest(
            5,
            DateTimeOffset.UtcNow,
            "output",
            "zzz_GFMusicProduct_MUS.ini",
            false,
            true,
            Array.Empty<GeneratedPluginOutput>(),
            Array.Empty<GeneratedMusicTrackOutput>(),
            Array.Empty<GeneratedWorldSpaceOutput>(),
            Array.Empty<GeneratedCellOutput>(),
            Array.Empty<GeneratedMusicTypeOutput>(),
            null,
            Array.Empty<GeneratedAssetOutput>(),
            new[]
            {
                new MusicGenerationPlanEntryOutput(
                    "asset-key",
                    "music\\excluded.xwm",
                    false,
                    Array.Empty<string>(),
                    Array.Empty<MusicConditionSource>())
            },
            0,
            4096);

        var json = JsonSerializer.Serialize(manifest);
        var restored = JsonSerializer.Deserialize<MusicGenerationManifest>(json);

        Assert.NotNull(restored);
        Assert.Single(restored!.PlanEntries);
        Assert.False(restored.PlanEntries[0].IsAdopted);
    }

    [Fact]
    public void Restorer_ReportsProgressAndGroupsMissingEntriesByMod()
    {
        var root = Directory.CreateTempSubdirectory("gf-existing-product-progress-");
        try
        {
            var sourcePath = Path.Combine(root.FullName, "source.xwm");
            File.WriteAllBytes(sourcePath, [1, 2, 3]);
            var asset = new AssetSource(
                "music\\combat\\source.xwm",
                AssetSourceKind.Loose,
                "Source Mod",
                Path.Combine(root.FullName, "Source Mod"),
                true,
                sourcePath,
                null,
                3);
            var plan = new MusicGenerationPlan();
            var entry = plan.GetOrCreate(
                asset,
                Array.Empty<MusicSettingSource>(),
                Array.Empty<MusicConditionSource>());
            var missingAssetKey = string.Join(
                "\u001f",
                Path.Combine(root.FullName, "Missing Mod"),
                "Missing Mod",
                "Loose",
                "music\\combat\\missing.xwm",
                Path.Combine(root.FullName, "Missing Mod", "missing.xwm"),
                string.Empty);
            var tracks = new[]
            {
                new GeneratedMusicTrackOutput(
                    entry.AssetKey,
                    asset.VirtualPath,
                    "Track_Source",
                    "000800:GF Music Product.esp",
                    "GF Music Product.esp",
                    Array.Empty<string>(),
                    Array.Empty<MusicConditionSource>()),
                new GeneratedMusicTrackOutput(
                    missingAssetKey,
                    "music\\combat\\missing.xwm",
                    "Track_Missing",
                    "000801:GF Music Product.esp",
                    "GF Music Product.esp",
                    Array.Empty<string>(),
                    Array.Empty<MusicConditionSource>())
            };
            var manifest = new MusicGenerationManifest(
                4,
                DateTimeOffset.UtcNow,
                root.FullName,
                "zzz_GFMusicProduct_MUS.ini",
                false,
                true,
                Array.Empty<GeneratedPluginOutput>(),
                tracks,
                Array.Empty<GeneratedWorldSpaceOutput>(),
                Array.Empty<GeneratedCellOutput>(),
                Array.Empty<GeneratedMusicTypeOutput>(),
                null,
                Array.Empty<GeneratedAssetOutput>(),
                Array.Empty<MusicGenerationPlanEntryOutput>(),
                0,
                4096);
            var reports = new List<ScanProgress>();

            var restore = new MusicGenerationPlanRestorer().Restore(
                plan,
                manifest,
                Array.Empty<MusicSettingSource>(),
                new RecordingProgress(reports));

            Assert.Equal(1, restore.RestoredEntryCount);
            Assert.Equal(1, restore.MissingEntryCount);
            Assert.Equal(1, restore.MissingEntriesByMod["Missing Mod"]);
            Assert.Contains(reports, report => report.Stage == "ResultRestore");
            Assert.Equal(2, reports.Last(report => report.Stage == "ResultRestore").Current);
            Assert.Equal(2, reports.Last(report => report.Stage == "ResultRestore").Total);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void RealMo2ExistingProduct_IsValidatedWhenEnvironmentIsProvided()
    {
        var mo2Root = Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_MO2_ROOT");
        if (string.IsNullOrWhiteSpace(mo2Root))
        {
            Console.WriteLine("REAL_MO2_EXISTING_PRODUCT_NOT_RUN: GF_MUSIC_AUDIT_MO2_ROOT was not provided.");
            return;
        }

        var result = new ExistingMusicProductLoader().Load(mo2Root);

        Assert.True(result.IsDetected);
        Assert.True(result.IsComplete, string.Join(Environment.NewLine, result.Warnings));
        Assert.NotNull(result.Manifest);
        if (result.Manifest!.OutputMode == MusicGenerationOutputMode.Dfg)
        {
            Assert.Empty(result.Manifest.Tracks);
            Assert.False(string.IsNullOrWhiteSpace(result.Manifest.DfgPackageDirectory));
            Assert.True(
                Directory.Exists(Path.Combine(
                    result.OutputModDirectory,
                    result.Manifest.DfgPackageDirectory!.Replace('/', Path.DirectorySeparatorChar))),
                result.Manifest.DfgPackageDirectory);
        }
        else
        {
            Assert.NotEmpty(result.Manifest.Tracks);
        }
    }

    private sealed class RecordingProgress(ICollection<ScanProgress> reports) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => reports.Add(value);
    }
}
