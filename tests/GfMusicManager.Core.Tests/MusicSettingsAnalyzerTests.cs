using GfMusicManager.Core.Analysis;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicSettingsAnalyzerTests
{
    [Fact]
    public void Analyze_PreservesAllMusicTypeDefinitionsAndSeparatesEffectiveDefinition()
    {
        var fantasyPlugin = new PluginSource(
            "Fantasy Mod.esp",
            @"C:\Fantasy Mod\Fantasy Mod.esp",
            "Fantasy Mod",
            @"C:\Fantasy Mod",
            true,
            true,
            10,
            10);
        var dreamPlugin = new PluginSource(
            "Dream Mod.esp",
            @"C:\Dream Mod\Dream Mod.esp",
            "Dream Mod",
            @"C:\Dream Mod",
            true,
            true,
            20,
            20);
        var fantasyTrackA = Record(
            "000010:Fantasy Mod.esp",
            "MusicTrack",
            "CombatA",
            fantasyPlugin,
            assets: new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"Data\music\combat\a.xwm")
            });
        var fantasyTrackA2 = Record(
            "000011:Fantasy Mod.esp",
            "MusicTrack",
            "CombatA2",
            fantasyPlugin,
            assets: new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"Data\music\combat\a2.xwm")
            });
        var dreamTrackB = Record(
            "000020:Dream Mod.esp",
            "MusicTrack",
            "CombatB",
            dreamPlugin,
            assets: new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"Data\music\combat\b.xwm")
            });
        var dreamTrackB2 = Record(
            "000021:Dream Mod.esp",
            "MusicTrack",
            "CombatB2",
            dreamPlugin,
            assets: new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"Data\music\combat\b2.xwm")
            });
        var fantasyMusicType = Record(
            "000100:Skyrim.esm",
            "MusicType",
            "TESTCombat",
            fantasyPlugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", fantasyTrackA.FormKey),
                new PluginRecordReferenceSource("Tracks", fantasyTrackA2.FormKey)
            },
            isWinner: false);
        var dreamMusicType = Record(
            "000100:Skyrim.esm",
            "MusicType",
            "TESTCombat",
            dreamPlugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", dreamTrackB.FormKey),
                new PluginRecordReferenceSource("Tracks", dreamTrackB2.FormKey)
            });
        var assets = new[]
        {
            Asset(@"music\combat\a.xwm", "Fantasy Mod", fantasyPlugin.ModPath),
            Asset(@"music\combat\a2.xwm", "Fantasy Mod", fantasyPlugin.ModPath),
            Asset(@"music\combat\b.xwm", "Dream Mod", dreamPlugin.ModPath),
            Asset(@"music\combat\b2.xwm", "Dream Mod", dreamPlugin.ModPath)
        };

        var result = new MusicSettingsAnalyzer().Analyze(
            new PluginRecordSource[]
            {
                fantasyTrackA,
                fantasyTrackA2,
                dreamTrackB,
                dreamTrackB2,
                fantasyMusicType,
                dreamMusicType
            },
            assets);

        Assert.Equal(2, result.Settings.Count(setting => setting.Scope == MusicSettingScope.MusicType));
        Assert.Single(result.EffectiveSettings);
        Assert.Equal("Dream Mod.esp", result.EffectiveSettings[0].Record.Plugin.Name);
        Assert.Contains(
            result.GetSettingsForAsset(@"music\combat\a.xwm"),
            setting => setting.Record.Plugin.Name == "Fantasy Mod.esp");
        Assert.Contains(
            result.GetSettingsForAsset(@"music\combat\b.xwm"),
            setting => setting.Record.Plugin.Name == "Dream Mod.esp");

        var conflict = Assert.Single(result.DefinitionConflicts);
        Assert.Equal("000100:Skyrim.esm", conflict.FormKey);
        Assert.Equal("MusicType", conflict.RecordType);
        Assert.Equal(2, conflict.DefinitionCount);
        Assert.Equal("Dream Mod.esp", conflict.CurrentWinner.Plugin.Name);
    }

    [Fact]
    public void Analyze_IncludesWorldSpaceAlongsideOtherMusicScopes()
    {
        var plugin = new PluginSource(
            "MusicFixture.esp",
            @"C:\MusicFixture\MusicFixture.esp",
            "Music Fixture",
            @"C:\MusicFixture",
            true,
            true,
            1,
            1);
        var track = Record(
            "000001:MusicFixture.esp",
            "MusicTrack",
            "Track_Fixture",
            plugin,
            assets: new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"Data\music\fixture.xwm")
            });
        var musicType = Record(
            "000002:MusicFixture.esp",
            "MusicType",
            "MusicType_Fixture",
            plugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", track.FormKey)
            });

        var scopeRecords = new[]
        {
            Record("000003:MusicFixture.esp", "Cell", "Cell_Fixture", plugin, MusicReference(musicType)),
            Record("000004:MusicFixture.esp", "Location", "Location_Fixture", plugin, MusicReference(musicType)),
            Record("000005:MusicFixture.esp", "Region", "Region_Fixture", plugin, MusicReference(musicType, "Sounds.Music")),
            Record("000006:MusicFixture.esp", "Worldspace", "Worldspace_Fixture", plugin, MusicReference(musicType))
        };
        var assets = new[]
        {
            new AssetSource(
                @"music\fixture.xwm",
                AssetSourceKind.Loose,
                "Music Fixture",
                @"C:\MusicFixture",
                true,
                @"C:\MusicFixture\music\fixture.xwm",
                null,
                12)
        };

        var result = new MusicSettingsAnalyzer().Analyze(
            scopeRecords.Append(musicType).Append(track).ToArray(),
            assets);

        Assert.Equal(5, result.Settings.Count);
        Assert.Contains(
            result.Settings,
            setting =>
                setting.Scope == MusicSettingScope.MusicType &&
                setting.ScopeName == "MusicType_Fixture");
        Assert.Contains(
            result.Settings,
            setting =>
                setting.Scope == MusicSettingScope.WorldSpace &&
                setting.ScopeName == "Worldspace_Fixture");
        Assert.Equal(5, result.GetSettingsForAsset(@"music\fixture.xwm").Count);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Analyze_MapsReplacementAudioWhenOnlyTheExtensionDiffers()
    {
        var plugin = new PluginSource(
            "VanillaMusic.esm",
            @"C:\VanillaMusic\VanillaMusic.esm",
            "Game Data",
            @"C:\VanillaMusic",
            true,
            true,
            0,
            int.MinValue);
        var track = Record(
            "000001:VanillaMusic.esm",
            "MusicTrack",
            "MUS_Town_Day_01",
            plugin,
            assets: new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"Data\Music\Town\MUS_Town_Day_01.wav")
            });
        var musicType = Record(
            "000002:VanillaMusic.esm",
            "MusicType",
            "MUSTown",
            plugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", track.FormKey)
            });
        var worldspace = Record(
            "000003:VanillaMusic.esm",
            "Worldspace",
            "Tamriel",
            plugin,
            references: MusicReference(musicType));
        var replacement = new AssetSource(
            @"music\town\mus_town_day_01.xwm",
            AssetSourceKind.Loose,
            "LORKHAN - Soundtrack Replacer",
            @"C:\Lorkhan",
            true,
            @"C:\Lorkhan\music\town\mus_town_day_01.xwm",
            null,
            12);

        var result = new MusicSettingsAnalyzer().Analyze(
            new[] { track, musicType, worldspace },
            new[] { replacement });

        var settings = result.GetSettingsForAsset(replacement.VirtualPath);
        Assert.Contains(settings, setting => setting.Scope == MusicSettingScope.WorldSpace);
        Assert.Contains(settings, setting => setting.Scope == MusicSettingScope.MusicType);
    }

    [Fact]
    public void Analyze_ExpandsNestedMusicTrackGroups()
    {
        var plugin = new PluginSource(
            "VanillaMusic.esm",
            @"C:\VanillaMusic\VanillaMusic.esm",
            "Game Data",
            @"C:\VanillaMusic",
            true,
            true,
            0,
            int.MinValue);
        var leafTrack = Record(
            "000001:VanillaMusic.esm",
            "MusicTrack",
            "MUS_Palette_01",
            plugin,
            assets: new[]
            {
                new PluginRecordAssetSource(
                    "TrackFilename",
                    @"Data\Music\Explore\Palette\MUS_Palette_01.wav")
            });
        var groupTrack = Record(
            "000002:VanillaMusic.esm",
            "MusicTrack",
            "MUS_Palette_Group",
            plugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", leafTrack.FormKey)
            });
        var musicType = Record(
            "000003:VanillaMusic.esm",
            "MusicType",
            "MUSExplore",
            plugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", groupTrack.FormKey)
            });
        var asset = new AssetSource(
            @"music\explore\palette\mus_palette_01.xwm",
            AssetSourceKind.Loose,
            "Replacement",
            @"C:\Replacement",
            true,
            @"C:\Replacement\music\explore\palette\mus_palette_01.xwm",
            null,
            12);

        var result = new MusicSettingsAnalyzer().Analyze(
            new[] { leafTrack, groupTrack, musicType },
            new[] { asset });

        Assert.Contains(
            result.GetSettingsForAsset(asset.VirtualPath),
            setting => setting.Scope == MusicSettingScope.MusicType);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Analyze_CollectsMusicTrackConditionsAsCandidates()
    {
        var plugin = new PluginSource(
            "MusicFixture.esp",
            @"C:\MusicFixture\MusicFixture.esp",
            "Music Fixture",
            @"C:\MusicFixture",
            true,
            true,
            1,
            1);
        var track = Record(
            "000001:MusicFixture.esp",
            "MusicTrack",
            "Track_TownDay",
            plugin,
            assets: new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"Data\Music\Town\day.xwm")
            },
            conditions: new[]
            {
                new PluginRecordConditionSource(
                    "GetCurrentTime",
                    "GreaterThanOrEqualTo",
                    5f,
                    string.Empty,
                    "GetCurrentTimeConditionData",
                    string.Empty),
                new PluginRecordConditionSource(
                    "GetCurrentTime",
                    "LessThanOrEqualTo",
                    22f,
                    string.Empty,
                    "GetCurrentTimeConditionData",
                    string.Empty)
            });
        var musicType = Record(
            "000002:MusicFixture.esp",
            "MusicType",
            "MUSTown",
            plugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", track.FormKey)
            });
        var asset = new AssetSource(
            @"music\town\day.xwm",
            AssetSourceKind.Loose,
            "Music Fixture",
            @"C:\MusicFixture",
            true,
            @"C:\MusicFixture\music\town\day.xwm",
            null,
            12);

        var result = new MusicSettingsAnalyzer().Analyze(
            new[] { track, musicType },
            new[] { asset });

        Assert.Equal(2, result.ConditionCandidates.Count);
        var analyzedTrack = Assert.Single(result.Settings.Single().Tracks);
        Assert.Equal(2, analyzedTrack.Conditions.Count);
        Assert.Contains(result.ConditionCandidates, condition =>
            condition.FunctionName == "GetCurrentTime" &&
            condition.CompareOperator == "GreaterThanOrEqualTo" &&
            condition.ComparisonValue == 5f);
    }

    [Fact]
    public void Analyze_ResolvesConditionKeywordFromScannedRecord()
    {
        var plugin = new PluginSource(
            "MusicFixture.esp",
            @"C:\MusicFixture\MusicFixture.esp",
            "Music Fixture",
            @"C:\MusicFixture",
            true,
            true,
            1,
            1);
        var keyword = Record(
            "035D59:Skyrim.esm",
            "Keyword",
            "ActorTypeDragon",
            plugin);
        var track = Record(
            "000001:MusicFixture.esp",
            "MusicTrack",
            "Track_Combat",
            plugin,
            assets: new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"Data\Music\Combat\combat.xwm")
            },
            conditions: new[]
            {
                new PluginRecordConditionSource(
                    "GetCombatTargetHasKeyword",
                    "EqualTo",
                    1f,
                    string.Empty,
                    "GetCombatTargetHasKeywordConditionData",
                    "Keyword=035D59:Skyrim.esm")
                {
                    KeywordFormKey = keyword.FormKey
                }
            });
        var musicType = Record(
            "000002:MusicFixture.esp",
            "MusicType",
            "MUSCombat",
            plugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", track.FormKey)
            });
        var asset = new AssetSource(
            @"music\combat\combat.xwm",
            AssetSourceKind.Loose,
            "Music Fixture",
            @"C:\MusicFixture",
            true,
            @"C:\MusicFixture\music\combat\combat.xwm",
            null,
            12);

        var result = new MusicSettingsAnalyzer().Analyze(
            new[] { keyword, track, musicType },
            new[] { asset });

        var condition = Assert.Single(result.Settings.Single().Tracks.Single().Conditions);
        Assert.Equal("ActorTypeDragon", condition.KeywordEditorId);
        Assert.Equal("ドラゴン", condition.KeywordJapaneseExplanation);
        Assert.Equal("EditorIDの一般語から自動補足", condition.KeywordExplanationSource);
        Assert.Equal("MusicFixture.esp", condition.KeywordDefinitionPluginName);
        Assert.Contains(result.ConditionCandidates, candidate =>
            candidate.KeywordFormKey == keyword.FormKey &&
            candidate.KeywordEditorId == "ActorTypeDragon");
    }

    [Fact]
    public void Analyze_ExposesKeywordAndWeatherRecordsAsAddCandidates()
    {
        var plugin = new PluginSource(
            "MusicFixture.esp",
            @"C:\MusicFixture\MusicFixture.esp",
            "Music Fixture",
            @"C:\MusicFixture",
            true,
            true,
            1,
            1);
        var keyword = Record("035D59:Skyrim.esm", "Keyword", "ActorTypeDragon", plugin);
        var weather = Record("001234:Skyrim.esm", "Weather", "SkyrimWeatherRain", plugin, displayName: "雨");
        var track = Record(
            "000001:MusicFixture.esp",
            "MusicTrack",
            "Track_Combat",
            plugin,
            assets: new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"Data\Music\Combat\combat.xwm")
            },
            conditions: new[]
            {
                new PluginRecordConditionSource(
                    "GetIsCurrentWeather",
                    "EqualTo",
                    1f,
                    string.Empty,
                    "GetIsCurrentWeatherConditionData",
                    $"Weather={weather.FormKey}")
                {
                    WeatherFormKey = weather.FormKey
                }
            });
        var musicType = Record(
            "000002:MusicFixture.esp",
            "MusicType",
            "MUSCombat",
            plugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", track.FormKey)
            });
        var asset = new AssetSource(
            @"music\combat\combat.xwm",
            AssetSourceKind.Loose,
            "Music Fixture",
            @"C:\MusicFixture",
            true,
            @"C:\MusicFixture\music\combat\combat.xwm",
            null,
            12);

        var result = new MusicSettingsAnalyzer().Analyze(
            new[] { keyword, weather, track, musicType },
            new[] { asset });

        Assert.Contains(result.KeywordCandidates, candidate => candidate.FormKey == keyword.FormKey);
        Assert.Contains(result.WeatherCandidates, candidate => candidate.FormKey == weather.FormKey);
        var analyzedWeather = Assert.Single(result.Settings.Single().Tracks.Single().Conditions);
        Assert.Equal(weather.FormKey, analyzedWeather.WeatherFormKey);
        Assert.Equal("SkyrimWeatherRain", analyzedWeather.WeatherEditorId);
        Assert.Equal("雨", analyzedWeather.WeatherDisplayName);
    }

    [Fact]
    public void Analyze_RepairsAdditionalMusicProjectKnownAudioPathMistakes()
    {
        var plugin = new PluginSource(
            "AdditionalMusicProjectReplacer.esp",
            @"C:\Additional Music Project\AdditionalMusicProjectReplacer.esp",
            "Additional Music Project",
            @"C:\Additional Music Project",
            true,
            true,
            10,
            10);
        var heroicsTrack = Record(
            "000001:AdditionalMusicProjectReplacer.esp",
            "MusicTrack",
            "ADMPIVCombat02",
            plugin,
            assets: new[]
            {
                new PluginRecordAssetSource(
                    "TrackFilename",
                    @"Data\music\Additional Music Project\ADMP Definitely Time For Heroïcs.xwm"),
                new PluginRecordAssetSource(
                    "FinaleFilename",
                    @"Data\Music\Combat\MUS_Combat_01_Finale.wav")
            });
        var aweTrack = Record(
            "000002:AdditionalMusicProjectReplacer.esp",
            "MusicTrack",
            "ADMPIVExploreNight04",
            plugin,
            assets: new[]
            {
                new PluginRecordAssetSource(
                    "TrackFilename",
                    @"Data\music\Additional Music Project\ADMP The Awe Of God Fearing Men.xwm")
            });
        var musicType = Record(
            "000003:AdditionalMusicProjectReplacer.esp",
            "MusicType",
            "ADMPMusicType",
            plugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", heroicsTrack.FormKey),
                new PluginRecordReferenceSource("Tracks", aweTrack.FormKey)
            });
        var heroicsAsset = Asset(
            @"music\Additional Music Project\ADMP Definitely A Time For Heroics.xwm",
            "Additional Music Project",
            plugin.ModPath);
        var workAsset = Asset(
            @"music\Additional Music Project\ADMP The Work Of God Fearing Men.xwm",
            "Additional Music Project",
            plugin.ModPath);

        var result = new MusicSettingsAnalyzer().Analyze(
            new[] { heroicsTrack, aweTrack, musicType },
            new[] { heroicsAsset, workAsset });

        Assert.True(result.AdditionalMusicProjectRepair.IsDetected);
        Assert.Equal(2, result.AdditionalMusicProjectRepair.AudioPathRepairs.Count);
        Assert.Equal(1, result.AdditionalMusicProjectRepair.CombatTrackCount);
        Assert.Empty(result.AdditionalMusicProjectRepair.UnresolvedAudioRepairs);
        Assert.Contains(
            result.GetSettingsForAsset(heroicsAsset.VirtualPath),
            setting => setting.MusicTypeEditorId == "ADMPMusicType");
        var analyzedHeroicsTrack = result.GetSettingsForAsset(heroicsAsset.VirtualPath)
            .SelectMany(setting => setting.Tracks)
            .Single(track => track.EditorId == "ADMPIVCombat02");
        Assert.Contains(
            heroicsAsset.VirtualPath,
            analyzedHeroicsTrack.MatchingAudioPaths,
            StringComparer.OrdinalIgnoreCase);
        Assert.Single(analyzedHeroicsTrack.ResolvedAudioPaths);
        Assert.Contains(
            heroicsAsset.VirtualPath,
            analyzedHeroicsTrack.ResolvedAudioPaths,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            @"Music\Combat\MUS_Combat_01_Finale.wav",
            analyzedHeroicsTrack.ResolvedAudioPaths,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            result.GetSettingsForAsset(workAsset.VirtualPath),
            setting => setting.MusicTypeEditorId == "ADMPMusicType");
    }

    [Fact]
    public void Analyze_RepairsFantasySoundtrackProjectTownPathMistake()
    {
        var plugin = new PluginSource(
            "Fantasy Soundtrack Project.esp",
            @"C:\Fantasy Soundtrack Project\Fantasy Soundtrack Project.esp",
            "Fantasy Soundtrack Project SE",
            @"C:\Fantasy Soundtrack Project",
            true,
            true,
            11,
            11);
        var track = Record(
            "000001:Fantasy Soundtrack Project.esp",
            "MusicTrack",
            "FSPTownGeneral01",
            plugin,
            assets: new[]
            {
                new PluginRecordAssetSource(
                    "TrackFilename",
                    @"Data\Music\fantasy_soundtrack\town\town_01.xwm")
            });
        var musicType = Record(
            "000002:Fantasy Soundtrack Project.esp",
            "MusicType",
            "MUSTownTest",
            plugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", track.FormKey)
            });
        var actualAsset = Asset(
            @"music\fantasy_soundtrack\towns\town_01.xwm",
            "Fantasy Soundtrack Project SE",
            plugin.ModPath);

        var result = new MusicSettingsAnalyzer().Analyze(
            new[] { track, musicType },
            new[] { actualAsset });

        Assert.True(result.FantasySoundtrackProjectRepair.IsDetected);
        var repair = Assert.Single(result.FantasySoundtrackProjectRepair.AudioPathRepairs);
        Assert.Equal(
            @"Data\Music\fantasy_soundtrack\town\town_01.xwm",
            repair.OriginalAudioPath);
        Assert.Equal(actualAsset.VirtualPath, repair.RepairedAudioPath);
        Assert.Empty(result.FantasySoundtrackProjectRepair.UnresolvedAudioRepairs);
        Assert.Contains(
            result.GetSettingsForAsset(actualAsset.VirtualPath),
            setting => setting.MusicTypeEditorId == "MUSTownTest");
        var analyzedTrack = result.GetSettingsForAsset(actualAsset.VirtualPath)
            .SelectMany(setting => setting.Tracks)
            .Single(track => track.EditorId == "FSPTownGeneral01");
        Assert.Contains(
            actualAsset.VirtualPath,
            analyzedTrack.MatchingAudioPaths,
            StringComparer.OrdinalIgnoreCase);
    }

    private static PluginRecordReferenceSource[] MusicReference(
        PluginRecordSource musicType,
        string fieldName = "Music") =>
        new[]
        {
            new PluginRecordReferenceSource(fieldName, musicType.FormKey)
        };

    private static PluginRecordSource Record(
        string formKey,
        string recordType,
        string editorId,
        PluginSource plugin,
        IReadOnlyList<PluginRecordReferenceSource>? references = null,
        IReadOnlyList<PluginRecordAssetSource>? assets = null,
        IReadOnlyList<PluginRecordConditionSource>? conditions = null,
        string? displayName = null,
        bool isWinner = true) =>
        new(formKey, recordType, editorId, false, plugin, isWinner)
        {
            DisplayName = displayName,
            References = references ?? Array.Empty<PluginRecordReferenceSource>(),
            Assets = assets ?? Array.Empty<PluginRecordAssetSource>(),
            Conditions = conditions ?? Array.Empty<PluginRecordConditionSource>()
        };

    private static AssetSource Asset(string virtualPath, string modName, string modPath) =>
        new(
            virtualPath,
            AssetSourceKind.Loose,
            modName,
            modPath,
            true,
            Path.Combine(modPath, virtualPath),
            null,
            12);
}
