using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Planning;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Plugins;
using SkyrimScan.Core.Scanning;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicGenerationScenarioMatrixTests
{
    public static TheoryData<string> Scenarios => new()
    {
        "normal-remove-vanilla",
        "normal-keep-vanilla",
        "cell-conflict-integration",
        "worldspace-off",
        "worldspace-on",
        "loose-and-bsa",
        "capacity-split",
        "overwrite-and-failure-protection",
        "individual-asset-exclusion",
        "music-type-addition",
        "track-condition-edit",
        "disabled-mod-off-on-generation"
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void GenerateAndValidateScenario(string scenario)
    {
        using var context = new ScenarioContext(scenario);

        switch (scenario)
        {
            case "normal-remove-vanilla":
                RunNormalScenario(context, keepVanilla: false);
                break;
            case "normal-keep-vanilla":
                RunNormalScenario(context, keepVanilla: true);
                break;
            case "cell-conflict-integration":
                RunCellConflictScenario(context);
                break;
            case "worldspace-off":
                RunWorldSpaceScenario(context, enabled: false);
                break;
            case "worldspace-on":
                RunWorldSpaceScenario(context, enabled: true);
                break;
            case "loose-and-bsa":
                RunLooseAndBsaScenario(context);
                break;
            case "capacity-split":
                RunCapacitySplitScenario(context);
                break;
            case "overwrite-and-failure-protection":
                RunOverwriteProtectionScenario(context);
                break;
            case "individual-asset-exclusion":
                RunIndividualAssetExclusionScenario(context);
                break;
            case "music-type-addition":
                RunMusicTypeAdditionScenario(context);
                break;
            case "track-condition-edit":
                RunTrackConditionEditScenario(context);
                break;
            case "disabled-mod-off-on-generation":
                RunDisabledModOffOnGenerationScenario(context);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void RunNormalScenario(ScenarioContext context, bool keepVanilla)
    {
        var asset = context.CreateLooseAsset("Music Mod", "normal.xwm", [1, 2, 3]);
        var setting = CreateMusicTypeSetting(context.Root, "MUSExploreScenario");
        if (keepVanilla)
        {
            var officialPlugin = CreatePlugin(context.Root, "Skyrim.esm", "Game Data", 0);
            setting = setting with
            {
                Tracks = new[]
                {
                    CreateTrackSource(officialPlugin, "000123:Skyrim.esm", "MUSOfficialScenario")
                }
            };
        }

        var plan = new MusicGenerationPlan { KeepVanillaMusic = keepVanilla };
        plan.GetOrCreate(asset, new[] { setting });

        var result = Generate(context, plan, new[] { setting });

        AssertCommonOutput(result, expectedAssets: 1, expectedTracks: 1);
        var generatedTrack = Assert.Single(result.Tracks);
        var generatedFormKey = FormKey.Factory(generatedTrack.FormKey);
        var mtd = File.ReadAllText(result.MtdFilePath, Encoding.UTF8);
        Assert.Contains(
            $"0x{generatedFormKey.ID:X6}~{generatedTrack.PluginFileName}",
            mtd,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(keepVanilla ? 2 : 1, CountMtdTrackReferences(mtd, "MUSExploreScenario"));
    }

    private static void RunCellConflictScenario(ScenarioContext context)
    {
        var firstType = CreateMusicTypeSetting(context.Root, "MUSCellFirst");
        var firstCell = CreateScopedSetting(firstType, MusicSettingScope.Cell, "000200:Fixture.esp", "CellScenario");
        var secondType = CreateMusicTypeSetting(
            context.Root,
            "MUSCellSecond",
            "Second.esp",
            2,
            "000101:Second.esp");
        var secondCell = CreateScopedSetting(secondType, MusicSettingScope.Cell, "000200:Fixture.esp", "CellScenario");
        var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
        plan.GetOrCreate(context.CreateLooseAsset("First Music", "first.xwm", [1]), new[] { firstCell });
        plan.GetOrCreate(context.CreateLooseAsset("Second Music", "second.xwm", [2]), new[] { secondCell });

        var result = Generate(context, plan, new[] { firstCell, secondCell });

        AssertCommonOutput(result, expectedAssets: 2, expectedTracks: 2);
        var integratedType = Assert.Single(result.IntegratedMusicTypes);
        var cell = Assert.Single(result.Cells);
        Assert.Equal(MusicSettingScope.Cell, integratedType.Scope);
        Assert.Equal(integratedType.MusicTypeFormKey, cell.MusicTypeFormKey);
        Assert.NotNull(result.CellSkyPatcherFilePath);
        var skyPatcher = File.ReadAllText(result.CellSkyPatcherFilePath!, Encoding.UTF8);
        Assert.Contains("filterByCells=Fixture.esp|000200:musicType=GF Music Product.esp|", skyPatcher);
    }

    private static void RunWorldSpaceScenario(ScenarioContext context, bool enabled)
    {
        var sourcePluginPath = Path.Combine(context.Root, "Worldspace Fixture.esp");
        var worldSpaceFormKey = WriteWorldSpaceSourcePlugin(sourcePluginPath);
        var setting = CreateWorldSpaceSetting(context.Root, sourcePluginPath, worldSpaceFormKey);
        var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
        plan.GetOrCreate(
            context.CreateLooseAsset("World Music", "world.xwm", [3, 4, 5]),
            new[] { setting });

        var result = Generate(
            context,
            plan,
            new[] { setting },
            new MusicGenerationOutputOptions
            {
                OutputModDirectory = context.OutputDirectory,
                WorldSpaceIndividualAssignment = enabled,
                SelectedWorldSpaceFormKeys = new HashSet<string>(
                    new[] { worldSpaceFormKey },
                    StringComparer.OrdinalIgnoreCase)
            });

        AssertCommonOutput(result, expectedAssets: 1, expectedTracks: 1);
        if (!enabled)
        {
            Assert.Empty(result.WorldSpaces);
            using var plugin = OpenGeneratedPlugin(result.Plugins[0], context.OutputDirectory);
            Assert.Empty(plugin.Worldspaces.Records);
            return;
        }

        var worldSpace = Assert.Single(result.WorldSpaces);
        Assert.Equal(worldSpaceFormKey, worldSpace.WorldSpaceFormKey);
        using var generatedPlugin = OpenGeneratedPlugin(result.Plugins[0], context.OutputDirectory);
        var generatedRecord = Assert.Single(generatedPlugin.Worldspaces.Records);
        var generatedType = Assert.Single(generatedPlugin.MusicTypes.Records);
        Assert.Equal(worldSpaceFormKey, generatedRecord.FormKey.ToString());
        Assert.Equal(generatedType.FormKey, generatedRecord.Music?.FormKeyNullable);
    }

    private static void RunLooseAndBsaScenario(ScenarioContext context)
    {
        var setting = CreateMusicTypeSetting(context.Root, "MUSMixedSources");
        var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
        var loose = context.CreateLooseAsset("Loose Music", "loose.xwm", [1, 2, 3, 4]);
        var archiveDirectory = Path.Combine(context.Root, "Archive Music");
        var archivePath = Path.Combine(archiveDirectory, "Archive Music.bsa");
        new BsaFixture(archivePath);
        var archived = new AssetSource(
            @"music\archive_track.xwm",
            AssetSourceKind.Bsa,
            "Archive Music",
            archiveDirectory,
            true,
            archivePath,
            @"music\archive_track.xwm",
            4)
        {
            IsVfsWinner = true
        };
        plan.GetOrCreate(loose, new[] { setting });
        plan.GetOrCreate(archived, new[] { setting });

        var result = Generate(context, plan, new[] { setting });

        AssertCommonOutput(result, expectedAssets: 2, expectedTracks: 2);
        Assert.True(File.Exists(Path.Combine(context.OutputDirectory, @"music\loose.xwm")));
        Assert.False(File.Exists(Path.Combine(context.OutputDirectory, @"music\archive_track.xwm")));
        Assert.Equal(1, result.Assets.Count(asset => asset.IsCopied));
        Assert.Equal(1, result.Assets.Count(asset => !asset.IsCopied));
    }

    private static void RunCapacitySplitScenario(ScenarioContext context)
    {
        var setting = CreateMusicTypeSetting(context.Root, "MUSCapacityScenario");
        var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
        for (var index = 1; index <= 5; index++)
        {
            plan.GetOrCreate(
                context.CreateLooseAsset("Capacity Music", $"track-{index}.xwm", [(byte)index]),
                new[] { setting });
        }

        var result = Generate(
            context,
            plan,
            new[] { setting },
            new MusicGenerationOutputOptions
            {
                OutputModDirectory = context.OutputDirectory,
                CapacityPolicy = new MusicGenerationCapacityPolicy(2)
            });

        AssertCommonOutput(result, expectedAssets: 5, expectedTracks: 5);
        Assert.Equal(3, result.Plugins.Count);
        Assert.Equal(
            new[] { "GF Music Product.esp", "GF Music Product - 02.esp", "GF Music Product - 03.esp" },
            result.Plugins.Select(plugin => plugin.PluginFileName));
        Assert.All(
            result.Plugins,
            plugin => Assert.InRange(
                plugin.NewMusicTrackRecordCount +
                plugin.NewMusicTypeRecordCount +
                plugin.WorldSpaceOverrideRecordCount,
                1,
                2));
    }

    private static void RunOverwriteProtectionScenario(ScenarioContext context)
    {
        var setting = CreateMusicTypeSetting(context.Root, "MUSOverwriteScenario");
        var initialPlan = new MusicGenerationPlan { KeepVanillaMusic = false };
        initialPlan.GetOrCreate(
            context.CreateLooseAsset("Initial Music", "initial.xwm", [1, 2, 3]),
            new[] { setting });
        var initial = Generate(context, initialPlan, new[] { setting });
        AssertCommonOutput(initial, expectedAssets: 1, expectedTracks: 1);

        var replacementPlan = new MusicGenerationPlan { KeepVanillaMusic = false };
        replacementPlan.GetOrCreate(
            context.CreateLooseAsset("Replacement Music", "replacement.xwm", [9, 8, 7]),
            new[] { setting });
        var replacement = Generate(
            context,
            replacementPlan,
            new[] { setting },
            new MusicGenerationOutputOptions
            {
                OutputModDirectory = context.OutputDirectory,
                OverwriteExisting = true
            });
        AssertCommonOutput(replacement, expectedAssets: 1, expectedTracks: 1);
        Assert.False(File.Exists(Path.Combine(context.OutputDirectory, @"music\initial.xwm")));
        Assert.True(File.Exists(Path.Combine(context.OutputDirectory, @"music\replacement.xwm")));

        var beforeFailure = HashOutputDirectory(context.OutputDirectory);
        var invalidPlan = new MusicGenerationPlan { KeepVanillaMusic = false };
        invalidPlan.GetOrCreate(
            context.CreateLooseAsset(
                "Conflict A",
                "same-a.xwm",
                [4],
                @"music\same.xwm"),
            new[] { setting });
        invalidPlan.GetOrCreate(
            context.CreateLooseAsset(
                "Conflict B",
                "same-b.xwm",
                [5],
                @"music\same.xwm"),
            new[] { setting });

        Assert.Throws<MusicGenerationOutputException>(() => Generate(
            context,
            invalidPlan,
            new[] { setting },
            new MusicGenerationOutputOptions
            {
                OutputModDirectory = context.OutputDirectory,
                OverwriteExisting = true
            }));
        Assert.Equal(beforeFailure, HashOutputDirectory(context.OutputDirectory));
    }

    private static void RunIndividualAssetExclusionScenario(ScenarioContext context)
    {
        var setting = CreateMusicTypeSetting(context.Root, "MUSExclusionScenario");
        var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
        var adopted = plan.GetOrCreate(
            context.CreateLooseAsset("Adopted Music", "adopted.xwm", [1, 2]),
            new[] { setting });
        var excluded = plan.GetOrCreate(
            context.CreateLooseAsset("Excluded Music", "excluded.xwm", [3, 4]),
            new[] { setting });
        adopted.IsAdopted = true;
        excluded.IsAdopted = false;

        var result = Generate(context, plan, new[] { setting });

        AssertCommonOutput(result, expectedAssets: 1, expectedTracks: 1);
        Assert.Equal(@"music\adopted.xwm", Assert.Single(result.Assets).VirtualPath);
        Assert.False(File.Exists(Path.Combine(context.OutputDirectory, @"music\excluded.xwm")));
        var manifest = ReadManifest(result.ManifestPath);
        Assert.Equal(2, manifest.PlanEntries.Count);
        Assert.False(manifest.PlanEntries.Single(entry =>
            entry.AssetKey.Equals(excluded.AssetKey, StringComparison.OrdinalIgnoreCase)).IsAdopted);
    }

    private static void RunMusicTypeAdditionScenario(ScenarioContext context)
    {
        var first = CreateMusicTypeSetting(context.Root, "MUSAddedFirst");
        var second = CreateMusicTypeSetting(
            context.Root,
            "MUSAddedSecond",
            "Second.esp",
            2,
            "000101:Second.esp");
        var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
        var entry = plan.GetOrCreate(
            context.CreateLooseAsset("Editable Music", "added-type.xwm", [5, 6]),
            new[] { first });
        entry.ReplaceDestinations(new[] { first, second });

        var result = Generate(context, plan, new[] { first, second });

        AssertCommonOutput(result, expectedAssets: 1, expectedTracks: 1);
        var mtd = File.ReadAllText(result.MtdFilePath, Encoding.UTF8);
        Assert.Contains("MUSAddedFirst!", mtd);
        Assert.Contains("MUSAddedSecond!", mtd);
        var manifestEntry = Assert.Single(ReadManifest(result.ManifestPath).PlanEntries);
        Assert.Equal(2, manifestEntry.DestinationKeys.Count);
    }

    private static void RunTrackConditionEditScenario(ScenarioContext context)
    {
        var setting = CreateMusicTypeSetting(context.Root, "MUSConditionEditScenario");
        var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
        var entry = plan.GetOrCreate(
            context.CreateLooseAsset("Condition Music", "condition.xwm", [7, 8]),
            new[] { setting },
            new[] { MusicConditionSource.CreateCurrentTime(6f, "GreaterThanOrEqualTo") });
        var track = Assert.Single(entry.Tracks);
        var editedConditions = new[]
        {
            MusicConditionSource.CreateCurrentTime(18f, "GreaterThanOrEqualTo"),
            MusicConditionSource.CreateCurrentTime(23f, "LessThanOrEqualTo")
        };
        entry.ApplyTrackConditions(new[]
        {
            new MusicGenerationTrackPlan(track.TrackKey, editedConditions)
        });

        var result = Generate(context, plan, new[] { setting });

        AssertCommonOutput(result, expectedAssets: 1, expectedTracks: 1);
        Assert.Equal(
            editedConditions.Select(MusicConditionFormatter.CreateRecordKey),
            Assert.Single(result.Tracks).Conditions.Select(MusicConditionFormatter.CreateRecordKey));
        var generatedPluginPath = Path.Combine(
            context.OutputDirectory,
            result.Plugins[0].PluginFileName);
        var generatedRecords = new PluginRecordScanner().Read(new PluginSource(
            result.Plugins[0].PluginFileName,
            generatedPluginPath,
            "GF Music Product",
            context.OutputDirectory,
            true,
            true,
            0,
            0));
        var generatedConditions = Assert.Single(generatedRecords.Where(record =>
                record.RecordType == "MusicTrack"))
            .Conditions
            .Select(condition => MusicConditionSource.From(condition))
            .ToArray();
        Assert.Equal(
            editedConditions.Select(MusicConditionFormatter.CreateRecordKey),
            generatedConditions.Select(MusicConditionFormatter.CreateRecordKey));
        var manifestTrack = Assert.Single(Assert.Single(ReadManifest(result.ManifestPath).PlanEntries).Tracks);
        Assert.Equal(2, manifestTrack.Conditions.Count);
    }

    private static void RunDisabledModOffOnGenerationScenario(ScenarioContext context)
    {
        var modsDirectory = Path.Combine(context.Root, "mods");
        var profileDirectory = Path.Combine(context.Root, "profiles", "Main");
        var enabledModDirectory = Path.Combine(modsDirectory, "Enabled Control");
        var disabledModDirectory = Path.Combine(modsDirectory, "Disabled Music");
        Directory.CreateDirectory(Path.Combine(enabledModDirectory, "music"));
        Directory.CreateDirectory(Path.Combine(disabledModDirectory, "music"));
        Directory.CreateDirectory(profileDirectory);

        File.WriteAllBytes(
            Path.Combine(enabledModDirectory, "music", "enabled_control.xwm"),
            [1, 2, 3]);
        File.WriteAllBytes(
            Path.Combine(disabledModDirectory, "music", "disabled_loose.xwm"),
            [4, 5, 6]);
        new BsaFixture(Path.Combine(disabledModDirectory, "Disabled Music.bsa"));
        WriteDisabledMusicPlugin(Path.Combine(disabledModDirectory, "DisabledMusic.esp"));

        var modListPath = Path.Combine(profileDirectory, "modlist.txt");
        File.WriteAllText(
            modListPath,
            "+Enabled Control\n-Disabled Music\n",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(profileDirectory, "loadorder.txt"),
            "DisabledMusic.esp\n",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(profileDirectory, "plugins.txt"),
            "*DisabledMusic.esp\n",
            Encoding.UTF8);
        var originalModList = File.ReadAllBytes(modListPath);

        var scanner = new Mo2Scanner();
        var disabledOff = scanner.Scan(new ScanOptions
        {
            Mo2Root = context.Root,
            ProfileName = "Main",
            IncludeDisabledMods = false,
            ReadPluginRecords = true
        });
        Assert.DoesNotContain(disabledOff.Mods, mod => mod.Name == "Disabled Music");
        Assert.DoesNotContain(disabledOff.Assets, asset => asset.ModName == "Disabled Music");
        Assert.DoesNotContain(disabledOff.Plugins, plugin => plugin.ModName == "Disabled Music");
        Assert.DoesNotContain(disabledOff.Records, record => record.Plugin.ModName == "Disabled Music");

        var disabledOn = scanner.Scan(new ScanOptions
        {
            Mo2Root = context.Root,
            ProfileName = "Main",
            IncludeDisabledMods = true,
            ReadPluginRecords = true
        });
        var disabledMod = Assert.Single(disabledOn.Mods.Where(mod => mod.Name == "Disabled Music"));
        Assert.False(disabledMod.Enabled);
        var disabledPlugin = Assert.Single(disabledOn.Plugins.Where(plugin => plugin.ModName == "Disabled Music"));
        Assert.False(disabledPlugin.ModEnabled);
        Assert.False(disabledPlugin.Enabled);
        Assert.Equal(3, disabledOn.Records.Count(record => record.Plugin.ModName == "Disabled Music"));

        var disabledAssets = disabledOn.Assets
            .Where(asset => asset.ModName == "Disabled Music")
            .OrderBy(asset => asset.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(2, disabledAssets.Length);
        Assert.Contains(disabledAssets, asset => asset.SourceKind == AssetSourceKind.Loose);
        Assert.Contains(disabledAssets, asset => asset.SourceKind == AssetSourceKind.Bsa);
        Assert.All(disabledAssets, asset =>
        {
            Assert.False(asset.ModEnabled);
            Assert.False(asset.IsVfsWinner);
        });

        var analysis = new MusicSettingsAnalyzer().Analyze(disabledOn);
        var setting = Assert.Single(analysis.Settings.Where(candidate =>
            candidate.MusicTypeEditorId == "MUSDisabledScenario"));
        var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
        foreach (var asset in disabledAssets)
        {
            var destinations = analysis.GetSettingsForAsset(asset.VirtualPath);
            Assert.Contains(destinations, candidate =>
                candidate.MusicTypeEditorId == "MUSDisabledScenario");
            plan.GetOrCreate(asset, destinations);
        }

        var result = Generate(context, plan, new[] { setting });

        AssertCommonOutput(result, expectedAssets: 2, expectedTracks: 2);
        Assert.All(result.Assets, asset => Assert.True(asset.IsCopied));
        Assert.True(File.Exists(Path.Combine(context.OutputDirectory, @"music\disabled_loose.xwm")));
        Assert.True(File.Exists(Path.Combine(context.OutputDirectory, @"music\archive_track.xwm")));
        Assert.Equal(Encoding.ASCII.GetBytes("test"), File.ReadAllBytes(
            Path.Combine(context.OutputDirectory, @"music\archive_track.xwm")));
        var generatedPluginPath = Path.Combine(
            context.OutputDirectory,
            Assert.Single(result.Plugins).PluginFileName);
        var generatedRecords = new PluginRecordScanner().Read(new PluginSource(
            Path.GetFileName(generatedPluginPath),
            generatedPluginPath,
            "GF Music Product",
            context.OutputDirectory,
            true,
            true,
            0,
            0));
        var generatedTrackPaths = generatedRecords
            .Where(record => record.RecordType == "MusicTrack")
            .SelectMany(record => record.Assets)
            .Select(asset => asset.VirtualPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(
            new[] { @"music\archive_track.xwm", @"music\disabled_loose.xwm" },
            generatedTrackPaths);
        var mtd = File.ReadAllText(result.MtdFilePath, Encoding.UTF8);
        Assert.Equal(2, CountMtdTrackReferences(mtd, "MUSDisabledScenario"));
        var manifest = ReadManifest(result.ManifestPath);
        Assert.Equal(2, manifest.PlanEntries.Count);
        Assert.All(manifest.PlanEntries, entry =>
        {
            Assert.True(entry.IsAdopted);
            Assert.Single(entry.DestinationKeys);
        });
        Assert.Equal(originalModList, File.ReadAllBytes(modListPath));
    }

    private static MusicGenerationOutputResult Generate(
        ScenarioContext context,
        MusicGenerationPlan plan,
        IReadOnlyList<MusicSettingSource> settings,
        MusicGenerationOutputOptions? options = null) =>
        new MusicGenerationOutputWriter().Generate(
            plan,
            settings,
            options ?? new MusicGenerationOutputOptions
            {
                OutputModDirectory = context.OutputDirectory
            });

    private static void AssertCommonOutput(
        MusicGenerationOutputResult result,
        int expectedAssets,
        int expectedTracks)
    {
        Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
        Assert.Empty(result.Diagnostic.Errors);
        Assert.Equal(expectedAssets, result.Assets.Count);
        Assert.Equal(expectedTracks, result.Tracks.Count);
        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(File.Exists(result.MtdFilePath));
        Assert.All(
            result.Plugins,
            plugin => Assert.True(File.Exists(Path.Combine(
                result.OutputModDirectory,
                plugin.PluginFileName))));

        var manifest = ReadManifest(result.ManifestPath);
        Assert.Equal(expectedAssets, manifest.Assets.Count);
        Assert.Equal(expectedTracks, manifest.Tracks.Count);
        Assert.Equal(result.Plugins.Count, manifest.Plugins.Count);
    }

    private static MusicGenerationManifest ReadManifest(string path) =>
        JsonSerializer.Deserialize<MusicGenerationManifest>(
            File.ReadAllText(path, Encoding.UTF8)) ??
        throw new InvalidDataException($"生成manifestを読み込めませんでした: {path}");

    private static int CountMtdTrackReferences(string mtd, string editorId)
    {
        var line = mtd.Split('\n').Single(value =>
            value.TrimStart().StartsWith(editorId + "!", StringComparison.OrdinalIgnoreCase));
        return line.Split('=', 2)[1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }

    private static ISkyrimModDisposableGetter OpenGeneratedPlugin(
        GeneratedPluginOutput plugin,
        string outputDirectory) =>
        SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                ModKey.FromNameAndExtension(plugin.PluginFileName),
                Path.Combine(outputDirectory, plugin.PluginFileName)),
            SkyrimRelease.SkyrimSE);

    private static MusicSettingSource CreateScopedSetting(
        MusicSettingSource musicType,
        MusicSettingScope scope,
        string formKey,
        string editorId)
    {
        var record = musicType.Record with
        {
            FormKey = formKey,
            RecordType = scope.ToString(),
            EditorId = editorId
        };
        return musicType with
        {
            Scope = scope,
            ScopeFormKey = formKey,
            ScopeEditorId = editorId,
            Record = record
        };
    }

    private static MusicSettingSource CreateMusicTypeSetting(
        string root,
        string editorId,
        string pluginName = "Fixture.esp",
        int loadOrderIndex = 1,
        string musicTypeFormKey = "000100:Fixture.esp")
    {
        var plugin = CreatePlugin(root, pluginName, Path.GetFileNameWithoutExtension(pluginName), loadOrderIndex);
        var record = new PluginRecordSource(
            musicTypeFormKey,
            "MusicType",
            editorId,
            false,
            plugin,
            true);
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

    private static MusicSettingSource CreateWorldSpaceSetting(
        string root,
        string pluginPath,
        string worldSpaceFormKey)
    {
        var plugin = new PluginSource(
            Path.GetFileName(pluginPath),
            pluginPath,
            "Worldspace Fixture",
            root,
            true,
            true,
            1,
            1);
        var worldSpaceRecord = new PluginRecordSource(
            worldSpaceFormKey,
            "Worldspace",
            "WorldspaceScenario",
            false,
            plugin,
            true);
        var musicTypeRecord = new PluginRecordSource(
            "000101:Worldspace Fixture.esp",
            "MusicType",
            "MUSWorldspaceScenario",
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

    private static PluginSource CreatePlugin(
        string root,
        string pluginName,
        string modName,
        int loadOrderIndex) =>
        new(
            pluginName,
            Path.Combine(root, pluginName),
            modName,
            root,
            true,
            true,
            loadOrderIndex,
            loadOrderIndex);

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
            formKey,
            editorId,
            Array.Empty<string>(),
            record);
    }

    private static string WriteWorldSpaceSourcePlugin(string path)
    {
        var modKey = ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
        var track = mod.MusicTracks.AddNew("SourceTrack");
        track.TrackFilename = new();
        track.TrackFilename.TrySetPath(@"music\source.xwm");
        var musicType = mod.MusicTypes.AddNew("MUSWorldspaceScenario");
        musicType.Tracks = new();
        musicType.Tracks.Add(new FormLink<IMusicTrackGetter>(track.FormKey));
        var worldSpace = mod.Worldspaces.AddNew("WorldspaceScenario");
        worldSpace.Music = new FormLinkNullable<IMusicTypeGetter>(musicType.FormKey);
        using var stream = File.Create(path);
        mod.WriteToBinary(stream, new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters());
        return worldSpace.FormKey.ToString();
    }

    private static void WriteDisabledMusicPlugin(string path)
    {
        var mod = new SkyrimMod(
            ModKey.FromNameAndExtension(Path.GetFileName(path)),
            SkyrimRelease.SkyrimSE);
        var looseTrack = mod.MusicTracks.AddNew("MUSTrackDisabledLoose");
        looseTrack.TrackFilename = new();
        looseTrack.TrackFilename.TrySetPath(@"music\disabled_loose.xwm");
        var archivedTrack = mod.MusicTracks.AddNew("MUSTrackDisabledArchive");
        archivedTrack.TrackFilename = new();
        archivedTrack.TrackFilename.TrySetPath(@"music\archive_track.xwm");
        var musicType = mod.MusicTypes.AddNew("MUSDisabledScenario");
        musicType.Tracks = new();
        musicType.Tracks.Add(new FormLink<IMusicTrackGetter>(looseTrack.FormKey));
        musicType.Tracks.Add(new FormLink<IMusicTrackGetter>(archivedTrack.FormKey));
        using var stream = File.Create(path);
        mod.WriteToBinary(
            stream,
            new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters());
    }

    private static string HashOutputDirectory(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(directory, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class ScenarioContext : IDisposable
    {
        private readonly DirectoryInfo _directory;

        public ScenarioContext(string scenario)
        {
            _directory = Directory.CreateTempSubdirectory($"gf-music-scenario-{scenario}-");
            Root = _directory.FullName;
            OutputDirectory = Path.Combine(Root, "GF Music Product");
        }

        public string Root { get; }

        public string OutputDirectory { get; }

        public AssetSource CreateLooseAsset(
            string modName,
            string fileName,
            byte[] bytes,
            string? virtualPath = null)
        {
            var modDirectory = Path.Combine(Root, modName);
            var sourcePath = Path.Combine(modDirectory, "music", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, bytes);
            return new AssetSource(
                virtualPath ?? $@"music\{fileName}",
                AssetSourceKind.Loose,
                modName,
                modDirectory,
                true,
                sourcePath,
                null,
                bytes.Length);
        }

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Temporary test data can be collected by the OS later.
            }
        }
    }

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
