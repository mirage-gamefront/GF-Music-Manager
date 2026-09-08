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
    public void ReviewGuidance_PresentsSharedActionBeforePageSpecificMeaning()
    {
        try
        {
            UiText.SetLanguage(UiLanguage.Japanese);

            Assert.Equal(
                "確認が必要な音源があります。不要な音源だけチェックを外してください。\n元のMODは変更されません。",
                UiText.Get("AudioDuplicate.Description"));
            Assert.Equal(
                "複数の音源が同じゲーム内パスを使用しています。\nGF Music Managerでは競合を回避してそれぞれ採用できます。不要な音源がある場合だけチェックを外してください。",
                UiText.Get("AudioDuplicate.Page.PathConflict"));
            Assert.Equal(
                "ゲーム内パスは異なりますが、内容が完全に同じ音源です。\nそのまま両方採用することもできます。重複を減らしたい場合は、残す音源を選んでください。",
                UiText.Get("AudioDuplicate.Page.ContentMatch"));
            Assert.Equal(
                "ゲーム内パスは異なりますが、内容がほぼ同じ音源です。\n同じ曲の別バージョンなどの可能性があるため、試聴して残す音源を確認してください。",
                UiText.Get("AudioDuplicate.Page.Similar"));

            Assert.DoesNotContain("判定方法", UiText.Get("AudioDuplicate.Page.PathConflict"));
            Assert.DoesNotContain("SHA-256", UiText.Get("AudioDuplicate.Page.ContentMatch"));
            Assert.DoesNotContain("特徴量", UiText.Get("AudioDuplicate.Page.Similar"));
            Assert.Contains("ゲーム内音源パス", UiText.Get("AudioDuplicate.Detection.PathConflict"));
            Assert.Contains("SHA-256", UiText.Get("AudioDuplicate.Detection.ContentMatch"));
            Assert.Contains("自動除外は行いません", UiText.Get("AudioDuplicate.Detection.Similar"));
            Assert.Equal("ロード順優先を残す", UiText.Get("AudioDuplicate.Batch.Prefer"));
            Assert.Equal("すべて採用", UiText.Get("AudioDuplicate.Batch.AdoptAll"));
            Assert.Equal("すべて除外", UiText.Get("AudioDuplicate.Batch.ExcludeAll"));
            Assert.Equal("選択を反映", UiText.Get("AudioDuplicate.ApplyButton"));
            Assert.DoesNotContain(
                "チェックあり",
                string.Join(
                    "\n",
                    UiText.Get("AudioDuplicate.Description"),
                    UiText.Get("AudioDuplicate.Page.PathConflict"),
                    UiText.Get("AudioDuplicate.Page.ContentMatch"),
                    UiText.Get("AudioDuplicate.Page.Similar")));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }

    [Fact]
    public void PathConflictDefaultsToAllSourcesAndAllowsMultipleOrNoSources()
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
            new HashSet<string>(
                new[]
                {
                    MusicGenerationPlanEntry.CreateAssetKey(first),
                    MusicGenerationPlanEntry.CreateAssetKey(second)
                },
                StringComparer.OrdinalIgnoreCase),
            0);

        Assert.All(review.Sources, source => Assert.True(source.IsIncluded));
        Assert.All(review.Sources, source => Assert.Equal(System.Windows.Visibility.Visible, source.CheckBoxVisibility));
        Assert.All(review.Sources, source => Assert.Equal(System.Windows.Visibility.Collapsed, source.RadioVisibility));
        Assert.True(review.HasValidDecision);
        Assert.Equal("パス競合・2音源", review.HeaderText);
        Assert.Equal(System.Windows.Visibility.Visible, review.SubjectVisibility);
        Assert.Equal(2, review.GetAdoptedAssetKeys().Count);

        review.Sources[0].IsIncluded = false;
        Assert.True(review.HasValidDecision);
        Assert.Single(review.GetAdoptedAssetKeys());
        Assert.Contains(review.Sources[1].Source.AssetKey, review.GetAdoptedAssetKeys());

        review.Sources[1].IsIncluded = false;
        Assert.True(review.HasValidDecision);
        Assert.Empty(review.GetAdoptedAssetKeys());
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
        Assert.Equal(System.Windows.Visibility.Collapsed, review.SubjectVisibility);
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

    [Fact]
    public void SimilarCandidateBatchActionsRemainAvailable()
    {
        var first = Asset("Mod A", "music\\first.xwm", "a-first.xwm");
        var second = Asset("Mod B", "music\\second.xwm", "b-second.xwm");
        var third = Asset("Mod B", "music\\third.xwm", "b-third.xwm");
        var group = new AudioDuplicateGroup(
            "similar:shared",
            AudioDuplicateKind.SimilarCandidate,
            "1234",
            "similar candidate",
            "test",
            new[]
            {
                new AudioDuplicateSource(first, "hash-a"),
                new AudioDuplicateSource(second, "hash-b"),
                new AudioDuplicateSource(third, "hash-c")
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
        Assert.Equal(2, review.GetAdoptedAssetKeys().Count);
        Assert.DoesNotContain(MusicGenerationPlanEntry.CreateAssetKey(first), review.GetAdoptedAssetKeys());

        review.ExcludeAllSources();
        Assert.Empty(review.GetAdoptedAssetKeys());

        review.AdoptAllSources();
        Assert.Equal(3, review.GetAdoptedAssetKeys().Count);
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
