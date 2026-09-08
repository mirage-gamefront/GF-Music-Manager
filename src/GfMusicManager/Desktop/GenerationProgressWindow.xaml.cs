using System.ComponentModel;
using System.Windows;
using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Localization;

namespace GfMusicManager.Desktop;

public partial class GenerationProgressWindow : Window
{
    private bool _canClose;

    public GenerationProgressWindow()
    {
        InitializeComponent();
        Update(new MusicGenerationProgress(
            MusicGenerationProgressStage.Preparing,
            UiText.Get("Main.GenerationPreparing"),
            0));
    }

    public void Update(MusicGenerationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var percent = Math.Clamp(progress.Percent, 0, 100);
        GenerationProgressBar.Value = percent;
        StageText.Text = progress.Message;
        CountText.Text = progress.Total > 0
            ? UiText.Format("Main.GenerationProgressDialogCount", progress.Current, progress.Total)
            : UiText.Get("Main.GenerationProgressDialogWorking");
        PercentText.Text = UiText.Format("Main.GenerationProgressDialogPercent", percent);
    }

    public void CloseAfterOperation()
    {
        _canClose = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_canClose)
        {
            e.Cancel = true;
        }
    }
}
