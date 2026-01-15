using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>
/// 設定画面のViewModel
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private AppSettings _settings;
    private readonly ISettingsService _settingsService;
    private readonly OverlayViewModel _overlayViewModel;

    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    public SettingsViewModel(IOptionsSnapshot<AppSettings> options, ISettingsService settingsService, OverlayViewModel overlayViewModel)
    {
        _settings = options.Value;
        _settingsService = settingsService;
        _overlayViewModel = overlayViewModel;
        Languages = new ReadOnlyCollection<LanguageType>(Enum.GetValues<LanguageType>());
        FontFamilies = new ReadOnlyCollection<string>(new[]
        {
            "Yu Gothic UI",
            "Meiryo UI",
            "Segoe UI",
            "MS Gothic",
            "Noto Sans CJK JP"
        });
        FontSizes = new ReadOnlyCollection<double>(new[] { 12d, 14d, 16d, 18d, 20d, 24d, 28d, 32d, 36d, 40d });
        TextColorOptions = new ReadOnlyCollection<ColorOption>(new ColorOption[]
        {
            new("白", "#FFFFFFFF"),
            new("薄白", "#CCFFFFFF"),
            new("黄", "#FFFFD700"),
            new("シアン", "#FF00FFFF"),
            new("緑", "#FF00FF7F"),
            new("赤", "#FFFF6B6B")
        });
        BackgroundColorOptions = new ReadOnlyCollection<ColorOption>(new ColorOption[]
        {
            new("黒（標準）", "#80000000"),
            new("黒（濃い）", "#CC000000"),
            new("黒（薄い）", "#40000000"),
            new("透明", "#00000000")
        });
        MaxLinesList = new ReadOnlyCollection<int>(new[] { 1, 2, 3, 4, 5 });
    }

    public AppSettings Settings => _settings;

    public ReadOnlyCollection<LanguageType> Languages { get; }

    public ReadOnlyCollection<string> FontFamilies { get; }

    public ReadOnlyCollection<double> FontSizes { get; }

    public ReadOnlyCollection<ColorOption> TextColorOptions { get; }

    public ReadOnlyCollection<ColorOption> BackgroundColorOptions { get; }

    public ReadOnlyCollection<int> MaxLinesList { get; }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private async Task SaveAsync()
    {
        await _settingsService.SaveAsync(_settings);
        _overlayViewModel.ReloadSettings();
        SettingsSaved?.Invoke(this, new SettingsSavedEventArgs(_settings));
        StatusMessage = $"設定を保存しました: {DateTime.Now:HH:mm:ss}";
    }
}

public sealed class ColorOption
{
    public ColorOption(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }
}

public class SettingsSavedEventArgs : EventArgs
{
    public AppSettings Settings { get; }

    public SettingsSavedEventArgs(AppSettings settings)
    {
        Settings = settings;
    }
}
