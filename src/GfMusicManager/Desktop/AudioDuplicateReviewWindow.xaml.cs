using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Localization;
using SkyrimScan.Core.Models;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace GfMusicManager.Desktop;

public partial class AudioDuplicateReviewWindow : Window
{
    private readonly Action<AssetSource> _previewSource;
    private readonly Action _stopPreview;
    private readonly Func<AssetSource, bool> _isPreviewActive;
    private readonly DispatcherTimer _previewStateTimer;

    public AudioDuplicateReviewWindow(
        IReadOnlyList<AudioDuplicateGroup> groups,
        IReadOnlySet<string> adoptedAssetKeys,
        Action<AssetSource> previewSource,
        Action stopPreview,
        Func<AssetSource, bool> isPreviewActive,
        IReadOnlyDictionary<string, IReadOnlyList<MusicSettingSource>>? usageByAssetKey = null,
        IReadOnlyDictionary<string, int>? modPriorities = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(adoptedAssetKeys);
        ArgumentNullException.ThrowIfNull(previewSource);
        ArgumentNullException.ThrowIfNull(stopPreview);
        ArgumentNullException.ThrowIfNull(isPreviewActive);
        _previewSource = previewSource;
        _stopPreview = stopPreview;
        _isPreviewActive = isPreviewActive;
        Groups = new ObservableCollection<AudioDuplicateGroupReview>(
            groups.Select((group, index) =>
                new AudioDuplicateGroupReview(
                    group,
                    adoptedAssetKeys,
                    index,
                    usageByAssetKey,
                    modPriorities)));
        PathConflictGroups = Groups
            .Where(group => group.Group.Kind == AudioDuplicateKind.PathConflict)
            .ToArray();
        ContentMatchGroups = Groups
            .Where(group => group.Group.Kind == AudioDuplicateKind.ContentMatch)
            .ToArray();
        SimilarCandidateGroups = Groups
            .Where(group => group.Group.Kind == AudioDuplicateKind.SimilarCandidate)
            .ToArray();
        InitializeComponent();
        DataContext = this;
        _previewStateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _previewStateTimer.Tick += PreviewStateTimer_Tick;
        Loaded += (_, _) =>
        {
            UpdatePreviewButtonStates();
            _previewStateTimer.Start();
        };
        Closed += (_, _) => _previewStateTimer.Stop();
    }

    public ObservableCollection<AudioDuplicateGroupReview> Groups { get; }
    public IReadOnlyList<AudioDuplicateGroupReview> PathConflictGroups { get; }
    public IReadOnlyList<AudioDuplicateGroupReview> ContentMatchGroups { get; }
    public IReadOnlyList<AudioDuplicateGroupReview> SimilarCandidateGroups { get; }
    public string PathConflictTabHeader =>
        UiText.Format("AudioDuplicate.Tab.PathConflict", PathConflictGroups.Count);
    public string ContentMatchTabHeader =>
        UiText.Format("AudioDuplicate.Tab.ContentMatch", ContentMatchGroups.Count);
    public string SimilarCandidateTabHeader =>
        UiText.Format("AudioDuplicate.Tab.Similar", SimilarCandidateGroups.Count);
    public string PathConflictPageDescription => UiText.Get("AudioDuplicate.Page.PathConflict");
    public string ContentMatchPageDescription => UiText.Get("AudioDuplicate.Page.ContentMatch");
    public string SimilarCandidatePageDescription => UiText.Get("AudioDuplicate.Page.Similar");
    public Visibility PathConflictEmptyVisibility =>
        PathConflictGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentMatchEmptyVisibility =>
        ContentMatchGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SimilarCandidateEmptyVisibility =>
        SimilarCandidateGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public IReadOnlyList<AudioDuplicateReviewDecision> Decisions { get; private set; } =
        Array.Empty<AudioDuplicateReviewDecision>();

    private void PreviewSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioDuplicateSourceReview source })
        {
            if (_isPreviewActive(source.Source.Asset))
            {
                GfMusicManagerLog.Info(
                    $"AudioDuplicateReview preview toggle: stop. asset={source.Source.AssetKey}.");
                _stopPreview();
            }
            else
            {
                GfMusicManagerLog.Info(
                    $"AudioDuplicateReview preview toggle: play. asset={source.Source.AssetKey}.");
                _stopPreview();
                _previewSource(source.Source.Asset);
            }

            UpdatePreviewButtonStates();
        }
    }

    private void PreviewStateTimer_Tick(object? sender, EventArgs e) =>
        UpdatePreviewButtonStates();

    private void UpdatePreviewButtonStates()
    {
        AudioDuplicateSourceReview? activeSource = null;
        foreach (var source in Groups.SelectMany(group => group.Sources))
        {
            if (_isPreviewActive(source.Source.Asset))
            {
                activeSource = source;
                break;
            }
        }

        foreach (var source in Groups.SelectMany(group => group.Sources))
        {
            source.SetPreviewActive(ReferenceEquals(source, activeSource));
        }
    }

    private void ContentMatchPreferLoadOrderButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBatch(
            "ContentMatch.PreferLoadOrder",
            ContentMatchGroups,
            group => group.AdoptHighestPriorityMod());

    private void ContentMatchAdoptAllButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBatch(
            "ContentMatch.AdoptAll",
            ContentMatchGroups,
            group => group.AdoptAllSources());

    private void ContentMatchExcludeAllButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBatch(
            "ContentMatch.ExcludeAll",
            ContentMatchGroups,
            group => group.ExcludeAllSources());

    private void SimilarCandidatePreferLoadOrderButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBatch(
            "SimilarCandidate.PreferLoadOrder",
            SimilarCandidateGroups,
            group => group.AdoptHighestPriorityMod());

    private void SimilarCandidateAdoptAllButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBatch(
            "SimilarCandidate.AdoptAll",
            SimilarCandidateGroups,
            group => group.AdoptAllSources());

    private void SimilarCandidateExcludeAllButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBatch(
            "SimilarCandidate.ExcludeAll",
            SimilarCandidateGroups,
            group => group.ExcludeAllSources());

    private static void ApplyBatch(
        string actionName,
        IReadOnlyList<AudioDuplicateGroupReview> groups,
        Action<AudioDuplicateGroupReview> action)
    {
        foreach (var group in groups)
        {
            action(group);
        }

        var adoptedCount = groups.Sum(group => group.GetAdoptedAssetKeys().Count);
        GfMusicManagerLog.Info(
            $"AudioDuplicateReview batch action: {actionName}, " +
            $"groups={groups.Count}, adoptedSources={adoptedCount}.");
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var invalidGroup = Groups.FirstOrDefault(group => !group.HasValidDecision);
        if (invalidGroup is not null)
        {
            MessageBox.Show(
                invalidGroup.ValidationMessage,
                UiText.Get("AudioDuplicate.ApplyTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Decisions = Groups
            .Select(group => new AudioDuplicateReviewDecision(
                group.Group,
                group.GetAdoptedAssetKeys()))
            .ToArray();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

public sealed class AudioDuplicateGroupReview
{
    public AudioDuplicateGroupReview(
        AudioDuplicateGroup group,
        IReadOnlySet<string> adoptedAssetKeys,
        int index,
        IReadOnlyDictionary<string, IReadOnlyList<MusicSettingSource>>? usageByAssetKey = null,
        IReadOnlyDictionary<string, int>? modPriorities = null)
    {
        Group = group;
        GroupName = $"AudioDuplicateGroup_{index}";
        _modPriorities = modPriorities ?? EmptyModPriorities;
        var existingAdoptedSources = group.Sources
            .Where(source => adoptedAssetKeys.Contains(source.AssetKey))
            .ToArray();
        var selectedPathAssetKey = group.RequiresSingleSelection
            ? existingAdoptedSources.Length == 1
                ? existingAdoptedSources[0].AssetKey
                : group.Sources
                    .FirstOrDefault(source => source.Asset.IsVfsWinner && source.Asset.ModEnabled)
                    ?.AssetKey
                    ?? group.Sources.FirstOrDefault()?.AssetKey
            : null;
        var defaultToAdopted = adoptedAssetKeys.Count == 0;
        Sources = new ObservableCollection<AudioDuplicateSourceReview>(
            group.Sources.Select(source =>
            {
                var usageSettings = usageByAssetKey is not null &&
                                    usageByAssetKey.TryGetValue(source.AssetKey, out var settings)
                    ? settings
                    : Array.Empty<MusicSettingSource>();
                return new AudioDuplicateSourceReview(
                    source,
                    GroupName,
                    group.RequiresSingleSelection,
                    group.RequiresSingleSelection
                        ? string.Equals(source.AssetKey, selectedPathAssetKey, StringComparison.OrdinalIgnoreCase)
                        : defaultToAdopted || adoptedAssetKeys.Contains(source.AssetKey),
                    usageSettings);
            }));
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyModPriorities =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, int> _modPriorities;

    public AudioDuplicateGroup Group { get; }
    public string GroupName { get; }
    public ObservableCollection<AudioDuplicateSourceReview> Sources { get; }
    public bool IsPathConflict => Group.RequiresSingleSelection;
    public string HeaderText =>
        UiText.Format("AudioDuplicate.Group.Header", Group.KindText, Sources.Count);
    public string SubjectText => Group.Kind == AudioDuplicateKind.PathConflict
        ? UiText.Format("AudioDuplicate.Subject.Path", Group.Subject)
        : UiText.Format("AudioDuplicate.Subject.Target", Group.Subject);
    public string MethodText => UiText.Format("AudioDuplicate.Method", Group.DetectionMethod);
    public string ScoreText => Group.ScoreText;
    public string SelectionHintText => Group.RequiresSingleSelection
        ? UiText.Get("AudioDuplicate.SelectionHint.Single")
        : UiText.Get("AudioDuplicate.SelectionHint.Multiple");

    public bool HasValidDecision => Group.RequiresSingleSelection
        ? Sources.Count(source => source.IsSelected) == 1
        : true;

    public string ValidationMessage => Group.RequiresSingleSelection
        ? UiText.Format("AudioDuplicate.Validation.Path", Group.Subject)
        : string.Empty;

    public IReadOnlySet<string> GetAdoptedAssetKeys() =>
        Sources
            .Where(source => Group.RequiresSingleSelection ? source.IsSelected : source.IsIncluded)
            .Select(source => source.Source.AssetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void AdoptHighestPriorityMod()
    {
        if (Group.RequiresSingleSelection)
        {
            return;
        }

        var preferredModName = GetHighestPriorityModName();
        foreach (var source in Sources)
        {
            source.IsIncluded = string.Equals(
                source.Source.Asset.ModName,
                preferredModName,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public void AdoptAllSources()
    {
        if (Group.RequiresSingleSelection)
        {
            return;
        }

        SetIncluded(true);
    }

    public void ExcludeAllSources()
    {
        if (Group.RequiresSingleSelection)
        {
            return;
        }

        SetIncluded(false);
    }

    private void SetIncluded(bool included)
    {
        foreach (var source in Sources)
        {
            source.IsIncluded = included;
        }
    }

    private string GetHighestPriorityModName() => Sources
        .Select(source => source.Source.Asset.ModName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(GetModPriority)
        .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault() ?? UiText.Get("Common.Unknown");

    private int GetModPriority(string modName) =>
        _modPriorities.TryGetValue(modName, out var priority)
            ? priority
            : int.MinValue;
}

public sealed class AudioDuplicateSourceReview : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isIncluded;
    private bool _isPreviewActive;

    public AudioDuplicateSourceReview(
        AudioDuplicateSource source,
        string groupName,
        bool isPathConflict,
        bool isAdopted,
        IReadOnlyList<MusicSettingSource>? usageSettings = null)
    {
        Source = source;
        GroupName = groupName;
        UsageSettings = usageSettings ?? Array.Empty<MusicSettingSource>();
        RadioVisibility = isPathConflict ? Visibility.Visible : Visibility.Collapsed;
        CheckBoxVisibility = isPathConflict ? Visibility.Collapsed : Visibility.Visible;
        _isSelected = isPathConflict && isAdopted;
        _isIncluded = !isPathConflict && isAdopted;
    }

    public AudioDuplicateSource Source { get; }
    public string GroupName { get; }
    public string Title => $"{Source.Asset.ModName} · {Path.GetFileName(Source.Asset.VirtualPath)}";
    public string DetailText =>
        UiText.Format(
            "AudioDuplicate.Detail",
            Source.SourceKindText,
            Source.WinnerText,
            Source.Asset.VirtualPath,
            Source.DurationSeconds is { } duration
                ? UiText.Format("AudioDuplicate.Detail.Duration", duration)
                : string.Empty);
    public string LocationText => Source.LocationText;
    public IReadOnlyList<MusicSettingSource> UsageSettings { get; }
    public string UsageText => UsageSettings.Count == 0
        ? UiText.Get("AudioDuplicate.Usage.None")
        : UiText.Format("AudioDuplicate.Usage.Count", UsageSettings.Count);
    public string UsageDetailText => UsageSettings.Count == 0
        ? UiText.Get("AudioDuplicate.Usage.NoSettings")
        : string.Join(
            " ／ ",
            UsageSettings
                .Select(FormatUsage)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)) +
          (UsageSettings
               .Select(FormatUsage)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .Skip(6)
               .Any()
              ? $" ／ {UiText.Get("AudioDuplicate.Usage.Other")}"
              : string.Empty);
    public Visibility RadioVisibility { get; }
    public Visibility CheckBoxVisibility { get; }
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

    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (_isIncluded == value)
            {
                return;
            }

            _isIncluded = value;
            OnPropertyChanged();
        }
    }

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

    public string PreviewButtonContent => IsPreviewActive
        ? UiText.Get("AudioDuplicate.Preview.Stop")
        : UiText.Get("AudioDuplicate.Preview.Play");
    public string PreviewButtonToolTip => IsPreviewActive
        ? UiText.Get("AudioDuplicate.Preview.StopTooltip")
        : UiText.Get("AudioDuplicate.Preview.PlayTooltip");

    public void SetPreviewActive(bool active) => IsPreviewActive = active;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatUsage(MusicSettingSource setting)
    {
        var musicType = setting.MusicTypeDisplayNameWithoutSuffix;
        var scopeLabel = UiText.Get($"Scope.{setting.Scope}");
        return setting.Scope == MusicSettingScope.MusicType
            ? UiText.Format("SourceDetails.Usage.MusicType", scopeLabel, musicType)
            : UiText.Format("SourceDetails.Usage.Scope", scopeLabel, setting.ScopeDisplayName, musicType);
    }
}
