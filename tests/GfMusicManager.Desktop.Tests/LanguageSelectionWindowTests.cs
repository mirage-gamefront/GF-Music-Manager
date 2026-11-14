using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GfMusicManager.Core.Localization;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class LanguageSelectionWindowTests
{
    [Theory]
    [InlineData(UiLanguage.English, UiLanguage.Japanese, UiLanguage.Japanese)]
    [InlineData(UiLanguage.Japanese, UiLanguage.English, UiLanguage.English)]
    public void ClickingALanguageButtonSelectsThatLanguage(
        string suggested,
        string clickedLanguage,
        string expected)
    {
        var selected = RunOnStaThread(() =>
        {
            var window = new LanguageSelectionWindow(suggested);
            var caption = clickedLanguage == UiLanguage.Japanese ? "日本語" : "English";
            var button = FindButtons(window).Single(candidate => Equals(candidate.Content, caption));
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return window.SelectedLanguage;
        });

        Assert.Equal(expected, selected);
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Japanese)]
    public void WithoutAChoiceTheLanguageSuggestedForTheSystemIsKept(string suggested)
    {
        var selected = RunOnStaThread(() =>
        {
            var window = new LanguageSelectionWindow(suggested);
            window.Close();
            return window.SelectedLanguage;
        });

        Assert.Equal(suggested, selected);
    }

    // The window is never shown: a Window needs no Application instance, and
    // ShowDialog returns immediately when another test left a shut-down
    // Application behind, which made showing it order-dependent.
    private static string RunOnStaThread(Func<string> action)
    {
        Exception? capturedException = null;
        string? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                capturedException = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(capturedException);
        return result!;
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button button)
            {
                yield return button;
            }

            foreach (var nested in FindButtons(child))
            {
                yield return nested;
            }
        }
    }
}
