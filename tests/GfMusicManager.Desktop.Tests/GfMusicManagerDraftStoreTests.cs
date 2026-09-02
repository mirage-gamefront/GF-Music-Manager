using System.IO;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;
using GfMusicManager.Desktop;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class GfMusicManagerDraftStoreTests
{
    [Fact]
    public void SaveLoadAndDelete_UsesProfileScopedAtomicDraft()
    {
        var baseDirectory = Path.Combine(
            Path.GetTempPath(),
            "GfMusicManagerDraftTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDirectory);

        try
        {
            var store = new GfMusicManagerDraftStore(baseDirectory);
            var condition = new MusicConditionSource(
                "GetCurrentTime",
                "GreaterThanOrEqualTo",
                18f,
                string.Empty,
                "GetCurrentTimeConditionData",
                string.Empty);
            var destination = new MusicSettingKey(
                MusicSettingScope.MusicType,
                "000001:Skyrim.esm",
                "000002:Skyrim.esm");
            var firstTrackCondition = MusicConditionSource.CreateCurrentTime(
                6,
                "GreaterThanOrEqualTo");
            var secondTrackCondition = MusicConditionSource.CreateCurrentTime(
                22,
                "GreaterThanOrEqualTo");
            var draft = new GfMusicManagerDraft(
                GfMusicManagerDraftStore.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                @"X:\TestData\MO2",
                "MusicProfile",
                true,
                true,
                false,
                new[]
                {
                    new GfMusicManagerDraftEntry(
                        "asset-key",
                        true,
                        new[] { destination },
                        new[] { condition })
                    {
                        Tracks = new[]
                        {
                            new GfMusicManagerDraftTrack("track-morning", new[] { firstTrackCondition }),
                            new GfMusicManagerDraftTrack("track-night", new[] { secondTrackCondition })
                        }
                    }
                });

            Assert.True(store.Save(draft));
            var path = store.GetDraftPath(draft.Mo2Root, draft.ProfileName);
            Assert.True(File.Exists(path));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(baseDirectory),
                file => file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

            var loaded = store.Load(draft.Mo2Root, draft.ProfileName);
            Assert.NotNull(loaded);
            Assert.Equal("MusicProfile", loaded!.ProfileName);
            Assert.Equal(true, loaded.KeepVanillaMusic);
            Assert.True(loaded.CreateWorldSpaceMusicSettings);
            Assert.False(loaded.DisableSourceEsp);
            Assert.Single(loaded.Entries);
            Assert.Equal(destination, Assert.Single(loaded.Entries[0].DestinationKeys));
            Assert.Equal(condition, Assert.Single(loaded.Entries[0].Conditions));
            Assert.Equal(
                new[] { "track-morning", "track-night" },
                loaded.Entries[0].Tracks.Select(track => track.TrackKey));
            Assert.Equal(
                firstTrackCondition,
                Assert.Single(loaded.Entries[0].Tracks[0].Conditions));
            Assert.Equal(
                secondTrackCondition,
                Assert.Single(loaded.Entries[0].Tracks[1].Conditions));

            Assert.True(store.Delete(draft.Mo2Root, draft.ProfileName));
            Assert.False(File.Exists(path));
            Assert.Null(store.Load(draft.Mo2Root, draft.ProfileName));
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, recursive: true);
            }
        }
    }
}
