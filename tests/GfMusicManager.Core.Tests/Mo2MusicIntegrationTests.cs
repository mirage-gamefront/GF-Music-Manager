using System.Text;
using GfMusicManager.Core.Analysis;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Scanning;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class Mo2MusicIntegrationTests
{
    [Fact]
    public void ScanIncludesGameDataMusicRecordsForModAudio()
    {
        using var fixture = new MusicMo2Fixture();
        var result = new Mo2Scanner().Scan(new ScanOptions
        {
            Mo2Root = fixture.Root,
            ProfileName = "Main",
            ReadPluginRecords = true,
            ScanArchives = false,
            ScanLooseAssets = true
        });
        var analysis = new MusicSettingsAnalyzer().Analyze(result);

        Assert.Contains(result.Records, record => record.Plugin.Name == "Skyrim.esm");
        Assert.Contains(
            analysis.Settings,
            setting => setting.Scope == MusicSettingScope.WorldSpace);
        Assert.Contains(
            analysis.GetSettingsForAsset(@"music\fixture.xwm"),
            setting => setting.Scope == MusicSettingScope.WorldSpace);
    }

    private sealed class MusicMo2Fixture : IDisposable
    {
        public MusicMo2Fixture()
        {
            Root = Directory.CreateTempSubdirectory("gf-music-integration-").FullName;
            var gameData = Path.Combine(Root, "Game", "Data");
            var modPath = Path.Combine(Root, "mods", "Music Fixture");
            var profilePath = Path.Combine(Root, "profiles", "Main");
            Directory.CreateDirectory(gameData);
            Directory.CreateDirectory(Path.Combine(modPath, "music"));
            Directory.CreateDirectory(profilePath);

            WriteBasePlugin(Path.Combine(gameData, "Skyrim.esm"));
            WriteEmptyPlugin(Path.Combine(modPath, "MusicFixture.esp"));
            File.WriteAllBytes(Path.Combine(modPath, "music", "fixture.xwm"), [1, 2, 3]);

            File.WriteAllText(
                Path.Combine(Root, "ModOrganizer.ini"),
                $"[General]{Environment.NewLine}gamePath=@ByteArray({EscapeQtPath(Path.Combine(Root, "Game"))}){Environment.NewLine}",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(profilePath, "modlist.txt"), "+Music Fixture\n", Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(profilePath, "loadorder.txt"),
                "Skyrim.esm\nMusicFixture.esp\n",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(profilePath, "plugins.txt"),
                "*Skyrim.esm\n*MusicFixture.esp\n",
                Encoding.UTF8);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // The temporary fixture can be cleaned by the OS later.
            }
        }

        private static void WriteBasePlugin(string path)
        {
            var mod = new SkyrimMod(
                ModKey.FromNameAndExtension("Skyrim.esm"),
                SkyrimRelease.SkyrimSE);
            var track = mod.MusicTracks.AddNew("MusicTrack_Fixture");
            track.TrackFilename = new();
            track.TrackFilename.TrySetPath(@"Data\music\fixture.wav");
            var musicType = mod.MusicTypes.AddNew("MusicType_Fixture");
            musicType.Tracks = new();
            musicType.Tracks.Add(new FormLink<IMusicTrackGetter>(track.FormKey));
            var worldspace = mod.Worldspaces.AddNew("Worldspace_Fixture");
            worldspace.Music = new FormLinkNullable<IMusicTypeGetter>(musicType.FormKey);

            using var stream = File.Create(path);
            mod.WriteToBinary(stream, new BinaryWriteParameters());
        }

        private static void WriteEmptyPlugin(string path)
        {
            var mod = new SkyrimMod(
                ModKey.FromNameAndExtension("MusicFixture.esp"),
                SkyrimRelease.SkyrimSE);
            using var stream = File.Create(path);
            mod.WriteToBinary(stream, new BinaryWriteParameters());
        }

        private static string EscapeQtPath(string path) => path.Replace("\\", "\\\\");
    }
}
