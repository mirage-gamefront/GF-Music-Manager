using GfMusicManager.Core.Analysis;
using Mutagen.Bethesda.Plugins;

namespace GfMusicManager.Core.Planning;

/// <summary>
/// Identifies Tracks defined by Skyrim, its DLC, or Creation Club plugins.
/// Physical placement in the game Data directory is not sufficient because
/// unmanaged third-party plugins may be installed there as well.
/// </summary>
public sealed class OfficialMusicTrackCatalog
{
    private static readonly IReadOnlySet<string> BaseGameAndDlcPlugins =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Skyrim.esm",
            "Update.esm",
            "Dawnguard.esm",
            "HearthFires.esm",
            "Dragonborn.esm"
        };

    public bool IsOfficial(MusicTrackSource track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!TryGetDefiningPluginName(track.FormKey, out var pluginName))
        {
            return false;
        }

        return BaseGameAndDlcPlugins.Contains(pluginName) ||
               IsCreationClubPlugin(pluginName);
    }

    private static bool TryGetDefiningPluginName(
        string formKey,
        out string pluginName)
    {
        try
        {
            pluginName = FormKey.Factory(formKey).ModKey.FileName.String;
            return !string.IsNullOrWhiteSpace(pluginName);
        }
        catch
        {
            pluginName = string.Empty;
            return false;
        }
    }

    private static bool IsCreationClubPlugin(string pluginName)
    {
        var extension = Path.GetExtension(pluginName);
        return pluginName.StartsWith("cc", StringComparison.OrdinalIgnoreCase) &&
               (extension.Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".esl", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The final Track sources for one Music Type before the serializer assigns
/// generated FormIDs.  Existing non-official MOD Tracks are intentionally not
/// exposed here.
/// </summary>
public sealed record MusicGenerationMusicTypeTrackSet(
    string MusicTypeFormKey,
    IReadOnlyList<MusicTrackSource> OfficialTracks,
    IReadOnlyList<MusicGenerationPlanEntry> GeneratedEntries)
{
    public IReadOnlyList<string> OfficialTrackFormKeys => OfficialTracks
        .Select(track => track.FormKey)
        .ToArray();

    public int TrackCountBeforeGeneratedFormIds =>
        OfficialTracks.Count + GeneratedEntries.Sum(entry => entry.Tracks.Count);
}

/// <summary>
/// The final Music Type used when one Cell, Location, Region, or WorldSpace
/// receives more than one source Music Type.  Its Track list is materialized as
/// one generated Music Type instead of choosing a load-order winner.
/// </summary>
public sealed record MusicGenerationIntegrationTarget(
    MusicSettingScope Scope,
    string ScopeFormKey,
    string? ScopeEditorId,
    string GeneratedMusicTypeEditorId,
    IReadOnlyList<MusicGenerationMusicTypeTrackSet> SourceMusicTypes,
    IReadOnlyList<MusicGenerationPlanEntry> GeneratedEntries)
{
    public string TargetKey => MusicGenerationPlanResolution.CreateTargetKey(
        Scope,
        ScopeFormKey);

    public IReadOnlyList<MusicTrackSource> OfficialTracks => SourceMusicTypes
        .SelectMany(musicType => musicType.OfficialTracks)
        .DistinctBy(CreateTrackIdentity, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string CreateTrackIdentity(MusicTrackSource track) =>
        string.Join(
            "\u001f",
            track.FormKey,
            track.Record.Plugin.Name,
            track.Record.Plugin.Path);
}

/// <summary>
/// Resolves the user's plan into the Track list that each target Music Type
/// must receive.  This model is deliberately independent from ESP/MTD
/// serialization so the same result can be checked before output is written.
/// </summary>
public sealed class MusicGenerationPlanResolution
{
    private readonly IReadOnlyDictionary<string, MusicGenerationMusicTypeTrackSet>
        _musicTypesByFormKey;
    private readonly IReadOnlyDictionary<string, MusicGenerationIntegrationTarget>
        _integrationTargetsByTargetKey;

    internal MusicGenerationPlanResolution(
        bool keepVanillaMusic,
        IReadOnlyList<MusicGenerationMusicTypeTrackSet> musicTypes,
        IReadOnlyList<MusicGenerationIntegrationTarget> integrationTargets)
    {
        KeepVanillaMusic = keepVanillaMusic;
        MusicTypes = musicTypes;
        IntegrationTargets = integrationTargets;
        _musicTypesByFormKey = musicTypes.ToDictionary(
            musicType => musicType.MusicTypeFormKey,
            StringComparer.OrdinalIgnoreCase);
        _integrationTargetsByTargetKey = integrationTargets.ToDictionary(
            target => target.TargetKey,
            StringComparer.OrdinalIgnoreCase);
    }

    public bool KeepVanillaMusic { get; }

    public IReadOnlyList<MusicGenerationMusicTypeTrackSet> MusicTypes { get; }

    public IReadOnlyList<MusicGenerationIntegrationTarget> IntegrationTargets { get; }

    public bool TryGetMusicType(
        string musicTypeFormKey,
        out MusicGenerationMusicTypeTrackSet musicType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(musicTypeFormKey);
        return _musicTypesByFormKey.TryGetValue(musicTypeFormKey, out musicType!);
    }

    public bool TryGetIntegrationTarget(
        MusicSettingScope scope,
        string scopeFormKey,
        out MusicGenerationIntegrationTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeFormKey);
        return _integrationTargetsByTargetKey.TryGetValue(
            CreateTargetKey(scope, scopeFormKey),
            out target!);
    }

    internal static string CreateTargetKey(
        MusicSettingScope scope,
        string scopeFormKey) => string.Join(
            "\u001f",
            scope,
            scopeFormKey);
}

/// <summary>
/// Builds the final Music Type Track lists from scan candidates and user
/// adoption decisions.
/// </summary>
public sealed class MusicGenerationPlanResolver
{
    private readonly OfficialMusicTrackCatalog _officialTrackCatalog;

    public MusicGenerationPlanResolver(
        OfficialMusicTrackCatalog? officialTrackCatalog = null)
    {
        _officialTrackCatalog = officialTrackCatalog ?? new OfficialMusicTrackCatalog();
    }

    public MusicGenerationPlanResolution Resolve(
        MusicGenerationPlan plan,
        IReadOnlyList<MusicSettingSource> settings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);

        if (plan.KeepVanillaMusic is null)
        {
            throw new InvalidOperationException(
                "Vanilla music policy must be selected before resolving Music Type Tracks.");
        }

        var adoptedEntries = plan.Entries
            .Where(entry => entry.IsAdopted && entry.Asset is not null)
            .ToArray();
        var targetMusicTypeKeys = adoptedEntries
            .SelectMany(entry => entry.DestinationKeys)
            .Select(destination => destination.MusicTypeFormKey)
            .Where(formKey => !string.IsNullOrWhiteSpace(formKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var targetMusicTypeKeySet = targetMusicTypeKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var settingsByMusicType = settings
            .Where(setting => targetMusicTypeKeySet.Contains(setting.MusicTypeFormKey))
            .GroupBy(setting => setting.MusicTypeFormKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MusicSettingSource>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var generatedEntriesByMusicType = new Dictionary<
            string,
            List<MusicGenerationPlanEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in adoptedEntries)
        {
            foreach (var musicTypeFormKey in entry.DestinationKeys
                         .Select(destination => destination.MusicTypeFormKey)
                         .Where(formKey => targetMusicTypeKeySet.Contains(formKey))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!generatedEntriesByMusicType.TryGetValue(
                        musicTypeFormKey,
                        out var entries))
                {
                    entries = new List<MusicGenerationPlanEntry>();
                    generatedEntriesByMusicType.Add(musicTypeFormKey, entries);
                }

                entries.Add(entry);
            }
        }

        var resolutions = new List<MusicGenerationMusicTypeTrackSet>(targetMusicTypeKeys.Length);
        foreach (var musicTypeFormKey in targetMusicTypeKeys)
        {
            settingsByMusicType.TryGetValue(musicTypeFormKey, out var typeSettings);
            typeSettings ??= Array.Empty<MusicSettingSource>();

            var officialTracks = plan.KeepVanillaMusic == true
                ? typeSettings
                    .SelectMany(setting => setting.Tracks)
                    .Where(_officialTrackCatalog.IsOfficial)
                    .DistinctBy(CreateTrackIdentity, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<MusicTrackSource>();

            var generatedEntries = generatedEntriesByMusicType.TryGetValue(
                    musicTypeFormKey,
                    out var entriesForMusicType)
                ? entriesForMusicType
                    .DistinctBy(entry => entry.AssetKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<MusicGenerationPlanEntry>();

            resolutions.Add(new MusicGenerationMusicTypeTrackSet(
                musicTypeFormKey,
                officialTracks,
                generatedEntries));
        }

        var resolvedMusicTypes = resolutions.ToArray();
        var resolvedMusicTypesByFormKey = resolvedMusicTypes.ToDictionary(
            musicType => musicType.MusicTypeFormKey,
            StringComparer.OrdinalIgnoreCase);
        var settingsByScope = settings
            .GroupBy(
                setting => CreateScopeKey(setting.Scope, setting.ScopeFormKey),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => SelectRepresentativeScopeSetting(group),
                StringComparer.OrdinalIgnoreCase);
        var conflicts = plan.Conflicts;
        var integrationTargets = conflicts
            .Where(conflict =>
                conflict.Kind == MusicGenerationPlanConflictKind.MultipleGeneratedMusicTypesForRecord &&
                conflict.TargetScope is not null &&
                !string.IsNullOrWhiteSpace(conflict.TargetFormKey))
            .Select(conflict =>
            {
                var scope = conflict.TargetScope!.Value;
                var scopeFormKey = conflict.TargetFormKey!;
                var sourceMusicTypeKeys = conflict.Entries
                    .SelectMany(entry => entry.DestinationKeys)
                    .Where(destination =>
                        destination.Scope == scope &&
                        destination.ScopeFormKey.Equals(
                            scopeFormKey,
                            StringComparison.OrdinalIgnoreCase))
                    .Select(destination => destination.MusicTypeFormKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var sourceMusicTypes = sourceMusicTypeKeys
                    .Where(resolvedMusicTypesByFormKey.ContainsKey)
                    .Select(key => resolvedMusicTypesByFormKey[key])
                    .ToArray();
                settingsByScope.TryGetValue(
                    CreateScopeKey(scope, scopeFormKey),
                    out var targetSetting);
                var scopeName = string.IsNullOrWhiteSpace(targetSetting?.ScopeEditorId)
                    ? scopeFormKey
                    : targetSetting!.ScopeEditorId!;
                var generatedEntries = conflict.Entries
                    .Where(entry => entry.IsAdopted && entry.Asset is not null)
                    .DistinctBy(entry => entry.AssetKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new MusicGenerationIntegrationTarget(
                    scope,
                    scopeFormKey,
                    targetSetting?.ScopeEditorId,
                    BuildIntegrationEditorId(scope, scopeName),
                    sourceMusicTypes,
                    generatedEntries);
            })
            .ToArray();

        return new MusicGenerationPlanResolution(
            plan.KeepVanillaMusic.Value,
            resolvedMusicTypes,
            integrationTargets);
    }

    private static string CreateTrackIdentity(MusicTrackSource track) =>
        string.Join(
            "\u001f",
            track.FormKey,
            track.Record.Plugin.Name,
            track.Record.Plugin.Path);

    private static string CreateScopeKey(
        MusicSettingScope scope,
        string scopeFormKey) => string.Join(
            "\u001f",
            scope,
            scopeFormKey);

    private static MusicSettingSource SelectRepresentativeScopeSetting(
        IEnumerable<MusicSettingSource> candidates) =>
        candidates
            .OrderByDescending(setting => setting.Record.IsWinner)
            .ThenByDescending(setting => setting.Record.Plugin.LoadOrderIndex)
            .ThenByDescending(setting => setting.Record.Plugin.ModPriority)
            .ThenBy(setting => setting.Record.Plugin.Name, StringComparer.OrdinalIgnoreCase)
            .First();

    private static string BuildIntegrationEditorId(
        MusicSettingScope scope,
        string value)
    {
        var safe = new string(value
            .Where(character => char.IsLetterOrDigit(character) || character == '_')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "Generated";
        }

        return $"GFITG_{GetScopeCode(scope)}_{safe}";
    }

    private static string GetScopeCode(MusicSettingScope scope) => scope switch
    {
        MusicSettingScope.Cell => "C",
        MusicSettingScope.Location => "L",
        MusicSettingScope.Region => "R",
        MusicSettingScope.WorldSpace => "W",
        MusicSettingScope.MusicType => "M",
        _ => scope.ToString()
    };
}
