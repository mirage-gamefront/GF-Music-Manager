using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using GfMusicManager.Application;
using GfMusicManager.Core.Audio;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Scanning;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace GfMusicManager.Desktop;

public partial class MainWindow : Window
{
    private const string FilterAll = "all";
    private const string FilterAdopted = "adopted";
    private const string FilterWarning = "warning";
    private const string FilterUnused = "unused";
    private const string FilterDisabled = "disabled";

    private readonly Mo2Scanner _scanner = new();
    private readonly MusicScanApplicationService _musicScanApplicationService = new();
    private readonly MusicPlanApplicationService _musicPlanApplicationService = new();
    private readonly MusicGenerationApplicationService _musicGenerationApplicationService = new();
    private readonly MusicMo2ApplicationService _musicMo2ApplicationService = new();
    private readonly MusicGenerationPrerequisiteDetector _prerequisiteDetector = new();
    private readonly ExistingMusicProductLoader _existingMusicProductLoader = new();
    private readonly MusicGenerationPlanRestorer _musicGenerationPlanRestorer = new();
    private readonly GfMusicManagerDraftStore _draftStore = new();
    private MusicGenerationPlan _generationPlan = new();
    private readonly GfMusicManagerSettingsStore _settingsStore;
    private readonly XwmaPreviewPlayer _previewPlayer = new();
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _draftSaveTimer;
    private ObservableCollection<TrackRow> _tracks = new();
    private readonly ObservableCollection<SourceFilterRow> _sourceFilters = new();
    private readonly ObservableCollection<string> _profiles = new();
    private ICollectionView _tracksView;
    private string? _sourceFilter;
    private string _filterMode = FilterAll;
    private string _appliedSearchText = string.Empty;
    private MusicFilterOptions _musicFilterOptions = MusicFilterOptions.Empty;
    private string? _mo2Root;
    private string? _selectedProfileName;
    private bool _createWorldSpaceMusicSettings;
    private IReadOnlyList<MusicSettingSource> _availableMusicSettings =
        Array.Empty<MusicSettingSource>();
    private IReadOnlyList<MusicDefinitionConflict> _musicDefinitionConflicts =
        Array.Empty<MusicDefinitionConflict>();
    private IReadOnlyList<AudioDuplicateGroup> _audioDuplicateGroups =
        Array.Empty<AudioDuplicateGroup>();
    private IReadOnlyList<ModSource> _scannedMods = Array.Empty<ModSource>();
    private IReadOnlyDictionary<string, int> _modPriorities =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _existingMtdFileNames = Array.Empty<string>();
    private bool _includeDisabledMods;
    private bool _enableFileLogging;
    private string _language = UiLanguage.Japanese;
    private bool _isScanning;
    private bool _hasCompletedScan;
    private bool _isLoadingProfiles;
    private bool _isRestoringSettings;
    private bool _disableSourceEsp = true;
    private bool _draftDirty;
    private bool _draftPersistenceSuppressed;
    private GfMusicManagerDraft? _scanBaselineDraft;
    private MusicScanApplicationResult? _applicationScanResult;
    private string? _scannedMo2Root;
    private string? _scannedProfileName;
    private bool _isPreviewSeekDragging;
    private bool _trackViewRefreshQueued;
    private bool _isRefreshingTrackView;
    private bool _isUpdatingVisibleSelection;
    private int _scanModCurrent;
    private int _scanModTotal;
    private int _scanPluginCurrent;
    private int _scanPluginTotal;
    private int _scanConflictCurrent;
    private int _scanConflictTotal;
    private int _scanResultCurrent;
    private int _scanResultTotal;

    public MainWindow()
        : this(new GfMusicManagerSettingsStore())
    {
    }

    internal MainWindow(GfMusicManagerSettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        GfMusicManagerLog.Info("MainWindow constructor: begin.");
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        ContentRendered += MainWindow_ContentRendered;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _previewTimer.Tick += PreviewTimer_Tick;

        _draftSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _draftSaveTimer.Tick += DraftSaveTimer_Tick;

        _tracksView = CollectionViewSource.GetDefaultView(_tracks);
        _tracksView.Filter = FilterTrack;
        TrackGrid.ItemsSource = _tracksView;
        SourceList.ItemsSource = _sourceFilters;
        SettingsProfileComboBox.ItemsSource = _profiles;
        LanguageComboBox.ItemsSource = UiLanguage.Supported.Select(code => new LanguageOption(
            code,
            UiText.Get(code == UiLanguage.Japanese
                ? "Settings.Language.Japanese"
                : "Settings.Language.English")));
        SettingsProfileComboBox.IsEnabled = false;
        _isRestoringSettings = true;
        try
        {
            RestoreSavedSettings();
        }
        finally
        {
            _isRestoringSettings = false;
        }
        RefreshSourceFilters();
        UpdateSummary();
        ClearSelectedTrack();
        UpdateEmptyLibraryState();
        GfMusicManagerLog.Info("MainWindow constructor: complete.");
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        GfMusicManagerLog.Info($"MainWindow Loaded. tracks={_tracks.Count}.");
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        GfMusicManagerLog.Info($"MainWindow ContentRendered. tracks={_tracks.Count}.");
    }

    protected override void OnClosed(EventArgs e)
    {
        GfMusicManagerLog.Info("MainWindow closing.");
        _draftSaveTimer.Stop();
        SaveDraftNow();
        _previewTimer.Stop();
        _previewPlayer.Dispose();
        base.OnClosed(e);
        GfMusicManagerLog.Info("MainWindow closed.");
    }

    private void RestoreSavedSettings()
    {
        GfMusicManagerLog.Info("RestoreSavedSettings: begin.");
        var settings = _settingsStore.Load();
        _includeDisabledMods = settings.IncludeDisabledMods;
        IncludeDisabledCheckBox.IsChecked = _includeDisabledMods;
        _createWorldSpaceMusicSettings = settings.CreateWorldSpaceMusicSettings;
        CreateWorldSpaceMusicSettingsCheckBox.IsChecked = _createWorldSpaceMusicSettings;
        _enableFileLogging = settings.EnableFileLogging;
        EnableFileLoggingCheckBox.IsChecked = _enableFileLogging;
        _language = UiLanguage.Normalize(settings.Language);
        LanguageComboBox.SelectedValue = _language;

        if (string.IsNullOrWhiteSpace(settings.Mo2Root))
        {
            GfMusicManagerLog.Info("RestoreSavedSettings: no saved MO2 root.");
            return;
        }

        var savedRoot = Path.GetFullPath(settings.Mo2Root);
        if (!TryValidateMo2Root(savedRoot, out _))
        {
            GfMusicManagerLog.Warning($"RestoreSavedSettings: saved MO2 root is invalid: {savedRoot}");
            return;
        }

        _mo2Root = savedRoot;
        ScanRootText.Text = savedRoot;
        LoadProfiles(savedRoot, settings.ProfileName, showErrorDialog: false);
        if (SettingsProfileComboBox.SelectedItem is string profileName)
        {
            _selectedProfileName = profileName;
        }

        UpdateMo2Summary();
        GfMusicManagerLog.Info(
            $"RestoreSavedSettings: restored root={savedRoot}, profile={_selectedProfileName ?? "<none>"}.");
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        GfMusicManagerLog.Info("RescanButton_Click: requested.");
        if (!TryGetScanConfiguration(out var mo2Root, out var errorMessage))
        {
            GfMusicManagerLog.Warning($"RescanButton_Click: configuration invalid: {errorMessage}");
            MessageBox.Show(
                errorMessage,
                UiText.Get("Main.Mo2RequiredTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            OpenSettingsOverlay();
            return;
        }

        if (_hasCompletedScan)
        {
            var confirmation = MessageBox.Show(
                UiText.Get("Main.RescanConfirmMessage"),
                UiText.Get("Main.RescanConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                GfMusicManagerLog.Info("RescanButton_Click: user canceled confirmation.");
                return;
            }
        }

        await ScanAsync(mo2Root);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        GfMusicManagerLog.Info("SettingsButton_Click: opening settings.");
        OpenSettingsOverlay();
    }

    private void MusicTypeManagementButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableMusicSettings.Count == 0)
        {
            GfMusicManagerLog.Warning("MusicTypeManagementButton_Click: no Music Type data is available.");
            return;
        }

        GfMusicManagerLog.Info(
            $"MusicTypeManagementButton_Click: opening Type management. " +
            $"settings={_availableMusicSettings.Count}, conflicts={_musicDefinitionConflicts.Count}.");
        var window = new MusicTypeManagementWindow(_availableMusicSettings, _musicDefinitionConflicts)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenSettingsOverlay()
    {
        SettingsMo2RootTextBox.Text = _mo2Root ?? string.Empty;
        SettingsProfileComboBox.SelectedIndex = -1;
        SettingsProfileComboBox.Text = string.IsNullOrWhiteSpace(_selectedProfileName)
            ? UiText.Get("Main.ProfileSelect")
            : _selectedProfileName;
        SettingsProfileStatusText.Text = string.IsNullOrWhiteSpace(_mo2Root)
            ? UiText.Get("Main.ProfileLoadStatus")
            : UiText.Get("Main.ProfileSaveInstruction");
        CreateWorldSpaceMusicSettingsCheckBox.IsChecked = _createWorldSpaceMusicSettings;
        SetSettingsRootStatus(
            string.IsNullOrWhiteSpace(_mo2Root)
                ? UiText.Get("Main.Unset")
                : UiText.Get("Main.CurrentSettings"),
            string.IsNullOrWhiteSpace(_mo2Root)
                ? "MutedForeground"
                : "GreenAccent");

        if (!string.IsNullOrWhiteSpace(_mo2Root))
        {
            LoadProfiles(_mo2Root, _selectedProfileName, showErrorDialog: false);
        }
        else
        {
            _profiles.Clear();
            SettingsProfileComboBox.IsEnabled = false;
        }

        UpdateDraftManagementState();
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void BrowseMo2RootButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = UiText.Get("Main.Mo2RootBrowseDescription"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = SettingsMo2RootTextBox.Text
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var selectedRoot = Path.GetFullPath(dialog.SelectedPath);
        if (!TryValidateMo2Root(selectedRoot, out var validationMessage))
        {
            MessageBox.Show(
                validationMessage,
                UiText.Get("Main.InvalidMo2RootTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var preferredProfile = string.Equals(selectedRoot, _mo2Root, StringComparison.OrdinalIgnoreCase)
            ? _selectedProfileName
            : null;
        SettingsMo2RootTextBox.Text = selectedRoot;
        SetSettingsRootStatus(UiText.Get("Main.Mo2RootValidated"), "GreenAccent");
        LoadProfiles(selectedRoot, preferredProfile);
        UpdateDraftManagementState();
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        GfMusicManagerLog.Info("SaveSettingsButton_Click: requested.");
        if (!TrySaveSettingsFromControls(out var languageChanged))
        {
            return;
        }

        if (languageChanged)
        {
            ReopenForLanguageChange();
        }
    }

    internal bool TrySaveSettingsFromControls(out bool languageChanged)
    {
        languageChanged = false;
        var previousWorldSpaceSetting = _createWorldSpaceMusicSettings;
        var previousLanguage = _language;
        var selectedRoot = SettingsMo2RootTextBox.Text.Trim();
        string? resolvedRoot = null;
        string? profileName = null;

        if (!string.IsNullOrWhiteSpace(selectedRoot))
        {
            if (!TryValidateMo2Root(selectedRoot, out var validationMessage))
            {
                GfMusicManagerLog.Warning($"SaveSettingsButton_Click: invalid root: {validationMessage}");
                MessageBox.Show(
                    validationMessage,
                    UiText.Get("Main.InvalidMo2RootTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (SettingsProfileComboBox.SelectedItem is not string selectedProfileName)
            {
                GfMusicManagerLog.Warning("SaveSettingsButton_Click: no profile selected.");
                MessageBox.Show(
                    UiText.Get("Main.ProfileNotSelectedMessage"),
                    UiText.Get("Main.ProfileNotSelectedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            resolvedRoot = Path.GetFullPath(selectedRoot);
            profileName = selectedProfileName;
        }

        _mo2Root = resolvedRoot;
        _selectedProfileName = profileName;
        _createWorldSpaceMusicSettings = CreateWorldSpaceMusicSettingsCheckBox.IsChecked == true;
        _enableFileLogging = EnableFileLoggingCheckBox.IsChecked == true;
        _language = UiLanguage.Normalize(LanguageComboBox.SelectedValue as string);
        GfMusicManagerLog.SetFileLoggingEnabled(_enableFileLogging);
        SaveSettings();
        if (previousWorldSpaceSetting != _createWorldSpaceMusicSettings)
        {
            QueueDraftSave();
        }
        UpdateMo2Summary();
        if (_mo2Root is not null)
        {
            SetScanRootStatus(UiText.Get("Main.ScanRootReady"), "GreenAccent");
        }
        SettingsOverlay.Visibility = Visibility.Collapsed;
        GfMusicManagerLog.Info(
            $"SaveSettingsButton_Click: saved root={_mo2Root ?? "<none>"}, " +
            $"profile={profileName ?? "<none>"}.");
        languageChanged = !string.Equals(previousLanguage, _language, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private void SettingsProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingProfiles || SettingsProfileComboBox.SelectedItem is not string profileName)
        {
            UpdateDraftManagementState();
            return;
        }

        SettingsProfileStatusText.Text = UiText.Format("Main.SelectedProfile", profileName);
        UpdateDraftManagementState();
    }

    private void UpdateDraftManagementState()
    {
        if (!TryGetSettingsDraftPath(out var path))
        {
            DraftStatusText.Text = UiText.Get("Main.DraftStatusNoConfig");
            DeleteDraftButton.IsEnabled = false;
            return;
        }

        var exists = File.Exists(path);
        DraftStatusText.Text = exists
            ? UiText.Get("Main.DraftStatusExists")
            : UiText.Get("Main.DraftStatusNone");
        DeleteDraftButton.IsEnabled = exists;
    }

    private bool TryGetSettingsDraftPath(out string path)
    {
        path = string.Empty;
        var root = SettingsMo2RootTextBox.Text.Trim();
        var profileName = SettingsProfileComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(profileName))
        {
            return false;
        }

        try
        {
            path = _draftStore.GetDraftPath(root, profileName);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void DeleteDraftButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedRoot = SettingsMo2RootTextBox.Text.Trim();
        if (!TryValidateMo2Root(selectedRoot, out var validationMessage))
        {
            GfMusicManagerLog.Warning(
                $"DeleteDraftButton_Click: invalid MO2 root: {validationMessage}");
            MessageBox.Show(
                validationMessage,
                UiText.Get("Main.InvalidMo2RootTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (SettingsProfileComboBox.SelectedItem is not string profileName ||
            string.IsNullOrWhiteSpace(profileName))
        {
            GfMusicManagerLog.Warning("DeleteDraftButton_Click: no profile selected.");
            MessageBox.Show(
                UiText.Get("Main.DraftDeleteNoProfile"),
                UiText.Get("Main.DraftDeleteTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string draftPath;
        try
        {
            draftPath = _draftStore.GetDraftPath(selectedRoot, profileName);
        }
        catch (ArgumentException exception)
        {
            GfMusicManagerLog.Exception(
                "DeleteDraftButton_Click: draft path could not be resolved",
                exception);
            MessageBox.Show(
                UiText.Get("Main.DraftPathUnavailable"),
                UiText.Get("Main.DraftDeleteTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (!File.Exists(draftPath))
        {
            UpdateDraftManagementState();
            return;
        }

        var confirmation = MessageBox.Show(
            UiText.Format("Main.DraftDeleteConfirm", profileName),
            UiText.Get("Main.DraftDeleteConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var normalizedRoot = Path.GetFullPath(selectedRoot);
        var isActiveProfile =
            string.Equals(_mo2Root, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_selectedProfileName, profileName, StringComparison.OrdinalIgnoreCase);
        if (isActiveProfile)
        {
            _draftSaveTimer.Stop();
        }

        if (!_draftStore.Delete(selectedRoot, profileName))
        {
            GfMusicManagerLog.Warning(
                $"DeleteDraftButton_Click: delete failed. profile={profileName}.");
            MessageBox.Show(
                UiText.Get("Main.DraftDeleteFailed"),
                UiText.Get("Main.DraftDeleteErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            UpdateDraftManagementState();
            return;
        }

        var restoredCurrentState = false;
        if (isActiveProfile)
        {
            restoredCurrentState = RestoreScanBaseline();
            _draftDirty = false;
            _draftPersistenceSuppressed = false;
        }

        DraftStatusText.Text = isActiveProfile && restoredCurrentState
            ? UiText.Get("Main.DraftDeletedAndRestored")
            : UiText.Get("Main.DraftDeleted");
        DeleteDraftButton.IsEnabled = false;
        GfMusicManagerLog.Info(
            $"DeleteDraftButton_Click: deleted. profile={profileName}, " +
            $"activeProfile={isActiveProfile}, restoredCurrentState={restoredCurrentState}.");
    }

    private bool RestoreScanBaseline()
    {
        if (_scanBaselineDraft is null || !_hasCompletedScan)
        {
            GfMusicManagerLog.Warning(
                "RestoreScanBaseline: baseline is unavailable; current state was not replaced.");
            return false;
        }

        _draftPersistenceSuppressed = true;
        try
        {
            RestoreDraftState(
                _generationPlan,
                _tracks,
                _scanBaselineDraft,
                _availableMusicSettings,
                replaceTrackPlans: true);
            _createWorldSpaceMusicSettings = _scanBaselineDraft.CreateWorldSpaceMusicSettings;
            _disableSourceEsp = _scanBaselineDraft.DisableSourceEsp;
            CreateWorldSpaceMusicSettingsCheckBox.IsChecked = _createWorldSpaceMusicSettings;
            DisableSourceEspCheckBox.IsChecked = _disableSourceEsp;
            RefreshSourceFilters();
            RefreshTrackView();
            if (TrackGrid.SelectedItem is TrackRow selectedRow)
            {
                UpdateSelectedTrack(selectedRow);
            }
            else
            {
                ClearSelectedTrack();
            }

            GfMusicManagerLog.Info(
                $"RestoreScanBaseline: complete. entries={_scanBaselineDraft.Entries.Count}, " +
                $"keepVanilla={_scanBaselineDraft.KeepVanillaMusic?.ToString() ?? "unset"}, " +
                $"worldSpace={_scanBaselineDraft.CreateWorldSpaceMusicSettings}.");
            return true;
        }
        finally
        {
            _draftDirty = false;
            _draftPersistenceSuppressed = false;
        }
    }

    private bool TryGetScanConfiguration(out string mo2Root, out string errorMessage)
    {
        mo2Root = _mo2Root ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_mo2Root))
        {
            errorMessage =
                UiText.Get("Main.Mo2ConfigMissing");
            return false;
        }

        if (!TryValidateMo2Root(_mo2Root, out errorMessage))
        {
            errorMessage = UiText.Format("Main.SavedMo2Invalid", errorMessage);
            return false;
        }

        if (string.IsNullOrWhiteSpace(_selectedProfileName))
        {
            errorMessage = UiText.Get("Main.NoProfileForScan");
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private void LoadProfiles(string mo2Root, string? preferredProfileName = null, bool showErrorDialog = true)
    {
        GfMusicManagerLog.Info(
            $"LoadProfiles: begin. root={mo2Root}, preferred={preferredProfileName ?? "<none>"}.");
        _isLoadingProfiles = true;
        try
        {
            _profiles.Clear();
            SettingsProfileComboBox.SelectedIndex = -1;
            SettingsProfileComboBox.Text = UiText.Get("Main.LoadingProfiles");

            foreach (var profileName in _scanner.GetProfileNames(mo2Root))
            {
                _profiles.Add(profileName);
            }
            GfMusicManagerLog.Info($"LoadProfiles: found {_profiles.Count} profile(s).");
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Exception("LoadProfiles failed", exception);
            SettingsProfileComboBox.IsEnabled = false;
            SettingsProfileComboBox.Text = UiText.Get("Main.ProfilesCannotLoad");
            SetSettingsRootStatus(UiText.Get("Main.ProfilesCannotCheck"), "RedAccent");
            SettingsProfileStatusText.Text = UiText.Get("Main.ProfilesLoadFailed");
            if (showErrorDialog)
            {
                MessageBox.Show(
                    exception.Message,
                    UiText.Get("Main.ProfileCheckTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return;
        }
        finally
        {
            _isLoadingProfiles = false;
        }

        SettingsProfileComboBox.IsEnabled = _profiles.Count > 0;
        if (_profiles.Count == 0)
        {
            GfMusicManagerLog.Warning("LoadProfiles: no profile with modlist.txt was found.");
            SettingsProfileComboBox.Text = UiText.Get("Main.ProfilesNotFound");
            SetSettingsRootStatus(UiText.Get("Main.ProfilesNotFound"), "RedAccent");
            SettingsProfileStatusText.Text = UiText.Get("Main.ProfilesNotFoundStatus");
            return;
        }

        var preferredProfile = _profiles.FirstOrDefault(profileName =>
            !string.IsNullOrWhiteSpace(preferredProfileName) &&
            profileName.Equals(preferredProfileName, StringComparison.OrdinalIgnoreCase));
        if (preferredProfile is not null)
        {
            SettingsProfileComboBox.SelectedItem = preferredProfile;
            SettingsProfileComboBox.Text = preferredProfile;
            SettingsProfileStatusText.Text = UiText.Format("Main.SavedProfile", preferredProfile);
            GfMusicManagerLog.Info($"LoadProfiles: selected preferred profile {preferredProfile}.");
            return;
        }

        if (_profiles.Count == 1)
        {
            SettingsProfileComboBox.SelectedIndex = 0;
            SettingsProfileComboBox.Text = _profiles[0];
            SettingsProfileStatusText.Text = UiText.Format("Main.ProfileStatus", _profiles[0]);
            GfMusicManagerLog.Info($"LoadProfiles: selected only profile {_profiles[0]}.");
            return;
        }

        SettingsProfileComboBox.Text = UiText.Get("Main.ProfileSelect");
        SettingsProfileStatusText.Text = UiText.Format("Main.ProfileCountPrompt", _profiles.Count);
        GfMusicManagerLog.Info("LoadProfiles: waiting for profile selection.");
    }

    private static bool TryValidateMo2Root(string path, out string message)
    {
        message = string.Empty;
        if (!Directory.Exists(path))
        {
            message = UiText.Format("Main.FolderNotFound", path);
            return false;
        }

        var modsPath = Path.Combine(path, "mods");
        var profilesPath = Path.Combine(path, "profiles");
        if (Directory.Exists(modsPath) && Directory.Exists(profilesPath))
        {
            return true;
        }

        var selectedFolder = new DirectoryInfo(path);
        var parent = selectedFolder.Parent;
        if (selectedFolder.Name.Equals("mods", StringComparison.OrdinalIgnoreCase) &&
            parent is not null &&
            Directory.Exists(Path.Combine(parent.FullName, "profiles")))
        {
            message = UiText.Format("Main.ModsFolderSelected", parent.FullName);
            return false;
        }

        if (selectedFolder.Name.Equals("profiles", StringComparison.OrdinalIgnoreCase) &&
            parent is not null &&
            Directory.Exists(Path.Combine(parent.FullName, "mods")))
        {
            message = UiText.Format("Main.ProfilesFolderSelected", parent.FullName);
            return false;
        }

        var missing = new List<string>();
        if (!Directory.Exists(modsPath))
        {
            missing.Add("mods");
        }

        if (!Directory.Exists(profilesPath))
        {
            missing.Add("profiles");
        }

        message = UiText.Format(
            "Main.MissingMo2Folders",
            string.Join("」「", missing));
        return false;
    }

    private void IncludeDisabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRestoringSettings)
        {
            return;
        }

        _includeDisabledMods = IncludeDisabledCheckBox.IsChecked == true;
        SaveSettings();
        GfMusicManagerLog.Info($"IncludeDisabledCheckBox_Changed: includeDisabled={_includeDisabledMods}.");
    }

    private async Task ScanAsync(string mo2Root)
    {
        if (_isScanning)
        {
            GfMusicManagerLog.Warning("ScanAsync: scan request ignored because another scan is active.");
            return;
        }

        var totalStopwatch = Stopwatch.StartNew();
        var scanCreateWorldSpaceMusicSettings = _createWorldSpaceMusicSettings;
        var scanDisableSourceEsp = _disableSourceEsp;
        GfMusicManagerLog.Info(
            $"ScanAsync: begin. root={mo2Root}, profile={_selectedProfileName ?? "<none>"}, " +
            $"includeDisabled={_includeDisabledMods}.");
        _isScanning = true;
        _applicationScanResult = null;
        RescanButton.IsEnabled = false;
        SettingsButton.IsEnabled = false;
        MusicTypeManagementButton.IsEnabled = false;
        FilterButton.IsEnabled = false;
        IncludeDisabledCheckBox.IsEnabled = false;
        ScanRootText.Text = mo2Root;
        SetScanRootStatus(UiText.Get("Main.ScanRootScanning"), "AmberAccent");
        LibrarySubheadingText.Text = UiText.Get("Main.ScanningLibrary");
        BeginScanProgressOverlay();
        MusicAnalysisResult? completedMusicAnalysis = null;
        ExistingMusicProductLoadResult? completedExistingProduct = null;
        MusicGenerationPlanRestoreResult? completedPlanRestore = null;
        try
        {
            IProgress<ScanProgress> progress = new Progress<ScanProgress>(item =>
            {
                var displayText = MusicScanProgressFormatter.Format(item);
                LibrarySubheadingText.Text = GetLibraryScanSummary(item.Stage);
                UpdateScanProgressOverlay(item);
                GfMusicManagerLog.Info(
                    $"ScanAsync progress: stage={item.Stage}, current={item.Current?.ToString() ?? "-"}, " +
                    $"total={item.Total?.ToString() ?? "-"}, mod={item.ModName ?? "-"}, " +
                    $"plugin={item.PluginName ?? "-"}, message={item.Message}.");
            });
            var scanRequest = new MusicScanRequest
            {
                Mo2Root = mo2Root,
                ProfileName = _selectedProfileName,
                IncludeDisabledMods = _includeDisabledMods,
                ReadPluginRecords = true,
                ScanArchives = true,
                ScanLooseAssets = true
            };
            GfMusicManagerLog.Info(
                "ScanAsync: application scan request prepared. ReadPluginRecords=true, " +
                "ScanArchives=true, ScanLooseAssets=true, " +
                $"includeGenerated={scanRequest.IncludeGeneratedProduct}.");
            var scanResult = await Task.Run(() =>
            {
                var workerStopwatch = Stopwatch.StartNew();
                var existingProduct = _existingMusicProductLoader.Load(mo2Root);
                var draft = string.IsNullOrWhiteSpace(_selectedProfileName)
                    ? null
                    : _draftStore.Load(mo2Root, _selectedProfileName);
                GfMusicManagerLog.Info(
                    $"ScanAsync worker: existing GF Music Product detected={existingProduct.IsDetected}, " +
                    $"complete={existingProduct.IsComplete}, warnings={existingProduct.Warnings.Count}.");
                GfMusicManagerLog.Info(
                    $"ScanAsync worker: profile draft detected={draft is not null}, " +
                    $"entries={draft?.Entries.Count ?? 0}.");
                GfMusicManagerLog.Info("ScanAsync worker: application scan begin.");
                var applicationScan = _musicScanApplicationService.Scan(scanRequest, progress);
                var result = applicationScan.Scan;
                GfMusicManagerLog.Info(
                    $"ScanAsync worker: application scan complete in {workerStopwatch.Elapsed}. " +
                    $"mods={result.Mods.Count}, plugins={result.Plugins.Count}, records={result.Records.Count}, " +
                    $"assets={result.Assets.Count}, warnings={result.WarningCount}.");
                var musicAnalysis = applicationScan.MusicAnalysis;
                GfMusicManagerLog.Info(
                    $"ScanAsync worker: music analysis complete. settings={musicAnalysis.Settings.Count}, " +
                    $"mappedAssets={musicAnalysis.SettingsByAssetPath.Count}, issues={musicAnalysis.Issues.Count}.");
                var audioDuplicateAnalysis = applicationScan.AudioDuplicates;
                GfMusicManagerLog.Info(
                    $"ScanAsync worker: audio duplicate analysis complete. groups={audioDuplicateAnalysis.Groups.Count}, " +
                    $"path={audioDuplicateAnalysis.PathConflictCount}, content={audioDuplicateAnalysis.ContentMatchCount}, " +
                    $"similar={audioDuplicateAnalysis.SimilarCandidateCount}, failures={audioDuplicateAnalysis.ReadFailureCount}.");
                var orderedAssets = result.Assets
                    .OrderBy(item => item.ModName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.VirtualPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var resultPreparationStopwatch = Stopwatch.StartNew();
                progress?.Report(new ScanProgress(
                    ScanIssueSeverity.Info,
                    "ResultPlan",
                    UiText.Get("Main.AudioDataOrganizing"),
                    0,
                    orderedAssets.Length));
                var availableMusicSettings = musicAnalysis.Settings
                    .OrderBy(setting => setting.Scope)
                    .ThenBy(setting => setting.ScopeName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var existingMtdFileNames =
                    MusicTypeDistributorOutputNameResolver.DiscoverExistingFileNames(
                        result.Mods);
                var planStopwatch = Stopwatch.StartNew();
                var planProgress = new Progress<MusicGenerationProgress>(item =>
                    progress?.Report(new ScanProgress(
                        ScanIssueSeverity.Info,
                        "ResultPlan",
                        item.Message,
                        item.Current,
                        item.Total)));
                var planPreparation = _musicPlanApplicationService.PreparePlan(
                    applicationScan,
                    planProgress);
                var generationPlan = planPreparation.Plan;
                var assetBindings = planPreparation.AssetBindings;
                GfMusicManagerLog.Info(
                    $"ScanAsync worker: audio list plan prepared. entries={generationPlan.Entries.Count}, " +
                    $"elapsed={planStopwatch.Elapsed}.");
                var duplicateAssetsByPath = result.Assets
                    .GroupBy(
                        asset => NormalizeAssetPathForDuplicate(asset.VirtualPath),
                        StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1 && AudioDuplicateDetector.ContainsMultipleMods(group))
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<AssetSource>)group.ToArray(),
                        StringComparer.OrdinalIgnoreCase);
                var audioDuplicateGroupsByAssetKey = audioDuplicateAnalysis.Groups
                    .SelectMany(group => group.Sources.Select(source => new
                    {
                        source.AssetKey,
                        Group = group
                    }))
                    .GroupBy(item => item.AssetKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<AudioDuplicateGroup>)group
                            .Select(item => item.Group)
                            .DistinctBy(item => item.GroupId, StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        StringComparer.OrdinalIgnoreCase);
                progress?.Report(new ScanProgress(
                    ScanIssueSeverity.Info,
                    "ResultPrepare",
                    UiText.Get("Main.AudioListCreating"),
                    0,
                    orderedAssets.Length));
                var rowPreparationStopwatch = Stopwatch.StartNew();
                var tracks = new TrackRow[orderedAssets.Length];
                for (var index = 0; index < orderedAssets.Length; index++)
                {
                    var asset = orderedAssets[index];
                    var duplicateSources = duplicateAssetsByPath.TryGetValue(
                        NormalizeAssetPathForDuplicate(asset.VirtualPath),
                        out var matches)
                        ? matches
                        : Array.Empty<AssetSource>();
                    var assetKey = MusicGenerationPlanEntry.CreateAssetKey(asset);
                    var audioDuplicateGroups = audioDuplicateGroupsByAssetKey.TryGetValue(
                        assetKey,
                        out var duplicateGroups)
                        ? duplicateGroups
                        : Array.Empty<AudioDuplicateGroup>();
                    tracks[index] = TrackRow.FromAsset(
                        asset,
                        musicAnalysis,
                        generationPlan,
                        availableMusicSettings,
                        duplicateSources,
                        audioDuplicateGroups,
                        assetBindings.Get(asset.VirtualPath));
                    if ((index + 1) % 16 == 0 || index + 1 == orderedAssets.Length)
                    {
                        progress?.Report(new ScanProgress(
                            ScanIssueSeverity.Info,
                            "ResultPrepare",
                            UiText.Get("Main.AudioListCreating"),
                            index + 1,
                            orderedAssets.Length,
                            asset.ModName,
                            asset.VirtualPath));
                    }
                }
                GfMusicManagerLog.Info(
                    $"ScanAsync worker: audio list rows prepared. rows={tracks.Length}, " +
                    $"elapsed={rowPreparationStopwatch.Elapsed}.");
                var restoreStopwatch = Stopwatch.StartNew();
                var planRestore = existingProduct.Manifest is null
                    ? MusicGenerationPlanRestoreResult.Empty
                    : _musicGenerationPlanRestorer.Restore(
                        generationPlan,
                        existingProduct.Manifest,
                        availableMusicSettings,
                        progress);
                foreach (var track in tracks.Where(track =>
                             planRestore.RestoredAssetKeys.Contains(
                                 track.GenerationPlanEntry.AssetKey)))
                {
                    track.ApplyRestoredPlanState(availableMusicSettings);
                }
                var draftAssetKeys = (draft?.Entries ?? Array.Empty<GfMusicManagerDraftEntry>())
                    .Select(entry => entry.AssetKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var defaultPathConflictChanges = planRestore.HasCompleteEntryState
                    ? 0
                    : _musicPlanApplicationService.ApplyDefaultPathConflictSelection(
                        generationPlan,
                        audioDuplicateAnalysis,
                        result.Mods,
                        draftAssetKeys);
                var scanBaselineDraft = CreateDraftStateSnapshot(
                    generationPlan,
                    mo2Root,
                    result.Profile.ProfileName,
                    scanCreateWorldSpaceMusicSettings,
                    scanDisableSourceEsp);
                var draftRestore = RestoreDraftState(
                    generationPlan,
                    tracks,
                    draft,
                    availableMusicSettings);
                GfMusicManagerLog.Info(
                    $"ScanAsync worker: existing state restored. manifestEntries={planRestore.RestoredEntryCount}, " +
                    $"draftEntries={draftRestore.RestoredEntryCount}, elapsed={restoreStopwatch.Elapsed}.");
                progress?.Report(new ScanProgress(
                    ScanIssueSeverity.Info,
                    "ResultFinalize",
                    UiText.Get("Main.AudioListApplying"),
                    1,
                    1));
                GfMusicManagerLog.Info(
                    $"ScanAsync worker: track row preparation complete. rows={tracks.Length}, " +
                    $"duplicateGroups={duplicateAssetsByPath.Count}, restoredEntries={planRestore.RestoredEntryCount}, " +
                    $"defaultPathConflictChanges={defaultPathConflictChanges}, " +
                    $"draftRestoredEntries={draftRestore.RestoredEntryCount}, " +
                    $"resultPreparationElapsed={resultPreparationStopwatch.Elapsed}, " +
                    $"workerElapsed={workerStopwatch.Elapsed}.");
                return (
                    ApplicationScan: applicationScan,
                    Result: result,
                    MusicAnalysis: musicAnalysis,
                    AudioDuplicateAnalysis: audioDuplicateAnalysis,
                    GenerationPlan: generationPlan,
                    ExistingMtdFileNames: existingMtdFileNames,
                    Tracks: tracks,
                    ExistingProduct: existingProduct,
                    PlanRestore: planRestore,
                    ScanBaselineDraft: scanBaselineDraft,
                    DraftRestore: draftRestore);
            });
            _mo2Root = mo2Root;
            _applicationScanResult = scanResult.ApplicationScan;
            _existingMtdFileNames = scanResult.ExistingMtdFileNames;
            ApplyScanResult(
                scanResult.Result,
                scanResult.MusicAnalysis,
                scanResult.AudioDuplicateAnalysis,
                scanResult.GenerationPlan,
                scanResult.Tracks,
                scanResult.ExistingProduct,
                scanResult.PlanRestore,
                scanResult.ScanBaselineDraft,
                scanResult.DraftRestore);
            _hasCompletedScan = true;
            completedMusicAnalysis = scanResult.MusicAnalysis;
            completedExistingProduct = scanResult.ExistingProduct;
            completedPlanRestore = scanResult.PlanRestore;
            GfMusicManagerLog.Info($"ScanAsync: complete in {totalStopwatch.Elapsed}.");
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Exception("ScanAsync failed", exception);
            SetScanRootStatus(UiText.Get("Main.ScanFailed"), "RedAccent");
            LibrarySubheadingText.Text = UiText.Get("Main.ScanFailedMessage");
            MessageBox.Show(
                exception.Message,
                UiText.Get("Main.ScanErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isScanning = false;
            RescanButton.IsEnabled = true;
            SettingsButton.IsEnabled = true;
            MusicTypeManagementButton.IsEnabled =
                completedMusicAnalysis is not null && completedMusicAnalysis.Settings.Count > 0;
            FilterButton.IsEnabled = true;
            IncludeDisabledCheckBox.IsEnabled = true;
            EndScanProgressOverlay();
            GfMusicManagerLog.Info($"ScanAsync: finished. elapsed={totalStopwatch.Elapsed}.");
            var existingMusicProductDetected = completedExistingProduct is { IsDetected: true };
            if (!existingMusicProductDetected &&
                completedMusicAnalysis?.AdditionalMusicProjectRepair.IsDetected == true)
            {
                var repairReport = completedMusicAnalysis.AdditionalMusicProjectRepair;
                GfMusicManagerLog.Info(
                    $"ScanAsync: AMP notice shown. pathRepairs={repairReport.AudioPathRepairs.Count}, " +
                    $"combatTracks={repairReport.CombatTrackCount}, " +
                    $"unresolved={repairReport.UnresolvedAudioRepairs.Count}.");
                ShowAdditionalMusicProjectRepairNotice(repairReport);
            }
            if (!existingMusicProductDetected &&
                completedMusicAnalysis?.FantasySoundtrackProjectRepair.IsDetected == true)
            {
                var repairReport = completedMusicAnalysis.FantasySoundtrackProjectRepair;
                GfMusicManagerLog.Info(
                    $"ScanAsync: FSP notice shown. pathRepairs={repairReport.AudioPathRepairs.Count}, " +
                    $"unresolved={repairReport.UnresolvedAudioRepairs.Count}.");
                ShowFantasySoundtrackProjectRepairNotice(repairReport);
            }
            if (completedExistingProduct is { IsDetected: true } existingProduct)
            {
                ShowExistingMusicProductNotice(
                    existingProduct,
                    completedPlanRestore ?? MusicGenerationPlanRestoreResult.Empty,
                    _includeDisabledMods);
            }
        }
    }

    private static void ShowExistingMusicProductNotice(
        ExistingMusicProductLoadResult existingProduct,
        MusicGenerationPlanRestoreResult planRestore,
        bool includeDisabledMods)
    {
        var message = existingProduct.IsComplete
            ? UiText.Get("Main.ExistingProductComplete")
            : UiText.Get("Main.ExistingProductIncomplete");
        message += Environment.NewLine + Environment.NewLine +
                   UiText.Format("Main.RestoredAudioCount", planRestore.RestoredEntryCount);
        if (planRestore.MissingEntryCount > 0)
        {
            message += Environment.NewLine +
                       UiText.Format("Main.MissingSavedAudioCount", planRestore.MissingEntryCount);
            if (planRestore.MissingEntriesByMod.Count > 0)
            {
                var modSummary = planRestore.MissingEntriesByMod
                    .OrderByDescending(item => item.Value)
                    .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => UiText.Format("Main.MissingModSummary", item.Key, item.Value));
                message += Environment.NewLine +
                           UiText.Format(
                               "Main.TargetMods",
                               string.Join(UiText.Get("Main.ListSeparator"), modSummary));
            }

            message += includeDisabledMods
                ? Environment.NewLine + UiText.Get("Main.CheckSourceMissing")
                : Environment.NewLine + UiText.Get("Main.IncludeDisabledRescan");
        }

        var missingWarningPrefix = UiText.Get("Main.MissingSavedAudioPrefix");
        var warnings = existingProduct.Warnings
            .Concat(planRestore.Warnings.Where(warning =>
                !warning.StartsWith(missingWarningPrefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (warnings.Length > 0)
        {
            message += Environment.NewLine + Environment.NewLine +
                       UiText.Format("Main.RequiredReview", string.Join(Environment.NewLine, warnings));
            if (existingProduct.Warnings.Count + planRestore.Warnings.Count > warnings.Length)
            {
                message += Environment.NewLine + "・" + UiText.Get("Main.OtherReviewInLog");
            }
        }

        MessageBox.Show(
            message,
            UiText.Get("Main.ExistingProductTitle"),
            MessageBoxButton.OK,
            existingProduct.IsComplete
                ? MessageBoxImage.Information
                : MessageBoxImage.Warning);
    }

    private void ShowAdditionalMusicProjectRepairNotice(
        AdditionalMusicProjectRepairReport repairReport)
    {
        var details = UiText.Get("Main.AmpDetails");
        var unresolved = repairReport.UnresolvedAudioRepairs.Count == 0
            ? string.Empty
            : UiText.Format(
                "Main.UnresolvedRepairWarning",
                repairReport.UnresolvedAudioRepairs.Count);
        MessageBox.Show(
            this,
            UiText.Format("Main.AmpNotice", details, unresolved),
            UiText.Get("Main.AmpDetectedTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ShowFantasySoundtrackProjectRepairNotice(
        FantasySoundtrackProjectRepairReport repairReport)
    {
        var details = UiText.Get("Main.FspDetails");
        var unresolved = repairReport.UnresolvedAudioRepairs.Count == 0
            ? string.Empty
            : UiText.Format(
                "Main.UnresolvedRepairWarning",
                repairReport.UnresolvedAudioRepairs.Count);
        MessageBox.Show(
            this,
            UiText.Format("Main.FspNotice", details, unresolved),
            UiText.Get("Main.FspDetectedTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void BeginScanProgressOverlay()
    {
        _scanModCurrent = 0;
        _scanModTotal = 0;
        _scanPluginCurrent = 0;
        _scanPluginTotal = 0;
        _scanConflictCurrent = 0;
        _scanConflictTotal = 0;
        _scanResultCurrent = 0;
        _scanResultTotal = 0;
        ScanProgressBar.Value = 0;
        ScanProgressTitleText.Text = UiText.Get("Main.Scanning");
        ScanProgressModText.Text = UiText.Format("Main.ProgressMods", 0, 0);
        ScanProgressPluginText.Text = UiText.Format("Main.ProgressPlugins", 0, 0);
        ScanProgressCountPanel.Visibility = Visibility.Visible;
        ScanProgressSummaryText.Visibility = Visibility.Collapsed;
        ScanProgressSummaryText.Text = string.Empty;
        ScanProgressStageText.Text = UiText.Get("Main.Preparing");
        ScanProgressStageText.ToolTip = null;
        ScanProgressOverlay.Visibility = Visibility.Visible;
        GfMusicManagerLog.Info("ScanProgressOverlay: shown.");
    }

    private void UpdateScanProgressOverlay(ScanProgress item)
    {
        var isConflictStage = IsConflictStage(item.Stage);
        var isResultStage = IsResultStage(item.Stage);
        if (string.Equals(item.Stage, "MOD", StringComparison.OrdinalIgnoreCase))
        {
            _scanModCurrent = Math.Max(0, item.Current ?? _scanModCurrent);
            _scanModTotal = Math.Max(0, item.Total ?? _scanModTotal);
        }
        else if (string.Equals(item.Stage, "Plugin", StringComparison.OrdinalIgnoreCase))
        {
            _scanPluginCurrent = Math.Max(0, item.Current ?? _scanPluginCurrent);
            _scanPluginTotal = Math.Max(0, item.Total ?? _scanPluginTotal);
        }
        else if (isConflictStage)
        {
            _scanConflictCurrent = Math.Max(0, item.Current ?? _scanConflictCurrent);
            _scanConflictTotal = Math.Max(0, item.Total ?? _scanConflictTotal);
        }
        else if (isResultStage)
        {
            _scanResultCurrent = Math.Max(0, item.Current ?? _scanResultCurrent);
            _scanResultTotal = Math.Max(0, item.Total ?? _scanResultTotal);
        }

        ScanProgressTitleText.Text = isConflictStage
            ? UiText.Get("Main.ProgressConflictChecking")
            : isResultStage
                ? UiText.Get("Main.ProgressResultApplying")
                : UiText.Get("Main.Scanning");
        var summaryText = isConflictStage
            ? UiText.Format(
                "Main.ConflictProgress",
                _scanConflictCurrent.ToString("N0"),
                _scanConflictTotal.ToString("N0"))
            : isResultStage
                ? GetResultSummary(item.Stage)
                : string.Empty;
        ScanProgressModText.Text = UiText.Format(
            "Main.ProgressMods",
            _scanModCurrent.ToString("N0"),
            _scanModTotal.ToString("N0"));
        ScanProgressPluginText.Text = UiText.Format(
            "Main.ProgressPlugins",
            _scanPluginCurrent.ToString("N0"),
            _scanPluginTotal.ToString("N0"));
        var showSeparateCounts = !isConflictStage && !isResultStage;
        ScanProgressCountPanel.Visibility = showSeparateCounts
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScanProgressSummaryText.Visibility = showSeparateCounts
            ? Visibility.Collapsed
            : Visibility.Visible;
        ScanProgressSummaryText.Text = summaryText;
        var currentSourceText = MusicScanProgressFormatter.Format(item);
        ScanProgressStageText.Text = currentSourceText;
        ScanProgressStageText.ToolTip = currentSourceText;

        var pluginProgress = _scanPluginTotal == 0
            ? 100
            : GetProgressPercent(_scanPluginCurrent, _scanPluginTotal);
        var progress = isConflictStage
            ? GetConflictProgress(item.Stage, _scanConflictCurrent, _scanConflictTotal)
            : isResultStage
                ? GetResultProgress(item.Stage, _scanResultCurrent, _scanResultTotal)
            : string.Equals(item.Stage, "Plugin", StringComparison.OrdinalIgnoreCase)
                ? 50 + pluginProgress / 2
                : GetProgressPercent(_scanModCurrent, _scanModTotal) / 2;
        ScanProgressBar.Value = Math.Clamp(progress, 0, 100);
    }

    private static bool IsConflictStage(string stage) =>
        stage.StartsWith("Conflict", StringComparison.OrdinalIgnoreCase) ||
        stage.Equals("Audio", StringComparison.OrdinalIgnoreCase);

    private static bool IsResultStage(string stage) =>
        stage.StartsWith("Result", StringComparison.OrdinalIgnoreCase);

    private string GetResultSummary(string stage) =>
        stage.Equals("ResultRestore", StringComparison.OrdinalIgnoreCase)
            ? UiText.Format(
                "Main.ResultRestoreProgress",
                _scanResultCurrent.ToString("N0"),
                _scanResultTotal.ToString("N0"))
            : stage.Equals("ResultFinalize", StringComparison.OrdinalIgnoreCase)
                ? UiText.Get("Main.ResultApplying")
                : stage.Equals("ResultPlan", StringComparison.OrdinalIgnoreCase)
                    ? UiText.Format(
                        "Main.ResultPlanProgress",
                        _scanResultCurrent.ToString("N0"),
                        _scanResultTotal.ToString("N0"))
                    : UiText.Format(
                        "Main.ResultListProgress",
                        _scanResultCurrent.ToString("N0"),
                        _scanResultTotal.ToString("N0"));

    private static string GetLibraryScanSummary(string stage) =>
        IsConflictStage(stage)
            ? UiText.Get("Main.LibraryConflictProgress")
            : stage.Equals("ResultRestore", StringComparison.OrdinalIgnoreCase)
                ? UiText.Get("Main.LibraryRestoreProgress")
            : stage.Equals("ResultPlan", StringComparison.OrdinalIgnoreCase)
                ? UiText.Get("Main.LibraryPlanProgress")
            : IsResultStage(stage)
                ? UiText.Get("Main.LibraryListProgress")
            : UiText.Get("Main.LibraryScanProgress");

    private static double GetConflictProgress(string stage, int current, int total)
    {
        var phaseIndex = stage switch
        {
            "ConflictRead" => 0,
            "ConflictFingerprint" => 1,
            "ConflictCompare" => 2,
            "ConflictFinalize" => 3,
            _ => 0
        };
        var phaseProgress = GetProgressPercent(current, total) / 100d;
        return 75 + ((phaseIndex + phaseProgress) / 5d * 25d);
    }

    private static double GetResultProgress(string stage, int current, int total)
    {
        var phaseIndex = stage switch
        {
            "ResultPlan" => 0,
            "ResultPrepare" => 1,
            "ResultRestore" => 2,
            "ResultFinalize" => 3,
            _ => 0
        };
        var phaseProgress = GetProgressPercent(current, total) / 100d;
        return 95 + ((phaseIndex + phaseProgress) / 4d * 5d);
    }

    private static double GetProgressPercent(int current, int total) =>
        total <= 0 ? 0 : Math.Clamp(current * 100d / total, 0, 100);

    private void EndScanProgressOverlay()
    {
        ScanProgressOverlay.Visibility = Visibility.Collapsed;
        GfMusicManagerLog.Info("ScanProgressOverlay: hidden.");
    }

    private void BeginGenerationProgressOverlay()
    {
        ScanProgressBar.Value = 0;
        ScanProgressTitleText.Text = UiText.Get("Main.GenerationProgress");
        ScanProgressCountPanel.Visibility = Visibility.Collapsed;
        ScanProgressSummaryText.Visibility = Visibility.Visible;
        ScanProgressSummaryText.Text = UiText.Get("Main.GenerationPreparing");
        ScanProgressStageText.Text = UiText.Get("Main.GenerationWaiting");
        ScanProgressStageText.ToolTip = null;
        ScanProgressOverlay.Visibility = Visibility.Visible;
        GfMusicManagerLog.Info("GenerationProgressOverlay: shown.");
    }

    private void UpdateGenerationProgressOverlay(MusicGenerationProgress item)
    {
        ScanProgressTitleText.Text = item.Stage == MusicGenerationProgressStage.Diagnosing
            ? UiText.Get("Main.GenerationDiagnosing")
            : UiText.Get("Main.GenerationProgress");
        ScanProgressBar.Value = Math.Clamp(item.Percent, 0, 100);
        ScanProgressSummaryText.Visibility = Visibility.Visible;
        ScanProgressSummaryText.Text = item.Total > 0
            ? UiText.Format("Main.ProgressCurrentTotal", item.Message, item.Current, item.Total)
            : item.Message;
        ScanProgressStageText.Text = item.Message;
        ScanProgressStageText.ToolTip = item.Message;
    }

    private void EndGenerationProgressOverlay()
    {
        ScanProgressOverlay.Visibility = Visibility.Collapsed;
        GfMusicManagerLog.Info("GenerationProgressOverlay: hidden.");
    }

    private void ApplyScanResult(
        ScanResult result,
        MusicAnalysisResult musicAnalysis,
        AudioDuplicateAnalysisResult audioDuplicateAnalysis,
        MusicGenerationPlan generationPlan,
        IReadOnlyList<TrackRow> tracks,
        ExistingMusicProductLoadResult existingProduct,
        MusicGenerationPlanRestoreResult planRestore,
        GfMusicManagerDraft scanBaselineDraft,
        DraftRestoreSummary draftRestore)
    {
        var stopwatch = Stopwatch.StartNew();
        GfMusicManagerLog.Info(
            $"ApplyScanResult: begin. rows={tracks.Count}, settings={musicAnalysis.Settings.Count}, " +
            $"definitionConflicts={musicAnalysis.DefinitionConflicts.Count}.");
        _generationPlan = generationPlan;
        _scanBaselineDraft = scanBaselineDraft;
        _draftPersistenceSuppressed = false;
        _draftDirty = false;
        _availableMusicSettings = musicAnalysis.Settings;
        _musicDefinitionConflicts = musicAnalysis.DefinitionConflicts;
        _audioDuplicateGroups = audioDuplicateAnalysis.Groups;
        _scannedMods = result.Mods;
        _modPriorities = result.Mods
            .ToDictionary(
                mod => mod.Name,
                mod => mod.Priority,
                StringComparer.OrdinalIgnoreCase);
        if (existingProduct.Manifest is not null)
        {
            GfMusicManagerLog.Info(
                $"ApplyScanResult: existing product state restored. " +
                $"KeepVanilla={generationPlan.KeepVanillaMusic}; " +
                $"WorldSpaceIndividualAssignment preserved from current settings=" +
                $"{_createWorldSpaceMusicSettings}.");
        }
        if (draftRestore.Draft is not null)
        {
            _createWorldSpaceMusicSettings = draftRestore.Draft.CreateWorldSpaceMusicSettings;
            _disableSourceEsp = draftRestore.Draft.DisableSourceEsp;
            CreateWorldSpaceMusicSettingsCheckBox.IsChecked = _createWorldSpaceMusicSettings;
            DisableSourceEspCheckBox.IsChecked = _disableSourceEsp;
            _draftPersistenceSuppressed = false;
            _draftDirty = false;
            GfMusicManagerLog.Info(
                $"ApplyScanResult: profile draft restored. " +
                $"entries={draftRestore.RestoredEntryCount}, missing={draftRestore.MissingEntryCount}, " +
                $"keepVanilla={_generationPlan.KeepVanillaMusic?.ToString() ?? "unset"}, " +
                $"worldSpace={_createWorldSpaceMusicSettings}, disableSourceEsp={_disableSourceEsp}.");
        }
        ReplaceTrackCollection(tracks);
        GfMusicManagerLog.Info($"ApplyScanResult: collection replaced in {stopwatch.Elapsed}.");

        RefreshSourceFilters();
        _sourceFilter = null;
        _filterMode = FilterAll;
        _musicFilterOptions = MusicFilterOptions.Empty;
        _selectedProfileName = result.Profile.ProfileName;
        _scannedMo2Root = _mo2Root;
        _scannedProfileName = _selectedProfileName;
        SaveSettings();
        UpdateMo2Summary();
        SetScanRootStatus(UiText.Get("Main.ScanCompleted"), "GreenAccent");
        GfMusicManagerLog.Info(
            $"ApplyScanResult: audioDuplicateGroups={audioDuplicateAnalysis.Groups.Count}, " +
            $"path={audioDuplicateAnalysis.PathConflictCount}, " +
            $"content={audioDuplicateAnalysis.ContentMatchCount}, " +
            $"similar={audioDuplicateAnalysis.SimilarCandidateCount}, " +
            $"definitionInfoGroups={musicAnalysis.DefinitionConflicts.Count}. " +
            "Audio duplicate groups require user review; definition groups are detail information.");
        var scanIssueWarningCount = result.WarningCount + musicAnalysis.Issues.Count(issue =>
            issue.Severity is ScanIssueSeverity.Warning or ScanIssueSeverity.Error);
        var disabledTrackCount = tracks.Count(track =>
            track.Asset is not null && !track.Asset.ModEnabled);
        var scanIssueWarningSuffix = scanIssueWarningCount > 0
            ? UiText.Format("Main.OtherWarnings", scanIssueWarningCount)
            : string.Empty;
        ScanSummaryText.Text = UiText.Format(
            "Main.ScanSummary",
            result.BsaAssetCount,
            result.Mods.Count,
            musicAnalysis.Settings.Count,
            _audioDuplicateGroups.Count,
            disabledTrackCount,
            scanIssueWarningSuffix);
        LibrarySubheadingText.Text = UiText.Format(
            "Main.LibraryScanCompleted",
            result.Profile.ProfileName,
            result.AudioAssetCount,
            musicAnalysis.Settings.Count,
            result.Plugins.Count);
        AllSourcesButtonText.Text = UiText.Format("Main.AllModsCount", result.AudioAssetCount);
        UpdateReviewSummary();

        RefreshTrackView();
        if (_tracks.Count > 0)
        {
            TrackGrid.SelectedIndex = 0;
        }
        else
        {
            ClearSelectedTrack();
        }
        UpdateEmptyLibraryState();
        GfMusicManagerLog.Info($"ApplyScanResult: complete in {stopwatch.Elapsed}.");
    }

    private static GfMusicManagerDraft CreateDraftStateSnapshot(
        MusicGenerationPlan plan,
        string mo2Root,
        string profileName,
        bool createWorldSpaceMusicSettings,
        bool disableSourceEsp)
    {
        return new GfMusicManagerDraft(
            GfMusicManagerDraftStore.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            mo2Root,
            profileName,
            plan.KeepVanillaMusic,
            createWorldSpaceMusicSettings,
            disableSourceEsp,
            plan.Entries
                .Select(entry => new GfMusicManagerDraftEntry(
                    entry.AssetKey,
                    entry.IsAdopted,
                    entry.DestinationKeys.ToArray(),
                    entry.Conditions.ToArray())
                {
                    Tracks = entry.Tracks
                        .Select(track => new GfMusicManagerDraftTrack(
                            track.TrackKey,
                            track.Conditions.ToArray()))
                        .ToArray()
                })
                .ToArray());
    }

    private static DraftRestoreSummary RestoreDraftState(
        MusicGenerationPlan plan,
        IReadOnlyList<TrackRow> tracks,
        GfMusicManagerDraft? draft,
        IReadOnlyList<MusicSettingSource> availableMusicSettings,
        bool replaceTrackPlans = false)
    {
        if (draft is null)
        {
            return DraftRestoreSummary.Empty;
        }

        plan.KeepVanillaMusic = draft.KeepVanillaMusic;
        var entriesByAssetKey = plan.Entries.ToDictionary(
            entry => entry.AssetKey,
            StringComparer.OrdinalIgnoreCase);
        var settingsByKey = availableMusicSettings
            .GroupBy(TrackRow.GetMusicSettingKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var restoredAssetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingEntryCount = 0;

        foreach (var savedEntry in draft.Entries)
        {
            if (!entriesByAssetKey.TryGetValue(savedEntry.AssetKey, out var entry))
            {
                missingEntryCount++;
                continue;
            }

            entry.IsAdopted = savedEntry.IsAdopted;
            var destinationKeys = savedEntry.DestinationKeys ?? Array.Empty<MusicSettingKey>();
            var destinations = destinationKeys
                .Select(key => settingsByKey.TryGetValue(
                    TrackRow.GetMusicSettingKey(key),
                    out var setting)
                    ? setting
                    : null)
                .Where(setting => setting is not null)
                .Cast<MusicSettingSource>()
                .ToArray();
            if (destinationKeys.Count == 0 || destinations.Length > 0)
            {
                entry.ReplaceDestinations(destinations);
            }

            if (savedEntry.Tracks is { Count: > 0 })
            {
                var trackPlans = savedEntry.Tracks.Select(track =>
                    new MusicGenerationTrackPlan(track.TrackKey, track.Conditions));
                if (replaceTrackPlans)
                {
                    entry.ReplaceTrackPlans(trackPlans);
                }
                else
                {
                    entry.ApplyTrackConditions(trackPlans);
                }
            }
            else if (replaceTrackPlans)
            {
                entry.ReplaceTrackPlans(Array.Empty<MusicGenerationTrackPlan>());
            }
            else if (entry.TryReplaceLegacyConditions(
                         savedEntry.Conditions ?? Array.Empty<MusicConditionSource>()))
            {
            }
            else if (savedEntry.Conditions is { Count: > 0 })
            {
                GfMusicManagerLog.Warning(
                    $"RestoreDraftState: legacy aggregate conditions ignored for multi-track entry. " +
                    $"assetKey={savedEntry.AssetKey}, tracks={entry.Tracks.Count}, " +
                    $"conditions={savedEntry.Conditions.Count}.");
            }
            restoredAssetKeys.Add(savedEntry.AssetKey);
        }

        foreach (var track in tracks.Where(track =>
                     restoredAssetKeys.Contains(track.GenerationPlanEntry.AssetKey)))
        {
            track.ApplyRestoredPlanState(availableMusicSettings);
        }

        return new DraftRestoreSummary(
            draft,
            restoredAssetKeys,
            restoredAssetKeys.Count,
            missingEntryCount);
    }

    private void SetScanRootStatus(string text, string brushKey)
    {
        ScanRootStatusText.Text = text;
        ScanRootStatusText.Foreground = FindResource(brushKey) as System.Windows.Media.Brush ?? Foreground;
    }

    private void SetSettingsRootStatus(string text, string brushKey)
    {
        SettingsRootStatusText.Text = text;
        SettingsRootStatusText.Foreground = FindResource(brushKey) as System.Windows.Media.Brush ?? Foreground;
    }

    private void UpdateMo2Summary()
    {
        var profileName = string.IsNullOrWhiteSpace(_selectedProfileName)
            ? UiText.Get("Main.Unset")
            : _selectedProfileName;
        ProfileNameText.Text = profileName;
        ProfileSummaryText.Text = UiText.Format("Main.ProfileSummary", profileName);
        ScanRootText.Text = string.IsNullOrWhiteSpace(_mo2Root)
            ? UiText.Get("Main.Unset")
            : _mo2Root;
    }

    private void SaveSettings()
    {
        _settingsStore.Save(new GfMusicManagerSettings(
            _mo2Root,
            _selectedProfileName,
            _includeDisabledMods,
            _createWorldSpaceMusicSettings,
            _enableFileLogging,
            _language));
    }

    private void ReopenForLanguageChange()
    {
        UiText.SetLanguage(_language);
        try
        {
            var replacement = new MainWindow();
            System.Windows.Application.Current.MainWindow = replacement;
            replacement.Show();
            Close();
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Exception("Language window reload failed", exception);
            MessageBox.Show(
                UiText.Get("Settings.Language.RestartFailed"),
                UiText.Get("Settings.Language.RestartFailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void DraftSaveTimer_Tick(object? sender, EventArgs e)
    {
        _draftSaveTimer.Stop();
        SaveDraftNow();
    }

    private void QueueDraftSave()
    {
        if (!_hasCompletedScan ||
            _draftPersistenceSuppressed ||
            string.IsNullOrWhiteSpace(_mo2Root) ||
            string.IsNullOrWhiteSpace(_selectedProfileName) ||
            !string.Equals(_mo2Root, _scannedMo2Root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_selectedProfileName, _scannedProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _draftDirty = true;
        _draftSaveTimer.Stop();
        _draftSaveTimer.Start();
    }

    private void SaveDraftNow()
    {
        if (!_draftDirty ||
            _draftPersistenceSuppressed ||
            !_hasCompletedScan ||
            string.IsNullOrWhiteSpace(_mo2Root) ||
            string.IsNullOrWhiteSpace(_selectedProfileName) ||
            !string.Equals(_mo2Root, _scannedMo2Root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_selectedProfileName, _scannedProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var draft = CreateDraftStateSnapshot(
            _generationPlan,
            _mo2Root,
            _selectedProfileName,
            _createWorldSpaceMusicSettings,
            _disableSourceEsp);
        if (_draftStore.Save(draft))
        {
            _draftDirty = false;
        }
    }

    private sealed record DraftRestoreSummary(
        GfMusicManagerDraft? Draft,
        IReadOnlySet<string> RestoredAssetKeys,
        int RestoredEntryCount,
        int MissingEntryCount)
    {
        public static DraftRestoreSummary Empty { get; } = new(
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            0,
            0);
    }

    private void ReplaceTrackCollection(IReadOnlyList<TrackRow> tracks)
    {
        foreach (var existingTrack in _tracks)
        {
            existingTrack.PropertyChanged -= TrackRow_PropertyChanged;
        }

        var replacement = new ObservableCollection<TrackRow>(tracks);
        foreach (var track in replacement)
        {
            track.PropertyChanged += TrackRow_PropertyChanged;
        }

        var replacementView = CollectionViewSource.GetDefaultView(replacement);
        replacementView.Filter = FilterTrack;
        _tracks = replacement;
        _tracksView = replacementView;
        TrackGrid.ItemsSource = _tracksView;
        GfMusicManagerLog.Info($"ReplaceTrackCollection: rows={_tracks.Count}.");
    }

    private void AllSourcesButton_Click(object sender, RoutedEventArgs e)
    {
        _sourceFilter = null;
        _filterMode = FilterAll;
        RefreshTrackView();
    }

    private void SelectedOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        _sourceFilter = null;
        _filterMode = FilterAdopted;
        RefreshTrackView();
    }

    private void WarningOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        _sourceFilter = null;
        _filterMode = FilterWarning;
        RefreshTrackView();
    }

    private void UnusedCountButton_Click(object sender, RoutedEventArgs e)
    {
        _sourceFilter = null;
        _filterMode = FilterUnused;
        GfMusicManagerLog.Info("UnusedCountButton_Click: showing unused audio only.");
        RefreshTrackView();
    }

    private void DisabledCountButton_Click(object sender, RoutedEventArgs e)
    {
        _sourceFilter = null;
        _filterMode = FilterDisabled;
        GfMusicManagerLog.Info("DisabledCountButton_Click: showing disabled-mod audio only.");
        RefreshTrackView();
    }

    private void VisibleSelectionToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var visibleTracks = _tracksView.OfType<TrackRow>().ToArray();
        if (visibleTracks.Length == 0)
        {
            GfMusicManagerLog.Warning("VisibleSelectionToggleButton_Click: no visible tracks.");
            return;
        }

        var selectVisibleTracks = !visibleTracks.All(track => track.IsSelected);
        _isUpdatingVisibleSelection = true;
        try
        {
            foreach (var track in visibleTracks)
            {
                track.IsSelected = selectVisibleTracks;
            }
        }
        finally
        {
            _isUpdatingVisibleSelection = false;
        }

        GfMusicManagerLog.Info(
            $"VisibleSelectionToggleButton_Click: visible={visibleTracks.Length}, " +
            $"selected={selectVisibleTracks}.");
        UpdateSummary();
    }

    private void WarningDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenAudioDuplicateReviewWindow();
    }

    private void OpenAudioDuplicateReviewWindow()
    {
        if (_audioDuplicateGroups.Count == 0)
        {
            MessageBox.Show(
                UiText.Get("Main.AudioWarningNone"),
                UiText.Get("Main.AudioWarningTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var adoptedAssetKeys = _tracks
            .Where(track => track.IsAdopted && track.Asset is not null)
            .Select(track => MusicGenerationPlanEntry.CreateAssetKey(track.Asset!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usageByAssetKey = _tracks
            .Where(track => track.Asset is not null)
            .ToDictionary(
                track => MusicGenerationPlanEntry.CreateAssetKey(track.Asset!),
                track => track.SourceMusicSettings,
                StringComparer.OrdinalIgnoreCase);
        var reviewWindow = new AudioDuplicateReviewWindow(
            _audioDuplicateGroups,
            adoptedAssetKeys,
            PlayDuplicateSource,
            StopPreview,
            IsPreviewActive,
            usageByAssetKey,
            _modPriorities)
        {
            Owner = this
        };
        GfMusicManagerLog.Info(
            $"OpenAudioDuplicateReviewWindow: groups={_audioDuplicateGroups.Count}, " +
            $"adoptedAssets={adoptedAssetKeys.Count}, " +
            $"usageAssets={usageByAssetKey.Count}, " +
            $"modPriorities={_modPriorities.Count}.");
        if (reviewWindow.ShowDialog() == true)
        {
            ApplyAudioDuplicateDecisions(reviewWindow.Decisions);
        }
    }

    private void PlayDuplicateSource(AssetSource asset)
    {
        var row = _tracks.FirstOrDefault(track =>
            track.Asset is not null &&
            string.Equals(
                MusicGenerationPlanEntry.CreateAssetKey(track.Asset),
                MusicGenerationPlanEntry.CreateAssetKey(asset),
                StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            GfMusicManagerLog.Warning(
                $"PlayDuplicateSource: row not found. mod={asset.ModName}, path={asset.VirtualPath}.");
            return;
        }

        TrackGrid.SelectedItem = row;
        UpdateSelectedTrack(row);
        StartPreview(row);
    }

    private bool IsPreviewActive(AssetSource asset) =>
        _previewPlayer.CurrentAsset is not null &&
        !_previewPlayer.IsEnded &&
        AreSameAsset(_previewPlayer.CurrentAsset, asset);

    private void ApplyAudioDuplicateDecisions(
        IReadOnlyList<AudioDuplicateReviewDecision> decisions)
    {
        var rowsByAssetKey = _tracks
            .Where(track => track.Asset is not null)
            .ToDictionary(
                track => MusicGenerationPlanEntry.CreateAssetKey(track.Asset!),
                StringComparer.OrdinalIgnoreCase);
        var changed = 0;
        foreach (var decision in decisions
                     .OrderBy(decision => decision.Group.Kind == AudioDuplicateKind.PathConflict))
        {
            foreach (var source in decision.Group.Sources)
            {
                if (!rowsByAssetKey.TryGetValue(source.AssetKey, out var row))
                {
                    continue;
                }

                var adopted = decision.AdoptedAssetKeys.Contains(source.AssetKey);
                if (row.IsAdopted != adopted)
                {
                    row.IsAdopted = adopted;
                    changed++;
                }
            }
        }

        GfMusicManagerLog.Info(
            $"ApplyAudioDuplicateDecisions: groups={decisions.Count}, changedRows={changed}.");
        RefreshTrackView();
    }

    private void SourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string source })
        {
            _sourceFilter = source;
            _filterMode = FilterAll;
            RefreshTrackView();
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        _appliedSearchText = SearchTextBox.Text.Trim();
        GfMusicManagerLog.Info(
            $"SearchButton_Click: applied search=" +
            $"'{(_appliedSearchText.Length == 0 ? "(empty)" : _appliedSearchText)}'.");
        RefreshTrackView();
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        var filterCandidates = MusicFilterCandidates.FromTracks(_tracks);
        GfMusicManagerLog.Info(
            $"FilterButton_Click: building candidates. rows={_tracks.Count}, " +
            $"combat={filterCandidates.Combat.Count - 1}, " +
            $"time={filterCandidates.TimeOfDay.Count - 1}, " +
            $"weather={filterCandidates.Weather.Count - 1}, " +
            $"other={filterCandidates.OtherCondition.Count - 1}.");
        var filterWindow = new MusicFilterWindow(_musicFilterOptions, filterCandidates)
        {
            Owner = this
        };

        if (filterWindow.ShowDialog() != true)
        {
            return;
        }

        _musicFilterOptions = filterWindow.Options;
        GfMusicManagerLog.Info(
            $"FilterButton_Click: applied activeRules={_musicFilterOptions.ActiveRuleCount}.");
        RefreshTrackView();
    }

    private void TrackRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TrackRow row)
        {
            return;
        }

        if (e.PropertyName == nameof(TrackRow.IsAdopted))
        {
            GfMusicManagerLog.Info($"TrackRow_PropertyChanged: adoption changed. title={row.Title}, adopted={row.IsAdopted}.");
            QueueDraftSave();
            if (TrackGrid.SelectedItem is TrackRow selected && ReferenceEquals(selected, row))
            {
                UpdateSelectedTrack(row);
            }

            QueueTrackViewRefresh();
            return;
        }

        if (e.PropertyName == nameof(TrackRow.IsSelected))
        {
            if (!_isUpdatingVisibleSelection)
            {
                GfMusicManagerLog.Info($"TrackRow_PropertyChanged: operation selection changed. title={row.Title}, selected={row.IsSelected}.");
                UpdateSummary();
            }

            return;
        }

        if (e.PropertyName is nameof(TrackRow.MusicSettings) or
            nameof(TrackRow.MusicConditions) or
            nameof(TrackRow.IsUnused))
        {
            GfMusicManagerLog.Info(
                $"TrackRow_PropertyChanged: music assignment changed. title={row.Title}, " +
                $"unused={row.IsUnused}, settings={row.MusicSettings.Count}, " +
                $"conditions={row.MusicConditions.Count}.");
            QueueDraftSave();
            QueueTrackViewRefresh();
        }
    }

    private void TrackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrackGrid.SelectedItem is TrackRow row)
        {
            if (!AreSameAsset(_previewPlayer.CurrentAsset, row.Asset))
            {
                StopPreview();
            }

            UpdateSelectedTrack(row);
            return;
        }

        StopPreview();
        ClearSelectedTrack();
    }

    private void PreviewTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TrackRow row })
        {
            GfMusicManagerLog.Info($"PreviewTrackButton_Click: title={row.Title}, path={row.AudioPath}.");
            if (row.IsPreviewActive)
            {
                StopPreview();
                return;
            }

            TrackGrid.SelectedItem = row;
            UpdateSelectedTrack(row);
            StartPreview(row);
        }
    }

    private void PlayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Name: "PlayerToggleButton" } || TrackGrid.SelectedItem is not TrackRow row)
        {
            GfMusicManagerLog.Warning("PlayerButton_Click: ignored because no player toggle or track is selected.");
            return;
        }

        GfMusicManagerLog.Info($"PlayerButton_Click: title={row.Title}.");

        if (row.Asset is null)
        {
            ShowPreviewError(UiText.Get("Main.NoScannedAudio"));
            return;
        }

        try
        {
            if (!AreSameAsset(_previewPlayer.CurrentAsset, row.Asset))
            {
                StartPreview(row);
            }
            else if (_previewPlayer.IsPaused)
            {
                _previewPlayer.Resume();
                _previewTimer.Start();
                UpdatePreviewUi();
            }
            else if (_previewPlayer.IsEnded)
            {
                StartPreview(row);
            }
            else if (_previewPlayer.HasLoaded)
            {
                _previewPlayer.Pause();
                UpdatePreviewUi();
            }
            else
            {
                StartPreview(row);
            }
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Exception("PlayerButton_Click failed", exception);
            ShowPreviewError(exception.Message);
        }
    }

    private void StartPreview(TrackRow row)
    {
        GfMusicManagerLog.Info($"StartPreview: title={row.Title}, path={row.AudioPath}.");
        if (row.Asset is null)
        {
            ShowPreviewError(UiText.Get("Main.NoScannedAudio"));
            return;
        }

        try
        {
            PreviewStatusText.Text = UiText.Get("Main.PreviewLoading");
            _previewPlayer.Play(row.Asset);
            _previewTimer.Start();
            UpdatePreviewUi();
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Exception("StartPreview failed", exception);
            ShowPreviewError(exception.Message);
        }
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (!_previewPlayer.HasLoaded)
        {
            _previewTimer.Stop();
            UpdatePreviewUi();
            return;
        }

        UpdatePreviewUi();
        if (_previewPlayer.IsEnded)
        {
            _previewTimer.Stop();
            PreviewStatusText.Text = UiText.Get("Main.PreviewEnded");
            PlayerToggleButton.Content = "▶";
        }
    }

    private void PreviewVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = Math.Clamp(e.NewValue, 0, 100);
        _previewPlayer.Volume = (float)(value / 100.0);
        if (PreviewVolumeText is not null)
        {
            PreviewVolumeText.Text = $"{value:0}%";
        }
    }

    private void PreviewSeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_previewPlayer.HasLoaded || _previewPlayer.Duration <= TimeSpan.Zero)
        {
            return;
        }

        _isPreviewSeekDragging = true;
        PreviewSeekSlider.CaptureMouse();
        UpdatePreviewSeekValue(e.GetPosition(PreviewSeekSlider).X);
        e.Handled = true;
    }

    private void PreviewSeekSlider_PreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isPreviewSeekDragging || !PreviewSeekSlider.IsMouseCaptured)
        {
            return;
        }

        UpdatePreviewSeekValue(e.GetPosition(PreviewSeekSlider).X);
        e.Handled = true;
    }

    private void PreviewSeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPreviewSeekDragging)
        {
            return;
        }

        UpdatePreviewSeekValue(e.GetPosition(PreviewSeekSlider).X);
        _isPreviewSeekDragging = false;
        if (PreviewSeekSlider.IsMouseCaptured)
        {
            PreviewSeekSlider.ReleaseMouseCapture();
        }

        _previewPlayer.Seek(TimeSpan.FromSeconds(PreviewSeekSlider.Value));
        UpdatePreviewUi();
        e.Handled = true;
    }

    private void UpdatePreviewSeekValue(double x)
    {
        if (PreviewSeekSlider.ActualWidth <= 0 || PreviewSeekSlider.Maximum <= PreviewSeekSlider.Minimum)
        {
            return;
        }

        var ratio = Math.Clamp(x / PreviewSeekSlider.ActualWidth, 0, 1);
        PreviewSeekSlider.Value = PreviewSeekSlider.Minimum +
                                  (PreviewSeekSlider.Maximum - PreviewSeekSlider.Minimum) * ratio;
    }

    private void StopPreview()
    {
        GfMusicManagerLog.Info("StopPreview: requested.");
        _previewTimer.Stop();
        _previewPlayer.Stop();
        UpdatePreviewUi();
    }

    private void UpdatePreviewUi()
    {
        var duration = _previewPlayer.Duration;
        var position = _previewPlayer.Position;
        var currentAsset = _previewPlayer.CurrentAsset;
        var previewActive = currentAsset is not null && !_previewPlayer.IsEnded;
        foreach (var track in _tracks)
        {
            track.SetPreviewActive(previewActive && AreSameAsset(currentAsset, track.Asset));
        }

        if (!_isPreviewSeekDragging)
        {
            PreviewSeekSlider.Maximum = Math.Max(duration.TotalSeconds, 1);
            PreviewSeekSlider.Value = duration.TotalSeconds <= 0
                ? 0
                : Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds);
        }

        var displayedPosition = _isPreviewSeekDragging
            ? TimeSpan.FromSeconds(PreviewSeekSlider.Value)
            : position;
        PreviewPositionText.Text = FormatPreviewTime(displayedPosition);
        PreviewDurationText.Text = FormatPreviewTime(duration);

        if (_previewPlayer.IsPaused)
        {
            PreviewStatusText.Text = UiText.Get("Main.PreviewPaused");
        }
        else if (_previewPlayer.IsPlaying)
        {
            PreviewStatusText.Text = UiText.Get("Main.PreviewPlaying");
        }
        else if (_previewPlayer.IsEnded)
        {
            PreviewStatusText.Text = UiText.Get("Main.PreviewEnded");
        }
        else if (!_previewPlayer.HasLoaded)
        {
            PreviewStatusText.Text = UiText.Get("Main.PreviewAvailable");
        }

        PlayerToggleButton.Content = _previewPlayer.IsPaused || _previewPlayer.IsEnded ? "▶" : "Ⅱ";
    }

    private void ShowPreviewError(string message)
    {
        GfMusicManagerLog.Error($"Preview error: {message}");
        _previewTimer.Stop();
        _previewPlayer.Stop();
        UpdatePreviewUi();
        PreviewStatusText.Text = UiText.Get("Main.PreviewUnavailable");
        MessageBox.Show(
            message,
            UiText.Get("Main.PreviewErrorTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string FormatPreviewTime(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    private static bool AreSameAsset(AssetSource? left, AssetSource? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(left.SourcePath, right.SourcePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ArchiveEntryPath, right.ArchiveEntryPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.VirtualPath, right.VirtualPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAssetPathForDuplicate(string path) => path
        .Replace('/', '\\')
        .TrimStart('\\');

    private void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrackGrid.SelectedItem is not TrackRow row)
        {
            GfMusicManagerLog.Warning("DetailsButton_Click: no track is selected.");
            return;
        }

        GfMusicManagerLog.Info(
            $"DetailsButton_Click: opening details. title={row.Title}, " +
            $"sourceSettings={row.SourceMusicSettings.Count}, generatedSettings={row.MusicSettings.Count}.");

        var detailsWindow = new MusicSourceDetailsWindow(row)
        {
            Owner = this
        };
        if (detailsWindow.ShowDialog() == true)
        {
            GfMusicManagerLog.Info(
                $"DetailsButton_Click: saved details. title={row.Title}, generatedSettings={row.MusicSettings.Count}.");
            RefreshTrackView();
            UpdateSelectedTrack(row);
        }
        else
        {
            GfMusicManagerLog.Info($"DetailsButton_Click: cancelled details. title={row.Title}.");
        }
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        SetAdoptionForSelected(true);
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        SetAdoptionForSelected(false);
    }

    private void BulkMusicTypeButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedTracks = _tracks
            .Where(track => track.IsSelected)
            .ToArray();
        if (selectedTracks.Length == 0)
        {
            GfMusicManagerLog.Warning("BulkMusicTypeButton_Click: no tracks selected.");
            MessageBox.Show(
                UiText.Get("Main.BulkNoSelection"),
                UiText.Get("Main.BulkTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var musicTypeSettings = _availableMusicSettings
            .Where(setting => setting.Scope == MusicSettingScope.MusicType)
            .GroupBy(setting => setting.MusicTypeFormKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(setting => setting.MusicTypeRecord.IsWinner)
                .ThenByDescending(setting => setting.MusicTypeRecord.Plugin.LoadOrderIndex)
                .ThenByDescending(setting => setting.MusicTypeRecord.Plugin.ModPriority)
                .ThenBy(setting => setting.MusicTypeRecord.Plugin.Name, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToArray();
        if (musicTypeSettings.Length == 0)
        {
            GfMusicManagerLog.Warning("BulkMusicTypeButton_Click: no Music Type candidates.");
            MessageBox.Show(
                UiText.Get("Main.BulkNoCandidates"),
                UiText.Get("Main.BulkTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        GfMusicManagerLog.Info(
            $"BulkMusicTypeButton_Click: opening picker. tracks={selectedTracks.Length}, " +
            $"musicTypes={musicTypeSettings.Length}.");
        var picker = new MusicTypeBulkAssignWindow(
            musicTypeSettings,
            _musicDefinitionConflicts)
        {
            Owner = this
        };
        if (picker.ShowDialog() != true)
        {
            GfMusicManagerLog.Info("BulkMusicTypeButton_Click: cancelled.");
            return;
        }

        var destinations = picker.SelectedSettings;
        var changed = 0;
        foreach (var track in selectedTracks)
        {
            var before = track.MusicSettings.Count;
            track.AddMusicSettings(destinations);
            if (track.MusicSettings.Count != before)
            {
                changed++;
            }
        }

        if (TrackGrid.SelectedItem is TrackRow selectedRow &&
            selectedTracks.Contains(selectedRow))
        {
            UpdateSelectedTrack(selectedRow);
        }

        RefreshTrackView();
        GfMusicManagerLog.Info(
            $"BulkMusicTypeButton_Click: applied. tracks={selectedTracks.Length}, " +
            $"destinations={destinations.Count}, changedRows={changed}, " +
            $"integrationTargets={_generationPlan.Conflicts.Count(IsActiveMusicTypeIntegrationTarget)}.");
    }

    private void SetAdoptionForSelected(bool adopted)
    {
        var selectedTracks = _tracks.Where(track => track.IsSelected).ToArray();
        if (selectedTracks.Length == 0)
        {
            GfMusicManagerLog.Warning($"SetAdoptionForSelected: no tracks selected. requestedAdoption={adopted}.");
            MessageBox.Show(
                UiText.Get("Main.AdoptionNoSelection"),
                UiText.Get("Main.AdoptionTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        foreach (var track in selectedTracks)
        {
            track.IsAdopted = adopted;
        }

        GfMusicManagerLog.Info(
            $"SetAdoptionForSelected: updated {selectedTracks.Length} track(s). adopted={adopted}.");
        RefreshTrackView();
    }

    private void OpenReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_mo2Root))
        {
            MessageBox.Show(
                UiText.Get("Main.ReviewRequiresSettings"),
                UiText.Get("Main.GenerationSettingsTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        KeepVanillaMusicRadioButton.IsChecked = _generationPlan.KeepVanillaMusic == true;
        RemoveVanillaMusicRadioButton.IsChecked = _generationPlan.KeepVanillaMusic == false;
        DisableSourceEspCheckBox.IsChecked = _disableSourceEsp;
        UpdateReviewSummary();
        ReviewOutputPathTextBox.Text = Path.Combine(
            _mo2Root,
            "mods",
            "GF Music Product");
        UpdatePrerequisiteWarning();
        var conflicts = _generationPlan.Conflicts;
        var duplicateCount = conflicts.Count(conflict =>
            conflict.Kind == MusicGenerationPlanConflictKind.DuplicateVirtualPath);
        var assignmentConflictCount = conflicts.Count(conflict =>
            conflict.Kind == MusicGenerationPlanConflictKind.MultipleGeneratedMusicTypesForRecord);
        GfMusicManagerLog.Info(
            $"OpenReviewButton_Click: conflicts={conflicts.Count}, " +
            $"duplicateVirtualPaths={duplicateCount}, assignmentConflicts={assignmentConflictCount}, " +
            $"definitionConflicts={_musicDefinitionConflicts.Count}.");
        ReviewOverlay.Visibility = Visibility.Visible;
    }

    private void VanillaMusicPolicyRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender == KeepVanillaMusicRadioButton)
        {
            _generationPlan.KeepVanillaMusic = true;
        }
        else if (sender == RemoveVanillaMusicRadioButton)
        {
            _generationPlan.KeepVanillaMusic = false;
        }

        GfMusicManagerLog.Info(
            $"VanillaMusicPolicyRadioButton_Checked: keep={_generationPlan.KeepVanillaMusic?.ToString() ?? "unset"}.");
        QueueDraftSave();
    }

    private void CloseReviewButton_Click(object sender, RoutedEventArgs e)
    {
        ReviewOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ApplyReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_generationPlan.KeepVanillaMusic is null)
        {
            GfMusicManagerLog.Warning("ApplyReviewButton_Click: vanilla music policy was not selected.");
            MessageBox.Show(
                UiText.Get("Main.VanillaPolicyRequired"),
                UiText.Get("Main.GenerationSettingsTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_mo2Root))
        {
            MessageBox.Show(
                UiText.Get("Main.Mo2RootRequired"),
                UiText.Get("Main.GenerationErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var outputDirectory = Path.Combine(_mo2Root, "mods", "GF Music Product");
        var selectedWorldSpaceFormKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var worldSpaceIndividualAssignment = _createWorldSpaceMusicSettings;
        var enableGeneratedMod = EnableGeneratedPluginCheckBox.IsChecked == true;
        var disableSourceEsp = DisableSourceEspCheckBox.IsChecked == true;
        _disableSourceEsp = disableSourceEsp;
        QueueDraftSave();
        var expectedPluginNames = GetExpectedGeneratedPluginNames();
        var sourcePluginNames = disableSourceEsp
            ? GetAdoptedSourcePluginNames()
            : Array.Empty<string>();

        var overwriteExisting = false;
        if (Directory.Exists(outputDirectory))
        {
            var answer = MessageBox.Show(
                UiText.Get("Main.RegenerateExisting"),
                UiText.Get("Main.ExistingProductTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                GfMusicManagerLog.Info("ApplyReviewButton_Click: existing output regeneration canceled.");
                return;
            }

            overwriteExisting = true;
        }

        var stateSummary = BuildMo2StateConfirmationMessage(
            enableGeneratedMod,
            expectedPluginNames,
            disableSourceEsp,
            sourcePluginNames);
        var stateAnswer = MessageBox.Show(
            stateSummary,
            UiText.Get("Main.Mo2StateConfirmationTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (stateAnswer != MessageBoxResult.Yes)
        {
            GfMusicManagerLog.Info("ApplyReviewButton_Click: MO2 state confirmation canceled.");
            return;
        }

        ApplyReviewButton.IsEnabled = false;
        try
        {
            GfMusicManagerLog.Info(
                $"ApplyReviewButton_Click: generation requested. output={outputDirectory}, " +
                    $"settings={_availableMusicSettings.Count}, overwrite={overwriteExisting}, " +
                    $"worldSpaceIndividual={worldSpaceIndividualAssignment}, " +
                    $"outputMode={MusicGenerationOutputMode.Normal}, " +
                    $"enableGeneratedMod={enableGeneratedMod}, disableSourceEsp={disableSourceEsp}, " +
                    $"expectedPlugins={string.Join(',', expectedPluginNames)}, " +
                    $"sourcePlugins={sourcePluginNames.Count}, " +
                    $"worldSpaces={(worldSpaceIndividualAssignment ? "all applicable adopted destinations" : "disabled")}.");
            if (_applicationScanResult is null)
            {
                throw new InvalidOperationException(
                    UiText.Get("Main.ScanRequired"));
            }

            var generationProgress = new Progress<MusicGenerationProgress>(
                UpdateGenerationProgressOverlay);
            BeginGenerationProgressOverlay();
            var applicationResult = await Task.Run(() => _musicGenerationApplicationService.Generate(
                _applicationScanResult,
                _generationPlan,
                new MusicGenerationApplicationOptions
                {
                    OutputModDirectory = outputDirectory,
                    OutputMode = MusicGenerationOutputMode.Normal,
                    OverwriteExisting = overwriteExisting,
                    WorldSpaceIndividualAssignment = worldSpaceIndividualAssignment,
                    SelectedWorldSpaceFormKeys = selectedWorldSpaceFormKeys,
                    ExistingMtdFileNames = _existingMtdFileNames,
                    Progress = generationProgress
                }));
            var result = applicationResult.Output;
            var generatedMusicTrackCount = result.Tracks.Count;

            _draftSaveTimer.Stop();
            var draftDeleted = !string.IsNullOrWhiteSpace(_selectedProfileName) &&
                               _draftStore.Delete(_mo2Root, _selectedProfileName);
            // A successful generation consumes the current draft, but it must not
            // disable automatic saving for edits made after generation.
            _draftPersistenceSuppressed = false;
            _draftDirty = !draftDeleted;
            GfMusicManagerLog.Info(
                $"ApplyReviewButton_Click: generation draft cleanup. deleted={draftDeleted}, " +
                "futureAutoSaveEnabled=True.");

            Mo2ProfileStateChangeResult? stateResult = null;
            Exception? stateException = null;
            try
            {
                if (string.IsNullOrWhiteSpace(_selectedProfileName))
                {
                    throw new InvalidOperationException(UiText.Get("Main.Mo2ProfileRequired"));
                }

                stateResult = await Task.Run(() => _musicMo2ApplicationService.Apply(
                    new MusicMo2ApplicationOptions
                    {
                        Mo2Root = _mo2Root,
                        ProfileName = _selectedProfileName,
                        GeneratedModName = "GF Music Product",
                        GeneratedPluginNames = result.Plugins
                            .Select(plugin => plugin.PluginFileName)
                            .ToArray(),
                        EnableGeneratedMod = enableGeneratedMod,
                        SourcePluginNames = sourcePluginNames,
                        DisableSourcePlugins = disableSourceEsp
                    }));
                GfMusicManagerLog.Info(
                    $"ApplyReviewButton_Click: MO2 state apply completed. " +
                    $"changed={stateResult.Changed}, " +
                    $"generatedPlugins={string.Join(',', result.Plugins.Select(plugin => plugin.PluginFileName))}, " +
                    $"generatedModEnabled={enableGeneratedMod}, " +
                    $"sourcePluginsDisabled={disableSourceEsp}, " +
                    $"sourcePluginCount={sourcePluginNames.Count}.");
            }
            catch (Exception exception)
            {
                stateException = exception;
                GfMusicManagerLog.Exception("ApplyReviewButton_Click MO2 state update failed", exception);
            }

            ReviewOverlay.Visibility = Visibility.Collapsed;
            LibrarySubheadingText.Text = UiText.Format(
                "Main.GenerationSummary",
                generatedMusicTrackCount,
                result.Plugins.Count,
                result.Assets.Count(asset => !asset.IsCopied),
                result.Assets.Count(asset => asset.IsCopied),
                stateException is null
                    ? UiText.Get("Main.GenerationStateOk")
                    : UiText.Get("Main.GenerationStateFailed"));
            PreviewStatusText.Text = stateException is null
                ? UiText.Get("Main.GenerationStateSummaryOk")
                : UiText.Get("Main.GenerationStateSummaryFailed");
            GfMusicManagerLog.Info(
                $"ApplyReviewButton_Click: generation completed. tracks={generatedMusicTrackCount}, " +
                $"plugins={result.Plugins.Count}, copied={result.Assets.Count(asset => asset.IsCopied)}, " +
                $"referenced={result.Assets.Count(asset => !asset.IsCopied)}, " +
                $"diagnostic={result.Diagnostic.Summary}, " +
                $"mo2StateChanged={stateResult?.Changed.ToString() ?? "false"}.");
            if (stateException is not null)
            {
                MessageBox.Show(
                    UiText.Format("Main.Mo2StateApplyFailed", stateException.Message),
                    UiText.Get("Main.Mo2StateApplyErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
                    BuildGenerationCompletedMessage(
                        result,
                        enableGeneratedMod,
                        _selectedProfileName ?? string.Empty),
                    UiText.Get("Main.GenerationCompletedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Exception("ApplyReviewButton_Click generation failed", exception);
            MessageBox.Show(
                exception.Message,
                UiText.Get("Main.GenerationOutputFailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            EndGenerationProgressOverlay();
            ApplyReviewButton.IsEnabled = true;
        }
    }

    private IReadOnlyList<string> GetExpectedGeneratedPluginNames()
    {
        try
        {
            var estimate = new MusicGenerationCapacityPlanner().Estimate(
                _generationPlan,
                MusicGenerationCapacityPolicy.CurrentAe,
                _createWorldSpaceMusicSettings,
                additionalMusicTypeRecordCount: GetExpectedIntegrationMusicTypeCount());
            if (estimate.Plugins.Count > 0)
            {
                return estimate.Plugins
                    .Select(plugin => plugin.PluginFileName)
                    .ToArray();
            }
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Exception(
                "GetExpectedGeneratedPluginNames: capacity estimate failed",
                exception);
        }

        return new[] { MusicGenerationCapacityPlanner.GetPluginFileName(1) };
    }

    private int GetExpectedIntegrationMusicTypeCount()
    {
        if (_generationPlan.KeepVanillaMusic is null)
        {
            return 0;
        }

        try
        {
            return new MusicGenerationPlanResolver()
                .Resolve(_generationPlan, _availableMusicSettings)
                .IntegrationTargets
                .Count(target => target.Scope != MusicSettingScope.WorldSpace);
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Exception(
                "GetExpectedIntegrationMusicTypeCount: resolution failed",
                exception);
            return 0;
        }
    }

    private static string BuildMo2StateConfirmationMessage(
        bool enableGeneratedMod,
        IReadOnlyList<string> generatedPluginNames,
        bool disableSourceEsp,
        IReadOnlyList<string> sourcePluginNames)
    {
        var generatedState = UiText.Get(
            enableGeneratedMod ? "Main.StateEnabled" : "Main.StateDisabled");
        var lines = new List<string>
        {
            UiText.Get("Main.Mo2StateHeader"),
            UiText.Get("Main.OutputModeNormal"),
            string.Empty
        };

        if (generatedPluginNames.Count == 1)
        {
            lines.Add(UiText.Format(
                "Main.GeneratedPlugin",
                generatedPluginNames[0],
                generatedState));
        }
        else
        {
            lines.Add(UiText.Get("Main.GeneratedPlugins"));
            lines.AddRange(generatedPluginNames.Select(pluginName => UiText.Format(
                "Main.PluginStateItem",
                pluginName,
                generatedState)));
        }

        if (!disableSourceEsp)
        {
            lines.Add(UiText.Get("Main.SourceEspUnchanged"));
        }
        else
        {
            lines.Add(UiText.Format("Main.SourceEspCount", sourcePluginNames.Count));
            if (sourcePluginNames.Count > 0)
            {
                lines.Add(string.Empty);
                lines.AddRange(sourcePluginNames.Select(pluginName => UiText.Format(
                    "Main.PluginNameItem",
                    pluginName)));
                lines.Add(string.Empty);
                lines.Add(UiText.Get("Main.SourceEspDisabled"));
                lines.Add(UiText.Get("Main.SourceEspWarning"));
            }
        }

        lines.Add(string.Empty);
        lines.Add(UiText.Get("Main.Continue"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildGenerationCompletedMessage(
        MusicGenerationOutputResult result,
        bool generatedModEnabled,
        string profileName)
    {
        var generatedState = UiText.Get(
            generatedModEnabled ? "Main.StateEnabled" : "Main.StateDisabled");
        var lines = new List<string>
        {
            UiText.Get("Main.GenerationCompleted"),
            string.Empty,
            UiText.Format("Main.GeneratedModState", generatedState),
            UiText.Format("Main.OutputPathSummary", result.OutputModDirectory)
        };

        if (result.Plugins.Count == 1)
        {
            lines.Add(UiText.Format(
                "Main.PluginStateLabel",
                result.Plugins[0].PluginFileName,
                generatedState));
        }
        else
        {
            lines.Add(UiText.Get("Main.Plugins"));
            lines.AddRange(result.Plugins.Select(plugin =>
                UiText.Format(
                    "Main.PluginStateItem",
                    plugin.PluginFileName,
                    generatedState)));
        }

        lines.Add(UiText.Format("Main.AudioCount", result.Tracks.Count));
        lines.Add(UiText.Format(
            "Main.AssetHandlingSummary",
            result.Assets.Count(asset => !asset.IsCopied),
            result.Assets.Count(asset => asset.IsCopied)));
        if (result.Cells.Count > 0 && result.CellSkyPatcherFilePath is not null)
        {
            lines.Add(UiText.Format("Main.CellApplySummary", result.Cells.Count));
            lines.Add(UiText.Format("Main.CellSettingsSummary", result.CellSkyPatcherFilePath));
        }
        lines.Add(UiText.Format("Main.ProfileSummaryLine", profileName));
        return string.Join(Environment.NewLine, lines);
    }

    private bool FilterTrack(object item)
    {
        if (item is not TrackRow row)
        {
            return false;
        }

        if (_sourceFilter is not null &&
            !string.Equals(row.Source, _sourceFilter, StringComparison.Ordinal))
        {
            return false;
        }

        if (_filterMode == FilterAdopted && !row.IsAdopted)
        {
            return false;
        }

        if (_filterMode == FilterWarning && !row.HasWarning)
        {
            return false;
        }

        if (_filterMode == FilterUnused && !row.IsUnused)
        {
            return false;
        }

        if (_filterMode == FilterDisabled && (row.Asset is null || row.Asset.ModEnabled))
        {
            return false;
        }

        if (!MusicFilterMatcher.Matches(row, _musicFilterOptions))
        {
            return false;
        }

        var search = _appliedSearchText;
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return row.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.Source.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.Placement.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<string> GetAdoptedSourcePluginNames()
    {
        var adoptedTracks = _tracks
            .Where(track => track.IsAdopted && track.Asset is not null)
            .ToArray();
        var sourceModNames = adoptedTracks
            .Select(track => track.Asset!.ModName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceSettings = adoptedTracks
            .SelectMany(track => track.SourceMusicSettings)
            .ToArray();
        var pluginNames = MusicSourcePluginSelector.Select(sourceSettings, sourceModNames);

        GfMusicManagerLog.Info(
            $"GetAdoptedSourcePluginNames: adoptedSourceMods={string.Join(',', sourceModNames.Order(StringComparer.OrdinalIgnoreCase))}, " +
            $"sourceSettings={sourceSettings.Length}, plugins={string.Join(',', pluginNames)}.");
        return pluginNames;
    }

    private void RefreshTrackView()
    {
        if (_isRefreshingTrackView)
        {
            GfMusicManagerLog.Warning("RefreshTrackView: ignored reentrant refresh request.");
            return;
        }

        _isRefreshingTrackView = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _tracksView.Refresh();
            UpdateSummary();
            UpdateReviewSummary();
        }
        finally
        {
            _isRefreshingTrackView = false;
            GfMusicManagerLog.Info($"RefreshTrackView: complete. rows={_tracks.Count}, elapsed={stopwatch.Elapsed}.");
        }
    }

    private void QueueTrackViewRefresh()
    {
        if (_trackViewRefreshQueued)
        {
            return;
        }

        _trackViewRefreshQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _trackViewRefreshQueued = false;
                RefreshTrackView();
            }));
    }

    private void RefreshSourceFilters()
    {
        _sourceFilters.Clear();
        foreach (var group in _tracks
                     .GroupBy(track => track.Source, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            _sourceFilters.Add(new SourceFilterRow(group.Key, group.Count()));
        }
    }

    private void UpdateSummary()
    {
        var adopted = _tracks.Where(x => x.IsAdopted).ToList();
        var selected = _tracks.Count(x => x.IsSelected);
        var visibleTracks = _tracksView.OfType<TrackRow>().ToArray();
        var references = adopted.Count(x => x.HandlingKind == TrackAssetHandling.Reference);
        var copies = adopted.Count(x => x.HandlingKind == TrackAssetHandling.Copy);
        var audioWarnings = _audioDuplicateGroups.Count;
        var disabled = _tracks.Count(x => x.Asset is not null && !x.Asset.ModEnabled);
        var unused = _tracks.Count(x => x.IsUnused);

        SelectionSummaryText.Text = UiText.Format("Main.SelectionSummary", adopted.Count, selected);
        ReferenceSummaryText.Text = UiText.Format("Main.ReferenceSummary", references);
        CopySummaryText.Text = UiText.Format("Main.CopySummary", copies);
        WarningSummaryText.Text = UiText.Format("Main.WarningSummary", audioWarnings);
        DisabledSummaryText.Text = UiText.Format("Main.DisabledSummary", disabled);
        UnusedCountText.Text = UiText.Format("Main.UnusedCount", unused);
        DisabledCountText.Text = UiText.Format("Main.DisabledCount", disabled);
        DisabledCountButton.Background = _filterMode == FilterDisabled
            ? FindResource("BluePanel") as System.Windows.Media.Brush
            : FindResource("PanelBackground") as System.Windows.Media.Brush;
        DisabledCountButton.BorderBrush = _filterMode == FilterDisabled
            ? FindResource("BlueAccent") as System.Windows.Media.Brush
            : FindResource("BorderBrush") as System.Windows.Media.Brush;
        UnusedCountButton.Background = _filterMode == FilterUnused
            ? FindResource("BluePanel") as System.Windows.Media.Brush
            : FindResource("PanelBackground") as System.Windows.Media.Brush;
        UnusedCountButton.BorderBrush = _filterMode == FilterUnused
            ? FindResource("BlueAccent") as System.Windows.Media.Brush
            : FindResource("BorderBrush") as System.Windows.Media.Brush;
        TrackCountText.Text = UiText.Format("Main.TrackCount", visibleTracks.Length, _tracks.Count);
        ReferenceCountText.Text = UiText.Format("Main.ReferenceCount", references);
        CopyCountText.Text = UiText.Format("Main.CopyCount", copies);
        WarningCountText.Text = UiText.Format("Main.WarningCount", audioWarnings);
        AllSourcesButtonText.Text = UiText.Format("Main.AllModsCount", _tracks.Count);
        var hasSelectedTracks = selected > 0;
        BulkMusicTypeButton.IsEnabled = hasSelectedTracks;
        SelectAdoptButton.IsEnabled = hasSelectedTracks;
        ExcludeSelectionButton.IsEnabled = hasSelectedTracks;
        OpenReviewButton.IsEnabled = adopted.Count > 0;
        var allVisibleTracksSelected = visibleTracks.Length > 0 &&
                                       visibleTracks.All(track => track.IsSelected);
        VisibleSelectionToggleText.Text = allVisibleTracksSelected
            ? UiText.Get("Main.VisibleSelectionClear")
            : UiText.Get("Main.VisibleSelectionAll");
        VisibleSelectionToggleButton.ToolTip = allVisibleTracksSelected
            ? UiText.Get("Main.VisibleSelectionClearTooltip")
            : UiText.Get("Main.VisibleSelectionAllTooltip");
        VisibleSelectionToggleButton.IsEnabled = visibleTracks.Length > 0 && !_isScanning;
        FilterButtonText.Text = _musicFilterOptions.IsEmpty
            ? UiText.Get("Main.Filter")
            : UiText.Format("Main.FilterWithCount", _musicFilterOptions.ActiveRuleCount);
        FilterButton.ToolTip = _musicFilterOptions.IsEmpty
            ? UiText.Get("Main.FilterTooltip")
            : UiText.Get("Main.FilterActiveTooltip");
        FilterButton.IsEnabled = !_isScanning;
        MusicTypeManagementButton.IsEnabled = _availableMusicSettings.Count > 0 && !_isScanning;
        UpdateNavigationSelectionVisuals();
    }

    private void UpdateNavigationSelectionVisuals()
    {
        SetNavigationButtonStyle(
            AllLibraryButton,
            _sourceFilter is null && _filterMode == FilterAll);
        SetNavigationButtonStyle(
            AdoptedLibraryButton,
            _sourceFilter is null && _filterMode == FilterAdopted);
        SetNavigationButtonStyle(
            WarningLibraryButton,
            _sourceFilter is null && _filterMode == FilterWarning);
        SetNavigationButtonStyle(
            AllSourcesButton,
            _sourceFilter is null && _filterMode == FilterAll);

        var sourceFilterIsActive = _sourceFilter is not null && _filterMode == FilterAll;
        foreach (var sourceFilter in _sourceFilters)
        {
            sourceFilter.IsActive = sourceFilterIsActive &&
                                    string.Equals(
                                        sourceFilter.Name,
                                        _sourceFilter,
                                        StringComparison.OrdinalIgnoreCase);
        }
    }

    private void SetNavigationButtonStyle(Button button, bool isActive)
    {
        button.Style = (Style)FindResource(isActive ? "SelectedNavButton" : "NavButton");
    }

    private void UpdateReviewSummary()
    {
        var adoptedCount = _tracks.Count(track => track.IsAdopted);
        var assignmentConflictCount = _generationPlan.Conflicts.Count(
            IsActiveMusicTypeIntegrationTarget);

        ReviewSummaryText.Text = UiText.Format("Main.ReviewSummary", adoptedCount);
        ReviewWarningText.Text = UiText.Format(
            "Main.ReviewWarningDetailed",
            _audioDuplicateGroups.Count,
            _audioDuplicateGroups.Count(group => group.Kind == AudioDuplicateKind.PathConflict),
            _audioDuplicateGroups.Count(group => group.Kind == AudioDuplicateKind.ContentMatch),
            _audioDuplicateGroups.Count(group => group.Kind == AudioDuplicateKind.SimilarCandidate));
        AssignmentConflictSummaryText.Text = UiText.Format(
            "Main.AssignmentConflictSummary",
            assignmentConflictCount);
        AssignmentConflictInfoButton.Visibility = assignmentConflictCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        AssignmentConflictInfoButton.IsEnabled = assignmentConflictCount > 0;
    }

    private void UpdatePrerequisiteWarning()
    {
        var adoptedEntries = _generationPlan.Entries
            .Where(entry => entry.IsAdopted && entry.Asset is not null)
            .ToArray();
        var requiresMusicTypeDistributor = adoptedEntries.Any(entry =>
            entry.DestinationKeys.Any(destination =>
                destination.Scope != MusicSettingScope.WorldSpace ||
                !_createWorldSpaceMusicSettings));
        var requiresSkyPatcher = adoptedEntries.Any(entry =>
            entry.DestinationKeys.Any(destination => destination.Scope == MusicSettingScope.Cell));

        var status = _prerequisiteDetector.Detect(_scannedMods);
        var warnings = new List<string>();
        if (requiresMusicTypeDistributor && !status.MusicTypeDistributorFound)
        {
            warnings.Add(UiText.Get("Main.PrerequisiteMtdMissing"));
        }

        if (requiresSkyPatcher && !status.SkyPatcherFound)
        {
            warnings.Add(UiText.Get("Main.PrerequisiteSkyPatcherMissing"));
        }

        if (warnings.Count == 0)
        {
            PrerequisiteWarningBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            warnings.Add(UiText.Get("Main.PrerequisiteEnableWarning"));
            PrerequisiteWarningText.Text = string.Join(Environment.NewLine, warnings);
            PrerequisiteWarningBorder.Visibility = Visibility.Visible;
        }

        GfMusicManagerLog.Info(
            "UpdatePrerequisiteWarning: " +
            $"requiredMtd={requiresMusicTypeDistributor}, foundMtd={status.MusicTypeDistributorFound}, " +
            $"requiredSkyPatcher={requiresSkyPatcher}, foundSkyPatcher={status.SkyPatcherFound}, " +
            $"warnings={warnings.Count}. " +
            $"mtdMod={status.MusicTypeDistributorModName ?? "<missing>"}, " +
            $"skyPatcherMod={status.SkyPatcherModName ?? "<missing>"}.");
    }

    private void AssignmentConflictInfoButton_Click(object sender, RoutedEventArgs e)
    {
        var conflicts = _generationPlan.Conflicts
            .Where(IsActiveMusicTypeIntegrationTarget)
            .ToArray();
        if (conflicts.Length == 0)
        {
            GfMusicManagerLog.Info("AssignmentConflictInfoButton_Click: no assignment conflicts.");
            return;
        }

        GfMusicManagerLog.Info(
            $"AssignmentConflictInfoButton_Click: opening details. conflicts={conflicts.Length}.");
        var detailsWindow = new MusicAssignmentConflictWindow(
            conflicts,
            _availableMusicSettings,
            _generationPlan.KeepVanillaMusic == true,
            includeWorldSpaceAssignments: _createWorldSpaceMusicSettings)
        {
            Owner = this
        };
        detailsWindow.ShowDialog();
        GfMusicManagerLog.Info("AssignmentConflictInfoButton_Click: details closed.");
    }

    private bool IsActiveMusicTypeIntegrationTarget(MusicGenerationPlanConflict conflict) =>
        conflict.Kind == MusicGenerationPlanConflictKind.MultipleGeneratedMusicTypesForRecord &&
        (_createWorldSpaceMusicSettings || conflict.TargetScope != MusicSettingScope.WorldSpace);

    private void UpdateSelectedTrack(TrackRow row)
    {
        SelectedTrackTitleText.Text = row.Title;
        NowPlayingTitleText.Text = row.Title;
        SelectedSourceText.Text = row.Source;
        SelectedHandlingText.Text = row.HandlingText;
        SelectedHandlingText.Foreground = row.HandlingKind == TrackAssetHandling.Reference
            ? FindResource("GreenAccent") as System.Windows.Media.Brush
            : FindResource("AmberAccent") as System.Windows.Media.Brush;
        SelectedPlacementText.Text = row.Placement;
        SelectedAdoptionText.Text = UiText.Get(
            row.IsAdopted ? "Main.SelectedAdopted" : "Main.SelectedExcluded");
        SelectedAdoptionText.Foreground = row.IsAdopted
            ? FindResource("GreenAccent") as System.Windows.Media.Brush
            : FindResource("MutedForeground") as System.Windows.Media.Brush;
        SelectedStatusText.Text = row.HasWarning
            ? UiText.Get("Main.SelectedNeedsReview")
            : row.HasAutomaticResolution
                ? UiText.Get("Main.SelectedAutoOrganized")
                : UiText.Get("Main.SelectedNoIssue");
        SelectedStatusText.Foreground = row.HasWarning
            ? FindResource("AmberAccent") as System.Windows.Media.Brush
            : FindResource("GreenAccent") as System.Windows.Media.Brush;

        if (row.HasWarning || row.HasAutomaticResolution)
        {
            WarningText.Text = string.Join(
                Environment.NewLine + Environment.NewLine,
                new[]
                {
                    row.Warning,
                    row.AutomaticResolutionText,
                    row.HasAudioDuplicateWarning
                        ? UiText.Get("Main.WarningReviewInstruction")
                        : string.Empty
                }
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            WarningBorder.Background = row.HasWarning
                ? FindResource("AmberPanel") as System.Windows.Media.Brush
                : FindResource("PanelRaisedBackground") as System.Windows.Media.Brush;
            WarningBorder.BorderBrush = row.HasWarning
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(123, 90, 48))
                : FindResource("BorderBrush") as System.Windows.Media.Brush;
            WarningText.Foreground = row.HasWarning
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 203, 141))
                : FindResource("MutedForeground") as System.Windows.Media.Brush;
            WarningBorder.Visibility = Visibility.Visible;
            WarningDetailsButton.Visibility = row.HasAudioDuplicateWarning
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        else
        {
            WarningText.Text = string.Empty;
            WarningBorder.Visibility = Visibility.Collapsed;
            WarningDetailsButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ClearSelectedTrack()
    {
        SelectedTrackTitleText.Text = UiText.Get("Main.SourceNoScan");
        NowPlayingTitleText.Text = UiText.Get("Main.SourceNotSelected");
        SelectedSourceText.Text = "—";
        SelectedHandlingText.Text = "—";
        SelectedPlacementText.Text = "—";
        SelectedAdoptionText.Text = "—";
        SelectedStatusText.Text = UiText.Get("Main.SourceScanWaiting");
        WarningText.Text = string.Empty;
        WarningBorder.Visibility = Visibility.Collapsed;
        WarningDetailsButton.Visibility = Visibility.Collapsed;
    }

    private void UpdateEmptyLibraryState()
    {
        EmptyLibraryBorder.Visibility = _tracks.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}

public sealed record LanguageOption(string Code, string DisplayName);

public enum TrackAssetHandling
{
    Reference,
    Copy
}

public sealed class TrackRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isPreviewActive;
    private readonly IReadOnlyList<MusicSettingSource> _sourceMusicSettings;
    private readonly IReadOnlyList<MusicConditionSource> _sourceMusicConditions;
    private readonly MusicGenerationPlanEntry _generationPlanEntry;
    private IReadOnlyList<MusicSettingSource> _musicSettings;
    private IReadOnlyList<MusicConditionSource> _musicConditions;

    public TrackRow(
        string title,
        string source,
        string placement,
        TrackAssetHandling handlingKind,
        string audioPath,
        string trackMeta,
        bool isSelected,
        string warning,
        AssetSource? asset = null,
        IReadOnlyList<MusicSettingSource>? musicSettings = null,
        IReadOnlyList<MusicSettingSource>? availableMusicSettings = null,
        MusicGenerationPlanEntry? generationPlanEntry = null,
        IReadOnlyList<MusicConditionSource>? musicConditions = null,
        IReadOnlyList<MusicConditionSource>? availableMusicConditions = null,
        IReadOnlyList<PluginRecordSource>? availableKeywordRecords = null,
        IReadOnlyList<PluginRecordSource>? availableWeatherRecords = null,
        IReadOnlyList<MusicDefinitionConflict>? definitionConflicts = null,
        string? automaticResolutionText = null,
        IReadOnlyList<AudioDuplicateGroup>? audioDuplicateGroups = null)
    {
        Title = title;
        Source = source;
        Placement = placement;
        HandlingKind = handlingKind;
        AudioPath = audioPath;
        TrackMeta = trackMeta;
        Warning = warning;
        AutomaticResolutionText = automaticResolutionText ?? string.Empty;
        AudioDuplicateGroups = audioDuplicateGroups ?? Array.Empty<AudioDuplicateGroup>();
        Asset = asset;
        _isSelected = isSelected;
        _musicSettings = musicSettings ?? Array.Empty<MusicSettingSource>();
        _sourceMusicSettings = _musicSettings;
        _musicConditions = musicConditions ?? Array.Empty<MusicConditionSource>();
        _sourceMusicConditions = _musicConditions;
        _generationPlanEntry = generationPlanEntry
            ?? new MusicGenerationPlanEntry(
                asset is not null
                    ? MusicGenerationPlanEntry.CreateAssetKey(asset)
                    : $"untracked:{title}",
                isSelected,
                _musicSettings,
                _musicConditions);
        AvailableMusicSettings = BuildAvailableSettings(_musicSettings, availableMusicSettings);
        AvailableMusicConditions = BuildAvailableConditions(_musicConditions, availableMusicConditions);
        AvailableKeywordRecords = availableKeywordRecords ?? Array.Empty<PluginRecordSource>();
        AvailableWeatherRecords = availableWeatherRecords ?? Array.Empty<PluginRecordSource>();
        DefinitionConflicts = definitionConflicts ?? Array.Empty<MusicDefinitionConflict>();
    }

    public static TrackRow FromAsset(
        AssetSource asset,
        MusicAnalysisResult? musicAnalysis = null,
        MusicGenerationPlan? generationPlan = null,
        IReadOnlyList<MusicSettingSource>? availableMusicSettings = null,
        IReadOnlyList<AssetSource>? duplicateSources = null,
        IReadOnlyList<AudioDuplicateGroup>? audioDuplicateGroups = null,
        MusicAssetBinding? assetBinding = null)
    {
        var title = Path.GetFileNameWithoutExtension(asset.VirtualPath);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = asset.VirtualPath;
        }

        var sourceKind = UiText.Get(
            asset.IsFromArchive ? "Main.AssetSourceBsa" : "Main.AssetSourceLoose");
        var handlingKind = asset.IsVfsWinner && asset.ModEnabled
            ? TrackAssetHandling.Reference
            : TrackAssetHandling.Copy;
        var musicSettings = assetBinding?.Settings ??
            musicAnalysis?.GetSettingsForAsset(asset.VirtualPath)
            ?? Array.Empty<MusicSettingSource>();
        var definitionConflicts = assetBinding?.DefinitionConflicts ??
            musicAnalysis?.GetDefinitionConflictsForAsset(asset.VirtualPath)
            ?? Array.Empty<MusicDefinitionConflict>();
        var musicConditions = assetBinding?.Conditions ??
            GetConditionsForAsset(asset.VirtualPath, musicSettings);
        var placement = MusicPlacementFormatter.FormatCount(musicSettings);
        var warning = asset.ModEnabled
            ? string.Empty
            : UiText.Get("Main.DisabledAudioWarning");
        var automaticResolutionText = duplicateSources is { Count: > 1 }
            ? UiText.Get("Main.DuplicateAudioWarning")
                .Replace("\n", Environment.NewLine, StringComparison.Ordinal)
            : string.Empty;
        var generationPlanEntry = generationPlan?.GetOrCreate(asset, musicSettings, musicConditions);
        return new TrackRow(
            title,
            asset.ModName,
            placement,
            handlingKind,
            asset.VirtualPath,
            $"{sourceKind} · {asset.VirtualPath}",
            false,
            warning,
            asset,
            musicSettings,
            availableMusicSettings ?? musicAnalysis?.Settings,
            generationPlanEntry,
            musicConditions,
            musicAnalysis?.ConditionCandidates,
            musicAnalysis?.KeywordCandidates,
            musicAnalysis?.WeatherCandidates,
            definitionConflicts,
            automaticResolutionText,
            audioDuplicateGroups);
    }

    public string Title { get; }
    public string Source { get; }
    public string Placement { get; private set; }
    public TrackAssetHandling HandlingKind { get; }
    public bool RequiresCopy => HandlingKind == TrackAssetHandling.Copy;
    public string HandlingText => UiText.Get(
        HandlingKind == TrackAssetHandling.Reference
            ? "Track.Handling.Reference"
            : "Track.Handling.Copy");
    public string AudioPath { get; }
    public string TrackMeta { get; }
    public string Warning { get; }
    public string AutomaticResolutionText { get; }
    public AssetSource? Asset { get; }
    public IReadOnlyList<AudioDuplicateGroup> AudioDuplicateGroups { get; }
    public IReadOnlyList<MusicSettingSource> SourceMusicSettings => _sourceMusicSettings;
    public IReadOnlyList<MusicSettingSource> MusicSettings => _musicSettings;
    public IReadOnlyList<MusicSettingSource> AvailableMusicSettings { get; }
    public IReadOnlyList<MusicConditionSource> SourceMusicConditions => _sourceMusicConditions;
    public IReadOnlyList<MusicConditionSource> MusicConditions => _musicConditions;
    public IReadOnlyList<MusicConditionSource> AvailableMusicConditions { get; }
    public IReadOnlyList<MusicGenerationTrackPlan> MusicTrackPlans =>
        _generationPlanEntry.Tracks;
    public IReadOnlyList<PluginRecordSource> AvailableKeywordRecords { get; }
    public IReadOnlyList<PluginRecordSource> AvailableWeatherRecords { get; }
    public IReadOnlyList<MusicDefinitionConflict> DefinitionConflicts { get; }
    public MusicGenerationPlanEntry GenerationPlanEntry => _generationPlanEntry;
    public string AssetKey => _generationPlanEntry.AssetKey;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning) || HasAudioDuplicateWarning;
    public bool HasAutomaticResolution => !string.IsNullOrWhiteSpace(AutomaticResolutionText);
    public bool HasAudioDuplicateWarning => AudioDuplicateGroups.Count > 0;
    public string MusicTypeCountText => UiText.Format(
        "Main.DefinitionCount",
        _musicSettings
            .Select(setting => setting.MusicTypeFormKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());

    public bool IsUnused => _musicSettings.Count == 0;

    public bool IsAdopted
    {
        get => _generationPlanEntry.IsAdopted;
        set
        {
            if (_generationPlanEntry.IsAdopted == value)
            {
                return;
            }

            _generationPlanEntry.IsAdopted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AdoptionStatusText));
        }
    }

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
            OnPropertyChanged();
        }
    }

    public string AdoptionStatusText => UiText.Get(
        IsAdopted ? "Main.SelectedAdopted" : "Main.SelectedExcluded");

    public void ReplaceMusicSettings(IEnumerable<MusicSettingSource> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _musicSettings = settings
            .DistinctBy(GetMusicSettingKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _generationPlanEntry.ReplaceDestinations(_musicSettings);
        Placement = MusicPlacementFormatter.FormatCount(_musicSettings);
        OnPropertyChanged(nameof(MusicSettings));
        OnPropertyChanged(nameof(Placement));
        OnPropertyChanged(nameof(MusicTypeCountText));
        OnPropertyChanged(nameof(IsUnused));
    }

    public void AddMusicSettings(IEnumerable<MusicSettingSource> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ReplaceMusicSettings(_musicSettings.Concat(settings));
    }

    public void ReplaceMusicConditions(IEnumerable<MusicConditionSource> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        _musicConditions = conditions
            .DistinctBy(MusicConditionFormatter.CreateKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _generationPlanEntry.ReplaceConditions(_musicConditions);
        OnPropertyChanged(nameof(MusicConditions));
    }

    public IReadOnlyList<MusicConditionSource>? GetMusicTrackConditions(string trackKey) =>
        _generationPlanEntry.GetTrackConditions(trackKey);

    public void ReplaceMusicTrackConditions(
        IEnumerable<MusicGenerationTrackPlan> trackPlans)
    {
        ArgumentNullException.ThrowIfNull(trackPlans);
        _generationPlanEntry.ApplyTrackConditions(trackPlans);
        SyncMusicConditionsFromPlan();
    }

    public void ApplyRestoredPlanState(
        IReadOnlyList<MusicSettingSource> availableMusicSettings)
    {
        ArgumentNullException.ThrowIfNull(availableMusicSettings);
        var restoredDestinations = _generationPlanEntry.DestinationKeys
            .Select(key => availableMusicSettings.FirstOrDefault(setting =>
                setting.Scope == key.Scope &&
                setting.ScopeFormKey.Equals(key.ScopeFormKey, StringComparison.OrdinalIgnoreCase) &&
                setting.MusicTypeFormKey.Equals(key.MusicTypeFormKey, StringComparison.OrdinalIgnoreCase)))
            .Where(setting => setting is not null)
            .Cast<MusicSettingSource>()
            .ToArray();
        if (_generationPlanEntry.DestinationKeys.Count == 0 || restoredDestinations.Length > 0)
        {
            ReplaceMusicSettings(restoredDestinations);
        }

        SyncMusicConditionsFromPlan();
    }

    private void SyncMusicConditionsFromPlan()
    {
        _musicConditions = _generationPlanEntry.Conditions;
        OnPropertyChanged(nameof(MusicConditions));
    }

    public static string GetMusicSettingKey(MusicSettingSource setting) =>
        string.Join(
            ":",
            setting.Scope,
            setting.ScopeFormKey,
            setting.MusicTypeFormKey);

    public static string GetMusicSettingKey(MusicSettingKey setting) =>
        string.Join(
            ":",
            setting.Scope,
            setting.ScopeFormKey,
            setting.MusicTypeFormKey);

    public bool IsPreviewActive
    {
        get => _isPreviewActive;
        private set
        {
            if (_isPreviewActive == value)
            {
                return;
            }

            _isPreviewActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewButtonContent));
            OnPropertyChanged(nameof(PreviewButtonToolTip));
        }
    }

    public string PreviewButtonContent => IsPreviewActive ? "■" : "▶";
    public string PreviewButtonToolTip => UiText.Get(
        IsPreviewActive ? "Main.PreviewStop" : "Main.PreviewPlay");

    public void SetPreviewActive(bool active)
    {
        IsPreviewActive = active;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static IReadOnlyList<MusicSettingSource> BuildAvailableSettings(
        IReadOnlyList<MusicSettingSource> current,
        IReadOnlyList<MusicSettingSource>? available)
    {
        if (available is not null)
        {
            return available;
        }

        return current
            .DistinctBy(GetMusicSettingKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(setting => setting.Scope)
            .ThenBy(setting => setting.ScopeName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<MusicConditionSource> BuildAvailableConditions(
        IReadOnlyList<MusicConditionSource> current,
        IReadOnlyList<MusicConditionSource>? available)
    {
        return (available ?? current)
            .DistinctBy(MusicConditionFormatter.CreateKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(condition => condition.FunctionName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(condition => condition.ComparisonValue)
            .ToArray();
    }

    private static IReadOnlyList<MusicConditionSource> GetConditionsForAsset(
        string virtualPath,
        IReadOnlyList<MusicSettingSource> settings)
    {
        return settings
            .SelectMany(setting => setting.Tracks)
            .Where(track => track.MatchesAudioPath(virtualPath))
            .SelectMany(track => track.Conditions)
            .DistinctBy(MusicConditionFormatter.CreateKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class SourceFilterRow : INotifyPropertyChanged
{
    private bool _isActive;

    public SourceFilterRow(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; }
    public int Count { get; }
    public string CountText => UiText.Format("Main.UsageCount", Count);
    public string DisplayText => $"● {Name}     {CountText}";

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
