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
            // システムフォント (Windows 既定)
            "Yu Gothic UI",
            "Meiryo UI",
            "Segoe UI",
            "MS Gothic",
            // 同梱フォント (Assets/Fonts/ から avares:// で解決、OverlayViewModel.EmbeddedFontMap 参照)
            "IBM Plex Sans JP",
            "Noto Sans JP",
            "LINE Seed JP",
            "Zen Maru Gothic",
            "M PLUS Rounded 1c"
        });
        FontSizes = new ReadOnlyCollection<double>(new[] { 12d, 14d, 16d, 18d, 20d, 24d, 28d, 32d, 36d, 40d });
        TextColorOptions = new ReadOnlyCollection<ColorOption>(new ColorOption[]
        {
            new("白",             "#FFFFFFFF"),
            new("薄白",           "#CCFFFFFF"),
            // OverlaySettings.PartialTextColor のデフォルト値と一致させる (未選択防止)
            new("白（半透明）",   "#80FFFFFF"),
            new("黄",             "#FFFFD700"),
            new("シアン",         "#FF00FFFF"),
            new("緑",             "#FF00FF7F"),
            new("赤",             "#FFFF6B6B")
        });
        BackgroundColorOptions = new ReadOnlyCollection<ColorOption>(new ColorOption[]
        {
            new("黒（標準）",   "#80000000"),
            new("黒（濃い）",   "#CC000000"),
            new("黒（薄い）",   "#40000000"),
            new("白（薄い）",   "#80FFFFFF"),
            new("白（濃い）",   "#CCEFEFEF"),
            new("灰（中）",     "#CC404040"),
            new("濃紺",         "#CC0F1A4C"),
            new("濃緑",         "#CC0F4C1F"),
            new("濃赤",         "#CC4C0F1F"),
            new("茶（暖色）",   "#CC3A2218"),
            new("紫（深）",     "#CC2A0F4C"),
            new("透明",         "#00000000")
        });
        MaxLinesList = new ReadOnlyCollection<int>(new[] { 1, 2, 3, 4, 5 });
        // 確定字幕がフェード開始するまでの時間 (秒)。 SubtitleDisplayItem の _displayEndTime に渡される。
        // 短文・会話多めなら短く、 長文・じっくり読みたいなら長く。
        DisplayDurations = new ReadOnlyCollection<double>(new[] { 2d, 3d, 5d, 7d, 10d, 15d });
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

        // 旧 settings.json から新フォント一覧 / 色一覧に存在しない値を持ち越したケース、
        // 範囲外のサイズ / 行数 / 言語が入っているケースを、 起動時にデフォルトへ矯正する。
        // ComboBox が未選択状態で出る UX バグ ( setter が呼ばれず実態とのズレが続く ) を防ぐ。
        SanitizeSettings();
    }

    public AppSettings Settings => _settings;

    /// <summary>
    /// 設定値が UI の選択肢一覧に存在するか検証し、存在しない場合は「最も妥当なデフォルト」に矯正する。
    /// 起動時に 1 回だけ実行し、矯正が走った場合は ScheduleAutoSave で settings.json にも反映する。
    ///
    /// 主なターゲット:
    ///  - フォント: 同梱フォント整理 (Noto Sans CJK JP 削除 / Tsukimi Rounded 削除) で旧設定が孤立する
    ///  - 字幕色 / 背景色: ラインナップ刷新で旧 16 進値が一覧から外れる
    ///  - サイズ / 行数: 異常値 (手書き編集で 15px や 7 行など) が入っていた場合
    ///  - 出力言語: コード規格外 (空文字 / 未対応 BCP-47) の場合
    /// </summary>
    private void SanitizeSettings()
    {
        bool changed = false;

        if (string.IsNullOrWhiteSpace(_settings.Overlay.FontFamily) ||
            !FontFamilies.Contains(_settings.Overlay.FontFamily))
        {
            // フォント未選択 / 一覧外 → 同梱の IBM Plex Sans JP に揃える
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: FontFamily='{_settings.Overlay.FontFamily}' が一覧外 → 'IBM Plex Sans JP' に矯正");
            _settings.Overlay.FontFamily = "IBM Plex Sans JP";
            changed = true;
        }

        if (!FontSizes.Contains(_settings.Overlay.FontSize))
        {
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: FontSize={_settings.Overlay.FontSize} が一覧外 → 24 に矯正");
            _settings.Overlay.FontSize = 24d;
            changed = true;
        }

        if (!MaxLinesList.Contains(_settings.Overlay.MaxLines))
        {
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: MaxLines={_settings.Overlay.MaxLines} が一覧外 → 3 に矯正");
            _settings.Overlay.MaxLines = 3;
            changed = true;
        }

        if (!DisplayDurations.Any(d => Math.Abs(d - _settings.Overlay.DisplayDuration) <= 0.01))
        {
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: DisplayDuration={_settings.Overlay.DisplayDuration} が一覧外 → 5 秒に矯正");
            _settings.Overlay.DisplayDuration = 5d;
            changed = true;
        }

        if (!TextColorOptions.Any(o => o.Value == _settings.Overlay.PartialTextColor))
        {
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: PartialTextColor='{_settings.Overlay.PartialTextColor}' が一覧外 → 先頭の '{TextColorOptions[0].Name}' に矯正");
            _settings.Overlay.PartialTextColor = TextColorOptions[0].Value;
            changed = true;
        }

        if (!TextColorOptions.Any(o => o.Value == _settings.Overlay.FinalTextColor))
        {
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: FinalTextColor='{_settings.Overlay.FinalTextColor}' が一覧外 → 先頭の '{TextColorOptions[0].Name}' に矯正");
            _settings.Overlay.FinalTextColor = TextColorOptions[0].Value;
            changed = true;
        }

        if (!BackgroundColorOptions.Any(o => o.Value == _settings.Overlay.BackgroundColor))
        {
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: BackgroundColor='{_settings.Overlay.BackgroundColor}' が一覧外 → 先頭の '{BackgroundColorOptions[0].Name}' に矯正");
            _settings.Overlay.BackgroundColor = BackgroundColorOptions[0].Value;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.OpenAIRealtime.OutputLanguage) ||
            !OutputLanguageOptions.Any(o => o.Code == _settings.OpenAIRealtime.OutputLanguage))
        {
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: OutputLanguage='{_settings.OpenAIRealtime.OutputLanguage}' が一覧外 → 'ja' に矯正");
            _settings.OpenAIRealtime.OutputLanguage = "ja";
            changed = true;
        }

        if (changed)
        {
            // 矯正結果を settings.json に永続化 (次回起動時の再矯正を避ける)
            ScheduleAutoSave();
        }
    }

    public ReadOnlyCollection<string> FontFamilies { get; }
    public ReadOnlyCollection<double> FontSizes { get; }
    public ReadOnlyCollection<ColorOption> TextColorOptions { get; }
    public ReadOnlyCollection<ColorOption> BackgroundColorOptions { get; }
    public ReadOnlyCollection<int> MaxLinesList { get; }
    public ReadOnlyCollection<double> DisplayDurations { get; }
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

    public double SelectedDisplayDuration
    {
        get => _settings.Overlay.DisplayDuration;
        set
        {
            if (Math.Abs(_settings.Overlay.DisplayDuration - value) > 0.01)
            {
                _settings.Overlay.DisplayDuration = value;
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

    // ───── VAD (Voice Activity Detection) 設定 ─────
    // 既存 Overlay 系プロパティと同じく `_settings.AudioCapture` を直接 get/set し、
    // 変更があれば ScheduleAutoSave で 1.5 秒後に settings.json へ atomic write。

    public bool EnableVad
    {
        get => _settings.AudioCapture.EnableVad;
        set
        {
            if (_settings.AudioCapture.EnableVad != value)
            {
                _settings.AudioCapture.EnableVad = value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public float VadThreshold
    {
        get => _settings.AudioCapture.VadThreshold;
        set
        {
            // Slider の連続値で発火するため微小差は無視 (settings.json 書き込み頻度抑制)
            if (Math.Abs(_settings.AudioCapture.VadThreshold - value) > 0.005f)
            {
                _settings.AudioCapture.VadThreshold = value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public int VadPreRollMs
    {
        get => _settings.AudioCapture.VadPreRollMs;
        set
        {
            if (_settings.AudioCapture.VadPreRollMs != value)
            {
                _settings.AudioCapture.VadPreRollMs = value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public int VadHangoverMs
    {
        get => _settings.AudioCapture.VadHangoverMs;
        set
        {
            if (_settings.AudioCapture.VadHangoverMs != value)
            {
                _settings.AudioCapture.VadHangoverMs = value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public int AutoPauseOnSilenceSec
    {
        get => _settings.AudioCapture.AutoPauseOnSilenceSec;
        set
        {
            if (_settings.AudioCapture.AutoPauseOnSilenceSec != value)
            {
                _settings.AudioCapture.AutoPauseOnSilenceSec = value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    // ───── 自動保存 ─────

    private void ScheduleAutoSave()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
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

// 値型 DTO は record + primary constructor で表現 (Equals/GetHashCode/ToString 自動生成)。
// 文字列比較のみで使われており参照同値性は要求されないため record 化で挙動同値。
public sealed record OutputLanguageOption(string Code, string DisplayName);
public sealed record ColorOption(string Name, string Value);
public sealed record SettingsSavedEventArgs(AppSettings Settings);
