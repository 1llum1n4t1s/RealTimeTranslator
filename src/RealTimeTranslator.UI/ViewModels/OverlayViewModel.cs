using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private readonly object _subtitlesLock = new();
    private bool _isDisposed;
    private readonly IDisposable? _settingsChangeSubscription;

    [ObservableProperty]
    public partial ObservableCollection<SubtitleDisplayItem> Subtitles { get; set; } = new();

    [ObservableProperty]
    public partial string FontFamily { get; set; } = "Yu Gothic UI";

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

    [ObservableProperty]
    public partial double BottomMarginPercent { get; set; } = 10;

    public OverlayViewModel(IOptionsMonitor<AppSettings>? optionsMonitor = null)
    {
        if (optionsMonitor != null)
        {
            _settings = optionsMonitor.CurrentValue.Overlay;
            _settingsChangeSubscription = optionsMonitor.OnChange(newSettings =>
            {
                LoggerService.LogInfo("Settings updated detected in OverlayViewModel.");
                _settings = newSettings.Overlay;
                ReloadSettings();
            });
        }
        else
        {
            _settings = new OverlaySettings();
        }
        FontFamily = ResolveFontFamily(_settings.FontFamily);
        FontSize = _settings.FontSize;
        FontWeight = ResolveFontWeight(_settings.FontWeight);
        BackgroundBrush = ParseBrush(_settings.BackgroundColor);
        BorderBrush = DeriveBorderBrush(_settings.BackgroundColor);
        BottomMarginPercent = _settings.BottomMarginPercent;
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
                var existing = Subtitles.FirstOrDefault(s => s.SegmentId == item.SegmentId);
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
            BottomMarginPercent = _settings.BottomMarginPercent;
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

    // settings.json には "IBM Plex Sans JP" のような表示名 (= フォント本体の family name) を保存し、
    // ここで avares:// URI に変換してレンダラに渡す。 これで「ユーザーが目にする選択肢名」と
    // 「Avalonia の FontFamily 文字列」を切り離せる。 システムフォント (Yu Gothic UI 等) は
    // 辞書ヒットせずそのまま渡してプラットフォームに解決させる。
    public static readonly IReadOnlyDictionary<string, string> EmbeddedFontMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["IBM Plex Sans JP"]  = "avares://RealTimeTranslator.UI/Assets/Fonts/IBMPlexSansJP-Regular.ttf#IBM Plex Sans JP",
            ["Noto Sans JP"]      = "avares://RealTimeTranslator.UI/Assets/Fonts/NotoSansJP-Variable.ttf#Noto Sans JP",
            ["LINE Seed JP"]      = "avares://RealTimeTranslator.UI/Assets/Fonts/LINESeedJP-Regular.ttf#LINE Seed JP",
            ["Zen Maru Gothic"]   = "avares://RealTimeTranslator.UI/Assets/Fonts/ZenMaruGothic-Regular.ttf#Zen Maru Gothic",
            ["M PLUS Rounded 1c"] = "avares://RealTimeTranslator.UI/Assets/Fonts/MPLUSRounded1c-Regular.ttf#M PLUS Rounded 1c",
        };

    private static string ResolveFontFamily(string familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            return "Yu Gothic UI";
        return EmbeddedFontMap.TryGetValue(familyName, out var uri) ? uri : familyName;
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
