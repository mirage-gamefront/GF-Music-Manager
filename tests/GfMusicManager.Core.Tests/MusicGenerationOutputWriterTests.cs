using System.Text;
using System.Text.Json;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Planning;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using SkyrimScan.Core.Archives;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Plugins;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicGenerationOutputWriterTests
{
    [Fact]
    public void Generate_WritesOneGeneratedTrackPerSourceTrackAndPersistsTrackConditions()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-multiple-tracks-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [1, 2, 3, 4]);
            var asset = CreateLooseAsset(root.FullName, "Music Mod", assetPath, @"music\fixture.xwm");
            var baseSetting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var morning = CreateTrackSource(
                baseSetting.Record.Plugin,
                "000201:Fixture.esp",
                "Track_Morning") with
            {
                AudioPaths = new[] { @"music\fixture.xwm" },
                Conditions = new[] { MusicConditionSource.CreateCurrentTime(5f, "GreaterThanOrEqualTo") }
            };
            var night = CreateTrackSource(
                baseSetting.Record.Plugin,
                "000202:Fixture.esp",
                "Track_Night") with
            {
                AudioPaths = new[] { @"music\fixture.xwm" },
                Conditions = new[] { MusicConditionSource.CreateCurrentTime(22f, "GreaterThanOrEqualTo") }
            };
            var setting = baseSetting with { Tracks = new[] { morning, night } };
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            var entry = plan.GetOrCreate(asset, new[] { setting });

            Assert.Equal(2, entry.Tracks.Count);
            Assert.Equal(2, entry.Conditions.Count);

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions { OutputModDirectory = outputDirectory });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            Assert.Equal(2, result.Tracks.Count);
            Assert.Equal(2, result.Plugins.Sum(plugin => plugin.NewMusicTrackRecordCount));
            Assert.Equal(
                entry.Tracks.Select(track => track.TrackKey).Order(StringComparer.OrdinalIgnoreCase),
                result.Tracks.Select(track => track.TrackKey).Order(StringComparer.OrdinalIgnoreCase));
            Assert.Equal(
                entry.Tracks.SelectMany(track => track.Conditions)
                    .Select(MusicConditionFormatter.CreateRecordKey)
                    .Order(StringComparer.OrdinalIgnoreCase),
                result.Tracks.SelectMany(track => track.Conditions)
                    .Select(MusicConditionFormatter.CreateRecordKey)
                    .Order(StringComparer.OrdinalIgnoreCase));

            using var generatedPlugin = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(
                    ModKey.FromNameAndExtension("GF Music Product.esp"),
                    Path.Combine(outputDirectory, "GF Music Product.esp")),
                SkyrimRelease.SkyrimSE);
            Assert.Equal(2, generatedPlugin.MusicTracks.Records.Count());

            var manifest = File.ReadAllText(result.ManifestPath, Encoding.UTF8);
            Assert.Contains("TrackKey", manifest, StringComparison.Ordinal);
            var manifestModel = System.Text.Json.JsonSerializer.Deserialize<MusicGenerationManifest>(manifest);
            Assert.NotNull(manifestModel);
            Assert.Equal(2, Assert.Single(manifestModel!.PlanEntries).Tracks.Count);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_WritesTrackAudioManifestAndMtdForNormalMode()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [1, 2, 3, 4]);
            var asset = CreateLooseAsset(root.FullName, "Music Mod", assetPath, @"music\fixture.xwm");
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = false
            };
            plan.GetOrCreate(
                asset,
                new[] { setting },
                new[] { MusicConditionSource.CreateCurrentTime(6f, "GreaterThanOrEqualTo") });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var trackOutput = Assert.Single(result.Tracks);
            Assert.Single(result.Plugins);
            var assetOutput = Assert.Single(result.Assets);
            Assert.True(assetOutput.IsCopied);
            Assert.Equal(@"music\fixture.xwm", assetOutput.OutputPath);
            Assert.True(File.Exists(Path.Combine(outputDirectory, "GF Music Product.esp")));
            Assert.True(File.Exists(Path.Combine(outputDirectory, @"music\fixture.xwm")));
            Assert.True(File.Exists(result.MtdFilePath));
            Assert.True(File.Exists(result.ManifestPath));
            Assert.Empty(result.Cells);
            Assert.Null(result.CellSkyPatcherFilePath);

            var mtd = File.ReadAllText(result.MtdFilePath, Encoding.UTF8);
            var generatedFormKey = FormKey.Factory(trackOutput.FormKey);
            Assert.Contains("[General]", mtd);
            Assert.Contains(
                $"MUSExploreFixture! = 0x{generatedFormKey.ID:X6}~GF Music Product.esp",
                mtd);

            using var generatedPlugin = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(
                    ModKey.FromNameAndExtension("GF Music Product.esp"),
                    Path.Combine(outputDirectory, "GF Music Product.esp")),
                SkyrimRelease.SkyrimSE);
            Assert.True(generatedPlugin.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small));
            var generatedTrack = Assert.Single(generatedPlugin.MusicTracks.Records);
            Assert.Equal(trackOutput.FormKey, generatedTrack.FormKey.ToString());
            Assert.Equal(MusicTrack.TypeEnum.SingleTrack, generatedTrack.Type);
            Assert.Single(generatedTrack.Conditions!);
            Assert.Empty(generatedTrack.CuePoints!);
            Assert.Equal(4, new FileInfo(Path.Combine(outputDirectory, @"music\fixture.xwm")).Length);

            var manifest = File.ReadAllText(result.ManifestPath, Encoding.UTF8);
            Assert.Contains("WorldSpaceIndividualAssignment", manifest);
            Assert.Contains("000100:Fixture.esp", manifest);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_DfgModeUsesSharedCommonOutputAndDfgTrackTypeOutput()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-dfg-hybrid-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [1, 2, 3, 4]);
            var asset = CreateLooseAsset(root.FullName, "Music Mod", assetPath, @"music\fixture.xwm");
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(asset, new[] { setting });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory,
                    OutputMode = MusicGenerationOutputMode.Dfg,
                    DfgPackageName = "GF Music Manager DFG",
                    ExistingMtdFileNames = Array.Empty<string>()
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            Assert.Contains(
                result.Diagnostic.Checks,
                check => check.StartsWith("DFG:", StringComparison.Ordinal));
            Assert.Equal(MusicGenerationOutputMode.Dfg, result.OutputMode);
            Assert.Empty(result.Tracks);
            Assert.Empty(result.Plugins);
            Assert.Single(result.Assets);
            Assert.True(File.Exists(Path.Combine(outputDirectory, @"music\fixture.xwm")));
            Assert.True(File.Exists(result.MtdFilePath));
            Assert.NotNull(result.DfgOutput);
            Assert.Equal(1, result.DfgOutput!.MusicTrackCount);
            Assert.Equal(1, result.DfgOutput.MusicTypeCount);
            Assert.Equal(1, result.DfgOutput.ExternalMusicTypePatchCount);
            Assert.True(File.Exists(result.DfgOutput.PackageDatabasePath));
            Assert.Single(result.DfgOutput.ImportPaths);
            Assert.Empty(Directory.EnumerateFiles(outputDirectory, "*.esp", SearchOption.AllDirectories));

            var manifest = System.Text.Json.JsonSerializer.Deserialize<MusicGenerationManifest>(
                File.ReadAllText(result.ManifestPath));
            Assert.NotNull(manifest);
            Assert.Equal(MusicGenerationOutputMode.Dfg, manifest!.OutputMode);
            Assert.False(string.IsNullOrWhiteSpace(manifest.DfgPackageDirectory));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_DfgOverwriteRemovesPreviousStaticMusicTracks()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-dfg-overwrite-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [1, 2, 3]);
            var asset = CreateLooseAsset(root.FullName, "Music Mod", assetPath, @"music\fixture.xwm");
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(asset, new[] { setting });
            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");

            var normalResult = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory,
                    OverwriteExisting = true
                });
            Assert.True(normalResult.Diagnostic.IsSuccess, normalResult.Diagnostic.Details);
            using (var normalPlugin = SkyrimMod.CreateFromBinaryOverlay(
                       new ModPath(
                           ModKey.FromNameAndExtension("GF Music Product.esp"),
                           Path.Combine(outputDirectory, "GF Music Product.esp")),
                       SkyrimRelease.SkyrimSE))
            {
                Assert.Single(normalPlugin.MusicTracks.Records);
            }

            var dfgResult = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory,
                    OutputMode = MusicGenerationOutputMode.Dfg,
                    DfgPackageName = "GF Music Manager DFG",
                    OverwriteExisting = true
                });

            Assert.True(dfgResult.Diagnostic.IsSuccess, dfgResult.Diagnostic.Details);
            Assert.NotNull(dfgResult.DfgOutput);
            Assert.Equal(1, dfgResult.DfgOutput!.MusicTrackCount);
            Assert.Empty(dfgResult.Plugins);
            Assert.Empty(Directory.EnumerateFiles(outputDirectory, "*.esp", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_ReferencesEnabledVfsWinnerWithoutCopyingAudio()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-reference-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Winning Music Mod", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [5, 6, 7]);
            var asset = CreateLooseAsset(
                root.FullName,
                "Winning Music Mod",
                assetPath,
                @"music\fixture.xwm") with
            {
                IsVfsWinner = true
            };
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = true
            };
            plan.GetOrCreate(asset, new[] { setting });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var assetOutput = Assert.Single(result.Assets);
            Assert.False(assetOutput.IsCopied);
            Assert.Null(assetOutput.OutputPath);
            Assert.False(File.Exists(Path.Combine(outputDirectory, @"music\fixture.xwm")));
            Assert.Equal(assetPath, assetOutput.SourcePath);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_KeepVanillaReplacesTypeWithOfficialAndGeneratedTracksOnly()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-final-type-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "generated.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [4, 3, 2, 1]);
            var asset = CreateLooseAsset(
                root.FullName,
                "Music Mod",
                assetPath,
                @"music\generated.xwm");
            var settings = CreateOfficialAndOldModSettings(root.FullName);
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = true
            };
            plan.GetOrCreate(asset, new[] { settings[0] });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                settings,
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var generatedTrack = FormKey.Factory(Assert.Single(result.Tracks).FormKey);
            var mtd = File.ReadAllText(result.MtdFilePath, Encoding.UTF8);
            Assert.Contains(
                $"MUSCombat! = 0x000001~Skyrim.esm,0x{generatedTrack.ID:X6}~GF Music Product.esp",
                mtd);
            Assert.DoesNotContain("Fantasy Music.esp", mtd, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                result.Diagnostic.Checks,
                check => check.Contains(
                    "MUSCombat uses the expected replacement Track list",
                    StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_MergesDefinitionsWithTheSameEditorIdIntoOneMtdTrackList()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-shared-editor-id-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "generated.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [8, 7, 6, 5]);
            var asset = CreateLooseAsset(
                root.FullName,
                "Music Mod",
                assetPath,
                @"music\generated.xwm");

            var firstSetting = CreateMusicTypeSetting(
                root.FullName,
                "MUSShared",
                "FirstType.esp",
                1,
                true,
                "000100:FirstType.esp");
            var secondSetting = CreateMusicTypeSetting(
                root.FullName,
                "MUSShared",
                "SecondType.esp",
                2,
                true,
                "000100:SecondType.esp");
            var officialPlugin = new PluginSource(
                "Skyrim.esm",
                Path.Combine(root.FullName, "Skyrim.esm"),
                "Game Data",
                root.FullName,
                true,
                true,
                0,
                0);
            firstSetting = firstSetting with
            {
                Tracks = new[]
                {
                    CreateTrackSource(
                        officialPlugin,
                        "000001:Skyrim.esm",
                        "MUSOfficialFirst")
                }
            };
            secondSetting = secondSetting with
            {
                Tracks = new[]
                {
                    CreateTrackSource(
                        officialPlugin,
                        "000002:Skyrim.esm",
                        "MUSOfficialSecond")
                }
            };

            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = true
            };
            plan.GetOrCreate(asset, new[] { firstSetting, secondSetting });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { firstSetting, secondSetting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var mtdLines = File.ReadAllLines(result.MtdFilePath, Encoding.UTF8);
            var sharedTypeLines = mtdLines
                .Where(line => line.StartsWith("MUSShared!", StringComparison.Ordinal))
                .ToArray();
            var sharedTypeLine = Assert.Single(sharedTypeLines);
            Assert.Contains("0x000001~Skyrim.esm", sharedTypeLine);
            Assert.Contains("0x000002~Skyrim.esm", sharedTypeLine);
            var generatedTrack = Assert.Single(result.Tracks);
            var generatedTrackId = FormKey.Factory(generatedTrack.FormKey);
            Assert.Contains(
                $"0x{generatedTrackId.ID:X6}~GF Music Product.esp",
                sharedTypeLine);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_RemoveVanillaReplacesTypeWithGeneratedTracksOnly()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-no-vanilla-");
        try
        {
            var firstPath = Path.Combine(root.FullName, "First Music", "music", "first.xwm");
            var secondPath = Path.Combine(root.FullName, "Second Music", "music", "second.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
            File.WriteAllBytes(firstPath, [1, 2]);
            File.WriteAllBytes(secondPath, [3, 4]);
            var settings = CreateOfficialAndOldModSettings(root.FullName);
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(
                CreateLooseAsset(root.FullName, "First Music", firstPath, @"music\first.xwm"),
                new[] { settings[0] });
            plan.GetOrCreate(
                CreateLooseAsset(root.FullName, "Second Music", secondPath, @"music\second.xwm"),
                new[] { settings[0] });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                settings,
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var generatedIds = result.Tracks
                .Select(track => FormKey.Factory(track.FormKey))
                .Select(formKey => $"0x{formKey.ID:X6}~GF Music Product.esp")
                .ToArray();
            var mtd = File.ReadAllText(result.MtdFilePath, Encoding.UTF8);
            Assert.Contains($"MUSCombat! = {string.Join(',', generatedIds)}", mtd);
            Assert.DoesNotContain("Skyrim.esm", mtd, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Fantasy Music.esp", mtd, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_SelectsAnMtdNameAfterExistingConfigurationsAndStoresItInManifest()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-mtd-name-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [1]);
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(
                CreateLooseAsset(root.FullName, "Music Mod", assetPath, @"music\fixture.xwm"),
                new[] { setting });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory,
                    ExistingMtdFileNames = new[] { "zzzz_Other_MUS.ini" }
                });

            var outputName = Path.GetFileName(result.MtdFilePath);
            Assert.True(string.Compare(
                outputName,
                "zzzz_Other_MUS.ini",
                StringComparison.OrdinalIgnoreCase) > 0);
            var manifest = File.ReadAllText(result.ManifestPath, Encoding.UTF8);
            Assert.Contains($"\"MtdFileName\": \"{outputName}\"", manifest);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_PreservesEditableConditionMetadataAndVerifiesTheRoundTrip()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-conditions-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "conditions.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [1, 3, 5, 7]);
            var asset = CreateLooseAsset(
                root.FullName,
                "Music Mod",
                assetPath,
                @"music\conditions.xwm");
            var setting = CreateMusicTypeSetting(root.FullName, "MUSConditionFixture");
            var skyrimPlugin = new PluginSource(
                "Skyrim.esm",
                Path.Combine(root.FullName, "Skyrim.esm"),
                "Skyrim",
                root.FullName,
                true,
                true,
                0,
                0);
            var dragonKeyword = new PluginRecordSource(
                "035D59:Skyrim.esm",
                "Keyword",
                "ActorTypeDragon",
                false,
                skyrimPlugin,
                true);
            var rainWeather = new PluginRecordSource(
                "000805:Skyrim.esm",
                "Weather",
                "SkyrimWeatherRain",
                false,
                skyrimPlugin,
                true);
            var expectedConditions = new MusicConditionSource[]
            {
                MusicConditionSource.CreateCurrentTime(22f, "GreaterThanOrEqualTo", "OR"),
                MusicConditionSource.CreateCurrentTime(5f, "LessThanOrEqualTo", "OR"),
                MusicConditionSource.CreateCombatKeyword(dragonKeyword, hasKeyword: true),
                MusicConditionSource.CreateCurrentWeather(rainWeather, matches: false)
            };
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = false
            };
            plan.GetOrCreate(asset, new[] { setting }, expectedConditions);

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            Assert.Contains(
                result.Diagnostic.Checks,
                check => check.Contains("再生条件4件が一致", StringComparison.Ordinal));

            var generatedPluginPath = Path.Combine(outputDirectory, "GF Music Product.esp");
            var generatedRecords = new PluginRecordScanner().Read(new PluginSource(
                "GF Music Product.esp",
                generatedPluginPath,
                "GF Music Product",
                outputDirectory,
                true,
                true,
                0,
                0));
            var actualConditions = Assert.Single(generatedRecords.Where(record =>
                    record.RecordType == "MusicTrack"))
                .Conditions
                .Select(condition => MusicConditionSource.From(condition))
                .ToArray();

            Assert.Equal(
                expectedConditions.Select(MusicConditionFormatter.CreateRecordKey),
                actualConditions.Select(MusicConditionFormatter.CreateRecordKey));
            var combat = Assert.Single(actualConditions.Where(condition =>
                condition.FunctionName == "GetCombatTargetHasKeyword"));
            Assert.Equal("Reference", combat.RunOnType);
            Assert.Equal("000014:Skyrim.esm", combat.ReferenceFormKey);
            Assert.Equal("035D59:Skyrim.esm", combat.KeywordFormKey);
            var weather = Assert.Single(actualConditions.Where(condition =>
                condition.FunctionName == "GetIsCurrentWeather"));
            Assert.Equal("000805:Skyrim.esm", weather.WeatherFormKey);

            var mismatchedTracks = result.Tracks
                .Select(track => track with
                {
                    Conditions = new[]
                    {
                        MusicConditionSource.CreateCurrentTime(
                            8f,
                            "GreaterThanOrEqualTo")
                    }
                })
                .ToArray();
            var mismatchDiagnostic = new MusicGenerationPostGenerationDiagnostic(
                    new BsaArchiveReader())
                .Run(
                    outputDirectory,
                    outputDirectory,
                    result.MtdFilePath,
                    result.ManifestPath,
                    new[] { setting },
                    result.Plugins,
                    mismatchedTracks,
                    result.WorldSpaces,
                    Array.Empty<GeneratedCellOutput>(),
                    Array.Empty<GeneratedMusicTypeOutput>(),
                    null,
                    result.Assets,
                    worldSpaceIndividualAssignment: false,
                    expectedNewRecordCount: 1,
                    maxNewRecordsPerPlugin: 4096);
            Assert.False(mismatchDiagnostic.IsSuccess);
            Assert.Contains(
                mismatchDiagnostic.Errors,
                error => error.Contains("再生条件が生成予定と一致しません", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_UsesEffectiveDefinitionWhenCandidateSettingsShareDestination()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-definition-conflict-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [5, 4, 3]);
            var asset = CreateLooseAsset(root.FullName, "Music Mod", assetPath, @"music\fixture.xwm");
            var replacedDefinition = CreateMusicTypeSetting(
                root.FullName,
                "MUSReplaced",
                "Fantasy.esp",
                1,
                false);
            var effectiveDefinition = CreateMusicTypeSetting(
                root.FullName,
                "MUSEffective",
                "Dream.esp",
                2,
                true);
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = false
            };
            plan.GetOrCreate(asset, new[] { replacedDefinition, effectiveDefinition });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { replacedDefinition, effectiveDefinition },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var mtd = File.ReadAllText(result.MtdFilePath, Encoding.UTF8);
            Assert.Contains("MUSEffective!", mtd);
            Assert.DoesNotContain("MUSReplaced!", mtd);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_ReferencesBsaBackedAudio()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-bsa-");
        try
        {
            var archivePath = Path.Combine(root.FullName, "Archive Music", "Archive Music.bsa");
            new BsaFixture(archivePath);
            var asset = new AssetSource(
                @"music\archive_track.xwm",
                AssetSourceKind.Bsa,
                "Archive Music",
                Path.Combine(root.FullName, "Archive Music"),
                true,
                archivePath,
                @"music\archive_track.xwm",
                4)
            {
                IsVfsWinner = true
            };
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = true
            };
            plan.GetOrCreate(asset, new[] { setting });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            Assert.False(File.Exists(Path.Combine(outputDirectory, @"music\archive_track.xwm")));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_CellAssignmentsUseAnIntegratedMusicTypeForConflictingCell()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-cell-conflict-generation-");
        try
        {
            var firstPath = Path.Combine(root.FullName, "First Music", "music", "first.xwm");
            var secondPath = Path.Combine(root.FullName, "Second Music", "music", "second.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
            File.WriteAllBytes(firstPath, [1]);
            File.WriteAllBytes(secondPath, [2]);

            var firstMusicType = CreateMusicTypeSetting(root.FullName, "MUSCellFirst");
            var firstCellRecord = firstMusicType.Record with
            {
                FormKey = "000200:Fixture.esp",
                RecordType = "Cell",
                EditorId = "Cell_Fixture"
            };
            var firstCell = firstMusicType with
            {
                Scope = MusicSettingScope.Cell,
                ScopeFormKey = firstCellRecord.FormKey,
                ScopeEditorId = firstCellRecord.EditorId,
                Record = firstCellRecord
            };

            var secondMusicType = CreateMusicTypeSetting(
                root.FullName,
                "MUSCellSecond",
                "Second.esp",
                2,
                true,
                "000101:Second.esp");
            var secondCellRecord = secondMusicType.Record with
            {
                FormKey = firstCellRecord.FormKey,
                RecordType = "Cell",
                EditorId = firstCellRecord.EditorId
            };
            var secondCell = secondMusicType with
            {
                Scope = MusicSettingScope.Cell,
                ScopeFormKey = secondCellRecord.FormKey,
                ScopeEditorId = secondCellRecord.EditorId,
                Record = secondCellRecord
            };

            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = false
            };
            plan.GetOrCreate(
                CreateLooseAsset(root.FullName, "First Music", firstPath, @"music\first.xwm"),
                new[] { firstCell });
            plan.GetOrCreate(
                CreateLooseAsset(root.FullName, "Second Music", secondPath, @"music\second.xwm"),
                new[] { secondCell });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { firstCell, secondCell },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var cell = Assert.Single(result.Cells);
            var integratedMusicType = Assert.Single(result.IntegratedMusicTypes);
            Assert.Equal(MusicSettingScope.Cell, integratedMusicType.Scope);
            Assert.Equal(firstCellRecord.FormKey, integratedMusicType.ScopeFormKey);
            Assert.Equal(integratedMusicType.MusicTypeFormKey, cell.MusicTypeFormKey);
            Assert.Equal(1, result.Plugins[0].NewMusicTypeRecordCount);
            var patcherText = File.ReadAllText(result.CellSkyPatcherFilePath!, Encoding.UTF8);
            var integratedFormKey = FormKey.Factory(integratedMusicType.MusicTypeFormKey);
            Assert.Contains(
                $"filterByCells=Fixture.esp|000200:musicType=GF Music Product.esp|{integratedFormKey.ID:X6}",
                patcherText);
            Assert.DoesNotContain(
                "musicType=Second.esp|000101",
                patcherText,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains(
                    "統合用Music Type",
                    StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_DfgCellConflictKeepsBridgeTypeInEspAndPatchesItWithDfgTracks()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-cell-conflict-dfg-");
        try
        {
            var firstPath = Path.Combine(root.FullName, "First Music", "music", "first.xwm");
            var secondPath = Path.Combine(root.FullName, "Second Music", "music", "second.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
            File.WriteAllBytes(firstPath, [1]);
            File.WriteAllBytes(secondPath, [2]);

            var firstMusicType = CreateMusicTypeSetting(root.FullName, "MUSCellFirst");
            var firstCellRecord = firstMusicType.Record with
            {
                FormKey = "000200:Fixture.esp",
                RecordType = "Cell",
                EditorId = "Cell_Fixture"
            };
            var firstCell = firstMusicType with
            {
                Scope = MusicSettingScope.Cell,
                ScopeFormKey = firstCellRecord.FormKey,
                ScopeEditorId = firstCellRecord.EditorId,
                Record = firstCellRecord
            };

            var secondMusicType = CreateMusicTypeSetting(
                root.FullName,
                "MUSCellSecond",
                "Second.esp",
                2,
                true,
                "000101:Second.esp");
            var secondCellRecord = secondMusicType.Record with
            {
                FormKey = firstCellRecord.FormKey,
                RecordType = "Cell",
                EditorId = firstCellRecord.EditorId
            };
            var secondCell = secondMusicType with
            {
                Scope = MusicSettingScope.Cell,
                ScopeFormKey = secondCellRecord.FormKey,
                ScopeEditorId = secondCellRecord.EditorId,
                Record = secondCellRecord
            };

            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = false
            };
            plan.GetOrCreate(
                CreateLooseAsset(root.FullName, "First Music", firstPath, @"music\first.xwm"),
                new[] { firstCell });
            plan.GetOrCreate(
                CreateLooseAsset(root.FullName, "Second Music", secondPath, @"music\second.xwm"),
                new[] { secondCell });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { firstCell, secondCell },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory,
                    OutputMode = MusicGenerationOutputMode.Dfg,
                    DfgPackageName = "GF Music Manager DFG"
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var plugin = Assert.Single(result.Plugins);
            Assert.Equal(0, plugin.NewMusicTrackRecordCount);
            Assert.Equal(1, plugin.NewMusicTypeRecordCount);
            Assert.Empty(result.Tracks);

            var integratedMusicType = Assert.Single(result.IntegratedMusicTypes);
            Assert.Empty(integratedMusicType.TrackFormKeys);
            var cell = Assert.Single(result.Cells);
            Assert.Equal(integratedMusicType.MusicTypeFormKey, cell.MusicTypeFormKey);
            var cellPatcher = File.ReadAllText(result.CellSkyPatcherFilePath!, Encoding.UTF8);
            var integratedFormKey = FormKey.Factory(integratedMusicType.MusicTypeFormKey);
            Assert.Contains(
                $"musicType=GF Music Product.esp|{integratedFormKey.ID:X6}",
                cellPatcher,
                StringComparison.OrdinalIgnoreCase);

            using var generatedPlugin = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(
                    ModKey.FromNameAndExtension("GF Music Product.esp"),
                    Path.Combine(outputDirectory, "GF Music Product.esp")),
                SkyrimRelease.SkyrimSE);
            Assert.Empty(generatedPlugin.MusicTracks.Records);
            Assert.Single(generatedPlugin.MusicTypes.Records);

            Assert.NotNull(result.DfgOutput);
            Assert.Equal(2, result.DfgOutput!.MusicTrackCount);
            Assert.Equal(3, result.DfgOutput.MusicTypeCount);
            Assert.Equal(3, result.DfgOutput.ExternalMusicTypePatchCount);

            var importedTrackIds = result.DfgOutput.ImportPaths
                .Select(path => JsonDocument.Parse(File.ReadAllText(path)))
                .Select(document =>
                {
                    using (document)
                    {
                        return document.RootElement
                            .GetProperty("editorId")
                            .GetString()!;
                    }
                })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bridgePatch = ReadDfgPatches(result.DfgOutput.PackageDatabasePath)
                .Single(patch =>
                    patch.SourcePlugin.Equals(
                        "GF Music Product.esp",
                        StringComparison.OrdinalIgnoreCase) &&
                    patch.LocalFormId == integratedFormKey.ID);
            using var changes = JsonDocument.Parse(bridgePatch.ChangesJson);
            var references = changes.RootElement
                .GetProperty("fields")
                .GetProperty("musicTypeTracks")
                .GetProperty("value")
                .EnumerateArray()
                .ToArray();
            Assert.Equal("replace", changes.RootElement
                .GetProperty("fields")
                .GetProperty("musicTypeTracks")
                .GetProperty("operation")
                .GetString());
            Assert.Equal(2, references.Length);
            Assert.All(references, reference =>
            {
                Assert.True(reference.TryGetProperty("editorID", out var editorId));
                Assert.False(reference.TryGetProperty("formID", out _));
                Assert.Contains(editorId.GetString()!, importedTrackIds);
            });
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_CellConflictAggregatesOfficialAndGeneratedTracks()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-cell-official-merge-");
        try
        {
            var firstSetting = CreateScopedSetting(
                root.FullName,
                MusicSettingScope.Cell,
                "Cell_Official_First",
                "MUSCellOfficialFirst",
                "First.esp",
                "000101:First.esp",
                "000200:First.esp") with
            {
                Tracks = new[]
                {
                    CreateTrackSource(
                        new PluginSource(
                            "Skyrim.esm",
                            Path.Combine(root.FullName, "Skyrim.esm"),
                            "Game Data",
                            root.FullName,
                            true,
                            true,
                            0,
                            0),
                        "000001:Skyrim.esm",
                        "MUSOfficialCellFirst")
                }
            };
            var secondSetting = CreateScopedSetting(
                root.FullName,
                MusicSettingScope.Cell,
                "Cell_Official_Second",
                "MUSCellOfficialSecond",
                "Second.esp",
                "000102:Second.esp",
                "000200:First.esp") with
            {
                Tracks = new[]
                {
                    CreateTrackSource(
                        new PluginSource(
                            "Update.esm",
                            Path.Combine(root.FullName, "Update.esm"),
                            "Game Data",
                            root.FullName,
                            true,
                            true,
                            0,
                            0),
                        "000002:Update.esm",
                        "MUSOfficialCellSecond")
                }
            };
            var firstAsset = CreateScopeAsset(
                root.FullName,
                "First Music",
                "cell-official-first.xwm");
            var secondAsset = CreateScopeAsset(
                root.FullName,
                "Second Music",
                "cell-official-second.xwm");
            var plan = new MusicGenerationPlan { KeepVanillaMusic = true };
            plan.GetOrCreate(firstAsset, new[] { firstSetting });
            plan.GetOrCreate(secondAsset, new[] { secondSetting });

            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { firstSetting, secondSetting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = Path.Combine(root.FullName, "GF Music Product")
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var integratedType = Assert.Single(result.IntegratedMusicTypes);
            Assert.Equal(4, integratedType.TrackFormKeys.Count);

            using var generatedPlugin = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(
                    ModKey.FromNameAndExtension("GF Music Product.esp"),
                    Path.Combine(result.OutputModDirectory, "GF Music Product.esp")),
                SkyrimRelease.SkyrimSE);
            var generatedMusicType = Assert.Single(generatedPlugin.MusicTypes.Records);
            Assert.Equal(
                integratedType.TrackFormKeys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                generatedMusicType.Tracks!
                    .Select(track => track.FormKey.ToString())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
            Assert.Contains(
                generatedMusicType.Tracks!,
                track => track.FormKey == FormKey.Factory("000001:Skyrim.esm"));
            Assert.Contains(
                generatedMusicType.Tracks!,
                track => track.FormKey == FormKey.Factory("000002:Update.esm"));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_IntegratesConflictingLocationAndRegionAssignments()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-scope-merge-");
        try
        {
            var locationFirst = CreateScopedSetting(
                root.FullName,
                MusicSettingScope.Location,
                "Location_Fixture",
                "MUSLocationFirst",
                "First.esp",
                "000101:First.esp",
                "000200:First.esp");
            var locationSecond = CreateScopedSetting(
                root.FullName,
                MusicSettingScope.Location,
                "Location_Fixture",
                "MUSLocationSecond",
                "Second.esp",
                "000102:Second.esp",
                "000200:First.esp");
            var regionFirst = CreateScopedSetting(
                root.FullName,
                MusicSettingScope.Region,
                "Region_Fixture",
                "MUSRegionFirst",
                "First.esp",
                "000103:First.esp",
                "000300:First.esp");
            var regionSecond = CreateScopedSetting(
                root.FullName,
                MusicSettingScope.Region,
                "Region_Fixture",
                "MUSRegionSecond",
                "Second.esp",
                "000104:Second.esp",
                "000300:First.esp");

            var assets = new[]
            {
                CreateScopeAsset(root.FullName, "Location One", "location-one.xwm"),
                CreateScopeAsset(root.FullName, "Location Two", "location-two.xwm"),
                CreateScopeAsset(root.FullName, "Region One", "region-one.xwm"),
                CreateScopeAsset(root.FullName, "Region Two", "region-two.xwm")
            };
            var settings = new[] { locationFirst, locationSecond, regionFirst, regionSecond };
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(assets[0], new[] { locationFirst });
            plan.GetOrCreate(assets[1], new[] { locationSecond });
            plan.GetOrCreate(assets[2], new[] { regionFirst });
            plan.GetOrCreate(assets[3], new[] { regionSecond });

            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                settings,
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = Path.Combine(root.FullName, "GF Music Product")
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            Assert.Equal(
                2,
                result.IntegratedMusicTypes.Count);
            Assert.Contains(
                result.IntegratedMusicTypes,
                type => type.Scope == MusicSettingScope.Location);
            Assert.Contains(
                result.IntegratedMusicTypes,
                type => type.Scope == MusicSettingScope.Region);

            var mtd = File.ReadAllText(result.MtdFilePath, Encoding.UTF8);
            Assert.Contains(
                "Location_Fixture = GFITG_L_Location_Fixture",
                mtd);
            Assert.Contains(
                "Region_Fixture = GFITG_R_Region_Fixture",
                mtd);
            Assert.DoesNotContain("MUSLocationFirst!", mtd);
            Assert.DoesNotContain("MUSLocationSecond!", mtd);
            Assert.DoesNotContain("MUSRegionFirst!", mtd);
            Assert.DoesNotContain("MUSRegionSecond!", mtd);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_CellWritesSkyPatcherMusicTypeAssignmentAndManifest()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-cell-generation-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "cell.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [4, 5, 6]);
            var asset = CreateLooseAsset(root.FullName, "Music Mod", assetPath, @"music\cell.xwm");
            var musicTypeSetting = CreateMusicTypeSetting(root.FullName, "MUSCellFixture");
            var cellRecord = musicTypeSetting.Record with
            {
                FormKey = "000200:Fixture.esp",
                RecordType = "Cell",
                EditorId = "Cell_Fixture"
            };
            var cellSetting = musicTypeSetting with
            {
                Scope = MusicSettingScope.Cell,
                ScopeFormKey = cellRecord.FormKey,
                ScopeEditorId = cellRecord.EditorId,
                Record = cellRecord
            };
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = false
            };
            plan.GetOrCreate(asset, new[] { cellSetting });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { cellSetting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var cell = Assert.Single(result.Cells);
            Assert.Equal(cellRecord.FormKey, cell.CellFormKey);
            Assert.NotNull(result.CellSkyPatcherFilePath);
            Assert.True(File.Exists(result.CellSkyPatcherFilePath));
            var patcherText = File.ReadAllText(result.CellSkyPatcherFilePath!, Encoding.UTF8);
            Assert.Contains(
                "filterByCells=Fixture.esp|000200:musicType=Fixture.esp|000100",
                patcherText);
            var track = Assert.Single(result.Tracks);
            var trackFormKey = FormKey.Factory(track.FormKey);
            var mtdText = File.ReadAllText(result.MtdFilePath, Encoding.UTF8);
            Assert.Contains(
                $"MUSCellFixture! = 0x{trackFormKey.ID:X6}~GF Music Product.esp",
                mtdText);
            Assert.Contains(
                result.Diagnostic.Checks,
                check => check.Contains(
                    "Cell用SkyPatcher設定: 1件の割り当てが一致",
                    StringComparison.Ordinal));

            var manifest = File.ReadAllText(result.ManifestPath, Encoding.UTF8);
            Assert.Contains("\"Cells\": [", manifest);
            Assert.Contains("GF Music Product.esp.ini", manifest);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_WorldSpaceModeWritesMusicTypeAndWorldSpaceOverride()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-worldspace-generation-");
        try
        {
            var pluginPath = Path.Combine(root.FullName, "Music Fixture.esp");
            var sourceFormKey = WriteWorldSpaceSourcePlugin(pluginPath);
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [9, 8, 7]);
            var asset = CreateLooseAsset(root.FullName, "Music Mod", assetPath, @"music\fixture.xwm");
            var setting = CreateWorldSpaceSetting(root.FullName, pluginPath, sourceFormKey);
            var officialPlugin = new PluginSource(
                "Skyrim.esm",
                Path.Combine(root.FullName, "Skyrim.esm"),
                "Game Data",
                root.FullName,
                true,
                true,
                0,
                0);
            var oldModPlugin = new PluginSource(
                "Old Music.esp",
                Path.Combine(root.FullName, "Old Music.esp"),
                "Old Music",
                root.FullName,
                true,
                true,
                2,
                2);
            var officialTrack = CreateTrackSource(
                officialPlugin,
                "000001:Skyrim.esm",
                "MUSOfficialWorldSpace");
            var oldModTrack = CreateTrackSource(
                oldModPlugin,
                "000001:Old Music.esp",
                "MUSOldModWorldSpace");
            setting = setting with { Tracks = new[] { officialTrack, oldModTrack } };
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = true
            };
            plan.GetOrCreate(asset, new[] { setting });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory,
                    WorldSpaceIndividualAssignment = true,
                    SelectedWorldSpaceFormKeys = new HashSet<string>(
                        new[] { sourceFormKey },
                        StringComparer.OrdinalIgnoreCase)
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var worldSpaceOutput = Assert.Single(result.WorldSpaces);
            Assert.Single(result.Plugins);
            Assert.Equal(1, result.Plugins[0].NewMusicTypeRecordCount);
            Assert.True(File.Exists(Path.Combine(outputDirectory, "GF Music Product.esp")));

            using var generatedPlugin = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(
                    ModKey.FromNameAndExtension("GF Music Product.esp"),
                    Path.Combine(outputDirectory, "GF Music Product.esp")),
                SkyrimRelease.SkyrimSE);
            var generatedType = Assert.Single(generatedPlugin.MusicTypes.Records);
            var generatedWorldSpace = Assert.Single(generatedPlugin.Worldspaces.Records);
            Assert.Equal(worldSpaceOutput.MusicTypeFormKey, generatedType.FormKey.ToString());
            Assert.Equal(sourceFormKey, generatedWorldSpace.FormKey.ToString());
            Assert.Equal(
                generatedType.FormKey,
                generatedWorldSpace.Music?.FormKeyNullable);
            Assert.Equal(2, generatedType.Tracks!.Count);
            Assert.Contains(
                generatedType.Tracks,
                track => track.FormKey == FormKey.Factory(officialTrack.FormKey));
            Assert.DoesNotContain(
                generatedType.Tracks,
                track => track.FormKey == FormKey.Factory(oldModTrack.FormKey));
            Assert.Equal(2, worldSpaceOutput.TrackFormKeys.Count);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("WorldSpace用の音楽設定", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_DfgWorldSpaceKeepsBridgeTypeInEspAndPatchesItWithDfgTracks()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-worldspace-dfg-");
        try
        {
            var pluginPath = Path.Combine(root.FullName, "Music Fixture.esp");
            var sourceFormKey = WriteWorldSpaceSourcePlugin(pluginPath);
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [9, 8, 7]);
            var asset = CreateLooseAsset(root.FullName, "Music Mod", assetPath, @"music\fixture.xwm");
            var setting = CreateWorldSpaceSetting(root.FullName, pluginPath, sourceFormKey);
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = false
            };
            plan.GetOrCreate(asset, new[] { setting });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory,
                    OutputMode = MusicGenerationOutputMode.Dfg,
                    DfgPackageName = "GF Music Manager DFG",
                    WorldSpaceIndividualAssignment = true,
                    SelectedWorldSpaceFormKeys = new HashSet<string>(
                        new[] { sourceFormKey },
                        StringComparer.OrdinalIgnoreCase)
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var plugin = Assert.Single(result.Plugins);
            Assert.Equal(0, plugin.NewMusicTrackRecordCount);
            Assert.Equal(1, plugin.NewMusicTypeRecordCount);
            Assert.Empty(result.Tracks);

            var worldSpace = Assert.Single(result.WorldSpaces);
            Assert.Empty(worldSpace.TrackFormKeys);
            using var generatedPlugin = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(
                    ModKey.FromNameAndExtension("GF Music Product.esp"),
                    Path.Combine(outputDirectory, "GF Music Product.esp")),
                SkyrimRelease.SkyrimSE);
            Assert.Empty(generatedPlugin.MusicTracks.Records);
            var generatedType = Assert.Single(generatedPlugin.MusicTypes.Records);
            Assert.Equal(worldSpace.MusicTypeFormKey, generatedType.FormKey.ToString());
            Assert.Equal(sourceFormKey, Assert.Single(generatedPlugin.Worldspaces.Records).FormKey.ToString());

            Assert.NotNull(result.DfgOutput);
            Assert.Equal(1, result.DfgOutput!.MusicTrackCount);
            Assert.Equal(2, result.DfgOutput.MusicTypeCount);
            Assert.Equal(2, result.DfgOutput.ExternalMusicTypePatchCount);

            var importedTrackIds = result.DfgOutput.ImportPaths
                .Select(path => JsonDocument.Parse(File.ReadAllText(path)))
                .Select(document =>
                {
                    using (document)
                    {
                        return document.RootElement.GetProperty("editorId").GetString()!;
                    }
                })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var worldSpaceFormKey = FormKey.Factory(worldSpace.MusicTypeFormKey);
            var bridgePatch = ReadDfgPatches(result.DfgOutput.PackageDatabasePath)
                .Single(patch =>
                    patch.SourcePlugin.Equals(
                        "GF Music Product.esp",
                        StringComparison.OrdinalIgnoreCase) &&
                    patch.LocalFormId == worldSpaceFormKey.ID);
            using var changes = JsonDocument.Parse(bridgePatch.ChangesJson);
            var references = changes.RootElement
                .GetProperty("fields")
                .GetProperty("musicTypeTracks")
                .GetProperty("value")
                .EnumerateArray()
                .ToArray();
            Assert.Equal("replace", changes.RootElement
                .GetProperty("fields")
                .GetProperty("musicTypeTracks")
                .GetProperty("operation")
                .GetString());
            var reference = Assert.Single(references);
            Assert.True(reference.TryGetProperty("editorID", out var editorId));
            Assert.False(reference.TryGetProperty("formID", out _));
            Assert.Contains(editorId.GetString()!, importedTrackIds);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_LeavesWorldSpaceAssignmentUntouchedWhenWorldSpaceOutputIsOff()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-worldspace-conflict-off-");
        try
        {
            var pluginPath = Path.Combine(root.FullName, "Music Fixture.esp");
            var worldSpaceFormKey = WriteWorldSpaceSourcePlugin(pluginPath);
            var firstSetting = CreateWorldSpaceSetting(
                root.FullName,
                pluginPath,
                worldSpaceFormKey);
            var secondMusicTypeRecord = new PluginRecordSource(
                "000102:Music Fixture.esp",
                "MusicType",
                "MUSExploreFixtureSecond",
                false,
                firstSetting.Record.Plugin,
                true);
            var secondSetting = firstSetting with
            {
                MusicTypeFormKey = secondMusicTypeRecord.FormKey,
                MusicTypeEditorId = secondMusicTypeRecord.EditorId,
                MusicTypeRecord = secondMusicTypeRecord
            };
            var asset = CreateScopeAsset(
                root.FullName,
                "Music Mod",
                "worldspace-conflict.xwm");
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(asset, new[] { firstSetting, secondSetting });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { firstSetting, secondSetting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            Assert.Empty(result.WorldSpaces);
            Assert.Empty(result.IntegratedMusicTypes);
            Assert.Single(result.Plugins);
            Assert.Equal(0, result.Plugins[0].NewMusicTypeRecordCount);
            Assert.DoesNotContain(
                result.Warnings,
                warning => warning.Contains("WorldSpace", StringComparison.Ordinal));

            var generatedTrack = FormKey.Factory(Assert.Single(result.Tracks).FormKey);
            var mtd = File.ReadAllText(result.MtdFilePath, Encoding.UTF8);
            Assert.Contains(
                $"{firstSetting.MusicTypeEditorId}! = 0x{generatedTrack.ID:X6}~GF Music Product.esp",
                mtd);
            Assert.Contains(
                $"{secondSetting.MusicTypeEditorId}! = 0x{generatedTrack.ID:X6}~GF Music Product.esp",
                mtd);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_WorldSpaceModeMergesOfficialTracksFromMultipleMusicTypes()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-worldspace-merge-");
        try
        {
            var pluginPath = Path.Combine(root.FullName, "Music Fixture.esp");
            var sourceFormKey = WriteWorldSpaceSourcePlugin(pluginPath);
            var assetPath = Path.Combine(root.FullName, "Music Mod", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [9, 8, 7]);
            var asset = CreateLooseAsset(root.FullName, "Music Mod", assetPath, @"music\fixture.xwm");
            var firstSetting = CreateWorldSpaceSetting(root.FullName, pluginPath, sourceFormKey);
            var firstOfficialTrack = CreateTrackSource(
                new PluginSource(
                    "Skyrim.esm",
                    Path.Combine(root.FullName, "Skyrim.esm"),
                    "Game Data",
                    root.FullName,
                    true,
                    true,
                    0,
                    0),
                "000001:Skyrim.esm",
                "MUSOfficialWorldSpaceFirst");
            firstSetting = firstSetting with { Tracks = new[] { firstOfficialTrack } };

            var secondMusicTypeRecord = new PluginRecordSource(
                "000102:Music Fixture.esp",
                "MusicType",
                "MUSExploreFixtureSecond",
                false,
                firstSetting.Record.Plugin,
                true);
            var secondOfficialTrack = CreateTrackSource(
                new PluginSource(
                    "Update.esm",
                    Path.Combine(root.FullName, "Update.esm"),
                    "Game Data",
                    root.FullName,
                    true,
                    true,
                    0,
                    0),
                "000002:Update.esm",
                "MUSOfficialWorldSpaceSecond");
            var secondSetting = firstSetting with
            {
                MusicTypeFormKey = secondMusicTypeRecord.FormKey,
                MusicTypeEditorId = secondMusicTypeRecord.EditorId,
                MusicTypeRecord = secondMusicTypeRecord,
                Tracks = new[] { secondOfficialTrack }
            };
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = true
            };
            plan.GetOrCreate(asset, new[] { firstSetting, secondSetting });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { firstSetting, secondSetting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory,
                    WorldSpaceIndividualAssignment = true,
                    SelectedWorldSpaceFormKeys = new HashSet<string>(
                        new[] { sourceFormKey },
                        StringComparer.OrdinalIgnoreCase)
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var worldSpaceOutput = Assert.Single(result.WorldSpaces);
            using var generatedPlugin = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(
                    ModKey.FromNameAndExtension("GF Music Product.esp"),
                    Path.Combine(outputDirectory, "GF Music Product.esp")),
                SkyrimRelease.SkyrimSE);
            var generatedType = Assert.Single(generatedPlugin.MusicTypes.Records);

            Assert.Equal(
                "GFITG_W_Worldspace_Fixture",
                generatedType.EditorID);
            Assert.Equal(3, generatedType.Tracks!.Count);
            Assert.Contains(
                generatedType.Tracks,
                track => track.FormKey == FormKey.Factory(firstOfficialTrack.FormKey));
            Assert.Contains(
                generatedType.Tracks,
                track => track.FormKey == FormKey.Factory(secondOfficialTrack.FormKey));
            Assert.Equal(3, worldSpaceOutput.TrackFormKeys.Count);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("統合", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_RejectsTwoAdoptedAssetsWithTheSameVirtualPath()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-duplicate-");
        try
        {
            var firstPath = Path.Combine(root.FullName, "First", "music", "same.xwm");
            var secondPath = Path.Combine(root.FullName, "Second", "music", "same.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
            File.WriteAllBytes(firstPath, [1]);
            File.WriteAllBytes(secondPath, [2]);
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = true
            };
            plan.GetOrCreate(
                CreateLooseAsset(root.FullName, "First", firstPath, @"music\same.xwm"),
                new[] { setting });
            plan.GetOrCreate(
                CreateLooseAsset(root.FullName, "Second", secondPath, @"music\same.xwm"),
                new[] { setting });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            Assert.Throws<MusicGenerationOutputException>(() =>
                new MusicGenerationOutputWriter().Generate(
                    plan,
                    new[] { setting },
                    new MusicGenerationOutputOptions
                    {
                        OutputModDirectory = outputDirectory
                    }));
            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_AllowsOneAdoptedAssetWhenAnotherAssetHasTheSameVirtualPath()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-generation-duplicate-excluded-");
        try
        {
            var firstPath = Path.Combine(root.FullName, "First", "music", "same.xwm");
            var secondPath = Path.Combine(root.FullName, "Second", "music", "same.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
            File.WriteAllBytes(firstPath, [1]);
            File.WriteAllBytes(secondPath, [2]);
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var plan = new MusicGenerationPlan
            {
                KeepVanillaMusic = true
            };
            plan.GetOrCreate(
                CreateLooseAsset(root.FullName, "First", firstPath, @"music\same.xwm"),
                new[] { setting });
            var excludedEntry = plan.GetOrCreate(
                CreateLooseAsset(root.FullName, "Second", secondPath, @"music\same.xwm"),
                new[] { setting });
            excludedEntry.IsAdopted = false;

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var assetOutput = Assert.Single(result.Assets);
            Assert.Equal(firstPath, assetOutput.SourcePath);
            Assert.True(assetOutput.IsCopied);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    private static AssetSource CreateLooseAsset(
        string root,
        string modName,
        string sourcePath,
        string virtualPath) =>
        new(
            virtualPath,
            AssetSourceKind.Loose,
            modName,
            Path.Combine(root, modName),
            true,
            sourcePath,
            null,
             new FileInfo(sourcePath).Length);

    private static AssetSource CreateScopeAsset(
        string root,
        string modName,
        string fileName)
    {
        var path = Path.Combine(root, modName, "music", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return CreateLooseAsset(root, modName, path, $@"music\{fileName}");
    }

    private static MusicSettingSource CreateScopedSetting(
        string root,
        MusicSettingScope scope,
        string scopeEditorId,
        string musicTypeEditorId,
        string pluginName,
        string musicTypeFormKey,
        string scopeFormKey)
    {
        var musicTypeSetting = CreateMusicTypeSetting(
            root,
            musicTypeEditorId,
            pluginName,
            1,
            true,
            musicTypeFormKey);
        var scopeRecord = musicTypeSetting.Record with
        {
            FormKey = scopeFormKey,
            RecordType = scope.ToString(),
            EditorId = scopeEditorId
        };
        return musicTypeSetting with
        {
            Scope = scope,
            ScopeFormKey = scopeFormKey,
            ScopeEditorId = scopeEditorId,
            Record = scopeRecord
        };
    }

    private static MusicSettingSource CreateMusicTypeSetting(
        string root,
        string editorId,
        string pluginName = "Fixture.esp",
        int loadOrderIndex = 1,
        bool isWinner = true,
        string? musicTypeFormKey = null)
    {
        var plugin = new PluginSource(
            pluginName,
            Path.Combine(root, pluginName),
            Path.GetFileNameWithoutExtension(pluginName),
            root,
            true,
            true,
            loadOrderIndex,
            1);
        var record = new PluginRecordSource(
            musicTypeFormKey ?? "000100:Fixture.esp",
            "MusicType",
            editorId,
            false,
            plugin,
            isWinner);
        return new MusicSettingSource(
            MusicSettingScope.MusicType,
            record.FormKey,
            editorId,
            record.FormKey,
            editorId,
            record,
            record,
            Array.Empty<MusicTrackSource>());
    }

    private static IReadOnlyList<MusicSettingSource> CreateOfficialAndOldModSettings(
        string root)
    {
        var officialPlugin = new PluginSource(
            "Skyrim.esm",
            Path.Combine(root, "Skyrim.esm"),
            "Game Data",
            root,
            true,
            true,
            0,
            0);
        var oldModPlugin = new PluginSource(
            "Fantasy Music.esp",
            Path.Combine(root, "Fantasy Music.esp"),
            "Fantasy Music",
            root,
            true,
            true,
            10,
            10);
        var officialTrackRecord = new PluginRecordSource(
            "000001:Skyrim.esm",
            "MusicTrack",
            "MUSCombatOfficial",
            false,
            officialPlugin,
            false);
        var oldTrackRecord = new PluginRecordSource(
            "000001:Fantasy Music.esp",
            "MusicTrack",
            "MUSCombatOldMod",
            false,
            oldModPlugin,
            true);
        var officialTypeRecord = new PluginRecordSource(
            "000100:Skyrim.esm",
            "MusicType",
            "MUSCombat",
            false,
            officialPlugin,
            false);
        var oldTypeRecord = officialTypeRecord with
        {
            Plugin = oldModPlugin,
            IsWinner = true
        };
        var officialTrack = new MusicTrackSource(
            officialTrackRecord.FormKey,
            officialTrackRecord.EditorId,
            Array.Empty<string>(),
            officialTrackRecord);
        var oldTrack = new MusicTrackSource(
            oldTrackRecord.FormKey,
            oldTrackRecord.EditorId,
            Array.Empty<string>(),
            oldTrackRecord);
        var officialSetting = new MusicSettingSource(
            MusicSettingScope.MusicType,
            officialTypeRecord.FormKey,
            officialTypeRecord.EditorId,
            officialTypeRecord.FormKey,
            officialTypeRecord.EditorId,
            officialTypeRecord,
            officialTypeRecord,
            new[] { officialTrack });
        var oldSetting = officialSetting with
        {
            Record = oldTypeRecord,
            MusicTypeRecord = oldTypeRecord,
            Tracks = new[] { oldTrack }
        };
        return new[] { officialSetting, oldSetting };
    }

    private static MusicSettingSource CreateWorldSpaceSetting(
        string root,
        string pluginPath,
        string worldSpaceFormKey)
    {
        var plugin = new PluginSource(
            "Music Fixture.esp",
            pluginPath,
            "Music Fixture",
            root,
            true,
            true,
            1,
            1);
        var worldSpaceRecord = new PluginRecordSource(
            worldSpaceFormKey,
            "Worldspace",
            "Worldspace_Fixture",
            false,
            plugin,
            true);
        var musicTypeRecord = new PluginRecordSource(
            "000101:Music Fixture.esp",
            "MusicType",
            "MUSExploreFixture",
            false,
            plugin,
            true);
        return new MusicSettingSource(
            MusicSettingScope.WorldSpace,
            worldSpaceFormKey,
            worldSpaceRecord.EditorId,
            musicTypeRecord.FormKey,
            musicTypeRecord.EditorId,
            worldSpaceRecord,
            musicTypeRecord,
            Array.Empty<MusicTrackSource>());
    }

    private static MusicTrackSource CreateTrackSource(
        PluginSource plugin,
        string formKey,
        string editorId)
    {
        var record = new PluginRecordSource(
            formKey,
            "MusicTrack",
            editorId,
            false,
            plugin,
            true);
        return new MusicTrackSource(
            record.FormKey,
            record.EditorId,
            Array.Empty<string>(),
            record);
    }

    private static string WriteWorldSpaceSourcePlugin(string path)
    {
        var mod = new SkyrimMod(
            ModKey.FromNameAndExtension("Music Fixture.esp"),
            SkyrimRelease.SkyrimSE);
        var track = mod.MusicTracks.AddNew("SourceTrack");
        track.TrackFilename = new();
        track.TrackFilename.TrySetPath(@"music\source.xwm");
        var musicType = mod.MusicTypes.AddNew("MUSExploreFixture");
        musicType.Tracks = new();
        musicType.Tracks.Add(new FormLink<IMusicTrackGetter>(track.FormKey));
        var worldSpace = mod.Worldspaces.AddNew("Worldspace_Fixture");
        worldSpace.Music = new FormLinkNullable<IMusicTypeGetter>(musicType.FormKey);
        using var stream = File.Create(path);
        mod.WriteToBinary(stream, new BinaryWriteParameters());
        return worldSpace.FormKey.ToString();
    }

    private static IReadOnlyList<DfgPatchRow> ReadDfgPatches(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_plugin, local_form_id, form_kind, editor_id, changes_json
            FROM external_patches
            ORDER BY source_plugin, local_form_id, form_kind;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<DfgPatchRow>();
        while (reader.Read())
        {
            result.Add(new DfgPatchRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return result;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temporary test data can be collected by the OS later.
        }
    }

    private sealed record DfgPatchRow(
        string SourcePlugin,
        long LocalFormId,
        string FormKind,
        string EditorId,
        string ChangesJson);

    private sealed class BsaFixture
    {
        private static readonly byte[] Payload = Encoding.ASCII.GetBytes("test");

        public BsaFixture(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(0x00415342u);
            writer.Write(105u);
            writer.Write(36u);
            writer.Write(0u);
            writer.Write(1u);
            writer.Write(1u);
            writer.Write(6u);
            writer.Write(11u);
            writer.Write(0u);

            writer.Write(0UL);
            writer.Write(1u);
            writer.Write(0u);
            writer.Write(0UL);

            writer.Write((byte)6);
            writer.Write(Encoding.ASCII.GetBytes("music\0"));
            writer.Write(0UL);
            writer.Write((uint)Payload.Length);
            var offsetPosition = stream.Position;
            writer.Write(0u);
            writer.Write(Encoding.ASCII.GetBytes("archive_track.xwm\0"));
            var payloadOffset = checked((uint)stream.Position);
            var endOfNames = stream.Position;
            stream.Position = offsetPosition;
            writer.Write(payloadOffset);
            stream.Position = endOfNames;
            writer.Write(Payload);
        }
    }
}
