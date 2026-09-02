using System.Text;
using SkyrimScan.Core.Archives;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Scanning;
using Xunit;

namespace SkyrimScan.Core.Tests;

public sealed class Mo2ScannerTests
{
    [Fact]
    public void Scan_ExcludesDisabledModsByDefaultAndFindsLooseAndBsaMusic()
    {
        using var fixture = new Mo2Fixture();
        var scanner = new Mo2Scanner();

        var result = scanner.Scan(new ScanOptions
        {
            Mo2Root = fixture.Root,
            ReadPluginRecords = false
        });

        Assert.Single(result.Mods);
        Assert.Equal("Enabled Music", result.Mods[0].Name);
        Assert.Equal(2, result.Assets.Count);
        Assert.Contains(result.Assets, asset => asset.SourceKind == AssetSourceKind.Loose);
        Assert.Contains(result.Assets, asset => asset.SourceKind == AssetSourceKind.Bsa);
        Assert.Empty(result.Issues.Where(issue => issue.Severity == ScanIssueSeverity.Error));
    }

    [Fact]
    public void Scan_IncludesDisabledModsWhenRequested()
    {
        using var fixture = new Mo2Fixture();
        var scanner = new Mo2Scanner();

        var result = scanner.Scan(new ScanOptions
        {
            Mo2Root = fixture.Root,
            IncludeDisabledMods = true,
            ReadPluginRecords = false
        });

        Assert.Equal(2, result.Mods.Count);
        Assert.Contains(result.Mods, mod => !mod.Enabled && mod.Name == "Disabled Music");
        Assert.Contains(result.Assets, asset => asset.ModName == "Disabled Music");
    }

    [Fact]
    public void Scan_ExcludesCallerSpecifiedModAndReportsTheExclusion()
    {
        using var fixture = new Mo2Fixture();
        var result = new Mo2Scanner().Scan(new ScanOptions
        {
            Mo2Root = fixture.Root,
            IncludeDisabledMods = true,
            ReadPluginRecords = false,
            ExcludedModNames = new HashSet<string>(
                new[] { "Disabled Music" },
                StringComparer.OrdinalIgnoreCase)
        });

        Assert.Single(result.Mods);
        Assert.DoesNotContain(result.Mods, mod => mod.Name == "Disabled Music");
        Assert.DoesNotContain(result.Assets, asset => asset.ModName == "Disabled Music");
        Assert.Contains(
            result.Issues,
            issue => issue.Severity == ScanIssueSeverity.Info &&
                     issue.Message.Contains("excluded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scan_MarksHighestPriorityEnabledAssetAsVfsWinner()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-vfs-winner-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "mods"));
            var profile = Path.Combine(root.FullName, "profiles", "Main");
            Directory.CreateDirectory(profile);
            foreach (var modName in new[] { "Low Priority", "High Priority" })
            {
                var musicPath = Path.Combine(root.FullName, "mods", modName, "music");
                Directory.CreateDirectory(musicPath);
                File.WriteAllBytes(Path.Combine(musicPath, "same.xwm"), [1, 2, 3]);
            }

            File.WriteAllText(
                Path.Combine(profile, "modlist.txt"),
                "+High Priority\n+Low Priority\n",
                Encoding.UTF8);

            var result = new Mo2Scanner().Scan(new ScanOptions
            {
                Mo2Root = root.FullName,
                ReadPluginRecords = false
            });
            var assets = result.Assets;

            Assert.Equal(2, assets.Count);
            Assert.Equal(1, result.Profile.GetModPriority("High Priority"));
            Assert.Equal(0, result.Profile.GetModPriority("Low Priority"));
            Assert.False(assets.Single(asset => asset.ModName == "Low Priority").IsVfsWinner);
            Assert.True(assets.Single(asset => asset.ModName == "High Priority").IsVfsWinner);
        }
        finally
        {
            try
            {
                Directory.Delete(root.FullName, recursive: true);
            }
            catch (IOException)
            {
                // The temporary fixture can be cleaned by the OS later.
            }
        }
    }

    [Fact]
    public void ScanReportsModProgressWithCurrentAndTotal()
    {
        using var fixture = new Mo2Fixture();
        var progressEntries = new List<ScanProgress>();

        new Mo2Scanner().Scan(
            new ScanOptions
            {
                Mo2Root = fixture.Root,
                ReadPluginRecords = false
            },
            new TestProgress<ScanProgress>(progressEntries.Add));

        Assert.Contains(
            progressEntries,
            entry => entry.Stage == "MOD" && entry.Current == 0 && entry.Total == 1);
        Assert.Contains(
            progressEntries,
            entry => entry.Stage == "MOD" && entry.Current == 1 && entry.Total == 1);
    }

    [Fact]
    public void ScannerRequiresExplicitProfileWhenMultipleProfilesExist()
    {
        using var fixture = new MultiProfileFixture();
        var scanner = new Mo2Scanner();

        Assert.Equal(new[] { "Main", "Testing" }, scanner.GetProfileNames(fixture.Root));
        Assert.Throws<InvalidOperationException>(() => scanner.Scan(new ScanOptions
        {
            Mo2Root = fixture.Root,
            ReadPluginRecords = false
        }));

        var result = scanner.Scan(new ScanOptions
        {
            Mo2Root = fixture.Root,
            ProfileName = "Testing",
            ReadPluginRecords = false
        });

        Assert.Equal("Testing", result.Profile.ProfileName);
    }

    [Fact]
    public void ScanReadsGamePathFromMo2Configuration()
    {
        using var fixture = new Mo2Fixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "ModOrganizer.ini"),
            "[General]\ngamePath=@ByteArray(C:\\\\Games\\\\Skyrim Special Edition)\n",
            Encoding.UTF8);

        var result = new Mo2Scanner().Scan(new ScanOptions
        {
            Mo2Root = fixture.Root,
            ReadPluginRecords = false
        });

        Assert.Equal(
            Path.GetFullPath(@"C:\Games\Skyrim Special Edition"),
            result.Profile.GamePath);
    }

    [Fact]
    public void BsaReader_IndexesAndReadsUncompressedEntry()
    {
        using var fixture = new BsaFixture();
        var reader = new BsaArchiveReader();

        var archive = reader.ReadIndex(fixture.Path);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("music\\archive_track.xwm", entry.VirtualPath);
        Assert.Equal((uint)4, entry.PackedSize);
        Assert.Equal(Encoding.ASCII.GetBytes("test"), reader.ReadEntry(fixture.Path, entry.VirtualPath));
    }

    private sealed class Mo2Fixture : IDisposable
    {
        public Mo2Fixture()
        {
            Root = Directory.CreateTempSubdirectory("gf-music-scan-").FullName;
            var mods = Path.Combine(Root, "mods");
            var profile = Path.Combine(Root, "profiles", "Main");
            Directory.CreateDirectory(mods);
            Directory.CreateDirectory(profile);

            var enabled = Path.Combine(mods, "Enabled Music");
            var disabled = Path.Combine(mods, "Disabled Music");
            Directory.CreateDirectory(Path.Combine(enabled, "music", "forest"));
            Directory.CreateDirectory(Path.Combine(disabled, "music", "disabled"));
            File.WriteAllBytes(Path.Combine(enabled, "music", "forest", "loose_track.xwm"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(disabled, "music", "disabled", "disabled_track.xwm"), [4, 5, 6]);
            new BsaFixture(Path.Combine(enabled, "Enabled Music.bsa"));

            File.WriteAllText(
                Path.Combine(profile, "modlist.txt"),
                "+Enabled Music\n-Disabled Music\n",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(profile, "loadorder.txt"),
                "",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(profile, "plugins.txt"),
                "",
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
                // The test fixture is temporary; a locked file can be cleaned by the OS later.
            }
        }
    }

    private sealed class TestProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class MultiProfileFixture : IDisposable
    {
        public MultiProfileFixture()
        {
            Root = Directory.CreateTempSubdirectory("gf-music-profiles-").FullName;
            Directory.CreateDirectory(Path.Combine(Root, "mods"));
            foreach (var profileName in new[] { "Main", "Testing" })
            {
                var profilePath = Path.Combine(Root, "profiles", profileName);
                Directory.CreateDirectory(profilePath);
                File.WriteAllText(Path.Combine(profilePath, "modlist.txt"), "", Encoding.UTF8);
            }
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
                // The test fixture is temporary; a locked file can be cleaned by the OS later.
            }
        }
    }

    private sealed class BsaFixture : IDisposable
    {
        private static readonly byte[] Payload = Encoding.ASCII.GetBytes("test");

        public BsaFixture(string? path = null)
        {
            Path = path ?? System.IO.Path.Combine(
                Directory.CreateTempSubdirectory("bsa-fixture-").FullName,
                "fixture.bsa");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            WriteArchive(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (directory is not null && Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // The test fixture is temporary; a locked file can be cleaned by the OS later.
            }
        }

        private static void WriteArchive(string path)
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(0x00415342u);
            writer.Write(105u);
            writer.Write(36u);
            writer.Write(0u);
            writer.Write(1u);
            writer.Write(1u);
            writer.Write(6u);
            writer.Write(11u);
            writer.Write(0u);

            writer.Write(0UL);
            writer.Write(1u);
            writer.Write(0u);
            writer.Write(0UL);

            writer.Write((byte)6);
            writer.Write(Encoding.ASCII.GetBytes("music\0"));
            writer.Write(0UL);
            writer.Write((uint)Payload.Length);
            var offsetPosition = stream.Position;
            writer.Write(0u);
            writer.Write(Encoding.ASCII.GetBytes("archive_track.xwm\0"));
            var payloadOffset = checked((uint)stream.Position);
            var endOfNames = stream.Position;
            stream.Position = offsetPosition;
            writer.Write(payloadOffset);
            stream.Position = endOfNames;
            writer.Write(Payload);
        }
    }
}
