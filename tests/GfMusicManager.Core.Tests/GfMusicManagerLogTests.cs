using GfMusicManager.Core.Diagnostics;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class GfMusicManagerLogTests
{
    [Fact]
    public void WritesMessagesToTheSessionLogFile()
    {
        GfMusicManagerLog.SetFileLoggingEnabled(true);
        try
        {
            var marker = $"logger-self-test-{Guid.NewGuid():N}";

            GfMusicManagerLog.Info(marker);

            Assert.True(File.Exists(GfMusicManagerLog.LogPath));
            using var stream = new FileStream(
                GfMusicManagerLog.LogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            Assert.Contains(marker, reader.ReadToEnd());
        }
        finally
        {
            GfMusicManagerLog.SetFileLoggingEnabled(false);
        }
    }

    [Fact]
    public void DisabledLoggingDoesNotCreateOrAppendToALogFile()
    {
        GfMusicManagerLog.SetFileLoggingEnabled(true);
        var enabledPath = GfMusicManagerLog.LogPath;
        GfMusicManagerLog.Info("before-disable");
        var lengthBeforeDisable = new FileInfo(enabledPath).Length;

        GfMusicManagerLog.SetFileLoggingEnabled(false);
        GfMusicManagerLog.Info("after-disable");

        Assert.False(GfMusicManagerLog.FileLoggingEnabled);
        Assert.Equal(string.Empty, GfMusicManagerLog.LogPath);
        Assert.Equal(lengthBeforeDisable, new FileInfo(enabledPath).Length);
    }
}
