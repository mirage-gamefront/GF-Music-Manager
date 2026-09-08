using System.Diagnostics;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Localization;

namespace GfMusicManager.Application;

public interface IMo2RunningInstanceRefresher
{
    void RefreshIfRunning(string mo2Root, string profileName);
}

/// <summary>
/// Synchronizes an already-running MO2 process with profile files changed by
/// GF Music Manager. MO2's refresh command is forwarded to its primary
/// process and is equivalent to pressing F5.
/// </summary>
public sealed class Mo2RunningInstanceRefresher : IMo2RunningInstanceRefresher
{
    private const string ExecutableFileName = "ModOrganizer.exe";
    private const int RefreshCommandTimeoutMilliseconds = 10_000;

    public void RefreshIfRunning(string mo2Root, string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mo2Root);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        var executablePath = Path.GetFullPath(
            Path.Combine(mo2Root, ExecutableFileName));
        if (!File.Exists(executablePath) ||
            !IsProcessRunningFrom(executablePath))
        {
            GfMusicManagerLog.Info(UiText.Get("Mo2.Refresh.NotRunning"));
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add(profileName);
        startInfo.ArgumentList.Add("refresh");

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException(
                UiText.Get("Mo2.Refresh.StartFailed"));
        if (!process.WaitForExit(RefreshCommandTimeoutMilliseconds))
        {
            throw new TimeoutException(
                UiText.Get("Mo2.Refresh.Timeout"));
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                UiText.Format("Mo2.Refresh.Failed", process.ExitCode));
        }

        GfMusicManagerLog.Info(UiText.Format(
            "Mo2.Refresh.Forwarded",
            profileName));
    }

    private static bool IsProcessRunningFrom(string executablePath)
    {
        var expectedPath = Path.GetFullPath(executablePath);
        foreach (var process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(expectedPath)))
        {
            using (process)
            {
                try
                {
                    var runningPath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(runningPath) &&
                        Path.GetFullPath(runningPath).Equals(
                            expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException)
                {
                    // A process that cannot be inspected is not safe to target.
                }
            }
        }

        return false;
    }
}
