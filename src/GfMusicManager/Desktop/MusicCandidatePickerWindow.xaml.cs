using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using GfMusicManager.Core.Localization;

namespace GfMusicManager.Desktop;

public partial class MusicCandidatePickerWindow : Window, INotifyPropertyChanged
{
    private readonly ICollectionView _filteredOptions;

    public MusicCandidatePickerWindow(
        string title,
        string prompt,
        IReadOnlyList<MusicCandidateOption> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(options);
        PickerTitle = title;
        PickerPrompt = prompt;
        Options = new ObservableCollection<MusicCandidateOption>(options);
        _filteredOptions = CollectionViewSource.GetDefaultView(Options);
        _filteredOptions.Filter = FilterOption;
        InitializeComponent();
        DataContext = this;
    }

    public string PickerTitle { get; }
    public string PickerPrompt { get; }
    public ObservableCollection<MusicCandidateOption> Options { get; }
    public ICollectionView FilteredOptions => _filteredOptions;

    private MusicCandidateOption? _selectedOption;
    public MusicCandidateOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (ReferenceEquals(_selectedOption, value))
            {
                return;
            }

            _selectedOption = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _filteredOptions.Refresh();
    }

    private bool FilterOption(object item)
    {
        if (item is not MusicCandidateOption option)
        {
            return false;
        }

        var search = SearchTextBox?.Text.Trim();
        return string.IsNullOrWhiteSpace(search) ||
               option.DisplayText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               option.DetailText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               option.Key.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOption is null)
        {
            System.Windows.MessageBox.Show(
                this,
                UiText.Get("Candidate.NoSelection"),
                UiText.Get("Candidate.NoSelectionTitle"),
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
