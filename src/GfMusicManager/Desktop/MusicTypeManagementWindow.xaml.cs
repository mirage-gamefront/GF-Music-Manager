using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Desktop;

public partial class MusicTypeManagementWindow : Window, INotifyPropertyChanged
{
    private readonly IReadOnlyList<MusicTypeManagementEntry> _allTypes;
    private MusicTypeManagementEntry? _selectedType;

    public MusicTypeManagementWindow(
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<MusicDefinitionConflict>? conflicts = null,
        string? initialMusicTypeFormKey = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _allTypes = BuildTypes(settings, conflicts);
        FilteredTypes = new ObservableCollection<MusicTypeManagementEntry>(_allTypes);

        InitializeComponent();
        DataContext = this;
        var initialType = string.IsNullOrWhiteSpace(initialMusicTypeFormKey)
            ? null
            : FilteredTypes.FirstOrDefault(type =>
                type.ContainsSourceFormKey(initialMusicTypeFormKey));
        SelectedType = initialType ?? FilteredTypes.FirstOrDefault();
    }

    public ObservableCollection<MusicTypeManagementEntry> FilteredTypes { get; }

    public MusicTypeManagementEntry? SelectedType
    {
        get => _selectedType;
        set
        {
            if (ReferenceEquals(_selectedType, value))
            {
                return;
            }

            _selectedType = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTypeVisibility));
            OnPropertyChanged(nameof(EmptyStateVisibility));
        }
    }

    public string SummaryText => UiText.Format("Management.Summary", _allTypes.Count);
    public string FilteredSummaryText => UiText.Format(
        "Management.FilteredSummary",
        _allTypes.Count(type => FilteredTypes.Contains(type)),
        _allTypes.Count);
    public Visibility SelectedTypeVisibility =>
        SelectedType is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyStateVisibility =>
        SelectedType is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static IReadOnlyList<MusicTypeManagementEntry> BuildTypes(
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<MusicDefinitionConflict>? conflicts)
    {
        return settings
            .GroupBy(CreateLogicalMusicTypeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MusicTypeManagementEntry(group.ToArray(), conflicts))
            .OrderBy(type => type.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ThenBy(type => type.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string CreateLogicalMusicTypeKey(MusicSettingSource setting) =>
        string.IsNullOrWhiteSpace(setting.MusicTypeEditorId)
            ? $"FormKey:{setting.MusicTypeFormKey}"
            : $"EditorID:{setting.MusicTypeEditorId}";

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        var query = SearchTextBox.Text.Trim();
        var matches = string.IsNullOrWhiteSpace(query)
            ? _allTypes
            : _allTypes.Where(type => type.Matches(query)).ToArray();

        FilteredTypes.Clear();
        foreach (var type in matches)
        {
            FilteredTypes.Add(type);
        }

        SelectedType = FilteredTypes.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredSummaryText));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MusicTypeManagementEntry
{
    public MusicTypeManagementEntry(
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<MusicDefinitionConflict>? conflicts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Count == 0)
        {
            throw new ArgumentException("Music Type group must contain at least one setting", nameof(settings));
        }

        var representative = settings
            .OrderByDescending(setting => setting.MusicTypeRecord.IsWinner)
            .ThenByDescending(setting => setting.MusicTypeRecord.Plugin.LoadOrderIndex)
            .ThenByDescending(setting => setting.MusicTypeRecord.Plugin.ModPriority)
            .ThenBy(setting => setting.MusicTypeRecord.Plugin.Name, StringComparer.OrdinalIgnoreCase)
            .First();
        var typeConflict = conflicts?.FirstOrDefault(conflict =>
            conflict.RecordType.Equals("MusicType", StringComparison.OrdinalIgnoreCase) &&
            conflict.FormKey.Equals(representative.MusicTypeFormKey, StringComparison.OrdinalIgnoreCase));
        var musicTypeRecord = typeConflict?.CurrentWinner ?? representative.MusicTypeRecord;

        FormKey = representative.MusicTypeFormKey;
        DisplayText = representative.MusicTypeDisplayNameWithoutSuffix;
        SourceFormKeys = settings
            .Select(setting => setting.MusicTypeFormKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(formKey => formKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SourcePluginNames = settings
            .Select(setting => setting.MusicTypeRecord.Plugin.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RecordSummaryText = FormatSourcePluginSummary(SourcePluginNames);
        TechnicalText = string.Join(
            "\n",
            UiText.Format("Management.RecordType", "MusicType"),
            UiText.Format("Management.FormId", GetFormId(representative.MusicTypeFormKey)),
            UiText.Format("Management.EditorId", representative.MusicTypeEditorId ?? "—"),
            UiText.Format("Management.DefinitionEsp", musicTypeRecord.Plugin.Name),
            UiText.Format("Management.ModState", UiText.Get(
                musicTypeRecord.Plugin.ModEnabled ? "Management.Enabled" : "Management.Disabled")),
            UiText.Format("Management.EspState", UiText.Get(
                musicTypeRecord.Plugin.Enabled ? "Management.Enabled" : "Management.Disabled")));

        Tracks = settings
            .SelectMany(setting => setting.Tracks)
            .GroupBy(MusicTrackIdentity.Create, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MusicTypeTrackManagementEntry(group.ToArray()))
            .OrderBy(track => track.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var definitionSettings = settings
            .Where(setting => setting.Scope != MusicSettingScope.MusicType)
            .GroupBy(CreateDefinitionIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MusicTypeDefinitionManagementEntry(group.First(), conflicts))
            .ToArray();
        DefinitionGroups = definitionSettings
            .GroupBy(definition => definition.Scope)
            .OrderBy(group => ScopeRank(group.Key))
            .Select(group => new MusicTypeDefinitionGroup(group.Key, group.ToArray()))
            .ToArray();
    }

    public string FormKey { get; }
    public string DisplayText { get; }
    public string RecordSummaryText { get; }
    public IReadOnlyList<string> SourceFormKeys { get; }
    public IReadOnlyList<string> SourcePluginNames { get; }
    public string TechnicalText { get; }
    public IReadOnlyList<MusicTypeTrackManagementEntry> Tracks { get; }
    public IReadOnlyList<MusicTypeDefinitionGroup> DefinitionGroups { get; }
    public string SummaryText => UiText.Format(
        "Management.EntrySummary",
        Tracks.Count,
        DefinitionGroups.Sum(group => group.Entries.Count));
    public string TrackHeaderText => UiText.Format("Management.TrackHeader", Tracks.Count);
    public string DefinitionHeaderText =>
        DefinitionGroups.Count == 0
            ? UiText.Get("Management.DefinitionHeaderEmpty")
            : UiText.Format(
                "Management.DefinitionHeader",
                DefinitionGroups.Sum(group => group.Entries.Count));

    public bool ContainsSourceFormKey(string formKey) =>
        SourceFormKeys.Any(sourceFormKey =>
            sourceFormKey.Equals(formKey, StringComparison.OrdinalIgnoreCase));

    public bool Matches(string query) =>
        DisplayText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        SourceFormKeys.Any(formKey => formKey.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
        SourcePluginNames.Any(pluginName => pluginName.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
        TechnicalText.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string FormatSourcePluginSummary(IReadOnlyList<string> pluginNames) =>
        pluginNames.Count switch
        {
            0 => UiText.Get("Management.SourceSummaryUnparsed"),
            1 => UiText.Format("Management.SourceSummary", pluginNames[0]),
            _ => UiText.Format(
                "Management.SourceSummaryMany",
                pluginNames.Count,
                string.Join(UiText.Get("Common.ListSeparator"), pluginNames))
        };

    private static string CreateDefinitionIdentity(MusicSettingSource setting) =>
        string.Join("\u001f", setting.Scope, setting.ScopeFormKey, setting.Record.Plugin.Path, setting.Record.FormKey);

    private static int ScopeRank(MusicSettingScope scope) => scope switch
    {
        MusicSettingScope.Cell => 0,
        MusicSettingScope.Location => 1,
        MusicSettingScope.Region => 2,
        MusicSettingScope.WorldSpace => 3,
        _ => 4
    };

    private static string GetFormId(string formKey)
    {
        var separator = formKey.IndexOf(':');
        return separator > 0 ? formKey[..separator] : formKey;
    }
}

public sealed class MusicTypeTrackManagementEntry
{
    public MusicTypeTrackManagementEntry(IReadOnlyList<MusicTrackSource> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            throw new ArgumentException("Track group must contain at least one track", nameof(tracks));
        }

        var track = tracks[0];
        DisplayText = track.EditorId ?? track.FormKey;
        AudioText = track.MatchingAudioPaths.Count == 0
            ? UiText.Get("Management.AudioUnparsed")
            : UiText.Format(
                "Management.Audio",
                string.Join(UiText.Get("Common.ListSeparator"), track.MatchingAudioPaths));
        ConditionsText = MusicConditionFormatter.FormatTrackConditions(track.Conditions);
        SourcePluginNames = tracks
            .Select(item => item.Record.Plugin.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RecordText = FormatSourcePluginSummary(SourcePluginNames);
    }

    public string DisplayText { get; }
    public string AudioText { get; }
    public string ConditionsText { get; }
    public string RecordText { get; }
    public IReadOnlyList<string> SourcePluginNames { get; }

    private static string FormatSourcePluginSummary(IReadOnlyList<string> pluginNames) =>
        pluginNames.Count switch
        {
            0 => UiText.Get("Management.SourceSummaryUnparsed"),
            1 => UiText.Format("Management.SourceSummary", pluginNames[0]),
            _ => UiText.Format(
                "Management.SourceSummaryMany",
                pluginNames.Count,
                string.Join(UiText.Get("Common.ListSeparator"), pluginNames))
        };
}

public sealed class MusicTypeDefinitionGroup
{
    public MusicTypeDefinitionGroup(
        MusicSettingScope scope,
        IReadOnlyList<MusicTypeDefinitionManagementEntry> entries)
    {
        Scope = scope;
        Entries = entries
            .OrderBy(entry => entry.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        HeaderText = UiText.Format(
            "Management.ScopeGroupHeader",
            entries.FirstOrDefault()?.ScopeLabel ?? UiText.Get($"Scope.{scope}"),
            Entries.Count);
    }

    public MusicSettingScope Scope { get; }
    public string HeaderText { get; }
    public IReadOnlyList<MusicTypeDefinitionManagementEntry> Entries { get; }
}

public sealed class MusicTypeDefinitionManagementEntry
{
    public MusicTypeDefinitionManagementEntry(
        MusicSettingSource setting,
        IReadOnlyList<MusicDefinitionConflict>? conflicts)
    {
        Setting = setting;
        ScopeLabel = UiText.Get($"Scope.{setting.Scope}");
        DisplayText = setting.ScopeDisplayName;
        var conflict = conflicts?.FirstOrDefault(item =>
            item.RecordType.Equals(setting.Record.RecordType, StringComparison.OrdinalIgnoreCase) &&
            item.FormKey.Equals(setting.Record.FormKey, StringComparison.OrdinalIgnoreCase));
        DetailText = conflict is null
            ? UiText.Format("Management.DefinitionEsp", setting.Record.Plugin.Name)
            : UiText.Format(
                "Management.DefinitionConflictSummary",
                conflict.DefinitionCount,
                conflict.WinnerPluginName);
        TechnicalText = string.Join(
            "\n",
            UiText.Format("Management.RecordType", setting.Record.RecordType),
            UiText.Format("Management.FormId", GetFormId(setting.Record.FormKey)),
            UiText.Format("Management.EditorId", setting.Record.EditorId ?? "—"),
            UiText.Format("Management.DefinitionEsp", setting.Record.Plugin.Name),
            UiText.Format("Management.ModState", UiText.Get(
                setting.Record.Plugin.ModEnabled ? "Management.Enabled" : "Management.Disabled")),
            UiText.Format("Management.EspState", UiText.Get(
                setting.Record.Plugin.Enabled ? "Management.Enabled" : "Management.Disabled")));
    }

    private MusicSettingSource Setting { get; }
    public MusicSettingScope Scope => Setting.Scope;
    public string ScopeLabel { get; }
    public string DisplayText { get; }
    public string DetailText { get; }
    public string TechnicalText { get; }

    private static string GetFormId(string formKey)
    {
        var separator = formKey.IndexOf(':');
        return separator > 0 ? formKey[..separator] : formKey;
    }
}
