using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;

namespace GfMusicManager.Desktop;

internal static class MusicTrackIdentity
{
    public static string CreateDefinitionIdentity(MusicTrackSource track)
        => MusicGenerationTrackKey.CreateDefinitionIdentity(track);

    public static string Create(MusicTrackSource track)
        => MusicGenerationTrackKey.Create(track);
}
