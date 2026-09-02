namespace SkyrimScan.Core.Models;

public enum ScanIssueSeverity
{
    Info,
    Warning,
    Error
}

public enum AssetSourceKind
{
    Loose,
    Bsa
}

public sealed record ScanOptions
{
    public required string Mo2Root { get; init; }
    public string? ProfileName { get; init; }
    public bool IncludeDisabledMods { get; init; }
    public bool ReadPluginRecords { get; init; } = true;

    /// <summary>
    /// Optional record-type filter applied before a plugin record is converted
    /// into the scan model.  Null preserves the general-purpose scanner
    /// behavior and reads every major record; a non-null set prevents
    /// unrelated records from being retained in memory.
    /// </summary>
    public IReadOnlySet<string>? IncludedRecordTypes { get; init; }

    /// <summary>
    /// When true, Cell/Location/Region/Worldspace records are retained only
    /// when their metadata contains a Music or Sounds.Music assignment.
    /// This is intentionally opt-in so the general scanner remains reusable.
    /// </summary>
    public bool RetainOnlyMusicAssignments { get; init; }

    public bool ScanArchives { get; init; } = true;
    public bool ScanLooseAssets { get; init; } = true;
    public IReadOnlySet<string> AssetExtensions { get; init; } = DefaultAssetExtensions;
    /// <summary>
    /// Mod names intentionally excluded by the caller, for example a tool's
    /// own generated output.  The scanner records the exclusion as an Info
    /// issue and does not enumerate that mod's assets or plugins.
    /// </summary>
    public IReadOnlySet<string> ExcludedModNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> DefaultAssetExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".xwm",
            ".wav",
            ".mp3",
            ".ogg",
            ".flac"
        };
}

public sealed record Mo2ProfileSnapshot(
    string Mo2Root,
    string ProfileName,
    string ProfilePath,
    IReadOnlyList<string> ModOrder,
    IReadOnlyDictionary<string, bool> ModEnabled,
    IReadOnlyDictionary<string, int> ModPriority,
    IReadOnlyDictionary<string, int> PluginLoadOrder,
    IReadOnlyDictionary<string, bool> PluginEnabled,
    bool HasModList,
    bool HasLoadOrder,
    bool HasPluginList,
    string? GamePath = null)
{
    public bool IsModEnabled(string modName) =>
        !HasModList || (ModEnabled.TryGetValue(modName, out var enabled) && enabled);

    public bool IsPluginEnabled(string pluginName)
    {
        if (!HasLoadOrder && !HasPluginList)
        {
            return true;
        }

        if (HasLoadOrder && !PluginLoadOrder.ContainsKey(pluginName))
        {
            return false;
        }

        if (PluginEnabled.TryGetValue(pluginName, out var enabled))
        {
            return enabled;
        }

        var extension = Path.GetExtension(pluginName);
        return extension.Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".esl", StringComparison.OrdinalIgnoreCase);
    }

    public int GetPluginLoadOrder(string pluginName) =>
        PluginLoadOrder.TryGetValue(pluginName, out var order) ? order : -1;

    public int GetModPriority(string modName) =>
        ModPriority.TryGetValue(modName, out var priority) ? priority : -1;
}

public sealed record ModSource(
    string Name,
    string Path,
    bool Enabled,
    int Priority,
    IReadOnlyList<string> PluginPaths,
    IReadOnlyList<string> ArchivePaths);

public sealed record PluginSource(
    string Name,
    string Path,
    string ModName,
    string ModPath,
    bool ModEnabled,
    bool Enabled,
    int LoadOrderIndex,
    int ModPriority);

public sealed record PluginRecordReferenceSource(
    string FieldName,
    string FormKey);

public sealed record PluginRecordAssetSource(
    string FieldName,
    string VirtualPath);

/// <summary>
/// A condition attached to a plugin record, kept as a stable inspection model
/// instead of exposing Mutagen's polymorphic condition object graph to callers.
/// </summary>
public sealed record PluginRecordConditionSource(
    string FunctionName,
    string CompareOperator,
    float ComparisonValue,
    string Flags,
    string DataType,
    string DataSummary)
{
    /// <summary>
    /// The FormKey of a Keyword referenced by condition data, when the
    /// underlying condition exposes one.  The scanner keeps this as raw
    /// provenance; callers resolve the record name from the complete scan.
    /// </summary>
    public string? KeywordFormKey { get; init; }

    /// <summary>
    /// The FormKey of a Weather referenced by condition data, when the
    /// underlying condition exposes one.
    /// </summary>
    public string? WeatherFormKey { get; init; }

    /// <summary>
    /// The concrete comparison arm used by the condition. Music-track
    /// conditions normally compare against a float, but the distinction is
    /// retained so a non-float condition is never silently rewritten.
    /// </summary>
    public string ComparisonValueType { get; init; } = "Float";

    public string? ComparisonGlobalFormKey { get; init; }

    /// <summary>
    /// Common CTDA execution metadata. These values affect which reference
    /// the condition function runs against and therefore must survive a
    /// scan/generate round trip.
    /// </summary>
    public string RunOnType { get; init; } = "Subject";

    public int RunOnTypeIndex { get; init; } = -1;

    public string? ReferenceFormKey { get; init; }

    public bool UseAliases { get; init; }

    public bool UsePackageData { get; init; }

    public int? FirstUnusedIntParameter { get; init; }

    public int? SecondUnusedIntParameter { get; init; }

    public string? FirstUnusedStringParameter { get; init; }

    public string? SecondUnusedStringParameter { get; init; }
}

public sealed record PluginRecordSource(
    string FormKey,
    string RecordType,
    string? EditorId,
    bool IsDeleted,
    PluginSource Plugin,
    bool IsWinner = false)
{
    public string? DisplayName { get; init; }

    public IReadOnlyList<PluginRecordReferenceSource> References { get; init; } =
        Array.Empty<PluginRecordReferenceSource>();

    public IReadOnlyList<PluginRecordAssetSource> Assets { get; init; } =
        Array.Empty<PluginRecordAssetSource>();

    public IReadOnlyList<PluginRecordConditionSource> Conditions { get; init; } =
        Array.Empty<PluginRecordConditionSource>();
}

public sealed record PluginRecordMetadataSource(
    IReadOnlyList<PluginRecordReferenceSource> References,
    IReadOnlyList<PluginRecordAssetSource> Assets,
    IReadOnlyList<PluginRecordConditionSource> Conditions);

public sealed record AssetSource(
    string VirtualPath,
    AssetSourceKind SourceKind,
    string ModName,
    string ModPath,
    bool ModEnabled,
    string SourcePath,
    string? ArchiveEntryPath,
    long? Length)
{
    public bool IsFromArchive => SourceKind == AssetSourceKind.Bsa;

    /// <summary>
    /// True when this source is the effective enabled MO2 VFS source for its
    /// virtual path.  A generated mod can reference this source without
    /// copying its bytes; losing or disabled sources must be copied when
    /// adopted.
    /// </summary>
    public bool IsVfsWinner { get; init; }

    public string DisplaySource => IsFromArchive
        ? $"{ModName} / {Path.GetFileName(SourcePath)}"
        : ModName;
}

public sealed record ScanIssue(
    ScanIssueSeverity Severity,
    string Scope,
    string SourcePath,
    string Message,
    string? Detail = null);

public sealed record ScanProgress(
    ScanIssueSeverity Level,
    string Stage,
    string Message,
    int? Current = null,
    int? Total = null,
    string? ModName = null,
    string? SourcePath = null,
    string? PluginName = null);

public sealed record ScanResult(
    Mo2ProfileSnapshot Profile,
    IReadOnlyList<ModSource> Mods,
    IReadOnlyList<PluginSource> Plugins,
    IReadOnlyList<PluginRecordSource> Records,
    IReadOnlyList<AssetSource> Assets,
    IReadOnlyList<ScanIssue> Issues)
{
    public int EnabledModCount => Mods.Count(x => x.Enabled);
    public int EnabledPluginCount => Plugins.Count(x => x.Enabled);
    public int AudioAssetCount => Assets.Count;
    public int BsaAssetCount => Assets.Count(x => x.IsFromArchive);
    public int WarningCount => Issues.Count(x => x.Severity == ScanIssueSeverity.Warning);
    public int ErrorCount => Issues.Count(x => x.Severity == ScanIssueSeverity.Error);
}
