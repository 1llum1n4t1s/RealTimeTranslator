using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RealTimeTranslator.UI.Services;

/// <summary>
/// メッセージボックス表示サービス
/// </summary>
public static class MessageBoxService
{
    public static async Task ShowAsync(string title, string message)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = CreateMessageWindow(title, message);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await window.ShowDialog(desktop.MainWindow);
            }
            else
            {
                window.Show();
            }
        });
    }

    public static async Task<bool> ShowYesNoAsync(string title, string message)
    {
        var result = false;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = CreateYesNoWindow(title, message, r => result = r);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await window.ShowDialog(desktop.MainWindow);
            }
            else
            {
                window.Show();
            }
        });
        return result;
    }

    public static async Task ShowWindowDialogAsync(Window owner, string title, string message)
    {
        var window = CreateMessageWindow(title, message);
        await window.ShowDialog(owner);
    }

    private static Window CreateMessageWindow(string title, string message)
    {
        var window = new Window
        {
            Title = title,
            Width = 450,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 20,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right }
                }
            }
        };
        var okButton = (Button)((StackPanel)window.Content!).Children[1];
        okButton.Click += (_, _) => window.Close();
        return window;
    }

    private static Window CreateYesNoWindow(string title, string message, Action<bool> onResult)
    {
        var yesButton = new Button { Content = "はい", Width = 80 };
        var noButton = new Button { Content = "いいえ", Width = 80 };
        var window = new Window
        {
            Title = title,
            Width = 450,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 20,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = { yesButton, noButton }
                    }
                }
            }
        };
        yesButton.Click += (_, _) => { onResult(true); window.Close(); };
        noButton.Click += (_, _) => { onResult(false); window.Close(); };
        return window;
    }
}
