using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;
using GfMusicManager.Desktop;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class AudioDuplicateReviewTests
{
    [Fact]
    public void PathConflictRequiresExactlyOneSelectedSource()
    {
        var first = Asset("Mod A", "music\\shared.xwm", "a.xwm");
        var second = Asset("Mod B", "music\\shared.xwm", "b.xwm");
        var group = new AudioDuplicateGroup(
            "path:shared",
            AudioDuplicateKind.PathConflict,
            "music\\shared.xwm",
            "path conflict",
            "test",
            new[]
            {
                new AudioDuplicateSource(first, "hash-a"),
                new AudioDuplicateSource(second, "hash-b")
            });
        var review = new AudioDuplicateGroupReview(
            group,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            0);

        Assert.True(review.HasValidDecision);
        Assert.Single(review.GetAdoptedAssetKeys());
        review.Sources[0].IsSelected = false;
        review.Sources[1].IsSelected = true;

        Assert.True(review.HasValidDecision);
        Assert.Single(review.GetAdoptedAssetKeys());
        Assert.Contains(review.Sources[1].Source.AssetKey, review.GetAdoptedAssetKeys());
    }

    [Fact]
    public void ContentMatchDefaultsToAdoptedAndAllowsPerSourceExclusion()
    {
        var first = Asset("Mod A", "music\\first.xwm", "a.xwm");
        var second = Asset("Mod B", "music\\second.xwm", "b.xwm");
        var group = new AudioDuplicateGroup(
            "content:shared",
            AudioDuplicateKind.ContentMatch,
            "1234",
            "content match",
            "test",
            new[]
            {
                new AudioDuplicateSource(first, "hash"),
                new AudioDuplicateSource(second, "hash")
            });
        var review = new AudioDuplicateGroupReview(
            group,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            0);

        Assert.True(review.HasValidDecision);
        Assert.Equal(2, review.GetAdoptedAssetKeys().Count);

        review.Sources[0].IsIncluded = false;

        Assert.True(review.HasValidDecision);
        Assert.Single(review.GetAdoptedAssetKeys());

        review.ExcludeAllSources();

        Assert.True(review.HasValidDecision);
        Assert.Empty(review.GetAdoptedAssetKeys());

        review.AdoptAllSources();

        Assert.Equal(2, review.GetAdoptedAssetKeys().Count);
    }

    [Fact]
    public void LoadOrderPreferenceKeepsAllSourcesFromHighestPriorityMod()
    {
        var first = Asset("Mod A", "music\\first.xwm", "a-first.xwm");
        var second = Asset("Mod B", "music\\second.xwm", "b-second.xwm");
        var third = Asset("Mod B", "music\\third.xwm", "b-third.xwm");
        var group = new AudioDuplicateGroup(
            "content:shared",
            AudioDuplicateKind.ContentMatch,
            "1234",
            "content match",
            "test",
            new[]
            {
                new AudioDuplicateSource(first, "hash"),
                new AudioDuplicateSource(second, "hash"),
                new AudioDuplicateSource(third, "hash")
            });
        var review = new AudioDuplicateGroupReview(
            group,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            0,
            modPriorities: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Mod A"] = 10,
                ["Mod B"] = 20
            });

        review.AdoptHighestPriorityMod();

        var adopted = review.GetAdoptedAssetKeys();
        Assert.Equal(2, adopted.Count);
        Assert.DoesNotContain(MusicGenerationPlanEntry.CreateAssetKey(first), adopted);
        Assert.Contains(MusicGenerationPlanEntry.CreateAssetKey(second), adopted);
        Assert.Contains(MusicGenerationPlanEntry.CreateAssetKey(third), adopted);
    }

    [Theory]
    [InlineData(UiLanguage.Japanese)]
    [InlineData(UiLanguage.English)]
    public void PreviewStateExposesPlayAndStopToggleText(string language)
    {
        try
        {
            UiText.SetLanguage(language);
            var asset = Asset("Mod A", "music\\preview.xwm", "preview.xwm");
            var source = new AudioDuplicateSourceReview(
                new AudioDuplicateSource(asset, "hash"),
                "group",
                false,
                true);

            Assert.False(source.IsPreviewActive);
            Assert.Equal(UiText.Get("AudioDuplicate.Preview.Play"), source.PreviewButtonContent);
            Assert.Equal(UiText.Get("AudioDuplicate.Preview.PlayTooltip"), source.PreviewButtonToolTip);

            source.SetPreviewActive(true);

            Assert.True(source.IsPreviewActive);
            Assert.Equal(UiText.Get("AudioDuplicate.Preview.Stop"), source.PreviewButtonContent);
            Assert.Equal(UiText.Get("AudioDuplicate.Preview.StopTooltip"), source.PreviewButtonToolTip);

            source.SetPreviewActive(false);

            Assert.Equal(UiText.Get("AudioDuplicate.Preview.Play"), source.PreviewButtonContent);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }

    private static AssetSource Asset(string modName, string virtualPath, string fileName) =>
        new(
            virtualPath,
            AssetSourceKind.Loose,
            modName,
            @"C:\Fixture\" + modName,
            true,
            @"C:\Fixture\" + modName + "\\" + fileName,
            null,
            1);
}
