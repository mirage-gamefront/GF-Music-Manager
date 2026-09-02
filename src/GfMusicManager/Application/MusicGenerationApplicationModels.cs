using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Planning;

namespace GfMusicManager.Application;

/// <summary>
/// Options for generation when the caller already has a scan result and an
/// editable generation plan.  No WPF or MO2 state mutation belongs here.
/// </summary>
public sealed record MusicGenerationApplicationOptions
{
    public string? OutputModDirectory { get; init; }

    public MusicGenerationOutputMode OutputMode { get; init; } =
        MusicGenerationOutputMode.Normal;

    public string DfgPackageName { get; init; } = "GF Music Manager DFG";

    public bool OverwriteExisting { get; init; }

    public bool WorldSpaceIndividualAssignment { get; init; }

    public IReadOnlySet<string> SelectedWorldSpaceFormKeys { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When omitted, names are discovered from enabled MO2 mods in the scan
    /// result.  Supplying the list is useful for deterministic callers and
    /// tests.
    /// </summary>
    public IReadOnlyList<string>? ExistingMtdFileNames { get; init; }

    public MusicGenerationCapacityPolicy CapacityPolicy { get; init; } =
        MusicGenerationCapacityPolicy.CurrentAe;

    public IProgress<MusicGenerationProgress>? Progress { get; init; }
}

/// <summary>
/// Options for DFG package output.  The output directory is explicit so a CLI
/// caller cannot accidentally place a package into the active MO2 mod.
/// </summary>
public sealed record DfgMusicGenerationApplicationOptions
{
    public required string OutputModDirectory { get; init; }

    public string PackageName { get; init; } = "GF Music Manager DFG";

    public bool OverwriteExisting { get; init; }

    public bool WorldSpaceIndividualAssignment { get; init; }

    public IReadOnlySet<string> SelectedWorldSpaceFormKeys { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string>? ExistingMtdFileNames { get; init; }

    public MusicGenerationCapacityPolicy CapacityPolicy { get; init; } =
        MusicGenerationCapacityPolicy.CurrentAe;

    public IProgress<MusicGenerationProgress>? Progress { get; init; }
}

public sealed record MusicGenerationApplicationValidationResult(
    bool IsValid,
    string OutputModDirectory,
    int EntryCount,
    int AdoptedEntryCount,
    int TrackCount,
    int IntegrationTargetCount,
    int ConflictCount,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record MusicGenerationApplicationResult(
    MusicGenerationPlan Plan,
    MusicPlanApplyResult PlanApplication,
    MusicGenerationOutputResult Output);

public sealed record DfgMusicGenerationApplicationResult(
    MusicGenerationPlan Plan,
    MusicPlanApplyResult PlanApplication,
    DfgMusicGenerationOutputResult Output);

public sealed record MusicGenerationOutputValidationResult(
    bool IsValid,
    string OutputModDirectory,
    string ManifestPath,
    MusicGenerationDiagnosticResult Diagnostic);

public sealed class MusicGenerationApplicationException : InvalidOperationException
{
    public MusicGenerationApplicationException(string message)
        : base(message)
    {
    }
}
