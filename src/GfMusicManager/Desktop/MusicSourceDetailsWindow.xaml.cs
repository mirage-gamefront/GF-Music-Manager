using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Desktop;

public partial class MusicSourceDetailsWindow : Window, INotifyPropertyChanged
{
    private readonly TrackRow _track;
    private readonly IReadOnlyList<MusicSettingSource> _allMusicTypeSettings;
    private readonly Dictionary<string, IReadOnlyList<MusicConditionSource>> _conditionDrafts =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MusicConditionSource> _preservedHiddenConditions =
        Array.Empty<MusicConditionSource>();
    private MusicTrackDetailGroup? _selectedMusicTrackGroup;

    public MusicSourceDetailsWindow(TrackRow track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _track = track;
        SelectedMusicTrackGroups = BuildMusicTrackGroups(track);
        foreach (var group in SelectedMusicTrackGroups)
        {
            _conditionDrafts[group.SelectionKey] =
                track.GetMusicTrackConditions(group.SelectionKey) ?? group.Conditions;
        }
        SelectedMusicTrackGroup = SelectedMusicTrackGroups.FirstOrDefault();
        // The editable generated Music Track is the source of truth after Save.
        // The selected source Track is only the context for choosing which record
        // the user is inspecting; reopening must not revert to the scan snapshot.
        RebuildConditionEditor(GetInitialConditionEditorConditions(track));
        CurrentMusicTypes = BuildMusicTypeGroups(track.SourceMusicSettings, track.DefinitionConflicts);
        _allMusicTypeSettings = BuildMusicTypeRepresentatives(track);

        foreach (var setting in _allMusicTypeSettings.Where(setting =>
                     track.MusicSettings.Any(current =>
                         current.MusicTypeFormKey.Equals(setting.MusicTypeFormKey, StringComparison.OrdinalIgnoreCase))))
        {
            MusicTypeOptions.Add(new MusicTypeOption(setting, track.DefinitionConflicts));
        }

        GfMusicManagerLog.Info(
            $"MusicSourceDetailsWindow: begin. title={track.Title}, " +
            $"sourceSettings={track.SourceMusicSettings.Count}, generatedSettings={track.MusicSettings.Count}, " +
            $"musicTypeCandidates={_allMusicTypeSettings.Count}, " +
            $"combatKeywordCandidates={track.AvailableKeywordRecords.Count}, " +
            $"weatherCandidates={track.AvailableWeatherRecords.Count}, " +
            $"conditions={track.MusicConditions.Count}.");

        InitializeComponent();
        DataContext = this;
        UpdateEditorStatus();

        GfMusicManagerLog.Info(
            $"MusicSourceDetailsWindow: ready. selectedMusicTypes={MusicTypeOptions.Count}, " +
            $"groups={ConditionGroups.Count}, conditionRows={ConditionRowCount}, " +
            $"selectedMusicTrackGroups={SelectedMusicTrackGroups.Count}, " +
            $"selectedMusicTrack={(SelectedMusicTrack is null ? "none" : SelectedMusicTrack.DisplayText)}.");
    }

    public string TrackTitle => _track.Title;
    public string SourceText => UiText.Format("SourceDetails.Source", _track.Source);
    public string AudioText => UiText.Format("SourceDetails.AudioPath", _track.AudioPath);
    public string AssetStateText => _track.Asset is null
        ? UiText.Get("SourceDetails.Asset.NoInfo")
        : _track.Asset.IsFromArchive
            ? UiText.Get("SourceDetails.Asset.Bsa")
            : UiText.Get("SourceDetails.Asset.Loose");
    public string AssetPathText => _track.Asset is null
        ? UiText.Format("SourceDetails.Asset.StoredFrom", "—")
        : _track.Asset.IsFromArchive
            ? UiText.Format("SourceDetails.Asset.StoredFrom", Path.GetFileName(_track.Asset.SourcePath))
            : UiText.Get("SourceDetails.Asset.StoredFromLoose");
    public string AssetPathToolTipText => _track.Asset is null
        ? "—"
        : _track.Asset.IsFromArchive
            ? $"{_track.Asset.SourcePath} :: {_track.Asset.ArchiveEntryPath}"
            : _track.Asset.SourcePath;
    public MusicTrackDetail? SelectedMusicTrack => SelectedMusicTrackGroup?.Representative;
    public IReadOnlyList<MusicTrackDetailGroup> SelectedMusicTrackGroups { get; }
    public MusicTrackDetailGroup? SelectedMusicTrackGroup
    {
        get => _selectedMusicTrackGroup;
        set
        {
            if (ReferenceEquals(_selectedMusicTrackGroup, value))
            {
                return;
            }

            SaveCurrentConditionDraft();
            if (_selectedMusicTrackGroup is not null)
            {
                _selectedMusicTrackGroup.IsSelected = false;
            }

            _selectedMusicTrackGroup = value;
            if (_selectedMusicTrackGroup is not null)
            {
                _selectedMusicTrackGroup.IsSelected = true;
                var conditions = _conditionDrafts.TryGetValue(
                        _selectedMusicTrackGroup.SelectionKey,
                        out var draft)
                    ? draft
                    : _selectedMusicTrackGroup.Conditions;
                RebuildConditionEditor(conditions);
            }
            else
            {
                RebuildConditionEditor(Array.Empty<MusicConditionSource>());
            }

            GfMusicManagerLog.Info(
                $"MusicSourceDetailsWindow.TrackSelection: title={_track.Title}, " +
                $"track={_selectedMusicTrackGroup?.DisplayText ?? "<none>"}, " +
                $"conditions={_selectedMusicTrackGroup?.Conditions.Count ?? 0}.");
            OnPropertyChanged(nameof(SelectedMusicTrackGroup));
            OnPropertyChanged(nameof(SelectedMusicTrack));
            OnPropertyChanged(nameof(HasSelectedMusicTrack));
            OnPropertyChanged(nameof(SelectedMusicTrackEditorHeaderText));
            OnPropertyChanged(nameof(SelectedMusicTrackEditorSummaryText));
            OnPropertyChanged(nameof(SelectedMusicTrackText));
            OnPropertyChanged(nameof(SelectedMusicTrackAudioText));
            OnPropertyChanged(nameof(SelectedMusicTrackDefinitionText));
            OnPropertyChanged(nameof(SelectedMusicTrackTechnicalText));
        }
    }

    public bool HasSelectedMusicTrack => SelectedMusicTrackGroup is not null;
    public string SelectedMusicTrackHeaderText =>
        UiText.Format("SourceDetails.TrackGroupHeader", SelectedMusicTrackGroups.Count);
    public Visibility SelectedMusicTrackEmptyVisibility =>
        SelectedMusicTrackGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string SelectedMusicTrackText =>
        SelectedMusicTrack?.DisplayText ?? UiText.Get("SourceDetails.NotAnalyzed");
    public string SelectedMusicTrackAudioText =>
        SelectedMusicTrack?.AudioText ?? UiText.Get("SourceDetails.NotAnalyzedAudio");
    public string SelectedMusicTrackDefinitionText =>
        SelectedMusicTrack?.DefinitionText ?? UiText.Get("SourceDetails.NotAnalyzedDefinition");
    public string SelectedMusicTrackTechnicalText => SelectedMusicTrack?.TechnicalText ?? "";
    public string SelectedMusicTrackEditorHeaderText => SelectedMusicTrack is null
        ? UiText.Get("SourceDetails.EditorTargetEmpty")
        : UiText.Format("SourceDetails.EditorTarget", SelectedMusicTrack.DisplayText);
    public string SelectedMusicTrackEditorSummaryText => SelectedMusicTrack is null
        ? UiText.Get("SourceDetails.EditorSummaryEmpty")
        : UiText.Format("SourceDetails.EditorSummary", SelectedMusicTrack.AudioText, SelectedMusicTrack.ConditionsText);
    public IReadOnlyList<MusicTypeGroupDetail> CurrentMusicTypes { get; }
    public IReadOnlyList<MusicDefinitionConflict> DefinitionConflicts => _track.DefinitionConflicts;
    public ObservableCollection<MusicTypeOption> MusicTypeOptions { get; } = new();
    public ObservableCollection<ConditionGroupEditor> ConditionGroups { get; } = new();
    public string EditorStatusText { get; private set; } =
        UiText.Get("SourceDetails.EditorStatusInitial");
    public string ConditionInstructionText => UiText.Get("SourceDetails.ConditionInstruction");

    private int ConditionRowCount =>
        ConditionGroups.Sum(group => group.Kind == ConditionGroupKind.Time
            ? group.SelectedTimeRange is null || group.SelectedTimeRange.IsNone ? 0 : 2
            : group.Rows.Count);

    private void RebuildConditionEditor(IReadOnlyList<MusicConditionSource> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        _preservedHiddenConditions = conditions
            .Where(condition => !condition.IsEditable)
            .ToArray();

        foreach (var group in ConditionGroups)
        {
            group.PropertyChanged -= Group_PropertyChanged;
        }

        ConditionGroups.Clear();
        foreach (var group in BuildConditionGroups(conditions))
        {
            AttachGroup(group);
            ConditionGroups.Add(group);
        }

        UpdateEditorStatus();
    }

    private void SaveCurrentConditionDraft()
    {
        if (_selectedMusicTrackGroup is null || ConditionGroups.Count == 0)
        {
            return;
        }

        _conditionDrafts[_selectedMusicTrackGroup.SelectionKey] = BuildConditionsForSave();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static IReadOnlyList<MusicTypeGroupDetail> BuildMusicTypeGroups(
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<MusicDefinitionConflict> conflicts) =>
        settings
            .GroupBy(setting => setting.MusicTypeFormKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MusicTypeGroupDetail(group.ToArray(), conflicts))
            .ToArray();

    internal static IReadOnlyList<MusicTrackDetailGroup> BuildMusicTrackGroups(TrackRow track)
    {
        ArgumentNullException.ThrowIfNull(track);

        var selectedVirtualPath = track.Asset?.VirtualPath;
        if (string.IsNullOrWhiteSpace(selectedVirtualPath))
        {
            return Array.Empty<MusicTrackDetailGroup>();
        }

        return track.SourceMusicSettings
            .SelectMany(setting => setting.Tracks)
            .Where(musicTrack => musicTrack.MatchesAudioPath(selectedVirtualPath))
            .GroupBy(MusicTrackIdentity.Create, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MusicTrackDetailGroup(group.ToArray(), track.DefinitionConflicts))
            .OrderBy(group => group.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.AudioText, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.ConditionsText, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<MusicConditionSource> GetInitialConditionEditorConditions(
        TrackRow track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var firstGroup = BuildMusicTrackGroups(track).FirstOrDefault();
        return firstGroup is null
            ? track.MusicConditions
            : track.GetMusicTrackConditions(firstGroup.SelectionKey) ?? firstGroup.Conditions;
    }

    private static IReadOnlyList<MusicSettingSource> BuildMusicTypeRepresentatives(TrackRow track)
    {
        var settings = track.AvailableMusicSettings
            .Concat(track.MusicSettings)
            .ToArray();
        return settings
            .GroupBy(setting => setting.MusicTypeFormKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var representative = group
                    .Where(setting => setting.Scope == MusicSettingScope.MusicType)
                    .OrderByDescending(setting => setting.MusicTypeRecord.IsWinner)
                    .ThenByDescending(setting => setting.MusicTypeRecord.Plugin.LoadOrderIndex)
                    .ThenBy(setting => setting.MusicTypeRecord.Plugin.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()
                    ?? group
                        .OrderByDescending(setting => setting.MusicTypeRecord.IsWinner)
                        .ThenByDescending(setting => setting.MusicTypeRecord.Plugin.LoadOrderIndex)
                        .ThenBy(setting => setting.MusicTypeRecord.Plugin.Name, StringComparer.OrdinalIgnoreCase)
                        .First();
                return representative.Scope == MusicSettingScope.MusicType
                    ? representative
                    : CreateMusicTypeRepresentative(representative);
            })
            .OrderBy(setting => setting.MusicTypeDisplayNameWithoutSuffix, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MusicSettingSource CreateMusicTypeRepresentative(MusicSettingSource setting) =>
        new(
            MusicSettingScope.MusicType,
            setting.MusicTypeFormKey,
            setting.MusicTypeEditorId,
            setting.MusicTypeFormKey,
            setting.MusicTypeEditorId,
            setting.MusicTypeRecord,
            setting.MusicTypeRecord,
            setting.Tracks);

    private static IReadOnlyList<ConditionGroupEditor> BuildConditionGroups(
        IReadOnlyList<MusicConditionSource> conditions)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ConditionGroupEditor>();

        var timeConditions = conditions
            .Where(condition => condition.FunctionName.Equals("GetCurrentTime", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var timeGroup = BuildTimeGroup(timeConditions, used);
        groups.Add(timeGroup);

        var combatGroup = new ConditionGroupEditor(
            ConditionGroupKind.CombatKeyword,
            UiText.Get("SourceDetails.ConditionGroup.CombatTitle"),
            UiText.Get("SourceDetails.ConditionGroup.CombatDescription"),
            canChooseLogic: true);
        foreach (var condition in conditions.Where(condition =>
                     condition.FunctionName.Equals("GetCombatTargetHasKeyword", StringComparison.OrdinalIgnoreCase)))
        {
            combatGroup.AddRow(new ConditionEditorRow(condition, ConditionRowChoice.Keyword));
            used.Add(MusicConditionFormatter.CreateKey(condition));
            if (condition.Flags.Contains("OR", StringComparison.OrdinalIgnoreCase))
            {
                combatGroup.SelectedLogic = UiText.Get("SourceDetails.Condition.LogicOr");
            }
        }
        groups.Add(combatGroup);

        var weatherGroup = new ConditionGroupEditor(
            ConditionGroupKind.Weather,
            UiText.Get("SourceDetails.ConditionGroup.WeatherTitle"),
            UiText.Get("SourceDetails.ConditionGroup.WeatherDescription"),
            canChooseLogic: true);
        foreach (var condition in conditions.Where(condition =>
                     condition.FunctionName.Equals("GetIsCurrentWeather", StringComparison.OrdinalIgnoreCase)))
        {
            weatherGroup.AddRow(new ConditionEditorRow(condition, ConditionRowChoice.Weather));
            used.Add(MusicConditionFormatter.CreateKey(condition));
            if (condition.Flags.Contains("OR", StringComparison.OrdinalIgnoreCase))
            {
                weatherGroup.SelectedLogic = UiText.Get("SourceDetails.Condition.LogicOr");
            }
        }
        groups.Add(weatherGroup);

        return groups;
    }

    private static ConditionGroupEditor BuildTimeGroup(
        IReadOnlyList<MusicConditionSource> conditions,
        ISet<string> used)
    {
        var options = TimeRangeOption.CreatePresets().ToList();
        TimeRangeOption? selected = null;
        if (conditions.Count > 0)
        {
            var start = conditions.FirstOrDefault(condition =>
                condition.CompareOperator.Equals("GreaterThanOrEqualTo", StringComparison.OrdinalIgnoreCase) ||
                condition.CompareOperator.Equals("GreaterThan", StringComparison.OrdinalIgnoreCase));
            var end = conditions.FirstOrDefault(condition =>
                condition.CompareOperator.Equals("LessThanOrEqualTo", StringComparison.OrdinalIgnoreCase) ||
                condition.CompareOperator.Equals("LessThan", StringComparison.OrdinalIgnoreCase));

            if (start is not null && end is not null)
            {
                selected = options.FirstOrDefault(option =>
                    option.StartHour.HasValue && option.EndHour.HasValue &&
                    NearlyEquals(option.StartHour.Value, start.ComparisonValue) &&
                    NearlyEquals(option.EndHour.Value, end.ComparisonValue));
                selected ??= TimeRangeOption.Custom(
                    MusicConditionFormatter.FormatTimeRange(start.ComparisonValue, end.ComparisonValue),
                    new[] { start, end });
                used.Add(MusicConditionFormatter.CreateKey(start));
                used.Add(MusicConditionFormatter.CreateKey(end));
            }
            else
            {
                var condition = conditions[0];
                selected = TimeRangeOption.Custom(
                    UiText.Format(
                        "SourceDetails.Time.Custom",
                        MusicConditionFormatter.FormatWithoutCategory(condition)),
                    new[] { condition });
                used.Add(MusicConditionFormatter.CreateKey(condition));
            }
        }

        if (selected is not null && selected.IsCustom)
        {
            options.Add(selected);
        }
        else if (selected is null)
        {
            selected = options[0];
        }

        return new ConditionGroupEditor(
            ConditionGroupKind.Time,
            UiText.Get("SourceDetails.ConditionGroup.TimeTitle"),
            UiText.Get("SourceDetails.ConditionGroup.TimeDescription"),
            canChooseLogic: false,
            options,
            selected);
    }

    private void AttachGroup(ConditionGroupEditor group)
    {
        group.PropertyChanged += Group_PropertyChanged;
    }

    private void Group_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ConditionGroupEditor group)
        {
            if (e.PropertyName == nameof(ConditionGroupEditor.SelectedLogic))
            {
                GfMusicManagerLog.Info(
                    $"MusicSourceDetailsWindow.ConditionLogic: kind={group.Kind}, logic={group.SelectedLogic}.");
            }
            else if (e.PropertyName == nameof(ConditionGroupEditor.SelectedTimeRange) &&
                     group.SelectedTimeRange is not null)
            {
                GfMusicManagerLog.Info(
                    $"MusicSourceDetailsWindow.TimeRange: value={group.SelectedTimeRange.DisplayText}.");
            }
            else if (e.PropertyName == nameof(ConditionGroupEditor.RowCount))
            {
                GfMusicManagerLog.Info(
                    $"MusicSourceDetailsWindow.ConditionRows: kind={group.Kind}, count={group.Rows.Count}.");
            }
        }

        UpdateEditorStatus();
    }

    private void AddConditionButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ConditionGroupEditor group)
        {
            return;
        }

        if (group.Kind == ConditionGroupKind.CombatKeyword)
        {
            var options = _track.AvailableKeywordRecords
                .Select(record => new MusicCandidateOption(record))
                .Where(option => group.Rows.All(row =>
                    !string.Equals(row.Condition.KeywordFormKey, option.Key, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            AddConditionFromPicker(
                group,
                UiText.Get("Candidate.CombatTitle"),
                UiText.Get("Candidate.CombatPrompt"),
                options);
            return;
        }

        if (group.Kind == ConditionGroupKind.Weather)
        {
            var options = _track.AvailableWeatherRecords
                .Select(record => new MusicCandidateOption(record))
                .Where(option => group.Rows.All(row =>
                    !string.Equals(row.Condition.WeatherFormKey, option.Key, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            AddConditionFromPicker(
                group,
                UiText.Get("Candidate.WeatherTitle"),
                UiText.Get("Candidate.WeatherPrompt"),
                options);
        }
    }

    private void AddConditionFromPicker(
        ConditionGroupEditor group,
        string title,
        string prompt,
        IReadOnlyList<MusicCandidateOption> options)
    {
        if (options.Count == 0)
        {
            EditorStatusText = UiText.Get("SourceDetails.NoCandidates");
            OnPropertyChanged(nameof(EditorStatusText));
            GfMusicManagerLog.Info($"MusicSourceDetailsWindow.AddCondition: no candidates. kind={group.Kind}.");
            return;
        }

        var picker = new MusicCandidatePickerWindow(title, prompt, options) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedOption is null)
        {
            GfMusicManagerLog.Info($"MusicSourceDetailsWindow.AddCondition: cancelled. kind={group.Kind}.");
            return;
        }

        if (group.Kind == ConditionGroupKind.CombatKeyword && picker.SelectedOption.Record is not null)
        {
            group.AddRow(new ConditionEditorRow(
                MusicConditionSource.CreateCombatKeyword(picker.SelectedOption.Record, hasKeyword: true),
                ConditionRowChoice.Keyword));
        }
        else if (group.Kind == ConditionGroupKind.Weather && picker.SelectedOption.Record is not null)
        {
            group.AddRow(new ConditionEditorRow(
                MusicConditionSource.CreateCurrentWeather(picker.SelectedOption.Record, matches: true),
                ConditionRowChoice.Weather));
        }

        GfMusicManagerLog.Info(
            $"MusicSourceDetailsWindow.AddCondition: added. kind={group.Kind}, key={picker.SelectedOption.Key}.");
        UpdateEditorStatus();
    }

    private void RemoveConditionRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ConditionEditorRow row)
        {
            return;
        }

        var group = ConditionGroups.FirstOrDefault(candidate => candidate.Rows.Contains(row));
        if (group is null)
        {
            return;
        }

        group.RemoveRow(row);
        GfMusicManagerLog.Info(
            $"MusicSourceDetailsWindow.RemoveCondition: kind={group.Kind}, condition={row.DisplayText}.");
        UpdateEditorStatus();
    }

    private void AddMusicTypeButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedKeys = MusicTypeOptions
            .Select(option => option.Setting.MusicTypeFormKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = _allMusicTypeSettings
            .Where(setting => !selectedKeys.Contains(setting.MusicTypeFormKey))
            .Select(setting => new MusicCandidateOption(setting, _track.DefinitionConflicts))
            .ToArray();
        if (options.Length == 0)
        {
            EditorStatusText = UiText.Get("SourceDetails.NoMusicTypes");
            OnPropertyChanged(nameof(EditorStatusText));
            GfMusicManagerLog.Info("MusicSourceDetailsWindow.AddMusicType: no candidates.");
            return;
        }

        var picker = new MusicCandidatePickerWindow(
            UiText.Get("Candidate.MusicTypeTitle"),
            UiText.Get("Candidate.MusicTypePrompt"),
            options)
        {
            Owner = this
        };
        if (picker.ShowDialog() != true || picker.SelectedOption?.Setting is null)
        {
            GfMusicManagerLog.Info("MusicSourceDetailsWindow.AddMusicType: cancelled.");
            return;
        }

        MusicTypeOptions.Add(new MusicTypeOption(picker.SelectedOption.Setting));
        GfMusicManagerLog.Info(
            $"MusicSourceDetailsWindow.AddMusicType: added. key={picker.SelectedOption.Key}.");
        UpdateEditorStatus();
    }

    private void OpenMusicTypeManagementButton_Click(object sender, RoutedEventArgs e)
    {
        if (_track.AvailableMusicSettings.Count == 0)
        {
            GfMusicManagerLog.Warning(
                $"MusicSourceDetailsWindow.OpenMusicTypeManagement: no Music Type data. title={_track.Title}.");
            return;
        }

        var initialMusicTypeFormKey = (sender as FrameworkElement)?.Tag as string;
        GfMusicManagerLog.Info(
            $"MusicSourceDetailsWindow.OpenMusicTypeManagement: opening Type management. " +
            $"title={_track.Title}, settings={_track.AvailableMusicSettings.Count}, " +
            $"initialType={initialMusicTypeFormKey ?? "<none>"}.");
        var window = new MusicTypeManagementWindow(
            _track.AvailableMusicSettings,
            _track.DefinitionConflicts,
            initialMusicTypeFormKey)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void RemoveMusicTypeButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MusicTypeOption option)
        {
            return;
        }

        MusicTypeOptions.Remove(option);
        GfMusicManagerLog.Info(
            $"MusicSourceDetailsWindow.RemoveMusicType: removed. key={option.Setting.MusicTypeFormKey}.");
        UpdateEditorStatus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var destinations = MusicTypeOptions.Select(option => option.Setting).ToArray();
        SaveCurrentConditionDraft();
        var editedTracks = SelectedMusicTrackGroups
            .Select(group => new MusicGenerationTrackPlan(
                group.SelectionKey,
                _conditionDrafts.TryGetValue(group.SelectionKey, out var draft)
                    ? draft
                    : group.Conditions))
            .ToArray();
        GfMusicManagerLog.Info(
            $"MusicSourceDetailsWindow.Save: title={_track.Title}, musicTypes={destinations.Length}, " +
            $"tracks={editedTracks.Length}, conditions={editedTracks.Sum(track => track.Conditions.Count)}, " +
            $"groups={ConditionGroups.Count}, " +
            $"selectedTrack={SelectedMusicTrack?.DisplayText ?? "<none>"}.");
        _track.ReplaceMusicSettings(destinations);
        _track.ReplaceMusicTrackConditions(editedTracks);
        DialogResult = true;
    }

    private IReadOnlyList<MusicConditionSource> BuildConditionsForSave()
    {
        var conditions = new List<MusicConditionSource>(_preservedHiddenConditions);
        foreach (var group in ConditionGroups)
        {
            switch (group.Kind)
            {
                case ConditionGroupKind.Time:
                    if (group.SelectedTimeRange is not null && !group.SelectedTimeRange.IsNone)
                    {
                        if (group.SelectedTimeRange.IsCustom && group.SelectedTimeRange.OriginalConditions.Count > 0)
                        {
                            conditions.AddRange(group.SelectedTimeRange.OriginalConditions);
                        }
                        else if (group.SelectedTimeRange.StartHour is { } start &&
                                 group.SelectedTimeRange.EndHour is { } end)
                        {
                            var timeFlags = start > end ? "OR" : string.Empty;
                            conditions.Add(MusicConditionSource.CreateCurrentTime(
                                start,
                                "GreaterThanOrEqualTo",
                                timeFlags));
                            conditions.Add(MusicConditionSource.CreateCurrentTime(
                                end,
                                "LessThanOrEqualTo",
                                timeFlags));
                        }
                    }
                    break;
                case ConditionGroupKind.CombatKeyword:
                case ConditionGroupKind.Weather:
                    var flags = group.IsOr ? "OR" : string.Empty;
                    conditions.AddRange(group.Rows.Select(row => row.Condition with { Flags = flags }));
                    break;
                case ConditionGroupKind.Other:
                    conditions.AddRange(group.Rows.Select(row => row.Condition));
                    break;
            }
        }

        return conditions
            .DistinctBy(MusicConditionFormatter.CreateKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        GfMusicManagerLog.Info($"MusicSourceDetailsWindow.Cancel: title={_track.Title}.");
        DialogResult = false;
    }

    private void UpdateEditorStatus()
    {
        EditorStatusText = UiText.Format(
            "SourceDetails.EditorStatusSummary",
            MusicTypeOptions.Count,
            ConditionRowCount);
        OnPropertyChanged(nameof(EditorStatusText));
        OnPropertyChanged(nameof(ConditionSummaryText));
    }

    public string ConditionSummaryText =>
        UiText.Format(
            "SourceDetails.ConditionSummary",
            HasSelectedTimeRange
                ? UiText.Get("SourceDetails.ConditionTimeSet")
                : UiText.Get("SourceDetails.ConditionTimeNone"),
            GetGroup(ConditionGroupKind.CombatKeyword)?.Rows.Count ?? 0,
            GetGroup(ConditionGroupKind.Weather)?.Rows.Count ?? 0);

    private bool HasSelectedTimeRange =>
        GetGroup(ConditionGroupKind.Time)?.SelectedTimeRange is { IsNone: false };

    private ConditionGroupEditor? GetGroup(ConditionGroupKind kind) =>
        ConditionGroups.FirstOrDefault(group => group.Kind == kind);

    private static bool NearlyEquals(float left, float right) => MathF.Abs(left - right) < 0.01f;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum ConditionGroupKind
{
    Time,
    CombatKeyword,
    Weather,
    Other
}

public enum ConditionRowChoice
{
    None,
    Keyword,
    Weather
}

public sealed class ConditionGroupEditor : INotifyPropertyChanged
{
    private string _selectedLogic = UiText.Get("SourceDetails.Condition.LogicAnd");
    private TimeRangeOption? _selectedTimeRange;

    public ConditionGroupEditor(
        ConditionGroupKind kind,
        string title,
        string description,
        bool canChooseLogic,
        IReadOnlyList<TimeRangeOption>? timeRangeOptions = null,
        TimeRangeOption? selectedTimeRange = null)
    {
        Kind = kind;
        Title = title;
        Description = description;
        CanChooseLogic = canChooseLogic;
        TimeRangeOptions = timeRangeOptions ?? Array.Empty<TimeRangeOption>();
        _selectedTimeRange = selectedTimeRange;
    }

    public ConditionGroupKind Kind { get; }
    public string Title { get; }
    public string Description { get; }
    public bool CanChooseLogic { get; }
    public bool IsTimeGroup => Kind == ConditionGroupKind.Time;
    public bool IsChoiceGroup => Kind is ConditionGroupKind.CombatKeyword or ConditionGroupKind.Weather;
    public bool IsOtherGroup => Kind == ConditionGroupKind.Other;
    public string AddButtonText => Kind switch
    {
        ConditionGroupKind.CombatKeyword => UiText.Get("SourceDetails.Condition.AddKeyword"),
        ConditionGroupKind.Weather => UiText.Get("SourceDetails.Condition.AddWeather"),
        _ => string.Empty
    };
    public Visibility LogicVisibility => CanChooseLogic ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AddButtonVisibility => IsChoiceGroup ? Visibility.Visible : Visibility.Collapsed;
    public IReadOnlyList<string> LogicOptions { get; } = new[]
    {
        UiText.Get("SourceDetails.Condition.LogicAnd"),
        UiText.Get("SourceDetails.Condition.LogicOr")
    };
    public ObservableCollection<ConditionEditorRow> Rows { get; } = new();
    public IReadOnlyList<TimeRangeOption> TimeRangeOptions { get; }
    public int RowCount => Rows.Count;
    public bool IsOr => string.Equals(
        _selectedLogic,
        UiText.Get("SourceDetails.Condition.LogicOr"),
        StringComparison.Ordinal);

    public string SelectedLogic
    {
        get => _selectedLogic;
        set
        {
            if (string.Equals(_selectedLogic, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedLogic = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOr));
        }
    }

    public TimeRangeOption? SelectedTimeRange
    {
        get => _selectedTimeRange;
        set
        {
            if (ReferenceEquals(_selectedTimeRange, value))
            {
                return;
            }

            _selectedTimeRange = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RowCount));
        }
    }

    public void AddRow(ConditionEditorRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.PropertyChanged += Row_PropertyChanged;
        Rows.Add(row);
        OnPropertyChanged(nameof(RowCount));
    }

    public void RemoveRow(ConditionEditorRow row)
    {
        if (!Rows.Remove(row))
        {
            return;
        }

        row.PropertyChanged -= Row_PropertyChanged;
        OnPropertyChanged(nameof(RowCount));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(RowCount));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ConditionEditorRow : INotifyPropertyChanged
{
    private MusicConditionSource _condition;
    private string? _selectedChoice;

    public ConditionEditorRow(MusicConditionSource condition, ConditionRowChoice choice)
    {
        _condition = condition;
        Choice = choice;
        ChoiceOptions = choice switch
        {
            ConditionRowChoice.Keyword => new[]
            {
                UiText.Get("SourceDetails.Condition.Has"),
                UiText.Get("SourceDetails.Condition.NotHas")
            },
            ConditionRowChoice.Weather => new[]
            {
                UiText.Get("SourceDetails.Condition.Match"),
                UiText.Get("SourceDetails.Condition.NotMatch")
            },
            _ => Array.Empty<string>()
        };
        _selectedChoice = choice switch
        {
            ConditionRowChoice.Keyword => condition.ComparisonValue != 0
                ? UiText.Get("SourceDetails.Condition.Has")
                : UiText.Get("SourceDetails.Condition.NotHas"),
            ConditionRowChoice.Weather => condition.ComparisonValue != 0
                ? UiText.Get("SourceDetails.Condition.Match")
                : UiText.Get("SourceDetails.Condition.NotMatch"),
            _ => null
        };
    }

    public ConditionRowChoice Choice { get; }
    public MusicConditionSource Condition => _condition;
    public IReadOnlyList<string> ChoiceOptions { get; }
    public bool ShowChoice => ChoiceOptions.Count > 0;
    public string DisplayText => MusicConditionFormatter.Format(_condition);
    public string DetailText => Choice switch
    {
        ConditionRowChoice.Keyword => UiText.Get("SourceDetails.Condition.CombatDetail"),
        ConditionRowChoice.Weather => UiText.Get("SourceDetails.Condition.WeatherDetail"),
        _ => MusicConditionFormatter.FormatTechnical(_condition)
    };
    public string TechnicalText => MusicConditionFormatter.FormatTechnical(_condition);

    public string? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (string.Equals(_selectedChoice, value, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            _selectedChoice = value;
            var isTrue = Choice == ConditionRowChoice.Keyword
                ? string.Equals(
                    value,
                    UiText.Get("SourceDetails.Condition.Has"),
                    StringComparison.Ordinal)
                : string.Equals(
                    value,
                    UiText.Get("SourceDetails.Condition.Match"),
                    StringComparison.Ordinal);
            _condition = _condition with
            {
                CompareOperator = "EqualTo",
                ComparisonValue = isTrue ? 1f : 0f
            };
            OnPropertyChanged(nameof(SelectedChoice));
            OnPropertyChanged(nameof(Condition));
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(TechnicalText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class TimeRangeOption
{
    private TimeRangeOption(
        string key,
        string displayText,
        float? startHour,
        float? endHour,
        bool isCustom,
        IReadOnlyList<MusicConditionSource>? originalConditions)
    {
        Key = key;
        DisplayText = displayText;
        StartHour = startHour;
        EndHour = endHour;
        IsCustom = isCustom;
        OriginalConditions = originalConditions ?? Array.Empty<MusicConditionSource>();
    }

    public string Key { get; }
    public string DisplayText { get; }
    public float? StartHour { get; }
    public float? EndHour { get; }
    public bool IsCustom { get; }
    public bool IsNone => Key.Equals("none", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<MusicConditionSource> OriginalConditions { get; }

    public static IReadOnlyList<TimeRangeOption> CreatePresets() => new[]
    {
        new TimeRangeOption("none", UiText.Get("SourceDetails.Time.None"), null, null, false, null),
        new TimeRangeOption("morning", UiText.Get("SourceDetails.Time.Morning"), 5f, 8f, false, null),
        new TimeRangeOption("day", UiText.Get("SourceDetails.Time.Day"), 8f, 18f, false, null),
        new TimeRangeOption("evening", UiText.Get("SourceDetails.Time.Evening"), 18f, 22f, false, null),
        new TimeRangeOption("night", UiText.Get("SourceDetails.Time.Night"), 22f, 5f, false, null),
        new TimeRangeOption("daytime", UiText.Get("SourceDetails.Time.Daytime"), 5f, 22f, false, null)
    };

    public static TimeRangeOption Custom(
        string displayText,
        IReadOnlyList<MusicConditionSource> originalConditions) =>
        new("custom", displayText, null, null, true, originalConditions);
}

public sealed class MusicTypeOption
{
    public MusicTypeOption(
        MusicSettingSource setting,
        IReadOnlyList<MusicDefinitionConflict>? conflicts = null)
    {
        Setting = setting;
        Conflict = conflicts?.FirstOrDefault(conflict =>
            conflict.RecordType.Equals("MusicType", StringComparison.OrdinalIgnoreCase) &&
            conflict.FormKey.Equals(setting.MusicTypeFormKey, StringComparison.OrdinalIgnoreCase));
    }

    public MusicSettingSource Setting { get; }
    public MusicDefinitionConflict? Conflict { get; }
    public string DisplayText => Setting.MusicTypeDisplayNameWithoutSuffix;
    public string DetailText =>
        Conflict is null
            ? UiText.Format(
                "SourceDetails.Assignment.Target",
                Setting.MusicTypeRecord.Plugin.Name)
            : UiText.Format(
                "SourceDetails.Assignment.TargetConflict",
                Conflict.DefinitionCount,
                Conflict.WinnerPluginName);
    public string TechnicalText =>
        UiText.Format("SourceDetails.FormId", Setting.MusicTypeRecord.FormKey) + "\n" +
        UiText.Format("SourceDetails.EditorId", Setting.MusicTypeRecord.EditorId ?? "—") + "\n" +
        (Conflict is null
            ? UiText.Format("SourceDetails.DefinitionEsp", Setting.MusicTypeRecord.Plugin.Name)
            : FormatConflictDetails(Conflict));

    private static string FormatConflictDetails(MusicDefinitionConflict conflict) =>
        string.Join(
            Environment.NewLine,
            conflict.Definitions.Select(record => UiText.Format(
                "SourceDetails.ConflictDefinitionLine",
                record.Plugin.Name,
                record.IsWinner
                    ? UiText.Get("SourceDetails.CurrentDefinitionStatus")
                    : UiText.Get("SourceDetails.OverriddenDefinition"))));
}

public sealed class MusicCandidateOption
{
    public MusicCandidateOption(
        MusicSettingSource setting,
        IReadOnlyList<MusicDefinitionConflict>? conflicts = null)
    {
        Setting = setting;
        Key = setting.MusicTypeFormKey;
        DisplayText = setting.MusicTypeDisplayNameWithoutSuffix;
        var conflict = conflicts?.FirstOrDefault(item =>
            item.RecordType.Equals("MusicType", StringComparison.OrdinalIgnoreCase) &&
            item.FormKey.Equals(setting.MusicTypeFormKey, StringComparison.OrdinalIgnoreCase));
        DetailText = conflict is null
            ? UiText.Format("SourceDetails.Assignment.MusicType", setting.MusicTypeRecord.Plugin.Name)
            : UiText.Format(
                "SourceDetails.Assignment.MusicTypeConflict",
                conflict.DefinitionCount,
                conflict.WinnerPluginName);
    }

    public MusicCandidateOption(PluginRecordSource record)
    {
        Record = record;
        Key = record.FormKey;
        var editorId = record.EditorId ?? record.FormKey;
        var displayName = CleanDisplayName(record.DisplayName);
        var inferredName = record.RecordType.Equals("Keyword", StringComparison.OrdinalIgnoreCase)
            ? MusicKeywordNameFormatter.InferJapaneseName(record.EditorId)
            : record.RecordType.Equals("Weather", StringComparison.OrdinalIgnoreCase)
                ? MusicWeatherNameFormatter.InferJapaneseName(record.EditorId)
                : null;
        var japaneseName = inferredName ?? displayName;
        DisplayText = !string.IsNullOrWhiteSpace(japaneseName) &&
                      !japaneseName.Equals(editorId, StringComparison.OrdinalIgnoreCase)
            ? $"{editorId}（{japaneseName}）"
            : editorId;
        DetailText = UiText.Format(
            "SourceDetails.RecordSource",
            record.RecordType,
            UiText.Format("SourceDetails.DefinitionEsp", record.Plugin.Name));
    }

    private static string? CleanDisplayName(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith("$", StringComparison.Ordinal)
            ? null
            : value.Trim();

    public string Key { get; }
    public string DisplayText { get; }
    public string DetailText { get; }
    public MusicSettingSource? Setting { get; }
    public PluginRecordSource? Record { get; }
}

public sealed class MusicTypeGroupDetail
{
    public MusicTypeGroupDetail(
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<MusicDefinitionConflict>? conflicts = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Count == 0)
        {
            throw new ArgumentException("Music Type group must contain at least one setting", nameof(settings));
        }

        var preliminaryRepresentative = settings
            .Where(setting => setting.Scope == MusicSettingScope.MusicType)
            .OrderByDescending(setting => setting.MusicTypeRecord.IsWinner)
            .ThenByDescending(setting => setting.MusicTypeRecord.Plugin.LoadOrderIndex)
            .ThenBy(setting => setting.MusicTypeRecord.Plugin.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? settings
                .OrderByDescending(setting => setting.MusicTypeRecord.IsWinner)
                .ThenByDescending(setting => setting.MusicTypeRecord.Plugin.LoadOrderIndex)
                .ThenBy(setting => setting.MusicTypeRecord.Plugin.Name, StringComparer.OrdinalIgnoreCase)
                .First();
        var preliminaryConflict = conflicts?.FirstOrDefault(item =>
            item.RecordType.Equals("MusicType", StringComparison.OrdinalIgnoreCase) &&
            item.FormKey.Equals(preliminaryRepresentative.MusicTypeFormKey, StringComparison.OrdinalIgnoreCase));
        var representative = preliminaryConflict is null
            ? preliminaryRepresentative
            : settings.FirstOrDefault(setting =>
                  setting.MusicTypeRecord.Plugin.Path.Equals(
                      preliminaryConflict.CurrentWinner.Plugin.Path,
                      StringComparison.OrdinalIgnoreCase) &&
                  setting.MusicTypeRecord.FormKey.Equals(
                      preliminaryConflict.CurrentWinner.FormKey,
                      StringComparison.OrdinalIgnoreCase))
              ?? preliminaryRepresentative;
        var musicTypeRecord = representative.Scope == MusicSettingScope.MusicType
            ? representative.Record
            : representative.MusicTypeRecord;

        var conflict = conflicts?.FirstOrDefault(item =>
            item.RecordType.Equals("MusicType", StringComparison.OrdinalIgnoreCase) &&
            item.FormKey.Equals(representative.MusicTypeFormKey, StringComparison.OrdinalIgnoreCase));
        var definitionRecords = conflict?.Definitions ?? settings
            .Select(setting => setting.Scope == MusicSettingScope.MusicType
                ? setting.Record
                : setting.MusicTypeRecord)
            .DistinctBy(CreateRecordIdentity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentDefinition = conflict?.CurrentWinner ??
                                 definitionRecords.FirstOrDefault(record => record.IsWinner) ??
                                 definitionRecords[0];

        FormKey = representative.MusicTypeFormKey;
        DisplayText = representative.MusicTypeDisplayNameWithoutSuffix;
        RecordText = UiText.Format("SourceDetails.RecordType", "MusicType");
        DefinitionText = definitionRecords.Count == 1
            ? UiText.Format("SourceDetails.DefinitionEsp", musicTypeRecord.Plugin.Name)
            : UiText.Format("SourceDetails.Definition", definitionRecords.Count);
        CurrentDefinitionText = definitionRecords.Count == 1
            ? string.Empty
            : UiText.Format("SourceDetails.CurrentDefinition", currentDefinition.Plugin.Name);
        DefinitionDetails = definitionRecords
            .Select(record => new MusicDefinitionDetail(record))
            .ToArray();
        PluginText = string.Join(
            UiText.Get("Common.DetailSeparator"),
            GetModState(musicTypeRecord.Plugin.ModEnabled),
            GetEspState(musicTypeRecord.Plugin.Enabled));
        RelatedSettings = settings
            .Where(setting => setting.Scope != MusicSettingScope.MusicType)
            .Select(setting => new MusicSettingDetail(setting, conflicts))
            .ToArray();
        TechnicalText = string.Join(
            "\n",
            UiText.Format("SourceDetails.RecordType", "MusicType"),
            UiText.Format("SourceDetails.DefinitionEsp", musicTypeRecord.Plugin.Name),
            UiText.Format("SourceDetails.FormId", GetFormId(musicTypeRecord.FormKey)),
            UiText.Format("SourceDetails.EditorId", musicTypeRecord.EditorId ?? "—"),
            UiText.Format("SourceDetails.ModState", GetEnabledState(musicTypeRecord.Plugin.ModEnabled)),
            UiText.Format("SourceDetails.EspState", GetEnabledState(musicTypeRecord.Plugin.Enabled)),
            UiText.Format("SourceDetails.RelatedSettings", RelatedSettings.Count),
            UiText.Format("SourceDetails.DefinitionCount", definitionRecords.Count),
            UiText.Format("SourceDetails.CurrentDefinition", currentDefinition.Plugin.Name));
    }

    public string FormKey { get; }
    public string DisplayText { get; }
    public string RecordText { get; }
    public string DefinitionText { get; }
    public string CurrentDefinitionText { get; }
    public string PluginText { get; }
    public string TechnicalText { get; }
    public IReadOnlyList<MusicDefinitionDetail> DefinitionDetails { get; }
    public Visibility ConflictVisibility => DefinitionDetails.Count > 1
        ? Visibility.Visible
        : Visibility.Collapsed;
    public IReadOnlyList<MusicSettingDetail> RelatedSettings { get; }

    private static string CreateRecordIdentity(PluginRecordSource record) =>
        string.Join("\u001f", record.RecordType, record.FormKey, record.Plugin.Name, record.Plugin.Path);

    private static string GetFormId(string formKey)
    {
        var separator = formKey.IndexOf(':');
        return separator > 0 ? formKey[..separator] : formKey;
    }

    private static string GetEnabledState(bool enabled) =>
        UiText.Get(enabled ? "SourceDetails.Enabled" : "SourceDetails.Disabled");

    private static string GetModState(bool enabled) =>
        UiText.Get(enabled ? "SourceDetails.ModEnabled" : "SourceDetails.ModDisabled");

    private static string GetEspState(bool enabled) =>
        UiText.Get(enabled ? "SourceDetails.EspEnabled" : "SourceDetails.EspDisabled");
}

public sealed class MusicDefinitionDetail
{
    private readonly PluginRecordSource _record;

    public MusicDefinitionDetail(PluginRecordSource record)
    {
        _record = record;
    }

    public string PluginText => _record.Plugin.Name;
    public string StatusText => _record.IsWinner
        ? UiText.Get("SourceDetails.CurrentDefinitionStatus")
        : UiText.Get("SourceDetails.OverriddenDefinition");
    public string StateText => string.Join(
        UiText.Get("Common.DetailSeparator"),
        UiText.Get(_record.Plugin.ModEnabled ? "SourceDetails.ModEnabled" : "SourceDetails.ModDisabled"),
        UiText.Get(_record.Plugin.Enabled ? "SourceDetails.EspEnabled" : "SourceDetails.EspDisabled"));
}

public sealed class MusicSettingDetail
{
    private readonly MusicSettingSource _setting;
    private readonly MusicDefinitionConflict? _conflict;

    public MusicSettingDetail(
        MusicSettingSource setting,
        IReadOnlyList<MusicDefinitionConflict>? conflicts = null)
    {
        _setting = setting;
        _conflict = conflicts?.FirstOrDefault(conflict =>
            conflict.RecordType.Equals(setting.Record.RecordType, StringComparison.OrdinalIgnoreCase) &&
            conflict.FormKey.Equals(setting.Record.FormKey, StringComparison.OrdinalIgnoreCase));
    }

    public string ScopeText => UiText.Format(
        "SourceDetails.Scope",
        UiText.Get($"Scope.{_setting.Scope}"),
        _setting.ScopeDisplayName);
    public string RecordText => UiText.Format("SourceDetails.RecordType", _setting.Record.RecordType);
    public string DefinitionText => _conflict is null
        ? UiText.Format("SourceDetails.DefinitionEsp", _setting.Record.Plugin.Name)
        : UiText.Format(
            "SourceDetails.DefinitionConflict",
            _conflict.DefinitionCount,
            _conflict.WinnerPluginName);
    public string CurrentDefinitionText => _conflict is null
        ? string.Empty
        : UiText.Format("SourceDetails.CurrentDefinition", _conflict.WinnerPluginName);
    public IReadOnlyList<MusicDefinitionDetail> DefinitionDetails =>
        _conflict?.Definitions.Select(record => new MusicDefinitionDetail(record)).ToArray()
        ?? Array.Empty<MusicDefinitionDetail>();
    public Visibility ConflictVisibility => DefinitionDetails.Count > 1
        ? Visibility.Visible
        : Visibility.Collapsed;
    public string TechnicalText => string.Join(
        "\n",
        UiText.Format("SourceDetails.RecordType", _setting.Record.RecordType),
        UiText.Format("SourceDetails.DefinitionEsp", _setting.Record.Plugin.Name),
        UiText.Format("SourceDetails.FormId", GetFormId(_setting.Record.FormKey)),
        UiText.Format("SourceDetails.EditorId", _setting.Record.EditorId ?? "—"),
        UiText.Format("SourceDetails.ModState", UiText.Get(
            _setting.Record.Plugin.ModEnabled ? "SourceDetails.Enabled" : "SourceDetails.Disabled")),
        UiText.Format("SourceDetails.EspState", UiText.Get(
            _setting.Record.Plugin.Enabled ? "SourceDetails.Enabled" : "SourceDetails.Disabled")),
        UiText.Format("SourceDetails.AssignedMusicType", _setting.MusicTypeDisplayNameWithoutSuffix),
        _conflict is null
            ? string.Empty
            : UiText.Format("SourceDetails.DefinitionCount", _conflict.DefinitionCount));
    public string PluginText => string.Join(
        UiText.Get("Common.DetailSeparator"),
        UiText.Get(_setting.Record.Plugin.ModEnabled ? "SourceDetails.ModEnabled" : "SourceDetails.ModDisabled"),
        UiText.Get(_setting.Record.Plugin.Enabled ? "SourceDetails.EspEnabled" : "SourceDetails.EspDisabled"));

    private static string GetFormId(string formKey)
    {
        var separator = formKey.IndexOf(':');
        return separator > 0 ? formKey[..separator] : formKey;
    }
}

public sealed class MusicTrackDetailGroup : INotifyPropertyChanged
{
    private bool _isSelected;

    public MusicTrackDetailGroup(
        IReadOnlyList<MusicTrackSource> tracks,
        IReadOnlyList<MusicDefinitionConflict>? conflicts = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            throw new ArgumentException("Music Track group must contain at least one track", nameof(tracks));
        }

        var representative = tracks[0];
        var definitionTracks = tracks
            .GroupBy(MusicTrackIdentity.CreateDefinitionIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        SourceDetails = definitionTracks
            .Select(track => new MusicTrackDetail(track, false, FindConflict(track, conflicts)))
            .ToArray();
        Representative = SourceDetails[0];
        SelectionKey = MusicTrackIdentity.Create(representative);
        Conditions = representative.Conditions;
        SourcePluginNames = SourceDetails
            .Select(detail => detail.SourcePluginName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        DisplayText = representative.EditorId ?? representative.FormKey;
        AudioText = representative.MatchingAudioPaths.Count == 0
            ? UiText.Get("SourceDetails.AudioPathUnparsed")
            : UiText.Format(
                "SourceDetails.AudioPath",
                string.Join(UiText.Get("Common.ListSeparator"), representative.MatchingAudioPaths));
        ConditionsText = MusicConditionFormatter.FormatTrackConditions(Conditions);
        DefinitionText = FormatSourcePluginSummary(SourcePluginNames);
        TechnicalHeaderText = SourcePluginNames.Count > 1
            ? UiText.Format("SourceDetails.TechnicalInfoByEsp", SourcePluginNames.Count)
            : UiText.Get("SourceDetails.TechnicalInfo");
    }

    public string DisplayText { get; }
    public string AudioText { get; }
    public string ConditionsText { get; }
    public string DefinitionText { get; }
    public string TechnicalHeaderText { get; }
    public string SelectionKey { get; }
    public MusicTrackDetail Representative { get; }
    public IReadOnlyList<MusicConditionSource> Conditions { get; }
    public IReadOnlyList<string> SourcePluginNames { get; }
    public IReadOnlyList<MusicTrackDetail> SourceDetails { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static MusicDefinitionConflict? FindConflict(
        MusicTrackSource track,
        IReadOnlyList<MusicDefinitionConflict>? conflicts) =>
        conflicts?.FirstOrDefault(item =>
            item.RecordType.Equals(track.Record.RecordType, StringComparison.OrdinalIgnoreCase) &&
            item.FormKey.Equals(track.Record.FormKey, StringComparison.OrdinalIgnoreCase));

    private static string FormatSourcePluginSummary(IReadOnlyList<string> pluginNames) =>
        pluginNames.Count switch
        {
            0 => UiText.Get("SourceDetails.TrackSourceSummaryUnparsed"),
            1 => UiText.Format("SourceDetails.DefinitionEsp", pluginNames[0]),
            _ => UiText.Format(
                "SourceDetails.TrackSourceSummary",
                pluginNames.Count,
                string.Join(UiText.Get("Common.ListSeparator"), pluginNames))
        };
}

public sealed class MusicTrackDetail
{
    public MusicTrackDetail(
        MusicTrackSource track,
        bool isSelected,
        MusicDefinitionConflict? conflict = null)
    {
        SourceTrack = track;
        DisplayText = track.EditorId ?? track.FormKey;
        SourcePluginName = track.Record.Plugin.Name;
        AudioText = track.AudioPaths.Count == 0
            ? UiText.Get("SourceDetails.AudioPathUnparsed")
            : UiText.Format(
                "SourceDetails.AudioPath",
                string.Join(UiText.Get("Common.ListSeparator"), track.AudioPaths));
        Conflict = conflict;
        DefinitionText = conflict is null
            ? UiText.Format("SourceDetails.DefinitionEsp", track.Record.Plugin.Name)
            : UiText.Format(
                "SourceDetails.DefinitionConflict",
                conflict.DefinitionCount,
                conflict.WinnerPluginName);
        CurrentDefinitionText = conflict is null
            ? string.Empty
            : UiText.Format("SourceDetails.CurrentDefinition", conflict.WinnerPluginName);
        DefinitionDetails = conflict?.Definitions
                .Select(record => new MusicDefinitionDetail(record))
                .ToArray()
            ?? Array.Empty<MusicDefinitionDetail>();
        Conditions = track.Conditions.ToArray();
        ConditionsText = MusicConditionFormatter.FormatTrackConditions(Conditions);
        var resolvedAudioText = track.ResolvedAudioPaths.Count == 0
            ? string.Empty
            : UiText.Format(
                "SourceDetails.RealAudioPath",
                string.Join(UiText.Get("Common.ListSeparator"), track.ResolvedAudioPaths));
        var technicalLines = new List<string>
        {
            UiText.Format("SourceDetails.RecordType", "MusicTrack"),
            UiText.Format("SourceDetails.DefinitionEsp", track.Record.Plugin.Name),
            UiText.Format("SourceDetails.FormId", GetFormId(track.FormKey)),
            UiText.Format("SourceDetails.EditorId", track.EditorId ?? "—"),
            UiText.Format(
                "SourceDetails.AudioPath",
                track.AudioPaths.Count == 0
                    ? UiText.Get("SourceDetails.NotAnalyzed")
                    : string.Join(UiText.Get("Common.ListSeparator"), track.AudioPaths)),
            ConditionsText
        };
        if (!string.IsNullOrWhiteSpace(resolvedAudioText))
        {
            technicalLines.Add(resolvedAudioText);
        }

        if (conflict is not null)
        {
            technicalLines.Add(UiText.Format("SourceDetails.DefinitionCount", conflict.DefinitionCount));
        }

        TechnicalText = string.Join("\n", technicalLines);
    }

    public string DisplayText { get; }
    public MusicTrackSource SourceTrack { get; }
    public string AudioText { get; }
    public string DefinitionText { get; }
    public string CurrentDefinitionText { get; }
    public string ConditionsText { get; }
    public IReadOnlyList<MusicConditionSource> Conditions { get; }
    public string TechnicalText { get; }
    public string SourcePluginName { get; }
    public MusicDefinitionConflict? Conflict { get; }
    public IReadOnlyList<MusicDefinitionDetail> DefinitionDetails { get; }
    public Visibility ConflictVisibility => DefinitionDetails.Count > 1
        ? Visibility.Visible
        : Visibility.Collapsed;

    private static string GetFormId(string formKey)
    {
        var separator = formKey.IndexOf(':');
        return separator > 0 ? formKey[..separator] : formKey;
    }
}
