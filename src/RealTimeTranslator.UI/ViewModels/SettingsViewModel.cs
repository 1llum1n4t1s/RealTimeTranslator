using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.UI.ViewModels;

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
        OutputLanguageOptions = new ReadOnlyCollection<OutputLanguageOption>(new OutputLanguageOption[]
        {
            new("ja", "日本語"),
            new("en", "English"),
            new("zh", "中文"),
            new("ko", "한국어"),
            new("es", "Español"),
            new("fr", "Français"),
            new("de", "Deutsch"),
            new("it", "Italiano"),
            new("pt", "Português"),
            new("ru", "Русский"),
            new("nl", "Nederlands"),
            new("pl", "Polski"),
            new("sv", "Svenska"),
            new("tr", "Türkçe"),
            new("hi", "हिन्दी")
        });
    }

    public AppSettings Settings => _settings;

    public ReadOnlyCollection<string> FontFamilies { get; }
    public ReadOnlyCollection<double> FontSizes { get; }
    public ReadOnlyCollection<ColorOption> TextColorOptions { get; }
    public ReadOnlyCollection<ColorOption> BackgroundColorOptions { get; }
    public ReadOnlyCollection<int> MaxLinesList { get; }
    public ReadOnlyCollection<OutputLanguageOption> OutputLanguageOptions { get; }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isTestingApi;

    [ObservableProperty]
    private string _apiTestResult = string.Empty;

    public OutputLanguageOption? SelectedOutputLanguage
    {
        get => OutputLanguageOptions.FirstOrDefault(o => o.Code == _settings.OpenAIRealtime.OutputLanguage)
               ?? OutputLanguageOptions[0];
        set
        {
            if (value != null)
            {
                _settings.OpenAIRealtime.OutputLanguage = value.Code;
                OnPropertyChanged();
            }
        }
    }

    public ColorOption? SelectedPartialTextColorOption
    {
        get => TextColorOptions.FirstOrDefault(o => o.Value == _settings.Overlay.PartialTextColor)
               ?? TextColorOptions[0];
        set { if (value != null) _settings.Overlay.PartialTextColor = value.Value; }
    }

    public ColorOption? SelectedFinalTextColorOption
    {
        get => TextColorOptions.FirstOrDefault(o => o.Value == _settings.Overlay.FinalTextColor)
               ?? TextColorOptions[0];
        set { if (value != null) _settings.Overlay.FinalTextColor = value.Value; }
    }

    public ColorOption? SelectedBackgroundColorOption
    {
        get => BackgroundColorOptions.FirstOrDefault(o => o.Value == _settings.Overlay.BackgroundColor)
               ?? BackgroundColorOptions[0];
        set { if (value != null) _settings.Overlay.BackgroundColor = value.Value; }
    }

    [RelayCommand]
    private async Task TestApiConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.OpenAIRealtime.ApiKey))
        {
            ApiTestResult = "APIキーを入力してください";
            return;
        }

        IsTestingApi = true;
        ApiTestResult = "接続テスト中...";

        try
        {
            var (success, message) = await OpenAIRealtimeClient.TestConnectionAsync(_settings.OpenAIRealtime);
            ApiTestResult = message;
        }
        catch (Exception ex)
        {
            ApiTestResult = $"テスト失敗: {ex.Message}";
        }
        finally
        {
            IsTestingApi = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await _settingsService.SaveAsync(_settings);
        _overlayViewModel.ReloadSettings();
        SettingsSaved?.Invoke(this, new SettingsSavedEventArgs(_settings));
        StatusMessage = $"設定を保存しました: {DateTime.Now:HH:mm:ss}";
    }
}

public sealed class OutputLanguageOption
{
    public OutputLanguageOption(string code, string displayName)
    {
        Code = code;
        DisplayName = displayName;
    }

    public string Code { get; }
    public string DisplayName { get; }
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
