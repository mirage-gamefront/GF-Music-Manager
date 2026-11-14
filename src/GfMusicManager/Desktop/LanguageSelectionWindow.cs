using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Localization;

namespace GfMusicManager.Desktop;

/// <summary>
/// Shown once at the first launch, before any UI language is known, so every
/// string here is written in both languages instead of using the UI catalog.
/// Closing the window without choosing keeps the language that matches the
/// operating system.
/// </summary>
internal sealed class LanguageSelectionWindow : Window
{
    public LanguageSelectionWindow(string suggestedLanguage)
    {
        SelectedLanguage = UiLanguage.Normalize(suggestedLanguage);
        Title = "GF Music Manager";
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 360;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "Select your language\n言語を選択してください",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        buttons.Children.Add(CreateButton("English", UiLanguage.English));
        buttons.Children.Add(CreateButton("日本語", UiLanguage.Japanese));
        panel.Children.Add(buttons);

        panel.Children.Add(new TextBlock
        {
            Text = "You can change this later in Settings.\n後で設定から変更できます。",
            FontSize = 12,
            Opacity = 0.7,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        });
        Content = panel;
    }

    public string SelectedLanguage { get; private set; }

    private Button CreateButton(string caption, string language)
    {
        var isSuggested = string.Equals(language, SelectedLanguage, StringComparison.OrdinalIgnoreCase);
        var button = new Button
        {
            Content = caption,
            MinWidth = 130,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(6, 0, 6, 0),
            IsDefault = isSuggested
        };
        button.Click += (_, _) =>
        {
            SelectedLanguage = language;
            GfMusicManagerLog.Info($"LanguageSelectionWindow: selected {language}.");
            Close();
        };
        return button;
    }
}
