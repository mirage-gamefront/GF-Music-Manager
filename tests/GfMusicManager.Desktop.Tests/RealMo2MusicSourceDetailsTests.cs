using System.IO;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Desktop;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Scanning;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class RealMo2MusicSourceDetailsTests
{
    [Fact]
    [Trait("Category", "RealMo2Audit")]
    public void ActualMo2Profile_TrackGroupsMatchTheirCommonIdentity()
    {
        var mo2Root = Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_MO2_ROOT");
        if (string.IsNullOrWhiteSpace(mo2Root) || !Directory.Exists(mo2Root))
        {
            Console.WriteLine(
                "REAL_MO2_SOURCE_DETAILS_NOT_RUN: GF_MUSIC_AUDIT_MO2_ROOT was not provided.");
            return;
        }

        var mo2RootPath = mo2Root!;

        var profileName = Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_PROFILE");
        var includeDisabled = bool.TryParse(
            Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_INCLUDE_DISABLED"),
            out var parsedIncludeDisabled) && parsedIncludeDisabled;
        var scan = new Mo2Scanner().Scan(new ScanOptions
        {
            Mo2Root = mo2RootPath,
            ProfileName = profileName,
            IncludeDisabledMods = includeDisabled,
            ReadPluginRecords = true,
            ScanArchives = true,
            ScanLooseAssets = true,
            ExcludedModNames = new HashSet<string>(new[] { "GF Music Product" }, StringComparer.OrdinalIgnoreCase)
        });
        var analysis = new MusicSettingsAnalyzer().Analyze(scan);
        var rows = scan.Assets
            .Select(asset => TrackRow.FromAsset(asset, analysis))
            .ToArray();

        var summaries = new List<string>
        {
            "AssetVirtualPath\tTrackReferences\tUniqueTrackDefinitions\tGroupedTracks\tCollapsedGroups\tGroup\tAudioPaths\tConditions\tSourceESP"
        };
        var mappedRows = 0;
        var trackReferenceCount = 0;
        var uniqueTrackDefinitionCount = 0;
        var groupedTrackCount = 0;
        var collapsedGroupCount = 0;

        foreach (var row in rows)
        {
            var matchingTracks = row.SourceMusicSettings
                .SelectMany(setting => setting.Tracks)
                .Where(track => track.MatchesAudioPath(row.Asset!.VirtualPath))
                .ToArray();
            var groups = MusicSourceDetailsWindow.BuildMusicTrackGroups(row);
            if (groups.Count == 0)
            {
                Assert.Empty(matchingTracks);
                continue;
            }

            mappedRows++;
            var uniqueMatchingTracks = matchingTracks
                .GroupBy(MusicTrackIdentity.CreateDefinitionIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var orderedMatchingGroups = matchingTracks
                .GroupBy(MusicTrackIdentity.Create, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.ToArray())
                .OrderBy(group => group[0].EditorId ?? group[0].FormKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group[0].MatchingAudioPaths.Count == 0
                    ? string.Empty
                    : string.Join("、", group[0].MatchingAudioPaths), StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => MusicConditionFormatter.FormatTrackConditions(group[0].Conditions), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.Equal(orderedMatchingGroups.Length, groups.Count);
            Assert.Equal(uniqueMatchingTracks.Length, groups.Sum(group => group.SourceDetails.Count));
            trackReferenceCount += matchingTracks.Length;
            uniqueTrackDefinitionCount += uniqueMatchingTracks.Length;
            groupedTrackCount += groups.Count;
            collapsedGroupCount += groups.Count(group => group.SourceDetails.Count > 1);

            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                var matchingGroup = orderedMatchingGroups[groupIndex];
                var representative = group.SourceDetails[0];
                Assert.All(group.SourceDetails, detail =>
                {
                    Assert.Equal(representative.DisplayText, detail.DisplayText);
                    Assert.Equal(representative.AudioText, detail.AudioText);
                    Assert.Equal(representative.ConditionsText, detail.ConditionsText);
                });

                summaries.Add(string.Join(
                    '\t',
                    row.AudioPath,
                    matchingGroup.Length,
                    group.SourceDetails.Count,
                    1,
                    group.SourceDetails.Count > 1 ? 1 : 0,
                    group.DisplayText,
                    group.AudioText,
                    group.ConditionsText,
                    string.Join("、", group.SourcePluginNames)));
            }
        }

        Assert.NotEmpty(rows);
        Assert.True(mappedRows > 0);
        Assert.True(groupedTrackCount > 0);
        Assert.True(collapsedGroupCount > 0);
        Assert.True(groupedTrackCount <= uniqueTrackDefinitionCount);

        var outputDirectory = Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_OUTPUT");
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllLines(
                Path.Combine(outputDirectory, "source-track-groups.tsv"),
                summaries);
        }

        Console.WriteLine(
            $"SOURCE_TRACK_GROUPS assets={rows.Length} mappedRows={mappedRows} " +
            $"trackReferences={trackReferenceCount} uniqueTrackDefinitions={uniqueTrackDefinitionCount} " +
            $"groupedTracks={groupedTrackCount} " +
            $"collapsedGroups={collapsedGroupCount}");
    }
}
