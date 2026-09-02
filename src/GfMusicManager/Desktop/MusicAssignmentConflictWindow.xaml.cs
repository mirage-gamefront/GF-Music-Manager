using System.Collections.ObjectModel;
using System.Windows;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;

namespace GfMusicManager.Desktop;

public partial class MusicAssignmentConflictWindow : Window
{
    public MusicAssignmentConflictWindow(
        IReadOnlyList<MusicGenerationPlanConflict> conflicts,
        IReadOnlyList<MusicSettingSource> availableMusicSettings,
        bool keepVanillaMusic,
        bool includeWorldSpaceAssignments = true)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(availableMusicSettings);

        Conflicts = new ObservableCollection<AssignmentConflictView>(
            conflicts
                .Where(conflict =>
                    conflict.Kind == MusicGenerationPlanConflictKind.MultipleGeneratedMusicTypesForRecord &&
                    (includeWorldSpaceAssignments ||
                     conflict.TargetScope != MusicSettingScope.WorldSpace))
                .Select(conflict => AssignmentConflictView.Create(
                    conflict,
                    availableMusicSettings,
                    keepVanillaMusic)));

        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<AssignmentConflictView> Conflicts { get; }

    public string SummaryText => UiText.Format("Assignment.Summary", Conflicts.Count);

    public Visibility EmptyVisibility =>
        Conflicts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}

public sealed class AssignmentConflictView
{
    private AssignmentConflictView(
        string targetText,
        string integratedMusicTypeText,
        string integratedTrackText,
        IReadOnlyList<AssignmentConflictMusicTypeView> assignments)
    {
        TargetText = targetText;
        IntegratedMusicTypeText = integratedMusicTypeText;
        IntegratedTrackText = integratedTrackText;
        Assignments = assignments;
    }

    public string TargetText { get; }
    public string IntegratedMusicTypeText { get; }
    public string IntegratedTrackText { get; }
    public IReadOnlyList<AssignmentConflictMusicTypeView> Assignments { get; }
    public string MusicTypeHeadingText =>
        UiText.Format("Assignment.MusicTypeHeading", Assignments.Count);

    public static AssignmentConflictView Create(
        MusicGenerationPlanConflict conflict,
        IReadOnlyList<MusicSettingSource> availableMusicSettings,
        bool keepVanillaMusic)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(availableMusicSettings);

        var parsedTarget = ParseTarget(conflict.Subject);
        var target = new ConflictTarget(
            conflict.TargetScope ?? parsedTarget.Scope,
            conflict.TargetFormKey ?? parsedTarget.ScopeFormKey);
        var targetSettings = availableMusicSettings
            .Where(setting =>
                setting.Scope == target.Scope &&
                string.Equals(setting.ScopeFormKey, target.ScopeFormKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var targetDisplayName = targetSettings
            .Select(setting => setting.ScopeDisplayName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? target.ScopeFormKey;
        var targetLabel = targetSettings
            .Select(setting => UiText.Get($"Scope.{setting.Scope}"))
            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label)) ??
            UiText.Get($"Scope.{target.Scope}");

        var musicTypeKeys = conflict.Entries
            .SelectMany(entry => entry.DestinationKeys)
            .Where(key =>
                key.Scope == target.Scope &&
                string.Equals(key.ScopeFormKey, target.ScopeFormKey, StringComparison.OrdinalIgnoreCase))
            .GroupBy(key => key.MusicTypeFormKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .ToArray();

        var assignments = musicTypeKeys
            .Select(musicTypeFormKey => CreateMusicTypeView(
                musicTypeFormKey,
                target,
                conflict.Entries,
                targetSettings,
                availableMusicSettings))
            .ToArray();

        var integratedMusicTypeEditorId = BuildIntegrationEditorId(
            target.Scope,
            targetSettings
                .Select(setting => setting.ScopeEditorId)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
            target.ScopeFormKey);
        var sourceMusicTypeKeys = musicTypeKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var officialTrackCount = keepVanillaMusic
            ? availableMusicSettings
                .Where(setting => sourceMusicTypeKeys.Contains(setting.MusicTypeFormKey))
                .SelectMany(setting => setting.Tracks)
                .Where(new OfficialMusicTrackCatalog().IsOfficial)
                .DistinctBy(track => track.FormKey, StringComparer.OrdinalIgnoreCase)
                .Count()
            : 0;
        var generatedTrackCount = conflict.Entries
            .Where(entry => entry.IsAdopted && entry.Asset is not null)
            .DistinctBy(entry => entry.AssetKey, StringComparer.OrdinalIgnoreCase)
            .Count();
        var integratedMusicTypeText = UiText.Format(
            "Assignment.IntegratedMusicType",
            integratedMusicTypeEditorId);
        var integratedTrackText = UiText.Format(
            "Assignment.IntegratedTrack",
            officialTrackCount + generatedTrackCount,
            officialTrackCount,
            generatedTrackCount);

        return new AssignmentConflictView(
            UiText.Format("Assignment.Target", targetLabel, targetDisplayName),
            integratedMusicTypeText,
            integratedTrackText,
            assignments);
    }

    private static AssignmentConflictMusicTypeView CreateMusicTypeView(
        string musicTypeFormKey,
        ConflictTarget target,
        IReadOnlyList<MusicGenerationPlanEntry> entries,
        IReadOnlyList<MusicSettingSource> targetSettings,
        IReadOnlyList<MusicSettingSource> availableMusicSettings)
    {
        var setting = targetSettings
            .Concat(availableMusicSettings)
            .FirstOrDefault(candidate =>
                candidate.Scope == target.Scope &&
                string.Equals(candidate.ScopeFormKey, target.ScopeFormKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.MusicTypeFormKey, musicTypeFormKey, StringComparison.OrdinalIgnoreCase));

        var musicTypeName = setting?.MusicTypeDisplayNameWithoutSuffix ?? musicTypeFormKey;
        var definitionPlugin = setting?.MusicTypeRecord.Plugin.Name ??
                               UiText.Get("Assignment.UnknownDefinition");
        var adoptedEntries = entries
            .Where(entry =>
                entry.IsAdopted &&
                entry.DestinationKeys.Any(key =>
                    key.Scope == target.Scope &&
                    string.Equals(key.ScopeFormKey, target.ScopeFormKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(key.MusicTypeFormKey, musicTypeFormKey, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var sourceMods = adoptedEntries
            .Select(entry => entry.Asset?.ModName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AssignmentConflictMusicTypeView(
            $"{musicTypeName}（{musicTypeFormKey}）",
            UiText.Format("Assignment.Definition", definitionPlugin),
            UiText.Format("Assignment.AdoptedTracks", adoptedEntries.Length),
            sourceMods.Length == 0
                ? UiText.Get("Assignment.SourceModsUnknown")
                : UiText.Format(
                    "Assignment.SourceMods",
                    string.Join(UiText.Get("Common.ListSeparator"), sourceMods)));
    }

    private static ConflictTarget ParseTarget(string subject)
    {
        var separator = subject.IndexOf(':');
        if (separator <= 0 || separator == subject.Length - 1 ||
            !Enum.TryParse<MusicSettingScope>(subject[..separator], out var scope))
        {
            return new ConflictTarget(MusicSettingScope.MusicType, subject);
        }

        return new ConflictTarget(scope, subject[(separator + 1)..]);
    }

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

    private sealed record ConflictTarget(MusicSettingScope Scope, string ScopeFormKey);
}

public sealed record AssignmentConflictMusicTypeView(
    string MusicTypeText,
    string DefinitionText,
    string AdoptedTrackText,
    string SourceModsText);
