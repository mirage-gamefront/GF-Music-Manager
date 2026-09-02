using System.Windows;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Diagnostics;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace GfMusicManager.Desktop;

public partial class MusicFilterWindow : Window
{
    public MusicFilterWindow(
        MusicFilterOptions currentOptions,
        MusicFilterCandidates candidates)
    {
        ArgumentNullException.ThrowIfNull(currentOptions);
        ArgumentNullException.ThrowIfNull(candidates);
        InitializeComponent();
        CombatComboBox.ItemsSource = candidates.Combat;
        TimeOfDayComboBox.ItemsSource = candidates.TimeOfDay;
        WeatherComboBox.ItemsSource = candidates.Weather;
        OtherConditionComboBox.ItemsSource = candidates.OtherCondition;
        LoadOptions(currentOptions);
        GfMusicManagerLog.Info(
            $"MusicFilterWindow opened. activeRules={currentOptions.ActiveRuleCount}, " +
            $"candidates=combat:{candidates.Combat.Count - 1}, " +
            $"time:{candidates.TimeOfDay.Count - 1}, " +
            $"weather:{candidates.Weather.Count - 1}, " +
            $"other:{candidates.OtherCondition.Count - 1}.");
    }

    public MusicFilterOptions Options { get; private set; } = MusicFilterOptions.Empty;

    private void LoadOptions(MusicFilterOptions options)
    {
        CombatCheckBox.IsChecked = options.PlaybackFilters.Contains(MusicPlaybackFilterKind.Combat);
        TimeOfDayCheckBox.IsChecked = options.PlaybackFilters.Contains(MusicPlaybackFilterKind.TimeOfDay);
        WeatherCheckBox.IsChecked = options.PlaybackFilters.Contains(MusicPlaybackFilterKind.Weather);
        OtherConditionCheckBox.IsChecked = options.PlaybackFilters.Contains(MusicPlaybackFilterKind.OtherCondition);
        NoConditionCheckBox.IsChecked = options.PlaybackFilters.Contains(MusicPlaybackFilterKind.NoCondition);
        SelectComboValue(
            CombatComboBox,
            GetPlaybackSelection(options, MusicPlaybackFilterKind.Combat));
        SelectComboValue(
            TimeOfDayComboBox,
            GetPlaybackSelection(options, MusicPlaybackFilterKind.TimeOfDay));
        SelectComboValue(
            WeatherComboBox,
            GetPlaybackSelection(options, MusicPlaybackFilterKind.Weather));
        SelectComboValue(
            OtherConditionComboBox,
            GetPlaybackSelection(options, MusicPlaybackFilterKind.OtherCondition));

        MusicTypeCheckBox.IsChecked = options.DefinitionSelections.ContainsKey(MusicSettingScope.MusicType);
        CellCheckBox.IsChecked = options.DefinitionSelections.ContainsKey(MusicSettingScope.Cell);
        WorldSpaceCheckBox.IsChecked = options.DefinitionSelections.ContainsKey(MusicSettingScope.WorldSpace);
        RegionCheckBox.IsChecked = options.DefinitionSelections.ContainsKey(MusicSettingScope.Region);
        LocationCheckBox.IsChecked = options.DefinitionSelections.ContainsKey(MusicSettingScope.Location);
        MusicTypeQueryTextBox.Text = GetDefinitionSelection(options, MusicSettingScope.MusicType);
        CellQueryTextBox.Text = GetDefinitionSelection(options, MusicSettingScope.Cell);
        WorldSpaceQueryTextBox.Text = GetDefinitionSelection(options, MusicSettingScope.WorldSpace);
        RegionQueryTextBox.Text = GetDefinitionSelection(options, MusicSettingScope.Region);
        LocationQueryTextBox.Text = GetDefinitionSelection(options, MusicSettingScope.Location);
        UpdatePlaybackComboStates();
        UpdateDefinitionQueryStates();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var playbackSelections = new List<KeyValuePair<MusicPlaybackFilterKind, string>>();
        AddPlaybackSelection(
            CombatCheckBox,
            CombatComboBox,
            MusicPlaybackFilterKind.Combat,
            playbackSelections);
        AddPlaybackSelection(
            TimeOfDayCheckBox,
            TimeOfDayComboBox,
            MusicPlaybackFilterKind.TimeOfDay,
            playbackSelections);
        AddPlaybackSelection(
            WeatherCheckBox,
            WeatherComboBox,
            MusicPlaybackFilterKind.Weather,
            playbackSelections);
        AddPlaybackSelection(
            OtherConditionCheckBox,
            OtherConditionComboBox,
            MusicPlaybackFilterKind.OtherCondition,
            playbackSelections);
        AddPlaybackSelection(
            NoConditionCheckBox,
            null,
            MusicPlaybackFilterKind.NoCondition,
            playbackSelections);

        var definitionSelections = new List<KeyValuePair<MusicSettingScope, string>>();
        AddDefinitionSelection(
            MusicTypeCheckBox,
            MusicTypeQueryTextBox,
            MusicSettingScope.MusicType,
            definitionSelections);
        AddDefinitionSelection(
            CellCheckBox,
            CellQueryTextBox,
            MusicSettingScope.Cell,
            definitionSelections);
        AddDefinitionSelection(
            WorldSpaceCheckBox,
            WorldSpaceQueryTextBox,
            MusicSettingScope.WorldSpace,
            definitionSelections);
        AddDefinitionSelection(
            RegionCheckBox,
            RegionQueryTextBox,
            MusicSettingScope.Region,
            definitionSelections);
        AddDefinitionSelection(
            LocationCheckBox,
            LocationQueryTextBox,
            MusicSettingScope.Location,
            definitionSelections);

        Options = new MusicFilterOptions(playbackSelections, definitionSelections);
        GfMusicManagerLog.Info(
            $"MusicFilterWindow applied. activeRules={Options.ActiveRuleCount}, " +
            $"playback={string.Join(',', Options.PlaybackSelections.Select(pair => $"{pair.Key}:{pair.Value}"))}, " +
            $"definitions={string.Join(',', Options.DefinitionSelections.Select(pair => $"{pair.Key}:{pair.Value}"))}.");
        DialogResult = true;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        CombatCheckBox.IsChecked = false;
        TimeOfDayCheckBox.IsChecked = false;
        WeatherCheckBox.IsChecked = false;
        OtherConditionCheckBox.IsChecked = false;
        NoConditionCheckBox.IsChecked = false;
        CombatComboBox.SelectedIndex = 0;
        TimeOfDayComboBox.SelectedIndex = 0;
        WeatherComboBox.SelectedIndex = 0;
        OtherConditionComboBox.SelectedIndex = 0;
        MusicTypeQueryTextBox.Clear();
        CellQueryTextBox.Clear();
        WorldSpaceQueryTextBox.Clear();
        RegionQueryTextBox.Clear();
        LocationQueryTextBox.Clear();
        MusicTypeCheckBox.IsChecked = false;
        CellCheckBox.IsChecked = false;
        WorldSpaceCheckBox.IsChecked = false;
        RegionCheckBox.IsChecked = false;
        LocationCheckBox.IsChecked = false;
        UpdatePlaybackComboStates();
        UpdateDefinitionQueryStates();
        GfMusicManagerLog.Info("MusicFilterWindow cleared pending values.");
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        GfMusicManagerLog.Info("MusicFilterWindow cancelled.");
        DialogResult = false;
    }

    private void PlaybackFilterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdatePlaybackComboStates();
    }

    private void DefinitionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateDefinitionQueryStates();
    }

    private void UpdatePlaybackComboStates()
    {
        // The checkbox controls whether a category participates in filtering.
        // Keep the dropdowns enabled so their current value remains readable
        // even before the category is checked.
        CombatComboBox.IsEnabled = true;
        TimeOfDayComboBox.IsEnabled = true;
        WeatherComboBox.IsEnabled = true;
        OtherConditionComboBox.IsEnabled = true;
    }

    private void UpdateDefinitionQueryStates()
    {
        MusicTypeQueryTextBox.IsEnabled = MusicTypeCheckBox.IsChecked == true;
        CellQueryTextBox.IsEnabled = CellCheckBox.IsChecked == true;
        WorldSpaceQueryTextBox.IsEnabled = WorldSpaceCheckBox.IsChecked == true;
        RegionQueryTextBox.IsEnabled = RegionCheckBox.IsChecked == true;
        LocationQueryTextBox.IsEnabled = LocationCheckBox.IsChecked == true;
    }

    private static void AddPlaybackSelection(
        WpfCheckBox checkBox,
        WpfComboBox? comboBox,
        MusicPlaybackFilterKind filter,
        ICollection<KeyValuePair<MusicPlaybackFilterKind, string>> selections)
    {
        if (checkBox.IsChecked != true)
        {
            return;
        }

        selections.Add(new KeyValuePair<MusicPlaybackFilterKind, string>(
            filter,
            comboBox?.SelectedValue as string ?? string.Empty));
    }

    private static void AddDefinitionSelection(
        WpfCheckBox checkBox,
        WpfTextBox textBox,
        MusicSettingScope scope,
        ICollection<KeyValuePair<MusicSettingScope, string>> selections)
    {
        if (checkBox.IsChecked == true)
        {
            selections.Add(new KeyValuePair<MusicSettingScope, string>(scope, textBox.Text));
        }
    }

    private static string GetPlaybackSelection(
        MusicFilterOptions options,
        MusicPlaybackFilterKind kind) =>
        options.PlaybackSelections.TryGetValue(kind, out var value) ? value : string.Empty;

    private static string GetDefinitionSelection(
        MusicFilterOptions options,
        MusicSettingScope scope) =>
        options.DefinitionSelections.TryGetValue(scope, out var value) ? value : string.Empty;

    private static void SelectComboValue(WpfComboBox comboBox, string value)
    {
        comboBox.SelectedValue = value;
        if (comboBox.SelectedIndex < 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }
}
