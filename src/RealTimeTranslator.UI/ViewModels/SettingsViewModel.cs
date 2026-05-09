using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly AppSettings _settings;
    private readonly ISettingsService _settingsService;
    private readonly OverlayViewModel _overlayViewModel;
    private CancellationTokenSource? _autoSaveCts;

    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    public SettingsViewModel(IOptionsMonitor<AppSettings> options, ISettingsService settingsService, OverlayViewModel overlayViewModel)
    {
        _settings = options.CurrentValue;
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
    private bool _isTestingApi;

    [ObservableProperty]
    private string _apiTestResult = string.Empty;

    // ───── API設定プロパティ（自動保存付き） ─────

    public string ApiKey
    {
        get => _settings.OpenAIRealtime.ApiKey;
        set
        {
            if (_settings.OpenAIRealtime.ApiKey != value)
            {
                _settings.OpenAIRealtime.ApiKey = value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public OutputLanguageOption? SelectedOutputLanguage
    {
        get => OutputLanguageOptions.FirstOrDefault(o => o.Code == _settings.OpenAIRealtime.OutputLanguage)
               ?? OutputLanguageOptions[0];
        set
        {
            if (value != null && _settings.OpenAIRealtime.OutputLanguage != value.Code)
            {
                _settings.OpenAIRealtime.OutputLanguage = value.Code;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    // ───── 表示設定プロパティ（自動保存付き） ─────

    public string? SelectedFontFamily
    {
        get => _settings.Overlay.FontFamily;
        set
        {
            if (value != null && _settings.Overlay.FontFamily != value)
            {
                _settings.Overlay.FontFamily = value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public double SelectedFontSize
    {
        get => _settings.Overlay.FontSize;
        set
        {
            if (Math.Abs(_settings.Overlay.FontSize - value) > 0.01)
            {
                _settings.Overlay.FontSize = value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public int SelectedMaxLines
    {
        get => _settings.Overlay.MaxLines;
        set
        {
            if (_settings.Overlay.MaxLines != value)
            {
                _settings.Overlay.MaxLines = value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public ColorOption? SelectedPartialTextColorOption
    {
        get => TextColorOptions.FirstOrDefault(o => o.Value == _settings.Overlay.PartialTextColor)
               ?? TextColorOptions[0];
        set
        {
            if (value != null && _settings.Overlay.PartialTextColor != value.Value)
            {
                _settings.Overlay.PartialTextColor = value.Value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public ColorOption? SelectedFinalTextColorOption
    {
        get => TextColorOptions.FirstOrDefault(o => o.Value == _settings.Overlay.FinalTextColor)
               ?? TextColorOptions[0];
        set
        {
            if (value != null && _settings.Overlay.FinalTextColor != value.Value)
            {
                _settings.Overlay.FinalTextColor = value.Value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public ColorOption? SelectedBackgroundColorOption
    {
        get => BackgroundColorOptions.FirstOrDefault(o => o.Value == _settings.Overlay.BackgroundColor)
               ?? BackgroundColorOptions[0];
        set
        {
            if (value != null && _settings.Overlay.BackgroundColor != value.Value)
            {
                _settings.Overlay.BackgroundColor = value.Value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    // ───── 自動保存 ─────

    private void ScheduleAutoSave()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts = new CancellationTokenSource();
        var token = _autoSaveCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AutoSaveDelay, token);
                if (!token.IsCancellationRequested)
                    await SaveInternalAsync();
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private async Task SaveInternalAsync()
    {
        try
        {
            await _settingsService.SaveAsync(_settings);
            // UIスレッドで通知（OverlayViewModel.ReloadSettings や MainViewModel.OnSettingsSaved が UI バインドプロパティを操作するため）
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlayViewModel.ReloadSettings();
                SettingsSaved?.Invoke(this, new SettingsSavedEventArgs(_settings));
            });
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"設定の自動保存に失敗: {ex.Message}");
        }
    }

    // ───── API接続テスト ─────

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
