using System;
using System.Collections.ObjectModel;
using System.Globalization;
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
    // CodeRabbit 指摘 [3329106464] 対応: debounce 中 (= 未書き込みの変更が保留中) かを示すフラグ。
    // アプリ終了時に FlushPendingSaveAsync で「保留中なら即保存」して、 終了直前のリサイズ等の取りこぼしを防ぐ。
    private volatile bool _autoSavePending;

    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    public SettingsViewModel(IOptionsMonitor<AppSettings> options, ISettingsService settingsService, OverlayViewModel overlayViewModel)
    {
        _settings = options.CurrentValue;
        _settingsService = settingsService;
        _overlayViewModel = overlayViewModel;
        // 字幕位置の編集確定/リセット時に OverlayViewModel から呼ばれて settings.json へ永続化する
        // (DI 循環を避けるため OverlayViewModel にコールバックを差し込む緩い結線)。
        _overlayViewModel.PersistSubtitleOffset = (x, y) =>
        {
            _settings.Overlay.SubtitleOffsetX = x;
            _settings.Overlay.SubtitleOffsetY = y;
            ScheduleAutoSave();
        };
        // オーバーレイのツールバーで編集を確定/キャンセルしたとき、 設定タブの編集ボタン活性も追従させる。
        _overlayViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.IsPositionEditMode))
                OnPropertyChanged(nameof(IsSubtitlePositionEditMode));
        };
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
        FontSizes = new ReadOnlyCollection<double>(new[] { 12d, 14d, 16d, 18d, 20d, 24d, 28d, 32d, 36d, 40d, 48d, 56d, 64d, 72d, 80d, 96d });
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
        // 背景色は「色 (RGB)」と「透明度 (Alpha)」を分離してドロップダウン化する。
        // 旧 BackgroundColorOptions (#AARRGGBB 12 色) は廃止、 ユーザーが色と濃さを独立に選べる UI に統一。
        BackgroundColorBaseOptions = new ReadOnlyCollection<ColorOption>(new ColorOption[]
        {
            new("黒",     "#000000"),
            new("白",     "#FFFFFF"),
            new("灰",     "#404040"),
            new("濃紺",   "#0F1A4C"),
            new("濃緑",   "#0F4C1F"),
            new("濃赤",   "#4C0F1F"),
            new("茶",     "#3A2218"),
            new("濃紫",   "#2A0F4C")
        });
        OpacityOptions = new ReadOnlyCollection<OpacityOption>(new OpacityOption[]
        {
            new(0,   "透明 (0%)"),
            new(25,  "薄い (25%)"),
            new(50,  "標準 (50%)"),
            new(75,  "濃い (75%)"),
            new(100, "ベタ (100%)")
        });
        FontWeightOptions = new ReadOnlyCollection<FontWeightOption>(new FontWeightOption[]
        {
            new("Normal", "標準"),
            new("Bold",   "太字")
        });
        VadPresetOptions = new ReadOnlyCollection<VadPresetOption>(new VadPresetOption[]
        {
            // v1.0.30 で全プリセット threshold を -0.2 シフト (0.5→0.3 基調) → BGM/SE 継続送信で
            // OpenAI server VAD が発話境界を引けず「字幕が句点なしで繋がる」回帰が発生。
            // v1.0.31 で全プリセット threshold を +0.1 シフト (回帰緩和) + MaxPartialChars 80→50 で
            // D-7 fallback も早めに発火するよう調整。 遠距離小音量の声拾いは入力プリプロセス DSP に委ねる方針。
            // preroll/hangover は Balanced を 1000/400 に変更 (PreRoll は頭の取りこぼしを更に抑える方向へ厚く、
            // Hangover は末尾の無音送信を削って token 節約する方向へ薄く) し、 全プリセットを同じ向きに
            // PreRoll +200ms / Hangover -200ms 一括シフトして相対関係 (頭尻尾重視 > ふつう > 節約) を維持。

            // Balanced (デフォルト): threshold=0.3 で短い発話も広めに拾う。
            // preroll=1000 / hangover=400 で発話冒頭の子音の取りこぼしを強めに防ぎつつ、 末尾の無音送信は短めに。
            new("Balanced",          "ふつう (推奨)",                    0.3f, 1000, 400),
            // PrioritizeEdges: threshold=0.2 で更に小さい発話 (「はい」「うん」等) も拾い、
            // preroll=1200 / hangover=600 で文の頭・尻尾の音素切れを更に強く抑える。 通話・会議・コマンド発話向け。
            // BGM 混入で課金増のリスクあり (ゆろさんの体感確認後に調整可)。
            new("PrioritizeEdges",   "頭と尻尾を取りこぼさない",         0.2f, 1200, 600),
            // AggressiveSavings: threshold=0.4 で誤検出 (BGM のドラム/拍手等) を抑え、
            // preroll=700 / hangover=150 で送信秒数を最小化。 長時間視聴で課金を切り詰めたい時向け。
            new("AggressiveSavings", "ガッツリ節約",                      0.4f, 700, 150),
            // Custom は下の詳細スライダーで個別調整するモード。 setter で 3 値は上書きしないため、
            // ここの 3 値は実際には参照されない (rere B2-007 対応: 死コード相当だったので 0/0/0 に)。
            new("Custom",            "カスタム (詳細設定)",               0f,   0,   0)
        });
        // 翻訳ログの保持期間 ComboBox (Windows ごみ箱風: 無制限がデフォルト)。
        // 0 = 無制限、 削除しない (ユーザーが手動で消すまで残す)。
        TranslationLogRetentionOptions = new ReadOnlyCollection<TranslationLogRetentionOption>(new TranslationLogRetentionOption[]
        {
            new(0,   "無制限 (手動削除のみ、 推奨)"),
            new(7,   "7 日"),
            new(30,  "30 日"),
            new(90,  "90 日"),
            new(180, "180 日"),
            new(365, "1 年")
        });
        AutoPauseOptions = new ReadOnlyCollection<AutoPauseOption>(new AutoPauseOption[]
        {
            new(0,    "無効 (停止しない)"),
            new(30,   "30 秒"),
            new(60,   "1 分"),
            new(180,  "3 分"),
            new(300,  "5 分"),
            new(600,  "10 分"),
            new(1800, "30 分")
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
    /// SanitizeSettings で「ユーザーが視認できる UI 要素」が黙って矯正された場合、 ここに通知文を蓄積する。
    /// MainViewModel がコンストラクタで読んで起動直後にバナー表示する (rere F-007 対応)。
    /// 対象: 背景色 (一覧外 → 黒に矯正) — 旧バージョンでマゼンタ等を設定していたユーザーが
    /// 「いつのまにか黒に戻った」と感じる UX バグを防ぐ。
    /// Font / Size / MaxLines / DisplayDuration 等の矯正は表示影響が軽微なのでバナー対象外。
    /// </summary>
    public IReadOnlyList<string> SanitizeWarnings => _sanitizeWarnings;
    private readonly List<string> _sanitizeWarnings = new();

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
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: FontSize={_settings.Overlay.FontSize} が一覧外 → 32 に矯正");
            _settings.Overlay.FontSize = 32d;
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

        // 背景色のマイグレーション + 矯正:
        //  1. BackgroundColorBase が空 (旧 settings.json) なら、 旧 BackgroundColor (#AARRGGBB) から
        //     RGB と Alpha を逆算して BackgroundColorBase / BackgroundOpacityPercent を埋める。
        //  2. その後、 一覧外の値なら標準 (黒 50%) に矯正。
        if (string.IsNullOrWhiteSpace(_settings.Overlay.BackgroundColorBase))
        {
            (string baseRgb, int opacityPct) = SplitArgbToRgbAndOpacity(_settings.Overlay.BackgroundColor);
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: 旧 BackgroundColor='{_settings.Overlay.BackgroundColor}' を BackgroundColorBase='{baseRgb}' / BackgroundOpacityPercent={opacityPct} に分解");
            _settings.Overlay.BackgroundColorBase = baseRgb;
            _settings.Overlay.BackgroundOpacityPercent = opacityPct;
            changed = true;
        }

        if (!BackgroundColorBaseOptions.Any(o => string.Equals(o.Value, _settings.Overlay.BackgroundColorBase, StringComparison.OrdinalIgnoreCase)))
        {
            string oldBase = _settings.Overlay.BackgroundColorBase;
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: BackgroundColorBase='{oldBase}' が一覧外 → 先頭の '{BackgroundColorBaseOptions[0].Name}' に矯正");
            _settings.Overlay.BackgroundColorBase = BackgroundColorBaseOptions[0].Value;
            changed = true;
            // rere F-007: UI に視認できる変化なので起動直後にバナーで通知 (MainViewModel が SanitizeWarnings を読む)。
            _sanitizeWarnings.Add(
                $"互換性対応のため背景色 ({oldBase}) を '{BackgroundColorBaseOptions[0].Name}' に変更しました。 " +
                "「表示設定」タブで好みの色 / 濃さを選び直してください。");
        }

        if (!OpacityOptions.Any(o => o.Percent == _settings.Overlay.BackgroundOpacityPercent))
        {
            // 最も近い百分位に丸める (旧 alpha 0x80=50, 0xCC=80→75 に丸まる、 0x40=25, 0x00=0)。
            int snapped = OpacityOptions
                .OrderBy(o => Math.Abs(o.Percent - _settings.Overlay.BackgroundOpacityPercent))
                .First().Percent;
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: BackgroundOpacityPercent={_settings.Overlay.BackgroundOpacityPercent} が一覧外 → {snapped}% に丸め");
            _settings.Overlay.BackgroundOpacityPercent = snapped;
            changed = true;
        }

        // 合成済み BackgroundColor (#AARRGGBB) を再生成。 これが OverlayViewModel が直接 parse する値。
        string composed = ComposeArgbHex(_settings.Overlay.BackgroundColorBase, _settings.Overlay.BackgroundOpacityPercent);
        if (!string.Equals(composed, _settings.Overlay.BackgroundColor, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Overlay.BackgroundColor = composed;
            changed = true;
        }

        if (!FontWeightOptions.Any(o => string.Equals(o.Key, _settings.Overlay.FontWeight, StringComparison.OrdinalIgnoreCase)))
        {
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: FontWeight='{_settings.Overlay.FontWeight}' が一覧外 → 'Normal' に矯正");
            _settings.Overlay.FontWeight = "Normal";
            changed = true;
        }

        if (!VadPresetOptions.Any(o => string.Equals(o.Key, _settings.AudioCapture.VadPreset, StringComparison.OrdinalIgnoreCase)))
        {
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: VadPreset='{_settings.AudioCapture.VadPreset}' が一覧外 → 'Balanced' に矯正");
            _settings.AudioCapture.VadPreset = "Balanced";
            changed = true;
        }
        // プリセット選択時 (Custom 以外) は 3 値をプリセット値に強制同期させる
        // (settings.json を手で書いて threshold だけ別値にしたケースの整合性確保)。
        var preset = VadPresetOptions.First(o => string.Equals(o.Key, _settings.AudioCapture.VadPreset, StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(preset.Key, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            if (Math.Abs(_settings.AudioCapture.VadThreshold - preset.Threshold) > 0.005f ||
                _settings.AudioCapture.VadPreRollMs != preset.PreRollMs ||
                _settings.AudioCapture.VadHangoverMs != preset.HangoverMs)
            {
                LoggerService.LogInfo($"SettingsViewModel.Sanitize: VadPreset='{preset.Key}' のため Threshold/PreRoll/Hangover を ({preset.Threshold}/{preset.PreRollMs}/{preset.HangoverMs}) に強制同期");
                _settings.AudioCapture.VadThreshold = preset.Threshold;
                _settings.AudioCapture.VadPreRollMs = preset.PreRollMs;
                _settings.AudioCapture.VadHangoverMs = preset.HangoverMs;
                changed = true;
            }
        }

        if (!AutoPauseOptions.Any(o => o.Seconds == _settings.AudioCapture.AutoPauseOnSilenceSec))
        {
            // 一覧にない秒数 (旧 NumericUpDown で 45 秒等を入れていた) は最も近い既定値に丸める。
            int snappedSec = AutoPauseOptions
                .OrderBy(o => Math.Abs(o.Seconds - _settings.AudioCapture.AutoPauseOnSilenceSec))
                .First().Seconds;
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: AutoPauseOnSilenceSec={_settings.AudioCapture.AutoPauseOnSilenceSec} が一覧外 → {snappedSec} 秒に丸め");
            _settings.AudioCapture.AutoPauseOnSilenceSec = snappedSec;
            changed = true;
        }

        // 翻訳ログ保持期間: 一覧外の値は最も近い既定値に丸める (負数は 0 = 無制限へ)。
        if (_settings.TranslationLog.RetentionDays < 0 ||
            !TranslationLogRetentionOptions.Any(o => o.Days == _settings.TranslationLog.RetentionDays))
        {
            int snappedDays = _settings.TranslationLog.RetentionDays < 0
                ? 0
                : TranslationLogRetentionOptions
                    .OrderBy(o => Math.Abs(o.Days - _settings.TranslationLog.RetentionDays))
                    .First().Days;
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: TranslationLog.RetentionDays={_settings.TranslationLog.RetentionDays} が一覧外 → {snappedDays} 日に丸め");
            _settings.TranslationLog.RetentionDays = snappedDays;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.OpenAIRealtime.OutputLanguage) ||
            !OutputLanguageOptions.Any(o => o.Code == _settings.OpenAIRealtime.OutputLanguage))
        {
            LoggerService.LogInfo($"SettingsViewModel.Sanitize: OutputLanguage='{_settings.OpenAIRealtime.OutputLanguage}' が一覧外 → 'ja' に矯正");
            _settings.OpenAIRealtime.OutputLanguage = "ja";
            changed = true;
        }

        // /rere F-003 対応: SilencePaddingMs の旧 default (v1.0.33-35: 8000ms) を新 default (v1.0.36: 5000ms) に
        // 1 度限り migration する。 8000ms ぴったりは旧 default の名残と判定し、 他の値 (7000 / 10000 等) は
        // ユーザーが明示的に設定した可能性があるためそのまま維持する。
        if (_settings.OpenAIRealtime.SilencePaddingMs == 8000)
        {
            LoggerService.LogInfo("SettingsViewModel.Sanitize: SilencePaddingMs=8000 (v1.0.33-35 旧 default) を 5000 (v1.0.36 新 default) に migration");
            _settings.OpenAIRealtime.SilencePaddingMs = 5000;
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
    public ReadOnlyCollection<FontWeightOption> FontWeightOptions { get; }
    public ReadOnlyCollection<ColorOption> TextColorOptions { get; }
    public ReadOnlyCollection<ColorOption> BackgroundColorBaseOptions { get; }
    public ReadOnlyCollection<OpacityOption> OpacityOptions { get; }
    public ReadOnlyCollection<int> MaxLinesList { get; }
    public ReadOnlyCollection<double> DisplayDurations { get; }
    public ReadOnlyCollection<OutputLanguageOption> OutputLanguageOptions { get; }
    public ReadOnlyCollection<VadPresetOption> VadPresetOptions { get; }
    public ReadOnlyCollection<AutoPauseOption> AutoPauseOptions { get; }
    public ReadOnlyCollection<TranslationLogRetentionOption> TranslationLogRetentionOptions { get; }

    [ObservableProperty]
    public partial bool IsTestingApi { get; set; }

    [ObservableProperty]
    public partial string ApiTestResult { get; set; } = string.Empty;

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

    /// <summary>
    /// 字幕オーバーレイ窓を表示するか (v1.0.41)。 OFF にすると画面に重ねる字幕は消えるが、 翻訳処理・
    /// 翻訳ログ記録は継続する (「翻訳ログ」タブで履歴を読む運用向け)。 トグル即時に OverlayViewModel へ
    /// 反映して窓を出す/消し (500ms debounce を待たない)、 永続化は ScheduleAutoSave に委ねる。
    /// </summary>
    public bool ShowSubtitleOverlay
    {
        get => _settings.Overlay.ShowSubtitleOverlay;
        set
        {
            if (_settings.Overlay.ShowSubtitleOverlay != value)
            {
                _settings.Overlay.ShowSubtitleOverlay = value;
                _overlayViewModel.IsOverlayVisible = value; // 即時反映 (debounce を待たずに窓を切替える)
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

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

    // 背景色は「色 (RGB)」と「透明度 (Alpha)」を独立に選び、 setter で合成して BackgroundColor に書き戻す。
    // OverlayViewModel は従来通り BackgroundColor (#AARRGGBB) を parse するだけで済む (互換維持)。
    public ColorOption? SelectedBackgroundColorBaseOption
    {
        get => BackgroundColorBaseOptions.FirstOrDefault(o => string.Equals(o.Value, _settings.Overlay.BackgroundColorBase, StringComparison.OrdinalIgnoreCase))
               ?? BackgroundColorBaseOptions[0];
        set
        {
            if (value != null && !string.Equals(_settings.Overlay.BackgroundColorBase, value.Value, StringComparison.OrdinalIgnoreCase))
            {
                _settings.Overlay.BackgroundColorBase = value.Value;
                _settings.Overlay.BackgroundColor = ComposeArgbHex(value.Value, _settings.Overlay.BackgroundOpacityPercent);
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public OpacityOption? SelectedBackgroundOpacityOption
    {
        get => OpacityOptions.FirstOrDefault(o => o.Percent == _settings.Overlay.BackgroundOpacityPercent)
               ?? OpacityOptions[3]; // default = 濃い (75%)
        set
        {
            if (value != null && _settings.Overlay.BackgroundOpacityPercent != value.Percent)
            {
                _settings.Overlay.BackgroundOpacityPercent = value.Percent;
                _settings.Overlay.BackgroundColor = ComposeArgbHex(_settings.Overlay.BackgroundColorBase, value.Percent);
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    public FontWeightOption? SelectedFontWeightOption
    {
        get => FontWeightOptions.FirstOrDefault(o => string.Equals(o.Key, _settings.Overlay.FontWeight, StringComparison.OrdinalIgnoreCase))
               ?? FontWeightOptions[0];
        set
        {
            if (value != null && !string.Equals(_settings.Overlay.FontWeight, value.Key, StringComparison.OrdinalIgnoreCase))
            {
                _settings.Overlay.FontWeight = value.Key;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    // ───── VAD (Voice Activity Detection) 設定 ─────
    // 既存 Overlay 系プロパティと同じく `_settings.AudioCapture` を直接 get/set し、
    // 変更があれば ScheduleAutoSave で AutoSaveDelay (500ms) 後に settings.json へ atomic write。
    // (rere B2-003 対応: 旧コメント「1.5 秒後」は AutoSaveDelay 変更時に追随していなかった嘘記述だったので訂正)

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

    /// <summary>
    /// VAD 感度プリセット ComboBox。 Custom 以外を選択すると、 Threshold / PreRoll / Hangover を
    /// プリセット値に一括上書きする (旧 UI の 3 つのスライダー/NumericUpDown を 1 つの選択肢に集約)。
    /// </summary>
    public VadPresetOption? SelectedVadPreset
    {
        get => VadPresetOptions.FirstOrDefault(o => string.Equals(o.Key, _settings.AudioCapture.VadPreset, StringComparison.OrdinalIgnoreCase))
               ?? VadPresetOptions[0];
        set
        {
            if (value == null || string.Equals(_settings.AudioCapture.VadPreset, value.Key, StringComparison.OrdinalIgnoreCase))
                return;
            _settings.AudioCapture.VadPreset = value.Key;
            // Custom 以外: プリセット値を強制適用 (現在のカスタム値は捨てる)。
            if (!string.Equals(value.Key, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                _settings.AudioCapture.VadThreshold = value.Threshold;
                _settings.AudioCapture.VadPreRollMs = value.PreRollMs;
                _settings.AudioCapture.VadHangoverMs = value.HangoverMs;
                // 詳細スライダーが裏で動くので追従通知する。 IsVadPresetCustom もここで切替わる。
                OnPropertyChanged(nameof(VadThreshold));
                OnPropertyChanged(nameof(VadPreRollMs));
                OnPropertyChanged(nameof(VadHangoverMs));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVadPresetCustom));
            ScheduleAutoSave();
        }
    }

    /// <summary>カスタムプリセット選択時のみ詳細スライダー UI を表示するフラグ。</summary>
    public bool IsVadPresetCustom => string.Equals(_settings.AudioCapture.VadPreset, "Custom", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// OpenAI に送信される PCM16 (24kHz / Mono) を %APPDATA%/RealTimeTranslator/debug/ に
    /// WAV 録音するデバッグ機能。 セッションごとに 1 ファイル。 サイレンス padding 含めて
    /// 「実送信と完全一致」のバイト列を記録するので、 字幕が来ないときに「何が送られているか」を
    /// 後から再生して確認できる。 token / 容量を消費するので恒常 ON は推奨しない。
    /// </summary>
    public bool DebugRecordSentAudio
    {
        get => _settings.AudioCapture.DebugRecordSentAudio;
        set
        {
            if (_settings.AudioCapture.DebugRecordSentAudio != value)
            {
                _settings.AudioCapture.DebugRecordSentAudio = value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    /// <summary>%APPDATA%/RealTimeTranslator/debug/ をエクスプローラーで開く。 録音した WAV の確認用。</summary>
    [RelayCommand]
    private void OpenDebugFolder()
    {
        try
        {
            Directory.CreateDirectory(DebugAudioRecorder.DebugDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DebugAudioRecorder.DebugDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"デバッグ録音フォルダのオープンに失敗: {ex.Message}");
        }
    }

    // AutoPause は ComboBox 化したため AutoPauseOnSilenceSec プロパティ (NumericUpDown 用) は廃止。
    // 旧 settings.json の任意秒数は SanitizeSettings で最も近い既定値に丸められる。
    public AutoPauseOption? SelectedAutoPauseOption
    {
        get => AutoPauseOptions.FirstOrDefault(o => o.Seconds == _settings.AudioCapture.AutoPauseOnSilenceSec)
               ?? AutoPauseOptions[0];
        set
        {
            if (value != null && _settings.AudioCapture.AutoPauseOnSilenceSec != value.Seconds)
            {
                _settings.AudioCapture.AutoPauseOnSilenceSec = value.Seconds;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    // ───── 入力プリプロセス DSP (v1.0.30 新規、 v1.0.32 で 4 段 → 3 段、 v1.0.36 で 3 段 → 2 段) ─────
    // WASAPI capture 直後・リサンプル前に挟む 2 段 DSP (InputGain + AntiClip) の設定。
    // 全部 false / InputGainDb=0 が default で、 完全 bypass 動作 (v1.0.29 以前と同一)。
    // 削除履歴:
    //   v1.0.32: LoudnessNormalizer 削除 (NightMode と機能重複)
    //   v1.0.36: NightModeCompressor 削除 (server VAD が句点を返さなくなる経路を誘発しやすく多層防御の相互依存が重かった)

    // Input level meter (audio tab): peak after input gain. Pushed in by MainViewModel. Norm=0..1, PeakDb numeric, Text label.
    private double _inputLevelNorm;
    public double InputLevelNorm { get => _inputLevelNorm; set => SetProperty(ref _inputLevelNorm, value); }

    private double _inputLevelPeakDb = -120d;
    public double InputLevelPeakDb { get => _inputLevelPeakDb; set => SetProperty(ref _inputLevelPeakDb, value); }

    private string _inputLevelText = "-∞ dB";
    public string InputLevelText { get => _inputLevelText; set => SetProperty(ref _inputLevelText, value); }

    // Called from MainViewModel when a post-gain peak level (dBFS) arrives from the pipeline.
    // Maps -60..0 dBFS to 0..1 for the OBS-like meter and updates the numeric label.
    public void UpdateInputLevel(double peakDb)
    {
        const double floorDb = -60.0;
        double norm = (peakDb - floorDb) / (0.0 - floorDb);
        if (norm < 0) norm = 0; else if (norm > 1) norm = 1;
        InputLevelNorm = norm;
        InputLevelPeakDb = peakDb;
        InputLevelText = peakDb <= floorDb ? "-∞ dB" : $"{peakDb:0.0} dB";
    }

    // Reset the meter to silence (called when translation stops).
    public void ResetInputLevel()
    {
        InputLevelNorm = 0d;
        InputLevelPeakDb = -120d;
        InputLevelText = "-∞ dB";
    }
    /// <summary>
    /// ユーザー手動の入力ゲイン (dB)。 範囲 -24〜+24、 default 0。
    /// 0 dB ピッタリ (差 ±0.01dB 以内) は <see cref="InputGainStage"/> が完全 bypass する。
    /// </summary>
    public float InputGainDb
    {
        get => _settings.AudioCapture.Preprocessing.InputGainDb;
        set
        {
            // 範囲制約 + 微小差は無視 (Slider 連続値での書き込み頻度抑制)
            float clamped = Math.Clamp(value, -24f, 24f);
            if (Math.Abs(_settings.AudioCapture.Preprocessing.InputGainDb - clamped) > 0.05f)
            {
                _settings.AudioCapture.Preprocessing.InputGainDb = clamped;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }
    }

    /// <summary>入力ゲインを 0 dB にリセットする (UI のゼロボタン用)。</summary>
    [RelayCommand]
    private void ResetInputGain() => InputGainDb = 0f;

    // ───── 翻訳字幕位置調整 (v1.0.41) ─────

    /// <summary>OverlayViewModel の編集モード状態 (UI バインド用。 編集ボタンの活性切替に使う)。</summary>
    public bool IsSubtitlePositionEditMode => _overlayViewModel.IsPositionEditMode;

    /// <summary>
    /// 字幕位置の編集モードを開始する。 オーバーレイにドラッグ可能なサンプル字幕が出て、
    /// マウスで自由に移動できる。 確定/キャンセルはオーバーレイ上のツールバーで行う。
    /// </summary>
    [RelayCommand]
    private void BeginSubtitlePositionEdit()
    {
        _overlayViewModel.BeginPositionEdit();
        OnPropertyChanged(nameof(IsSubtitlePositionEditMode));
    }

    /// <summary>字幕位置をデフォルト (下部中央) にリセットして保存する。 編集中でなくても使える。</summary>
    [RelayCommand]
    private void ResetSubtitlePosition()
    {
        _settings.Overlay.SubtitleOffsetX = 0;
        _settings.Overlay.SubtitleOffsetY = 0;
        _overlayViewModel.UpdateSubtitleOffset(0, 0, persist: false);
        ScheduleAutoSave();
    }

    // ───── メインウィンドウサイズの保存 / 復元 (v1.0.41、 Codex 指摘 [3329103856] で SettingsViewModel に集約) ─────
    // ウィンドウサイズ保存を MainViewModel ではなく SettingsViewModel 経由 (= autosave と同じ _settings インスタンス /
    // ScheduleAutoSave 経路) に一本化する。 MainViewModel._settings は reloadOnChange で差し替わるため、 そちらで
    // 直接 SaveAsync すると、 SettingsViewModel が抱える別インスタンスの autosave が古いサイズで上書きして
    // リサイズを巻き戻すレースがあった。 保存元を 1 つにして解消する。

    /// <summary>
    /// 保存済みのウィンドウサイズを返す (未保存/不正値なら null)。 MainWindow が起動時に復元するために呼ぶ。
    /// MinWidth/MinHeight 未満はガードして null 扱い (壊れた settings.json での極小窓化を防ぐ)。
    /// </summary>
    public (double Width, double Height)? GetSavedWindowSize()
    {
        var w = _settings.WindowWidth;
        var h = _settings.WindowHeight;
        return (w >= 650 && h >= 450) ? (w, h) : null;
    }

    /// <summary>
    /// ウィンドウサイズを記録し、 debounce (ScheduleAutoSave 500ms) で settings.json へ保存する。
    /// MainWindow のリサイズイベント (連続発火) から呼ばれる前提で、 値変化時のみ autosave を起こす。
    /// </summary>
    public void SaveWindowSize(double width, double height)
    {
        if (width < 650 || height < 450) return; // Min 未満は無視 (最小化・異常値ガード)
        if (Math.Abs(_settings.WindowWidth - width) < 0.5 && Math.Abs(_settings.WindowHeight - height) < 0.5)
            return; // 変化なし (ピクセル誤差) は無視
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        ScheduleAutoSave();
    }

    /// <summary>
    /// 翻訳ログの保持期間 ComboBox。 0 = 無制限、 7/30/90/180/365 日。
    /// 設定変更は次回起動時の <see cref="ITranslationLogger.PerformRetentionCleanupAsync"/> で反映される。
    /// </summary>
    public TranslationLogRetentionOption? SelectedTranslationLogRetention
    {
        get => TranslationLogRetentionOptions.FirstOrDefault(o => o.Days == _settings.TranslationLog.RetentionDays)
               ?? TranslationLogRetentionOptions[0];
        set
        {
            if (value != null && _settings.TranslationLog.RetentionDays != value.Days)
            {
                _settings.TranslationLog.RetentionDays = value.Days;
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
        _autoSavePending = true; // 未書き込みの変更あり (終了時 flush 対象)

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

    /// <summary>
    /// CodeRabbit 指摘 [3329106464] 対応: アプリ終了時に呼ぶ。 debounce 待ち中の保留保存があれば、
    /// 500ms 待たずに即座に flush して settings.json に書き込む。 これで「リサイズ直後に終了」しても
    /// 最後の値が永続化される。 保留が無ければ何もしない。 App.OnExit から await して呼ばれる想定。
    /// </summary>
    public async Task FlushPendingSaveAsync()
    {
        if (!_autoSavePending) return;
        _autoSaveCts?.Cancel(); // 進行中の debounce 待ちを打ち切り、 二重保存を避ける
        await SaveInternalAsync().ConfigureAwait(false);
    }

    private async Task SaveInternalAsync()
    {
        try
        {
            _autoSavePending = false; // 書き込みに入った時点で保留を解消 (flush と debounce の二重実行を吸収)
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

    // ───── 背景色 合成 / 分解ヘルパー ─────

    /// <summary>
    /// #RRGGBB と Opacity (0-100%) を #AARRGGBB に合成する。 OverlayViewModel が parse する形式に揃える。
    /// テスト容易化のため internal (InternalsVisibleTo=RealTimeTranslator.Tests、 rere B2-001 対応)。
    /// </summary>
    internal static string ComposeArgbHex(string rgbHex, int opacityPercent)
    {
        if (string.IsNullOrWhiteSpace(rgbHex)) rgbHex = "#000000";
        // # 抜き、 RRGGBB だけ取り出す。 旧データで #AARRGGBB が紛れ込んだら下位 6 桁を使う。
        string trimmed = rgbHex.TrimStart('#');
        if (trimmed.Length == 8) trimmed = trimmed[2..];
        if (trimmed.Length != 6) trimmed = "000000";
        int pct = Math.Clamp(opacityPercent, 0, 100);
        int alpha = (int)Math.Round(pct * 255.0 / 100.0);
        return $"#{alpha:X2}{trimmed.ToUpperInvariant()}";
    }

    /// <summary>
    /// 旧形式 #AARRGGBB から RGB 部分 (#RRGGBB) と Opacity (%) を逆算する。
    /// マイグレーション用。 不正値は (#000000, 50%) を返す。
    /// テスト容易化のため internal (InternalsVisibleTo=RealTimeTranslator.Tests、 rere B2-001 対応)。
    /// </summary>
    internal static (string Rgb, int OpacityPercent) SplitArgbToRgbAndOpacity(string argbHex)
    {
        if (string.IsNullOrWhiteSpace(argbHex)) return ("#000000", 50);
        string trimmed = argbHex.TrimStart('#');
        if (trimmed.Length == 6) return ("#" + trimmed.ToUpperInvariant(), 100); // alpha 省略 = ベタ
        if (trimmed.Length != 8) return ("#000000", 50);
        if (!byte.TryParse(trimmed.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte alpha))
            return ("#000000", 50);
        int pct = (int)Math.Round(alpha * 100.0 / 255.0);
        return ("#" + trimmed[2..].ToUpperInvariant(), pct);
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

/// <summary>VAD 感度プリセット。 SelectedVadPreset の setter で 3 値を一括上書きする。</summary>
public sealed record VadPresetOption(string Key, string Name, float Threshold, int PreRollMs, int HangoverMs);

/// <summary>自動 Pause の選択肢。 Seconds=0 が無効。</summary>
public sealed record AutoPauseOption(int Seconds, string Name);

/// <summary>フォントの太さ。 Key は OverlaySettings.FontWeight に保存される。</summary>
public sealed record FontWeightOption(string Key, string Name);

/// <summary>背景の不透明度。 Percent は OverlaySettings.BackgroundOpacityPercent に保存される。</summary>
public sealed record OpacityOption(int Percent, string Name);

/// <summary>翻訳ログの保持期間 (日)。 Days=0 は「無制限」を意味する。</summary>
public sealed record TranslationLogRetentionOption(int Days, string Name);
