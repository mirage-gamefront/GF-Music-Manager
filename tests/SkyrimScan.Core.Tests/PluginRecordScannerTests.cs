using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Plugins;
using Xunit;

namespace SkyrimScan.Core.Tests;

public sealed class PluginRecordScannerTests
{
    [Fact]
    public void Read_ExtractsWorldSpaceMusicAndMusicTypeTrackLinks()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-plugin-");
        try
        {
            var pluginPath = Path.Combine(root.FullName, "MusicFixture.esp");
            var modKey = ModKey.FromNameAndExtension("MusicFixture.esp");
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var musicTrack = mod.MusicTracks.AddNew("MusicTrack_Fixture");
            musicTrack.TrackFilename = new();
            musicTrack.TrackFilename.TrySetPath(@"music\fixture.xwm");
            musicTrack.Conditions = new();
            musicTrack.Conditions.Add(new ConditionFloat
            {
                ComparisonValue = 5f,
                CompareOperator = CompareOperator.GreaterThanOrEqualTo,
                Data = new GetCurrentTimeConditionData()
            });
            var musicType = mod.MusicTypes.AddNew("MusicType_Fixture");
            musicType.Tracks = new();
            musicType.Tracks.Add(new FormLink<IMusicTrackGetter>(musicTrack.FormKey));
            var worldspace = mod.Worldspaces.AddNew("Worldspace_Fixture");
            worldspace.Name = "Fixture Worldspace Name";
            worldspace.Music = new FormLinkNullable<IMusicTypeGetter>(musicType.FormKey);

            using (var stream = File.Create(pluginPath))
            {
                mod.WriteToBinary(stream, new BinaryWriteParameters());
            }

            var source = new PluginSource(
                "MusicFixture.esp",
                pluginPath,
                "Music Fixture",
                root.FullName,
                true,
                true,
                1,
                1);
            var records = new PluginRecordScanner().Read(source);

            Assert.NotEmpty(records);
            var worldspaceRecord = Assert.Single(
                records.Where(record => record.RecordType.Contains("World", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(
                worldspaceRecord.References,
                reference => reference.FieldName == "Music" &&
                             reference.FormKey == musicType.FormKey.ToString());
            Assert.Equal("Fixture Worldspace Name", worldspaceRecord.DisplayName);

            Assert.Contains(records, record => record.RecordType == "MusicType");
            var musicTypeRecord = Assert.Single(records.Where(record => record.RecordType == "MusicType"));
            Assert.Contains(
                musicTypeRecord.References,
                reference => reference.FieldName == "Tracks" &&
                             reference.FormKey == musicTrack.FormKey.ToString());
            var musicTrackRecord = Assert.Single(records.Where(record => record.RecordType == "MusicTrack"));
            Assert.Contains(
                musicTrackRecord.Assets,
                asset => asset.FieldName == "TrackFilename" &&
                         asset.VirtualPath == @"music\fixture.xwm");
            var condition = Assert.Single(musicTrackRecord.Conditions);
            Assert.Equal("GetCurrentTime", condition.FunctionName);
            Assert.Equal("GreaterThanOrEqualTo", condition.CompareOperator);
            Assert.Equal(5f, condition.ComparisonValue);
        }
        finally
        {
            try
            {
                root.Delete(true);
            }
            catch (IOException)
            {
                // The temporary fixture can be cleaned by the OS after the test.
            }
        }
    }

    [Fact]
    public void Read_PreservesEditableMusicConditionDataAndFormLinks()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-condition-plugin-");
        try
        {
            var pluginPath = Path.Combine(root.FullName, "ConditionFixture.esp");
            var modKey = ModKey.FromNameAndExtension("ConditionFixture.esp");
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var keyword = mod.Keywords.AddNew("Keyword_Fixture");
            var weather = mod.Weathers.AddNew("Weather_Fixture");
            var playerFormKey = new FormKey(
                ModKey.FromNameAndExtension("Skyrim.esm"),
                0x14);
            var track = mod.MusicTracks.AddNew("MusicTrack_Conditions");
            track.TrackFilename = new();
            track.TrackFilename.TrySetPath(@"music\conditions.xwm");
            track.Conditions = new();
            track.Conditions.Add(new ConditionFloat
            {
                CompareOperator = CompareOperator.GreaterThanOrEqualTo,
                ComparisonValue = 22f,
                Flags = Condition.Flag.OR,
                Data = new GetCurrentTimeConditionData
                {
                    RunOnType = Condition.RunOnType.Subject,
                    RunOnTypeIndex = -1
                }
            });

            var combatData = new GetCombatTargetHasKeywordConditionData
            {
                RunOnType = Condition.RunOnType.Reference,
                RunOnTypeIndex = -1,
                Reference = new FormLink<ISkyrimMajorRecordGetter>(playerFormKey)
            };
            combatData.Keyword = new FormLinkOrIndex<IKeywordGetter>(combatData, keyword.FormKey);
            track.Conditions.Add(new ConditionFloat
            {
                CompareOperator = CompareOperator.EqualTo,
                ComparisonValue = 1f,
                Data = combatData
            });

            var weatherData = new GetIsCurrentWeatherConditionData
            {
                RunOnType = Condition.RunOnType.Subject,
                RunOnTypeIndex = -1
            };
            weatherData.Weather = new FormLinkOrIndex<IWeatherGetter>(weatherData, weather.FormKey);
            track.Conditions.Add(new ConditionFloat
            {
                CompareOperator = CompareOperator.EqualTo,
                ComparisonValue = 0f,
                Data = weatherData
            });

            using (var stream = File.Create(pluginPath))
            {
                mod.WriteToBinary(stream, new BinaryWriteParameters());
            }

            var source = new PluginSource(
                "ConditionFixture.esp",
                pluginPath,
                "Condition Fixture",
                root.FullName,
                true,
                true,
                1,
                1);
            var records = new PluginRecordScanner().Read(source);
            var conditions = Assert.Single(records.Where(record => record.RecordType == "MusicTrack"))
                .Conditions;

            var time = Assert.Single(conditions.Where(condition =>
                condition.FunctionName == "GetCurrentTime"));
            Assert.Equal("OR", time.Flags);
            Assert.Equal("Float", time.ComparisonValueType);
            Assert.Equal("Subject", time.RunOnType);
            Assert.Equal(-1, time.RunOnTypeIndex);

            var combat = Assert.Single(conditions.Where(condition =>
                condition.FunctionName == "GetCombatTargetHasKeyword"));
            Assert.Equal(keyword.FormKey.ToString(), combat.KeywordFormKey);
            Assert.Equal("Reference", combat.RunOnType);
            Assert.Equal(playerFormKey.ToString(), combat.ReferenceFormKey);

            var currentWeather = Assert.Single(conditions.Where(condition =>
                condition.FunctionName == "GetIsCurrentWeather"));
            Assert.Equal(weather.FormKey.ToString(), currentWeather.WeatherFormKey);
            Assert.Equal("Subject", currentWeather.RunOnType);
        }
        finally
        {
            try
            {
                root.Delete(true);
            }
            catch (IOException)
            {
                // The temporary fixture can be cleaned by the OS after the test.
            }
        }
    }

    [Fact]
    public void Read_WithRecordTypeFilter_DoesNotRetainUnrelatedRecords()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-filtered-plugin-");
        try
        {
            var pluginPath = Path.Combine(root.FullName, "FilteredFixture.esp");
            var modKey = ModKey.FromNameAndExtension("FilteredFixture.esp");
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var musicType = mod.MusicTypes.AddNew("MusicType_Fixture");
            musicType.Tracks = new();

            using (var stream = File.Create(pluginPath))
            {
                mod.WriteToBinary(stream, new BinaryWriteParameters());
            }

            var source = new PluginSource(
                "FilteredFixture.esp",
                pluginPath,
                "Filtered Fixture",
                root.FullName,
                true,
                true,
                1,
                1);
            var records = new PluginRecordScanner().Read(
                source,
                includedRecordTypes: new HashSet<string>(
                    new[] { "MusicType" },
                    StringComparer.OrdinalIgnoreCase));

            Assert.Single(records);
            Assert.Equal("MusicType", records[0].RecordType);
        }
        finally
        {
            try
            {
                root.Delete(true);
            }
            catch (IOException)
            {
                // The temporary fixture can be cleaned by the OS after the test.
            }
        }
    }
}
