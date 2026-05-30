using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>
/// オーバーレイウィンドウのViewModel
/// </summary>
public partial class OverlayViewModel : ObservableObject, IDisposable
{
    private const int CleanupIntervalMs = 500;

    private OverlaySettings _settings;
    private readonly DispatcherTimer _cleanupTimer;
    private readonly System.Threading.Lock _subtitlesLock = new();
    private bool _isDisposed;
    private readonly IDisposable? _settingsChangeSubscription;

    [ObservableProperty]
    public partial ObservableCollection<SubtitleDisplayItem> Subtitles { get; set; } = new();

    // /rere 第2R #B2-010-CONT (v1.0.29 候補): 既定値を AppSettings.cs:70 / SettingsViewModel.cs SanitizeSettings 矯正先と統一。
    // 旧 "Yu Gothic UI" は OS フォールバック想定の歴史残骸、 現行アプリ既定は同梱 IBM Plex Sans JP に統一。
    // ⚠️ 型は string ではなく Avalonia.Media.FontFamily にする (フォント反映バグ修正): string で公開して
    //    OverlayWindow.axaml の TextBlock.FontFamily (FontFamily 型) にバインドすると、 compiled binding 経路で
    //    string→FontFamily の暗黙変換が効かず avares URI が無視され、 全フォントが既定 (App.axaml の Yu Gothic UI)
    //    にフォールバックして「どれを選んでも見た目が変わらない」状態になっていた。 型を揃えれば変換不要で確実に効く。
    [ObservableProperty]
    public partial FontFamily FontFamily { get; set; } = ResolveFontFamily("IBM Plex Sans JP");

    [ObservableProperty]
    public partial double FontSize { get; set; } = 24;

    // OverlaySettings.FontWeight ("Normal"/"Bold") を Avalonia.Media.FontWeight enum に変換して保持。
    // Bold だと暗背景でもクッキリ見えるが、 細フォントの繊細さが消える。 ユーザー選択肢。
    [ObservableProperty]
    public partial FontWeight FontWeight { get; set; } = Avalonia.Media.FontWeight.Normal;

    [ObservableProperty]
    public partial IBrush BackgroundBrush { get; set; } = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));

    // 背景色の輝度から自動派生する枠色（明るい背景には暗枠、暗い背景には明枠）。
    [ObservableProperty]
    public partial IBrush BorderBrush { get; set; } = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255));

    // 位置編集モードのサンプル字幕で使う確定字幕色のプレビュー Brush。
    [ObservableProperty]
    public partial IBrush FinalTextBrushPreview { get; set; } = Brushes.White;

    [ObservableProperty]
    public partial double BottomMarginPercent { get; set; } = 10;

    // ───── 字幕位置 微調整オフセット (px) + 編集モード (v1.0.41) ─────
    // 下部中央 (BottomMarginPercent 基準) からの px オフセット。 編集モードでドラッグして決める。
    [ObservableProperty]
    public partial double SubtitleOffsetX { get; set; }

    [ObservableProperty]
    public partial double SubtitleOffsetY { get; set; }

    // 位置編集モード中か。 true のとき OverlayWindow はクリック透過を解除し、 サンプル字幕をドラッグ可能にする。
    // 確定 (false) でクリック透過 ON に戻り、 ドラッグ不可になる。
    [ObservableProperty]
    public partial bool IsPositionEditMode { get; set; }

    /// <summary>位置編集モードの開始/終了を OverlayWindow (code-behind) に伝えるイベント (true=編集開始)。</summary>
    public event EventHandler<bool>? PositionEditModeChanged;

    partial void OnIsPositionEditModeChanged(bool value)
        => PositionEditModeChanged?.Invoke(this, value);

    /// <summary>
    /// 編集モードでドラッグ確定した位置を保存するコールバック。 OverlayWindow が px オフセットを渡してくる。
    /// SettingsViewModel 側で settings.json に永続化するため、 外部から差し込む (DI 循環を避ける緩い結線)。
    /// </summary>
    public Action<double, double>? PersistSubtitleOffset { get; set; }

    /// <summary>OverlayWindow からドラッグ中/確定時に呼ばれ、 表示オフセットを即時更新する。</summary>
    public void UpdateSubtitleOffset(double offsetX, double offsetY, bool persist)
    {
        SubtitleOffsetX = offsetX;
        SubtitleOffsetY = offsetY;
        if (persist) PersistSubtitleOffset?.Invoke(offsetX, offsetY);
    }

    // キャンセル時に戻す「編集開始時点の保存済みオフセット」。
    private double _editStartOffsetX;
    private double _editStartOffsetY;

    /// <summary>位置編集モードを開始する (SettingsViewModel の「位置を調整」ボタンから呼ばれる)。</summary>
    public void BeginPositionEdit()
    {
        _editStartOffsetX = _settings.SubtitleOffsetX;
        _editStartOffsetY = _settings.SubtitleOffsetY;
        SubtitleOffsetX = _editStartOffsetX;
        SubtitleOffsetY = _editStartOffsetY;
        IsPositionEditMode = true;
    }

    /// <summary>現在のドラッグ位置を保存して編集モードを終了する (オーバーレイの「確定」ボタン)。</summary>
    [RelayCommand]
    private void ConfirmPosition()
    {
        PersistSubtitleOffset?.Invoke(SubtitleOffsetX, SubtitleOffsetY);
        IsPositionEditMode = false;
    }

    /// <summary>
    /// 字幕位置をデフォルト (下部中央、 オフセット 0,0) に戻す。 編集モードは継続。
    /// Codex 指摘 [3329103854]: ここでは settings に保存しない (ライブ変更のみ)。
    /// 「カスタム位置で編集開始 → リセット → キャンセル」のとき、 即保存すると settings に 0,0 が
    /// 書かれてしまい、 キャンセルしたのに次回起動で位置が失われる。 保存は ConfirmPosition (確定) に一本化し、
    /// キャンセル時は CancelPosition が編集開始時点の値へ戻す (settings は無変更のまま)。
    /// </summary>
    [RelayCommand]
    private void ResetPosition()
    {
        SubtitleOffsetX = 0;
        SubtitleOffsetY = 0;
    }

    /// <summary>ドラッグを破棄して編集開始時点の位置に戻し、 編集モードを終了する (オーバーレイの「キャンセル」)。</summary>
    [RelayCommand]
    private void CancelPosition()
    {
        SubtitleOffsetX = _editStartOffsetX;
        SubtitleOffsetY = _editStartOffsetY;
        IsPositionEditMode = false;
    }

    /// <summary>
    /// /opop N1-003: コンストラクタ引数を required (non-nullable) に変更 (v1.0.33)。
    /// 旧実装は `optionsMonitor = null` フォールバックで OverlaySettings 空インスタンスを作る経路があり、
    /// 「テスト用に new OverlayViewModel() できる」ためのワークアラウンドだった。 実際の呼び出し元は DI のみ
    /// (App.axaml.cs で IOptionsMonitor 必ず注入) で、 テストも new していない (Grep 確認済) ため required 化で安全。
    /// </summary>
    public OverlayViewModel(IOptionsMonitor<AppSettings> optionsMonitor)
    {
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        _settings = optionsMonitor.CurrentValue.Overlay;
        _settingsChangeSubscription = optionsMonitor.OnChange(newSettings =>
        {
            // 設定保存のたびに reloadOnChange ファイル監視で発火する診断ログ。 位置ドラッグ等で頻発するため Debug に降格。
            LoggerService.LogDebug("Settings updated detected in OverlayViewModel.");
            _settings = newSettings.Overlay;
            ReloadSettings();
        });
        FontFamily = ResolveFontFamily(_settings.FontFamily);
        FontSize = _settings.FontSize;
        FontWeight = ResolveFontWeight(_settings.FontWeight);
        BackgroundBrush = ParseBrush(_settings.BackgroundColor);
        BorderBrush = DeriveBorderBrush(_settings.BackgroundColor);
        BottomMarginPercent = _settings.BottomMarginPercent;
        SubtitleOffsetX = _settings.SubtitleOffsetX;
        SubtitleOffsetY = _settings.SubtitleOffsetY;
        FinalTextBrushPreview = ParseBrush(_settings.FinalTextColor);
        _cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CleanupIntervalMs) };
        _cleanupTimer.Tick += CleanupOldSubtitles;
        _cleanupTimer.Start();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _settingsChangeSubscription?.Dispose();
        _cleanupTimer.Stop();
        _cleanupTimer.Tick -= CleanupOldSubtitles;
        _isDisposed = true;
    }

    public void AddOrUpdateSubtitle(SubtitleItem item)
    {
        Dispatcher.UIThread.Post(() =>
        {
            lock (_subtitlesLock)
            {
                // /opop P2-MEM-E (v1.0.33): partial 字幕の連続更新は **常に末尾要素** が同 SegmentId に当たる
                // (新 SegmentId 発行ごとに Add → MaxLines 超過で先頭側 RemoveAt 設計のため、 更新対象は必ず末尾)。
                // 旧 LINQ FirstOrDefault は per-call closure alloc + enumerator alloc を発生させていた
                // (50Hz partial = 60 min で ~9 MB Gen0)。 末尾 indexer 直接参照で alloc ゼロ化。
                var existing = Subtitles.Count > 0 && Subtitles[^1].SegmentId == item.SegmentId
                    ? Subtitles[^1] : null;
                if (existing != null)
                {
                    existing.Update(item, _settings);
                }
                else
                {
                    var displayItem = new SubtitleDisplayItem(item, _settings);
                    Subtitles.Add(displayItem);
                    while (Subtitles.Count > _settings.MaxLines)
                    {
                        Subtitles.RemoveAt(0);
                    }
                }
            }
        });
    }

    private void CleanupOldSubtitles(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            lock (_subtitlesLock)
            {
                var now = DateTime.Now;
                for (var i = Subtitles.Count - 1; i >= 0; i--)
                {
                    if (Subtitles[i].ShouldRemove(now))
                        Subtitles.RemoveAt(i);
                }
            }
        });
    }

    public void ClearSubtitles()
    {
        Dispatcher.UIThread.Post(() =>
        {
            lock (_subtitlesLock)
            {
                Subtitles.Clear();
            }
        });
    }

    public void ReloadSettings()
    {
        Dispatcher.UIThread.Post(() =>
        {
            FontFamily = ResolveFontFamily(_settings.FontFamily);
            FontSize = _settings.FontSize;
            FontWeight = ResolveFontWeight(_settings.FontWeight);
            BackgroundBrush = ParseBrush(_settings.BackgroundColor);
            BorderBrush = DeriveBorderBrush(_settings.BackgroundColor);
            FinalTextBrushPreview = ParseBrush(_settings.FinalTextColor);
            BottomMarginPercent = _settings.BottomMarginPercent;
            // 編集モード中はドラッグ中の値を settings 由来の値で上書きしない (確定前のブレ防止)。
            if (!IsPositionEditMode)
            {
                SubtitleOffsetX = _settings.SubtitleOffsetX;
                SubtitleOffsetY = _settings.SubtitleOffsetY;
            }
        });
    }

    // settings.json には "Normal" / "Bold" の文字列で保存し、 ここで Avalonia.Media.FontWeight enum に変換する。
    // 不明値 (旧設定の null / 想定外文字列) は Normal にフォールバック。
    private static FontWeight ResolveFontWeight(string weightName)
    {
        if (string.IsNullOrWhiteSpace(weightName)) return Avalonia.Media.FontWeight.Normal;
        return Enum.TryParse<FontWeight>(weightName, ignoreCase: true, out var parsed)
            ? parsed
            : Avalonia.Media.FontWeight.Normal;
    }

    // 同梱フォントを avares:// で参照する際のフォルダ URI。 個別 .ttf ファイルではなく Assets/Fonts
    // フォルダ全体を指す形式 (フォルダ#Family) にすることで、 同 family の Regular/Bold 両 .ttf が読み込まれ、
    // FontWeight=Bold 選択時に擬似ボールドではなく専用 Bold 字形が使われる。 (可変フォント Noto は単一
    // ファイルだが weight 軸を内包するため同様に Bold が出る。)
    private const string EmbeddedFontFolderUri = "avares://RealTimeTranslator.UI/Assets/Fonts";

    // settings.json には "M PLUS Rounded 1c" のような「表示名」を保存し、 ここで「実際のフォント内部 family 名」
    // (ttf の name table。 fonttools で実測) に変換する。 ⚠️ 表示名 ≠ 内部名 のフォントがあるため両者を分離する:
    //   - "M PLUS Rounded 1c" の内部 family 名は "Rounded Mplus 1c" (語順/大小が表示名と異なる)
    //   - その他 4 種は表示名 = 内部名 で一致
    // 内部名がズレていると Avalonia が avares 解決に失敗して既定フォントにフォールバックする (= フォント反映バグの一因)。
    // システムフォント (Yu Gothic UI 等) は辞書に無いので名前のまま渡してプラットフォームに解決させる。
    public static readonly IReadOnlyDictionary<string, string> EmbeddedFontMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["IBM Plex Sans JP"]  = "IBM Plex Sans JP",
            ["Noto Sans JP"]      = "Noto Sans JP",
            ["LINE Seed JP"]      = "LINE Seed JP",
            ["Zen Maru Gothic"]   = "Zen Maru Gothic",
            ["M PLUS Rounded 1c"] = "Rounded Mplus 1c",
        };

    private static FontFamily ResolveFontFamily(string familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            familyName = "IBM Plex Sans JP"; // /rere 第2R #B2-010-CONT: AppSettings.cs:70 default と統一
        // 同梱フォントは avares フォルダ URI + 実内部名で解決、 システムフォントは名前のまま OS に委ねる。
        return EmbeddedFontMap.TryGetValue(familyName, out var realName)
            ? new FontFamily($"{EmbeddedFontFolderUri}#{realName}")
            : new FontFamily(familyName);
    }

    private static IBrush ParseBrush(string colorString)
    {
        return BrushHelper.ParseBrush(colorString, Colors.Black);
    }

    // 背景色の RGB 輝度 (BT.601 近似) から枠色を派生する。
    // 明るい背景 (輝度 > 0.5) には暗い枠、暗い背景には明るい枠を当てて視認性を保つ。
    private static IBrush DeriveBorderBrush(string backgroundColorString)
    {
        var parsed = BrushHelper.ParseBrush(backgroundColorString, Colors.Black);
        if (parsed is not SolidColorBrush solid)
            return new SolidColorBrush(Color.FromArgb(128, 255, 255, 255));

        var c = solid.Color;
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance > 0.5
            ? new SolidColorBrush(Color.FromArgb(160, 32, 32, 32))
            : new SolidColorBrush(Color.FromArgb(128, 255, 255, 255));
    }
}

internal static class BrushHelper
{
    public static IBrush ParseBrush(string colorString, Color fallbackColor)
    {
        if (string.IsNullOrWhiteSpace(colorString))
        {
            LoggerService.LogWarning($"Color string is null or empty, using fallback: {fallbackColor}");
            return new SolidColorBrush(fallbackColor);
        }
        try
        {
            var color = Color.Parse(colorString);
            return new SolidColorBrush(color);
        }
        catch (FormatException ex)
        {
            LoggerService.LogWarning($"Invalid color format '{colorString}': {ex.Message}. Using fallback: {fallbackColor}");
            return new SolidColorBrush(fallbackColor);
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Error parsing color '{colorString}': {ex.Message}. Using fallback: {fallbackColor}");
            return new SolidColorBrush(fallbackColor);
        }
    }
}

public partial class SubtitleDisplayItem : ObservableObject
{
    public string SegmentId { get; }

    [ObservableProperty]
    public partial string DisplayText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IBrush TextBrush { get; set; } = Brushes.White;

    [ObservableProperty]
    public partial double Opacity { get; set; } = 1.0;

    private DateTime _displayEndTime;
    private readonly double _fadeOutDuration;

    // rere C2-006 対応: partial/final 用の Brush を 2 個だけキャッシュして使い回す。
    // 旧実装は Update のたびに `new SolidColorBrush(...)` を行い、 partial 字幕 30ms 周期 = 33Hz で
    // SolidColorBrush 生成、 さらに毎回 Color.Parse(colorString) も走って Gen0 圧をかけていた。
    // SubtitleDisplayItem 寿命中 (= 1 字幕の表示時間) は色設定が変わらない前提で、 コンストラクタで一度だけ確保する。
    private readonly IBrush _partialBrush;
    private readonly IBrush _finalBrush;

    public SubtitleDisplayItem(SubtitleItem item, OverlaySettings settings)
    {
        SegmentId = item.SegmentId;
        _fadeOutDuration = settings.FadeOutDuration;
        _partialBrush = BrushHelper.ParseBrush(settings.PartialTextColor, Colors.White);
        _finalBrush = BrushHelper.ParseBrush(settings.FinalTextColor, Colors.White);
        Update(item, settings);
    }

    public void Update(SubtitleItem item, OverlaySettings settings)
    {
        DisplayText = item.DisplayText;
        // キャッシュ済み brush を切替えるだけ。 ref が同じなら setter ガード (CommunityToolkit の
        // EqualityComparer<T>.Default 比較) で OnPropertyChanged も発火しない → UI 再描画も skip。
        var newBrush = item.IsFinal ? _finalBrush : _partialBrush;
        if (!ReferenceEquals(TextBrush, newBrush))
        {
            TextBrush = newBrush;
        }
        // v1.0.27: partial / final ともに DisplayDuration 経過で消える (旧 v1.0.24 以前の挙動)。
        // v1.0.24-26 は partial 連結方式 + 最大寿命 45 秒タイマーの前提で partial を永続表示してたが、
        // v1.0.27 で server gap 対策を「無音 PCM 継続送信」に置換 → partial 連結方式を削除 →
        // partial も次の delta or 句点完結まで数秒以内に置換される前提に戻ったため、 永続表示は不要。
        _displayEndTime = DateTime.Now.AddSeconds(settings.DisplayDuration);
        Opacity = 1.0;
    }

    public bool ShouldRemove(DateTime now)
    {
        if (now < _displayEndTime)
            return false;
        var fadeProgress = (now - _displayEndTime).TotalSeconds / _fadeOutDuration;
        if (fadeProgress < 1.0)
        {
            Opacity = 1.0 - fadeProgress;
            return false;
        }
        return true;
    }
}
