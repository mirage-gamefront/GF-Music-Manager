using GfMusicManager.Core.Audio;
using SkyrimScan.Core.Models;
using System.Text;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class XwmaPreviewPlayerTests
{
    [Fact]
    public void PlayReportsThatWavPreviewIsUnsupported()
    {
        using var player = new XwmaPreviewPlayer();
        var asset = new AssetSource(
            "music\\fixture.wav",
            AssetSourceKind.Loose,
            "Fixture Music",
            Path.GetTempPath(),
            true,
            Path.Combine(Path.GetTempPath(), "fixture.wav"),
            null,
            null);

        var exception = Assert.Throws<NotSupportedException>(() => player.Play(asset));

        Assert.Contains("WAV形式は現在の試聴に対応していません。", exception.Message);
        Assert.Contains("試聴できる形式はXWMのみです。", exception.Message);
        Assert.Contains("対象形式：WAV", exception.Message);
    }

    [Fact]
    public void PlayRejectsChunkThatExceedsTheRemainingFileRange()
    {
        var directory = Directory.CreateTempSubdirectory("gf-xwma-invalid-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "invalid.xwm");
            File.WriteAllBytes(path, CreateXwmaWithChunkSize("data", uint.MaxValue));
            var asset = new AssetSource(
                "music\\fixture\\invalid.xwm",
                AssetSourceKind.Loose,
                "Fixture Music",
                directory.FullName,
                true,
                path,
                null,
                new FileInfo(path).Length);

            using var player = new XwmaPreviewPlayer();
            var exception = Assert.Throws<InvalidDataException>(() => player.Play(asset));

            Assert.Contains("ファイル範囲を超えています", exception.Message);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static byte[] CreateXwmaWithChunkSize(string fourCc, uint size)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0u);
        writer.Write(Encoding.ASCII.GetBytes("XWMA"));
        writer.Write(Encoding.ASCII.GetBytes(fourCc));
        writer.Write(size);
        writer.Flush();
        return stream.ToArray();
    }
}
