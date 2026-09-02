using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;
using GfMusicManager.Desktop;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class TrackRowPresentationTests
{
    [Fact]
    public void FromAsset_UsesDistinctMusicTypeCountText()
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
        var track = Record(
            "000001:Fixture.esp",
            "MusicTrack",
            "Track_Fixture",
            plugin,
            assets: new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"Data\Music\Combat\fixture.wav")
            });
        var records = new List<PluginRecordSource> { track };
        var musicType = Record(
            "000002:Fixture.esp",
            "MusicType",
            "MUSCombat",
            plugin,
            references: new[]
            {
                new PluginRecordReferenceSource("Tracks", track.FormKey)
            });
        records.Add(musicType);
        foreach (var (formKey, editorId) in new[]
                 {
                     ("000003:Fixture.esp", "CellA"),
                     ("000004:Fixture.esp", "CellB")
                 })
        {
            records.Add(Record(
                formKey,
                "Cell",
                editorId,
                plugin,
                references: new[]
                {
                    new PluginRecordReferenceSource("Music", musicType.FormKey)
                }));
        }

        var asset = new AssetSource(
            @"music\combat\fixture.xwm",
            AssetSourceKind.Loose,
            "Fixture",
            @"C:\Fixture",
            true,
            @"C:\Fixture\music\combat\fixture.xwm",
            null,
            12);
        var analysis = new MusicSettingsAnalyzer().Analyze(records, new[] { asset });

        var row = TrackRow.FromAsset(asset, analysis);

        Assert.Equal("1件", row.MusicTypeCountText);
        Assert.Equal("3件", row.Placement);
    }

    [Fact]
    public void AdoptionStatusAndOperationSelectionAreIndependent()
    {
        var asset = new AssetSource(
            @"music\explore\forest.xwm",
            AssetSourceKind.Loose,
            "Fixture",
            @"C:\Fixture",
            true,
            @"C:\Fixture\music\explore\forest.xwm",
            null,
            12);
        var row = TrackRow.FromAsset(asset, generationPlan: new MusicGenerationPlan());

        Assert.True(row.IsAdopted);
        Assert.False(row.IsSelected);
        Assert.Equal("採用", row.AdoptionStatusText);

        row.IsSelected = true;
        Assert.True(row.IsAdopted);

        row.IsAdopted = false;
        Assert.True(row.IsSelected);
        Assert.Equal("除外", row.AdoptionStatusText);
    }

    [Fact]
    public void FromAsset_WithoutMusicTypeAssignmentIsMarkedUnused()
    {
        var asset = new AssetSource(
            @"music\unassigned\unused.xwm",
            AssetSourceKind.Loose,
            "Fixture",
            @"C:\Fixture",
            true,
            @"C:\Fixture\music\unassigned\unused.xwm",
            null,
            12);

        var row = TrackRow.FromAsset(asset, new MusicAnalysisResult(
            Array.Empty<MusicSettingSource>(),
            new Dictionary<string, IReadOnlyList<MusicSettingSource>>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<ScanIssue>()));

        Assert.True(row.IsUnused);

        var plugin = new PluginSource(
            "Fixture.esp",
            @"C:\Fixture\Fixture.esp",
            "Fixture",
            @"C:\Fixture",
            true,
            true,
            1,
            1);
        var musicType = Record(
            "000010:Fixture.esp",
            "MusicType",
            "MUSExploreFixture",
            plugin);
        var setting = new MusicSettingSource(
            MusicSettingScope.MusicType,
            musicType.FormKey,
            musicType.EditorId,
            musicType.FormKey,
            musicType.EditorId,
            musicType,
            musicType,
            Array.Empty<MusicTrackSource>());

        row.ReplaceMusicSettings(new[] { setting });

        Assert.False(row.IsUnused);
    }

    [Fact]
    public void PreviewState_ChangesListButtonAndTooltip()
    {
        var row = new TrackRow(
            "Preview",
            "Fixture",
            "Music Type / MUSCombat",
            TrackAssetHandling.Reference,
            @"music\combat\preview.xwm",
            "ルーズ · music\\combat\\preview.xwm",
            false,
            "");

        Assert.False(row.IsPreviewActive);
        Assert.Equal("▶", row.PreviewButtonContent);
        Assert.Equal("試聴", row.PreviewButtonToolTip);

        var changedProperties = new List<string?>();
        row.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        row.SetPreviewActive(true);

        Assert.True(row.IsPreviewActive);
        Assert.Equal("■", row.PreviewButtonContent);
        Assert.Equal("停止", row.PreviewButtonToolTip);
        Assert.Contains(nameof(TrackRow.IsPreviewActive), changedProperties);
        Assert.Contains(nameof(TrackRow.PreviewButtonContent), changedProperties);
        Assert.Contains(nameof(TrackRow.PreviewButtonToolTip), changedProperties);

        row.SetPreviewActive(false);

        Assert.Equal("▶", row.PreviewButtonContent);
        Assert.Equal("試聴", row.PreviewButtonToolTip);
    }

    [Fact]
    public void ReplaceMusicSettings_UpdatesGeneratedPlacementOnly()
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
        var scopeRecord = Record(
            "000010:Fixture.esp",
            "Location",
            "RiverwoodLocation",
            plugin);
        var musicTypeRecord = Record(
            "000011:Fixture.esp",
            "MusicType",
            "MUSExploreForest",
            plugin);
        var setting = new MusicSettingSource(
            MusicSettingScope.Location,
            scopeRecord.FormKey,
            scopeRecord.EditorId,
            musicTypeRecord.FormKey,
            musicTypeRecord.EditorId,
            scopeRecord,
            musicTypeRecord,
            Array.Empty<MusicTrackSource>());
        var row = new TrackRow(
            "Preview",
            "Fixture",
            "音源のみ / 設定未解析",
            TrackAssetHandling.Reference,
            @"music\explore\preview.xwm",
            "ルーズ · music\\explore\\preview.xwm",
            false,
            "",
            musicSettings: new[] { setting },
            availableMusicSettings: new[] { setting });

        row.ReplaceMusicSettings(Array.Empty<MusicSettingSource>());

        Assert.Equal("0件", row.Placement);
        Assert.Empty(row.MusicSettings);
        Assert.Single(row.SourceMusicSettings);
        Assert.Empty(row.GenerationPlanEntry.DestinationKeys);
        Assert.Equal(scopeRecord.FormKey, setting.Record.FormKey);

        row.ReplaceMusicSettings(new[] { setting });

        Assert.Equal("1件", row.Placement);
        Assert.Single(row.MusicSettings);
        Assert.Single(row.SourceMusicSettings);
        Assert.Single(row.GenerationPlanEntry.DestinationKeys);
    }

    [Fact]
    public void AddMusicSettings_AppendsMultipleMusicTypesWithoutDuplicates()
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
        var scopeRecord = Record(
            "000020:Fixture.esp",
            "MusicType",
            "MUSExploreForest",
            plugin);
        var firstType = new MusicSettingSource(
            MusicSettingScope.MusicType,
            scopeRecord.FormKey,
            scopeRecord.EditorId,
            scopeRecord.FormKey,
            scopeRecord.EditorId,
            scopeRecord,
            scopeRecord,
            Array.Empty<MusicTrackSource>());
        var secondRecord = Record(
            "000021:Fixture.esp",
            "MusicType",
            "MUSExploreSnow",
            plugin);
        var secondType = new MusicSettingSource(
            MusicSettingScope.MusicType,
            secondRecord.FormKey,
            secondRecord.EditorId,
            secondRecord.FormKey,
            secondRecord.EditorId,
            secondRecord,
            secondRecord,
            Array.Empty<MusicTrackSource>());
        var row = new TrackRow(
            "Bulk",
            "Fixture",
            "1件",
            TrackAssetHandling.Reference,
            @"music\explore\bulk.xwm",
            "ルーズ · music\\explore\\bulk.xwm",
            false,
            "",
            musicSettings: new[] { firstType },
            availableMusicSettings: new[] { firstType, secondType });

        row.AddMusicSettings(new[] { secondType, secondType });

        Assert.Equal(2, row.MusicSettings.Count);
        Assert.Equal("2件", row.Placement);
        Assert.Equal(2, row.GenerationPlanEntry.DestinationKeys.Count);
    }

    [Fact]
    public void ReplaceMusicConditions_UpdatesGeneratedConditionsOnly()
    {
        var condition = new MusicConditionSource(
            "GetCurrentTime",
            "LessThanOrEqualTo",
            22f,
            string.Empty,
            "GetCurrentTimeConditionData",
            string.Empty);
        var row = new TrackRow(
            "Preview",
            "Fixture",
            "0件",
            TrackAssetHandling.Reference,
            @"music\town\day.xwm",
            "ルーズ · music\\town\\day.xwm",
            false,
            "",
            musicConditions: new[] { condition },
            availableMusicConditions: new[] { condition });

        Assert.Single(row.SourceMusicConditions);
        Assert.Single(row.MusicConditions);
        Assert.Single(row.GenerationPlanEntry.Conditions);

        row.ReplaceMusicConditions(Array.Empty<MusicConditionSource>());

        Assert.Single(row.SourceMusicConditions);
        Assert.Empty(row.MusicConditions);
        Assert.Empty(row.GenerationPlanEntry.Conditions);
    }

    [Fact]
    public void FromAsset_ExplainsThatDuplicateSourcesAreHandledDuringGeneration()
    {
        var asset = new AssetSource(
            @"music\shared.xwm",
            AssetSourceKind.Bsa,
            "Mod B",
            @"C:\Fixture\Mod B",
            true,
            @"C:\Fixture\Mod B\Mod B.bsa",
            @"music\shared.xwm",
            42);
        var duplicate = new AssetSource(
            @"music\shared.xwm",
            AssetSourceKind.Loose,
            "Mod A",
            @"C:\Fixture\Mod A",
            true,
            @"C:\Fixture\Mod A\music\shared.xwm",
            null,
            24);
        var duplicateGroup = new AudioDuplicateGroup(
            "path:music\\shared.xwm",
            AudioDuplicateKind.PathConflict,
            @"music\shared.xwm",
            "path conflict",
            "test",
            new[]
            {
                new AudioDuplicateSource(duplicate, "hash-a"),
                new AudioDuplicateSource(asset, "hash-b")
            });

        var row = TrackRow.FromAsset(
            asset,
            duplicateSources: new[] { duplicate, asset },
            audioDuplicateGroups: new[] { duplicateGroup });

        Assert.True(row.HasWarning);
        Assert.True(row.HasAudioDuplicateWarning);
        Assert.True(row.HasAutomaticResolution);
        Assert.Equal(
            "同じゲーム内パスに重複音源があります。" + Environment.NewLine +
            "競合で負けたファイルのみGF Music Productへコピーし、利用できるようにします。",
            row.AutomaticResolutionText);
    }

    [Fact]
    public void MusicTypeGroup_NestsRelatedLocationRecordsUnderMusicType()
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
        var musicTypeRecord = Record(
            "000020:Fixture.esp",
            "MusicType",
            "MUSExploreForest",
            plugin);
        var cellRecord = Record(
            "000021:Fixture.esp",
            "Cell",
            "RiverwoodCell",
            plugin);
        var worldspaceRecord = Record(
            "000022:Fixture.esp",
            "WorldSpace",
            "Tamriel",
            plugin);
        var musicTrackRecord = Record(
            "000023:Fixture.esp",
            "MusicTrack",
            "Track_ExploreForest",
            plugin);
        var musicTrack = new MusicTrackSource(
            musicTrackRecord.FormKey,
            musicTrackRecord.EditorId,
            new[] { @"music\explore\forest.xwm" },
            musicTrackRecord);

        var musicTypeSetting = new MusicSettingSource(
            MusicSettingScope.MusicType,
            musicTypeRecord.FormKey,
            musicTypeRecord.EditorId,
            musicTypeRecord.FormKey,
            musicTypeRecord.EditorId,
            musicTypeRecord,
            musicTypeRecord,
            new[] { musicTrack });
        var cellSetting = new MusicSettingSource(
            MusicSettingScope.Cell,
            cellRecord.FormKey,
            cellRecord.EditorId,
            musicTypeRecord.FormKey,
            musicTypeRecord.EditorId,
            cellRecord,
            musicTypeRecord,
            new[] { musicTrack });
        var worldspaceSetting = new MusicSettingSource(
            MusicSettingScope.WorldSpace,
            worldspaceRecord.FormKey,
            worldspaceRecord.EditorId,
            musicTypeRecord.FormKey,
            musicTypeRecord.EditorId,
            worldspaceRecord,
            musicTypeRecord,
            new[] { musicTrack });

        var group = new MusicTypeGroupDetail(new[]
        {
            musicTypeSetting,
            cellSetting,
            worldspaceSetting
        });

        Assert.Equal("000020:Fixture.esp", group.FormKey);
        Assert.Equal("探索用（MUSExploreForest）", group.DisplayText);
        Assert.Equal(2, group.RelatedSettings.Count);
        Assert.Equal(
            UiText.Format("SourceDetails.Scope", UiText.Get("Scope.Cell"), "セル（RiverwoodCell）"),
            group.RelatedSettings[0].ScopeText);
        Assert.Equal(
            UiText.Format("SourceDetails.Scope", UiText.Get("Scope.WorldSpace"), "スカイリム（Tamriel）"),
            group.RelatedSettings[1].ScopeText);
        Assert.Contains(UiText.Format("SourceDetails.RecordType", "MusicType"), group.TechnicalText);
        Assert.Contains(UiText.Format("SourceDetails.FormId", "000020"), group.TechnicalText);
        Assert.Contains(UiText.Format("SourceDetails.RelatedSettings", 2), group.TechnicalText);
        Assert.Contains(
            UiText.Format("SourceDetails.AssignedMusicType", "探索用（MUSExploreForest）"),
            group.RelatedSettings[0].TechnicalText);
    }

    [Fact]
    public void MusicTrackDetail_UsesCompleteRecordDetails()
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
        var record = Record(
            "000023:Fixture.esp",
            "MusicTrack",
            "Track_ExploreForest",
            plugin,
            assets: new[]
            {
                new PluginRecordAssetSource("TrackFilename", @"music\explore\forest.xwm")
            });
        var condition = new MusicConditionSource(
            "GetCurrentTime",
            "GreaterThanOrEqualTo",
            8f,
            string.Empty,
            "GetCurrentTimeConditionData",
            string.Empty);
        var track = new MusicTrackSource(
            record.FormKey,
            record.EditorId,
            new[] { @"music\explore\forest.xwm" },
            record)
        {
            Conditions = new[] { condition }
        };

        var detail = new MusicTrackDetail(track, true);

        Assert.Contains(UiText.Format("SourceDetails.DefinitionEsp", "Fixture.esp"), detail.TechnicalText);
        Assert.Contains(UiText.Format("SourceDetails.FormId", "000023"), detail.TechnicalText);
        Assert.Contains(
            UiText.Format("SourceDetails.AudioPath", "music\\explore\\forest.xwm"),
            detail.TechnicalText);
        Assert.Contains("時間帯：午前8時以降", detail.TechnicalText);
    }

    [Theory]
    [InlineData(UiLanguage.Japanese)]
    [InlineData(UiLanguage.English)]
    public void ConditionEditorRow_ProvidesReadableChoiceAndUpdatesComparison(string language)
    {
        try
        {
            UiText.SetLanguage(language);
            var condition = new MusicConditionSource(
                "GetCombatTargetHasKeyword",
                "EqualTo",
                1f,
                string.Empty,
                "GetCombatTargetHasKeywordConditionData",
                "Keyword=035D59:Skyrim.esm")
            {
                KeywordFormKey = "035D59:Skyrim.esm",
                KeywordEditorId = "ActorTypeDragon",
                KeywordJapaneseExplanation = "ドラゴン"
            };
            var row = new ConditionEditorRow(condition, ConditionRowChoice.Keyword);

            Assert.Equal(
                new[]
                {
                    UiText.Get("SourceDetails.Condition.Has"),
                    UiText.Get("SourceDetails.Condition.NotHas")
                },
                row.ChoiceOptions);
            Assert.Equal(UiText.Get("SourceDetails.Condition.Has"), row.SelectedChoice);
            Assert.Equal(
                UiText.Get("SourceDetails.Condition.CombatDetail"),
                row.DetailText);

            row.SelectedChoice = UiText.Get("SourceDetails.Condition.NotHas");

            Assert.Equal(0f, row.Condition.ComparisonValue);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }

    [Fact]
    public void MusicCandidateOption_ShowsJapaneseWeatherNameAlongsideEditorId()
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
        var weather = Record(
            "001234:Fixture.esp",
            "Weather",
            "SovngardeClear",
            plugin);

        var option = new MusicCandidateOption(weather);

        Assert.Equal("SovngardeClear（ソブンガルデ・晴天）", option.DisplayText);
    }

    [Fact]
    public void ConditionGroupEditor_SupportsOrForKeywordRows()
    {
        var group = new ConditionGroupEditor(
            ConditionGroupKind.CombatKeyword,
            "戦闘対象のキーワード",
            "説明",
            canChooseLogic: true);
        group.AddRow(new ConditionEditorRow(
            new MusicConditionSource(
                "GetCombatTargetHasKeyword",
                "EqualTo",
                1f,
                string.Empty,
                "GetCombatTargetHasKeywordConditionData",
                "Keyword=035D59:Skyrim.esm"),
            ConditionRowChoice.Keyword));

        group.SelectedLogic = UiText.Get("SourceDetails.Condition.LogicOr");

        Assert.True(group.IsOr);
        Assert.Single(group.Rows);
        Assert.Equal(UiText.Get("SourceDetails.Condition.LogicOr"), group.SelectedLogic);
    }

    [Fact]
    public void ReplaceMusicSettings_SupportsMultipleMusicTypeAssignments()
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
        var musicTypeA = Record("000030:Fixture.esp", "MusicType", "MUSExploreForest", plugin);
        var musicTypeB = Record("000031:Fixture.esp", "MusicType", "MUSExploreTundra", plugin);
        var settingA = new MusicSettingSource(
            MusicSettingScope.MusicType,
            musicTypeA.FormKey,
            musicTypeA.EditorId,
            musicTypeA.FormKey,
            musicTypeA.EditorId,
            musicTypeA,
            musicTypeA,
            Array.Empty<MusicTrackSource>());
        var settingB = new MusicSettingSource(
            MusicSettingScope.MusicType,
            musicTypeB.FormKey,
            musicTypeB.EditorId,
            musicTypeB.FormKey,
            musicTypeB.EditorId,
            musicTypeB,
            musicTypeB,
            Array.Empty<MusicTrackSource>());
        var row = new TrackRow(
            "Preview",
            "Fixture",
            "0件",
            TrackAssetHandling.Reference,
            @"music\explore\preview.xwm",
            "ルーズ · music\\explore\\preview.xwm",
            false,
            "",
            musicSettings: new[] { settingA },
            availableMusicSettings: new[] { settingA, settingB });

        row.ReplaceMusicSettings(new[] { settingA, settingB });

        Assert.Equal("2件", row.Placement);
        Assert.Equal(2, row.GenerationPlanEntry.DestinationKeys.Count);
        Assert.All(row.GenerationPlanEntry.DestinationKeys, destination =>
            Assert.Equal(MusicSettingScope.MusicType, destination.Scope));
    }

    private static PluginRecordSource Record(
        string formKey,
        string recordType,
        string editorId,
        PluginSource plugin,
        IReadOnlyList<PluginRecordReferenceSource>? references = null,
        IReadOnlyList<PluginRecordAssetSource>? assets = null) =>
        new(formKey, recordType, editorId, false, plugin, true)
        {
            References = references ?? Array.Empty<PluginRecordReferenceSource>(),
            Assets = assets ?? Array.Empty<PluginRecordAssetSource>()
        };
}
