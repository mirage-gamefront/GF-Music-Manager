using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Desktop;

public partial class MusicTypeBulkAssignWindow : Window, INotifyPropertyChanged
{
    private readonly ICollectionView _filteredOptions;

    public MusicTypeBulkAssignWindow(
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<MusicDefinitionConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(conflicts);

        Options = new ObservableCollection<MusicTypeBulkOption>(
            settings
                .GroupBy(setting => setting.MusicTypeFormKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => SelectRepresentative(group, conflicts))
                .OrderBy(option => option.DisplayText, StringComparer.OrdinalIgnoreCase));
        _filteredOptions = CollectionViewSource.GetDefaultView(Options);
        _filteredOptions.Filter = FilterOption;
        foreach (var option in Options)
        {
            option.PropertyChanged += Option_PropertyChanged;
        }

        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<MusicTypeBulkOption> Options { get; }
    public ICollectionView FilteredOptions => _filteredOptions;
    public IReadOnlyList<MusicSettingSource> SelectedSettings =>
        Options.Where(option => option.IsSelected)
            .Select(option => option.Setting)
            .ToArray();

    public string SelectedSummaryText => UiText.Format(
        "Bulk.Summary",
        Options.Count(option => option.IsSelected),
        Options.Count);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool FilterOption(object item)
    {
        if (item is not MusicTypeBulkOption option)
        {
            return false;
        }

        var search = SearchTextBox?.Text.Trim();
        return string.IsNullOrWhiteSpace(search) ||
               option.DisplayText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               option.DetailText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               option.Setting.MusicTypeFormKey.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static MusicTypeBulkOption SelectRepresentative(
        IEnumerable<MusicSettingSource> settings,
        IReadOnlyList<MusicDefinitionConflict> conflicts)
    {
        var setting = settings
            .OrderByDescending(item => item.Scope == MusicSettingScope.MusicType)
            .ThenByDescending(item => item.MusicTypeRecord.IsWinner)
            .ThenByDescending(item => item.MusicTypeRecord.Plugin.LoadOrderIndex)
            .ThenByDescending(item => item.MusicTypeRecord.Plugin.ModPriority)
            .ThenBy(item => item.MusicTypeRecord.Plugin.Name, StringComparer.OrdinalIgnoreCase)
            .First();
        var conflict = conflicts.FirstOrDefault(item =>
            item.RecordType.Equals("MusicType", StringComparison.OrdinalIgnoreCase) &&
            item.FormKey.Equals(setting.MusicTypeFormKey, StringComparison.OrdinalIgnoreCase));
        return new MusicTypeBulkOption(setting, conflict);
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _filteredOptions.Refresh();
    }

    private void OptionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectedSummaryText));
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSettings.Count == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                UiText.Get("Bulk.NoSelection"),
                UiText.Get("Bulk.NoSelectionTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var option in Options)
        {
            option.PropertyChanged -= Option_PropertyChanged;
        }

        base.OnClosed(e);
    }

    private void Option_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicTypeBulkOption.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedSummaryText));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MusicTypeBulkOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public MusicTypeBulkOption(
        MusicSettingSource setting,
        MusicDefinitionConflict? conflict)
    {
        Setting = setting;
        Conflict = conflict;
    }

    public MusicSettingSource Setting { get; }
    public MusicDefinitionConflict? Conflict { get; }
    public string DisplayText => Setting.MusicTypeDisplayNameWithoutSuffix;
    public string DetailText => Conflict is null
        ? UiText.Format("Bulk.OptionDetail", Setting.MusicTypeRecord.Plugin.Name)
        : UiText.Format(
            "Bulk.OptionConflictDetail",
            Conflict.DefinitionCount,
            Conflict.WinnerPluginName);

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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
