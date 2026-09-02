namespace GfMusicManager.Core.Generation;

/// <summary>
/// A coarse-grained progress notification for the generation pipeline.
/// The values are intended for UI feedback and logs, not for controlling the
/// generation algorithm.
/// </summary>
public enum MusicGenerationProgressStage
{
    Preparing,
    Validating,
    Resolving,
    Writing,
    Diagnosing,
    Completed
}

public sealed record MusicGenerationProgress(
    MusicGenerationProgressStage Stage,
    string Message,
    double Percent,
    int Current = 0,
    int Total = 0);
