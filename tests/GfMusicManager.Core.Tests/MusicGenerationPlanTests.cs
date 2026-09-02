using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicGenerationPlanTests
{
    [Fact]
    public void GetOrCreate_ReusesTheGenerationEntryForTheSameAsset()
    {
        var asset = new AssetSource(
            @"music\explore\fixture.xwm",
            AssetSourceKind.Loose,
            "Fixture Music",
            @"C:\Fixture",
            true,
            @"C:\Fixture\music\explore\fixture.xwm",
            null,
            12);
        var plan = new MusicGenerationPlan();

        var first = plan.GetOrCreate(asset, Array.Empty<GfMusicManager.Core.Analysis.MusicSettingSource>());
        first.IsAdopted = true;
        var second = plan.GetOrCreate(asset, Array.Empty<GfMusicManager.Core.Analysis.MusicSettingSource>());

        Assert.Same(first, second);
        Assert.True(second.IsAdopted);
        Assert.Single(plan.Entries);
    }

    [Fact]
    public void GetOrCreate_DefaultsNewScanEntriesToAdopted()
    {
        var plan = new MusicGenerationPlan();

        var entry = plan.GetOrCreate(
            Asset("Mod A", @"music\explore\forest.xwm"),
            Array.Empty<MusicSettingSource>());

        Assert.True(entry.IsAdopted);
    }

    [Fact]
    public void Conflicts_DetectsDuplicateVirtualPathsWithoutChoosingAnAsset()
    {
        var firstAsset = Asset("Mod A", @"music\shared.xwm");
        var secondAsset = Asset("Mod B", @"music\shared.xwm");
        var plan = new MusicGenerationPlan();

        plan.GetOrCreate(firstAsset, Array.Empty<MusicSettingSource>());
        plan.GetOrCreate(secondAsset, Array.Empty<MusicSettingSource>());

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(MusicGenerationPlanConflictKind.DuplicateVirtualPath, conflict.Kind);
        Assert.Equal(@"music\shared.xwm", conflict.Subject);
        Assert.Equal(2, conflict.Entries.Count);
        Assert.Null(conflict.TargetScope);
        Assert.Null(conflict.TargetFormKey);
    }

    [Fact]
    public void Conflicts_IgnoresDuplicateVirtualPathsWithinOneMod()
    {
        var firstAsset = Asset("Mod A", @"music\silence.xwm");
        var secondAsset = firstAsset with
        {
            SourcePath = @"C:\Fixture\Mod A\alternate\silence.xwm"
        };
        var plan = new MusicGenerationPlan();

        plan.GetOrCreate(firstAsset, Array.Empty<MusicSettingSource>());
        plan.GetOrCreate(secondAsset, Array.Empty<MusicSettingSource>());

        Assert.DoesNotContain(
            plan.Conflicts,
            conflict => conflict.Kind == MusicGenerationPlanConflictKind.DuplicateVirtualPath);
    }

    [Fact]
    public void Conflicts_ReportsTheDestinationWithoutChoosingALaterMusicType()
    {
        var plugin = new PluginSource(
            "Fixture.esp",
            @"C:\Fixture\Fixture.esp",
            "Fixture",
            @"C:\Fixture",
            true,
            true,
            1,
            1);
        var scope = new PluginRecordSource(
            "000010:Fixture.esp",
            "Location",
            "RiverwoodLocation",
            false,
            plugin,
            true);
        var musicType = new PluginRecordSource(
            "000011:Fixture.esp",
            "MusicType",
            "MUSExplore",
            false,
            plugin,
            true);
        var destination = new MusicSettingSource(
            MusicSettingScope.Location,
            scope.FormKey,
            scope.EditorId,
            musicType.FormKey,
            musicType.EditorId,
            scope,
            musicType,
            Array.Empty<MusicTrackSource>());
        var secondMusicType = musicType with { FormKey = "000012:Fixture.esp", EditorId = "MUSCombat" };
        var secondDestination = destination with
        {
            MusicTypeFormKey = secondMusicType.FormKey,
            MusicTypeEditorId = secondMusicType.EditorId,
            MusicTypeRecord = secondMusicType
        };
        var first = new AssetSource(
            @"music\first.xwm",
            AssetSourceKind.Loose,
            "Mod A",
            @"C:\Fixture\Mod A",
            true,
            @"C:\Fixture\Mod A\music\first.xwm",
            null,
            1);
        var second = new AssetSource(
            @"music\second.xwm",
            AssetSourceKind.Loose,
            "Mod B",
            @"C:\Fixture\Mod B",
            true,
            @"C:\Fixture\Mod B\music\second.xwm",
            null,
            1);
        var plan = new MusicGenerationPlan();
        var firstEntry = plan.GetOrCreate(first, new[] { destination });
        var secondEntry = plan.GetOrCreate(second, new[] { secondDestination });
        firstEntry.IsAdopted = true;
        secondEntry.IsAdopted = true;

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(
            MusicGenerationPlanConflictKind.MultipleGeneratedMusicTypesForRecord,
            conflict.Kind);
        Assert.Equal(2, conflict.Entries.Count);
        Assert.Equal(MusicSettingScope.Location, conflict.TargetScope);
        Assert.Equal(scope.FormKey, conflict.TargetFormKey);
        Assert.Contains("統合用Music Type", conflict.Message);
        Assert.DoesNotContain("後", conflict.Message);
    }

    [Fact]
    public void Conflicts_IgnoresMultipleAssetsUsingTheSameMusicTypeForTheSameDestination()
    {
        var plugin = new PluginSource(
            "Fixture.esp",
            @"C:\Fixture\Fixture.esp",
            "Fixture",
            @"C:\Fixture",
            true,
            true,
            1,
            1);
        var scope = new PluginRecordSource(
            "000010:Fixture.esp",
            "Cell",
            "TestCell",
            false,
            plugin,
            true);
        var musicType = new PluginRecordSource(
            "000011:Fixture.esp",
            "MusicType",
            "MUSExplore",
            false,
            plugin,
            true);
        var destination = new MusicSettingSource(
            MusicSettingScope.Cell,
            scope.FormKey,
            scope.EditorId,
            musicType.FormKey,
            musicType.EditorId,
            scope,
            musicType,
            Array.Empty<MusicTrackSource>());
        var plan = new MusicGenerationPlan();
        var first = plan.GetOrCreate(
            Asset("Mod A", @"music\first.xwm"),
            new[] { destination });
        var second = plan.GetOrCreate(
            Asset("Mod B", @"music\second.xwm"),
            new[] { destination });
        first.IsAdopted = true;
        second.IsAdopted = true;

        Assert.DoesNotContain(
            plan.Conflicts,
            conflict => conflict.Kind == MusicGenerationPlanConflictKind.MultipleGeneratedMusicTypesForRecord);
    }

    [Fact]
    public void GetOrCreate_PreservesConditionsForTheGeneratedTrack()
    {
        var asset = Asset("Mod A", @"music\town\day.xwm");
        var condition = new MusicConditionSource(
            "GetCurrentTime",
            "GreaterThanOrEqualTo",
            5f,
            string.Empty,
            "GetCurrentTimeConditionData",
            string.Empty);
        var plan = new MusicGenerationPlan();

        var entry = plan.GetOrCreate(
            asset,
            Array.Empty<MusicSettingSource>(),
            new[] { condition });

        Assert.Single(entry.Conditions);
        Assert.Equal(condition, entry.Conditions[0]);

        entry.ReplaceConditions(Array.Empty<MusicConditionSource>());

        Assert.Empty(entry.Conditions);
    }

    [Fact]
    public void TryReplaceLegacyConditions_DoesNotOverwriteMultipleTrackConditions()
    {
        var morning = MusicConditionSource.CreateCurrentTime(6, "GreaterThanOrEqualTo");
        var night = MusicConditionSource.CreateCurrentTime(22, "GreaterThanOrEqualTo");
        var legacy = MusicConditionSource.CreateCurrentTime(18, "GreaterThanOrEqualTo");
        var entry = new MusicGenerationPlanEntry(
            "asset-key",
            true,
            Array.Empty<MusicSettingSource>());

        entry.ApplyTrackConditions(new[]
        {
            new MusicGenerationTrackPlan("track-morning", new[] { morning }),
            new MusicGenerationTrackPlan("track-night", new[] { night })
        });

        Assert.False(entry.TryReplaceLegacyConditions(new[] { legacy }));
        Assert.Contains(morning, entry.Tracks.SelectMany(track => track.Conditions));
        Assert.Contains(night, entry.Tracks.SelectMany(track => track.Conditions));
        Assert.DoesNotContain(legacy, entry.Tracks.SelectMany(track => track.Conditions));
    }

    [Fact]
    public void ReplaceTrackPlans_ReplacesTheExactTrackSetIncludingEmpty()
    {
        var entry = new MusicGenerationPlanEntry(
            "asset-key",
            true,
            Array.Empty<MusicSettingSource>());
        entry.ApplyTrackConditions(new[]
        {
            new MusicGenerationTrackPlan(
                "track-morning",
                new[] { MusicConditionSource.CreateCurrentTime(6, "GreaterThanOrEqualTo") }),
            new MusicGenerationTrackPlan(
                "track-night",
                new[] { MusicConditionSource.CreateCurrentTime(22, "GreaterThanOrEqualTo") })
        });

        entry.ReplaceTrackPlans(new[]
        {
            new MusicGenerationTrackPlan(
                "track-day",
                new[] { MusicConditionSource.CreateCurrentTime(8, "GreaterThanOrEqualTo") })
        });
        Assert.Equal("track-day", Assert.Single(entry.Tracks).TrackKey);

        entry.ReplaceTrackPlans(Array.Empty<MusicGenerationTrackPlan>());
        Assert.Empty(entry.Tracks);
    }

    [Fact]
    public void Resolve_KeepVanillaUsesOfficialTracksAndAdoptedGeneratedEntries()
    {
        var fixture = CreateResolutionFixture();
        fixture.Plan.KeepVanillaMusic = true;

        var resolution = new MusicGenerationPlanResolver().Resolve(
            fixture.Plan,
            fixture.Settings);

        var musicType = Assert.Single(resolution.MusicTypes);
        Assert.True(resolution.KeepVanillaMusic);
        Assert.Equal(
            new[] { fixture.OfficialTrack.FormKey },
            musicType.OfficialTrackFormKeys);
        Assert.DoesNotContain(
            fixture.OldModTrack.FormKey,
            musicType.OfficialTrackFormKeys,
            StringComparer.OrdinalIgnoreCase);
        Assert.Single(musicType.GeneratedEntries);
        Assert.Same(fixture.GeneratedEntry, musicType.GeneratedEntries[0]);
    }

    [Fact]
    public void Resolve_RemoveVanillaUsesOnlyAdoptedGeneratedEntries()
    {
        var fixture = CreateResolutionFixture();
        fixture.Plan.KeepVanillaMusic = false;

        var resolution = new MusicGenerationPlanResolver().Resolve(
            fixture.Plan,
            fixture.Settings);

        var musicType = Assert.Single(resolution.MusicTypes);
        Assert.False(resolution.KeepVanillaMusic);
        Assert.Empty(musicType.OfficialTracks);
        Assert.Single(musicType.GeneratedEntries);
        Assert.Same(fixture.GeneratedEntry, musicType.GeneratedEntries[0]);
    }

    [Theory]
    [InlineData("000001:Skyrim.esm", true)]
    [InlineData("000001:Dawnguard.esm", true)]
    [InlineData("000001:Dragonborn.esm", true)]
    [InlineData("000001:ccBGSSSE001-Fish.esm", true)]
    [InlineData("000001:ccQDRSSE001-SurvivalMode.esl", true)]
    [InlineData("000001:UnofficialMusic.esp", false)]
    [InlineData("not-a-form-key", false)]
    public void OfficialCatalog_UsesTheDefiningPluginFromTheFormKey(
        string formKey,
        bool expected)
    {
        var pluginName = formKey.Contains(':', StringComparison.Ordinal)
            ? formKey[(formKey.IndexOf(':') + 1)..]
            : "Unmanaged.esp";
        var plugin = new PluginSource(
            pluginName,
            $@"C:\Game\Data\{pluginName}",
            "Game Data",
            @"C:\Game\Data",
            true,
            true,
            0,
            int.MinValue);
        var record = new PluginRecordSource(
            formKey,
            "MusicTrack",
            "MUSTest",
            false,
            plugin,
            true);
        var track = new MusicTrackSource(
            formKey,
            record.EditorId,
            Array.Empty<string>(),
            record);

        Assert.Equal(expected, new OfficialMusicTrackCatalog().IsOfficial(track));
    }

    [Fact]
    public void Resolve_KeepsAllAdoptedGeneratedEntriesForTheSameType()
    {
        var fixture = CreateResolutionFixture();
        fixture.Plan.KeepVanillaMusic = false;
        var destination = fixture.Settings[0];
        var second = fixture.Plan.GetOrCreate(
            Asset("Dream Music", @"music\second.xwm"),
            new[] { destination });
        var excluded = fixture.Plan.GetOrCreate(
            Asset("Excluded Music", @"music\excluded.xwm"),
            new[] { destination });
        excluded.IsAdopted = false;

        var resolution = new MusicGenerationPlanResolver().Resolve(
            fixture.Plan,
            fixture.Settings);

        var generatedEntries = Assert.Single(resolution.MusicTypes).GeneratedEntries;
        Assert.Equal(2, generatedEntries.Count);
        Assert.Contains(fixture.GeneratedEntry, generatedEntries);
        Assert.Contains(second, generatedEntries);
        Assert.DoesNotContain(excluded, generatedEntries);
    }

    private static ResolutionFixture CreateResolutionFixture()
    {
        var officialPlugin = new PluginSource(
            "Skyrim.esm",
            @"C:\\Game\\Data\\Skyrim.esm",
            "Game Data",
            @"C:\\Game\\Data",
            true,
            true,
            0,
            int.MinValue);
        var oldModPlugin = new PluginSource(
            "Fantasy Music.esp",
            @"C:\\Mods\\Fantasy Music\\Fantasy Music.esp",
            "Fantasy Music",
            @"C:\\Mods\\Fantasy Music",
            true,
            true,
            10,
            10);
        var officialTrackRecord = new PluginRecordSource(
            "000001:Skyrim.esm",
            "MusicTrack",
            "MUSOfficial",
            false,
            officialPlugin,
            false)
        {
            Assets = new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"music\\official.xwm")
            }
        };
        var oldModTrackRecord = new PluginRecordSource(
            "000001:Fantasy Music.esp",
            "MusicTrack",
            "MUSOldMod",
            false,
            oldModPlugin,
            true)
        {
            Assets = new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"music\\old-mod.xwm")
            }
        };
        var officialMusicTypeRecord = new PluginRecordSource(
            "000100:Skyrim.esm",
            "MusicType",
            "TESTCombat",
            false,
            officialPlugin,
            true)
        {
            References = new[]
            {
                new PluginRecordReferenceSource("Tracks", officialTrackRecord.FormKey)
            }
        };
        var oldModMusicTypeRecord = officialMusicTypeRecord with
        {
            Plugin = oldModPlugin,
            IsWinner = false,
            References = new[]
            {
                new PluginRecordReferenceSource("Tracks", oldModTrackRecord.FormKey)
            }
        };
        var officialTrack = new MusicTrackSource(
            officialTrackRecord.FormKey,
            officialTrackRecord.EditorId,
            new[] { @"music\\official.xwm" },
            officialTrackRecord);
        var oldModTrack = new MusicTrackSource(
            oldModTrackRecord.FormKey,
            oldModTrackRecord.EditorId,
            new[] { @"music\\old-mod.xwm" },
            oldModTrackRecord);
        var officialSetting = new MusicSettingSource(
            MusicSettingScope.MusicType,
            officialMusicTypeRecord.FormKey,
            officialMusicTypeRecord.EditorId,
            officialMusicTypeRecord.FormKey,
            officialMusicTypeRecord.EditorId,
            officialMusicTypeRecord,
            officialMusicTypeRecord,
            new[] { officialTrack });
        var oldModSetting = officialSetting with
        {
            Record = oldModMusicTypeRecord,
            MusicTypeRecord = oldModMusicTypeRecord,
            Tracks = new[] { oldModTrack }
        };
        var plan = new MusicGenerationPlan();
        var generatedEntry = plan.GetOrCreate(
            Asset("Fantasy Music", @"music\\generated.xwm"),
            new[] { officialSetting });

        return new ResolutionFixture(
            plan,
            new[] { officialSetting, oldModSetting },
            generatedEntry,
            officialTrack,
            oldModTrack);
    }

    private sealed record ResolutionFixture(
        MusicGenerationPlan Plan,
        IReadOnlyList<MusicSettingSource> Settings,
        MusicGenerationPlanEntry GeneratedEntry,
        MusicTrackSource OfficialTrack,
        MusicTrackSource OldModTrack);

    private static AssetSource Asset(string modName, string virtualPath) =>
        new(
            virtualPath,
            AssetSourceKind.Loose,
            modName,
            $@"C:\Fixture\{modName}",
            true,
            $@"C:\Fixture\{modName}\{virtualPath.Replace('\\', Path.DirectorySeparatorChar)}",
            null,
            1);
}
