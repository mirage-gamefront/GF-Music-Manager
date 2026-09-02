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
    private readonly Mo2ProfileStateWriter _writer = new();

    public Mo2ProfileStateChangeResult Apply(MusicMo2ApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _writer.Apply(
            options.Mo2Root,
            options.ProfileName,
            options.GeneratedModName,
            options.GeneratedPluginNames,
            options.EnableGeneratedMod,
            options.SourcePluginNames,
            options.DisableSourcePlugins);
    }
}

