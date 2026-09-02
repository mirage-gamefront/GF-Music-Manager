using GfMusicManager.Application;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicScanApplicationTests
{
    [Fact]
    public void PlanService_CreatesAdoptedEntriesInStableAssetOrder()
    {
        var first = new AssetSource(
            @"music\z.xwm",
            AssetSourceKind.Loose,
            "Mod Z",
            @"C:\ModZ",
            true,
            @"C:\ModZ\music\z.xwm",
            null,
            10);
        var second = first with
        {
            VirtualPath = @"music\a.xwm",
            ModName = "Mod A",
            ModPath = @"C:\ModA",
            SourcePath = @"C:\ModA\music\a.xwm"
        };
        var scan = CreateEmptyResult() with
        {
            Scan = CreateEmptyResult().Scan with
            {
                Assets = new[] { first, second }
            }
        };

        var plan = new MusicPlanApplicationService().CreatePlan(scan);

        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal("Mod A", plan.Entries[0].Asset?.ModName);
        Assert.Equal("Mod Z", plan.Entries[1].Asset?.ModName);
        Assert.All(plan.Entries, entry => Assert.True(entry.IsAdopted));
    }

    [Fact]
    public void PlanService_ReportsAssetListProgressWithoutChangingPlanContents()
    {
        var first = new AssetSource(
            @"music\a.xwm",
            AssetSourceKind.Loose,
            "Fixture Mod",
            @"C:\Fixture",
            true,
            @"C:\Fixture\music\a.xwm",
            null,
            10);
        var second = first with
        {
            VirtualPath = @"music\b.xwm",
            SourcePath = @"C:\Fixture\music\b.xwm"
        };
        var scan = CreateEmptyResult() with
        {
            Scan = CreateEmptyResult().Scan with { Assets = new[] { first, second } }
        };
        var reports = new List<(int Current, int Total)>();

        var plan = new MusicPlanApplicationService().CreatePlan(
            scan,
            (current, total) => reports.Add((current, total)));

        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal((0, 2), reports[0]);
        Assert.Equal((2, 2), reports[^1]);
    }

    [Fact]
    public void PreparedBindings_PreserveMatchedTracksConditionsAndDefinitionConflicts()
    {
        var asset = new AssetSource(
            @"music\fixture.xwm",
            AssetSourceKind.Loose,
            "Fixture Mod",
            @"C:\Fixture",
            true,
            @"C:\Fixture\music\fixture.xwm",
            null,
            10);
        var plugin = new PluginSource(
            "Fixture.esp",
            @"C:\Fixture\Fixture.esp",
            "Fixture Mod",
            @"C:\Fixture",
            true,
            true,
            1,
            1);
        var typeRecord = new PluginRecordSource(
            "000100:Fixture.esp",
            "MusicType",
            "MUSFixture",
            false,
            plugin);
        var trackRecord = new PluginRecordSource(
            "000200:Fixture.esp",
            "MusicTrack",
            "TrackFixture",
            false,
            plugin);
        var unrelatedTrackRecord = new PluginRecordSource(
            "000201:Fixture.esp",
            "MusicTrack",
            "TrackOther",
            false,
            plugin);
        var condition = MusicConditionSource.CreateCurrentTime(8, "GreaterThanOrEqualTo");
        var matchingTrack = new MusicTrackSource(
            trackRecord.FormKey,
            trackRecord.EditorId,
            new[] { asset.VirtualPath },
            trackRecord)
        {
            Conditions = new[] { condition }
        };
        var unrelatedTrack = new MusicTrackSource(
            unrelatedTrackRecord.FormKey,
            unrelatedTrackRecord.EditorId,
            new[] { @"music\other.xwm" },
            unrelatedTrackRecord);
        var setting = new MusicSettingSource(
            MusicSettingScope.MusicType,
            typeRecord.FormKey,
            typeRecord.EditorId,
            typeRecord.FormKey,
            typeRecord.EditorId,
            typeRecord,
            typeRecord,
            new[] { matchingTrack, unrelatedTrack });
        var conflict = new MusicDefinitionConflict(
            typeRecord.FormKey,
            "MusicType",
            new[] { typeRecord },
            typeRecord);
        var analysis = new MusicAnalysisResult(
            new[] { setting },
            new Dictionary<string, IReadOnlyList<MusicSettingSource>>(
                StringComparer.OrdinalIgnoreCase)
            {
                [asset.VirtualPath] = new[] { setting }
            },
            Array.Empty<ScanIssue>())
        {
            DefinitionConflicts = new[] { conflict }
        };
        var empty = CreateEmptyResult();
        var scan = empty with
        {
            Scan = empty.Scan with { Assets = new[] { asset } },
            MusicAnalysis = analysis
        };

        var prepared = new MusicPlanApplicationService().PreparePlan(scan);
        var binding = prepared.AssetBindings.Get(asset.VirtualPath);
        var entry = Assert.Single(prepared.Plan.Entries);

        Assert.Same(setting, Assert.Single(binding.Settings));
        Assert.Same(matchingTrack, Assert.Single(binding.Tracks));
        Assert.Equal(condition, Assert.Single(binding.Conditions));
        Assert.Same(conflict, Assert.Single(binding.DefinitionConflicts));
        Assert.Single(entry.Tracks);
        Assert.Equal(
            MusicGenerationTrackKey.Create(matchingTrack),
            entry.Tracks[0].TrackKey);
        Assert.Equal(condition, Assert.Single(entry.Tracks[0].Conditions));
    }

    [Fact]
    public void PlanService_CapturesAndAppliesAllTrackConditions()
    {
        var asset = new AssetSource(
            @"music\fixture.xwm",
            AssetSourceKind.Loose,
            "Fixture Mod",
            @"C:\Fixture",
            true,
            @"C:\Fixture\music\fixture.xwm",
            null,
            10);
        var scan = CreateEmptyResult() with
        {
            Scan = CreateEmptyResult().Scan with { Assets = new[] { asset } }
        };
        var service = new MusicPlanApplicationService();
        var plan = service.CreatePlan(scan);
        var entry = Assert.Single(plan.Entries);
        var morning = MusicConditionSource.CreateCurrentTime(6, "GreaterThanOrEqualTo");
        var night = MusicConditionSource.CreateCurrentTime(22, "GreaterThanOrEqualTo");
        entry.ReplaceTrackPlans(new[]
        {
            new MusicGenerationTrackPlan("track-morning", new[] { morning }),
            new MusicGenerationTrackPlan("track-night", new[] { night })
        });
        entry.IsAdopted = false;
        plan.KeepVanillaMusic = false;

        var snapshot = service.Capture(plan);
        var restored = service.CreatePlan(scan);
        var result = service.Apply(restored, snapshot, Array.Empty<MusicSettingSource>());

        Assert.Equal(1, result.RestoredEntryCount);
        Assert.Equal(0, result.MissingEntryCount);
        var restoredEntry = Assert.Single(restored.Entries);
        Assert.False(restoredEntry.IsAdopted);
        Assert.False(restored.KeepVanillaMusic);
        Assert.Equal(
            new[] { "track-morning", "track-night" },
            restoredEntry.Tracks.Select(track => track.TrackKey));
        Assert.Equal(morning, restoredEntry.Tracks[0].Conditions.Single());
        Assert.Equal(night, restoredEntry.Tracks[1].Conditions.Single());
    }

    [Fact]
    public void PlanJson_RoundTripsMultiTrackSnapshot()
    {
        var asset = new AssetSource(
            @"music\fixture.xwm",
            AssetSourceKind.Loose,
            "Fixture Mod",
            @"C:\Fixture",
            true,
            @"C:\Fixture\music\fixture.xwm",
            null,
            10);
        var scan = CreateEmptyResult() with
        {
            Scan = CreateEmptyResult().Scan with { Assets = new[] { asset } }
        };
        var service = new MusicPlanApplicationService();
        var plan = service.CreatePlan(scan);
        var entry = Assert.Single(plan.Entries);
        entry.ReplaceTrackPlans(new[]
        {
            new MusicGenerationTrackPlan(
                "track-day",
                new[] { MusicConditionSource.CreateCurrentTime(8, "GreaterThanOrEqualTo") }),
            new MusicGenerationTrackPlan(
                "track-night",
                new[] { MusicConditionSource.CreateCurrentTime(22, "GreaterThanOrEqualTo") })
        });

        var path = Path.Combine(Path.GetTempPath(), $"gfmm-plan-{Guid.NewGuid():N}.json");
        try
        {
            MusicPlanJson.Save(path, service.Capture(plan));
            var loaded = MusicPlanJson.Load(path);

            Assert.Equal(MusicPlanDocument.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(2, Assert.Single(loaded.Plan.Entries).Tracks.Count);
            Assert.Equal("track-night", loaded.Plan.Entries[0].Tracks[1].TrackKey);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Request_ExcludesGeneratedProductByDefault()
    {
        var request = new MusicScanRequest
        {
            Mo2Root = @"C:\MO2"
        };

        var options = request.ToCoreOptions();

        Assert.Contains("GF Music Product", options.ExcludedModNames);
    }

    [Fact]
    public void Request_CanIncludeGeneratedProductExplicitly()
    {
        var request = new MusicScanRequest
        {
            Mo2Root = @"C:\MO2",
            IncludeGeneratedProduct = true
        };

        var options = request.ToCoreOptions();

        Assert.DoesNotContain("GF Music Product", options.ExcludedModNames);
    }

    [Fact]
    public void Request_UsesMusicRecordFilterForApplicationScans()
    {
        var request = new MusicScanRequest
        {
            Mo2Root = @"C:\MO2"
        };

        var options = request.ToCoreOptions();

        Assert.NotNull(options.IncludedRecordTypes);
        Assert.Contains("MusicType", options.IncludedRecordTypes!);
        Assert.Contains("MusicTrack", options.IncludedRecordTypes!);
        Assert.Contains("Cell", options.IncludedRecordTypes!);
        Assert.Contains("Worldspace", options.IncludedRecordTypes!);
        Assert.Contains("Keyword", options.IncludedRecordTypes!);
        Assert.DoesNotContain("NPC", options.IncludedRecordTypes!);
        Assert.True(options.RetainOnlyMusicAssignments);
    }

    [Fact]
    public void ScanResultJson_RoundTripsVersionedDocument()
    {
        var result = CreateEmptyResult();
        var path = Path.Combine(Path.GetTempPath(), $"gfmm-scan-{Guid.NewGuid():N}.json");

        try
        {
            MusicScanResultJson.Save(path, result);
            var loaded = MusicScanResultJson.Load(path);

            Assert.Equal(MusicScanResultDocument.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(result.Request.Mo2Root, loaded.Result.Request.Mo2Root);
            Assert.Equal(result.Scan.Profile.ProfileName, loaded.Result.Scan.Profile.ProfileName);
            Assert.Equal(result.MusicAnalysis.Settings.Count, loaded.Result.MusicAnalysis.Settings.Count);
            Assert.Equal(result.AudioDuplicates.Groups.Count, loaded.Result.AudioDuplicates.Groups.Count);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ScanResultJson_DropsUnrelatedRecordsFromPersistedArtifact()
    {
        var result = CreateEmptyResult();
        var plugin = new PluginSource(
            "Fixture.esp",
            @"C:\Fixture.esp",
            "Fixture",
            @"C:\",
            true,
            true,
            1,
            1);
        var musicRecord = new PluginRecordSource(
            "000001:Fixture.esp",
            "MusicType",
            "MusicType_Fixture",
            false,
            plugin);
        var unrelatedRecord = new PluginRecordSource(
            "000002:Fixture.esp",
            "NPC",
            "Npc_Fixture",
            false,
            plugin);
        result = result with
        {
            Scan = result.Scan with
            {
                Records = new[] { musicRecord, unrelatedRecord }
            }
        };
        var path = Path.Combine(Path.GetTempPath(), $"gfmm-scan-filter-{Guid.NewGuid():N}.json");

        try
        {
            MusicScanResultJson.Save(path, result);
            var loaded = MusicScanResultJson.Load(path);

            Assert.Single(loaded.Result.Scan.Records);
            Assert.Equal("MusicType", loaded.Result.Scan.Records[0].RecordType);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static MusicScanApplicationResult CreateEmptyResult()
    {
        var profile = new Mo2ProfileSnapshot(
            @"C:\MO2",
            "Default",
            @"C:\MO2\profiles\Default",
            Array.Empty<string>(),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            false,
            false,
            false);
        var scan = new ScanResult(
            profile,
            Array.Empty<ModSource>(),
            Array.Empty<PluginSource>(),
            Array.Empty<PluginRecordSource>(),
            Array.Empty<AssetSource>(),
            Array.Empty<ScanIssue>());
        var analysis = new MusicAnalysisResult(
            Array.Empty<MusicSettingSource>(),
            new Dictionary<string, IReadOnlyList<MusicSettingSource>>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<ScanIssue>());
        var duplicates = new AudioDuplicateAnalysisResult(
            Array.Empty<AudioDuplicateGroup>(),
            0,
            0,
            0,
            false);
        var request = new MusicScanRequest { Mo2Root = @"C:\MO2" };
        return new MusicScanApplicationResult(request, scan, analysis, duplicates);
    }
}
