using SkyrimScan.Core.Scanning;

namespace GfMusicManager.Application;

public sealed record MusicMo2ApplicationOptions
{
    public required string Mo2Root { get; init; }

    public required string ProfileName { get; init; }

    public string GeneratedModName { get; init; } = "GF Music Product";

    public IReadOnlyList<string> GeneratedPluginNames { get; init; } = Array.Empty<string>();

    public bool EnableGeneratedMod { get; init; } = true;

    public IReadOnlyList<string> SourcePluginNames { get; init; } = Array.Empty<string>();

    public bool DisableSourcePlugins { get; init; }
}

public sealed class MusicMo2ApplicationService
{
    private readonly Mo2ProfileStateWriter _writer;
    private readonly IMo2RunningInstanceRefresher _runningInstanceRefresher;

    public MusicMo2ApplicationService(
        Mo2ProfileStateWriter? writer = null,
        IMo2RunningInstanceRefresher? runningInstanceRefresher = null)
    {
        _writer = writer ?? new Mo2ProfileStateWriter();
        _runningInstanceRefresher = runningInstanceRefresher ??
            new Mo2RunningInstanceRefresher();
    }

    public Mo2ProfileStateChangeResult Apply(MusicMo2ApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = _writer.Apply(
            options.Mo2Root,
            options.ProfileName,
            options.GeneratedModName,
            options.GeneratedPluginNames,
            options.EnableGeneratedMod,
            options.SourcePluginNames,
            options.DisableSourcePlugins);

        // MO2 owns an in-memory copy of mod/plugin state while it is running.
        // Always refresh that copy after applying the desired state, even when
        // the files already matched. Otherwise a stale MO2 session can later
        // overwrite plugins.txt and silently disable the generated plugin.
        _runningInstanceRefresher.RefreshIfRunning(
            options.Mo2Root,
            options.ProfileName);

        return result;
    }
}

