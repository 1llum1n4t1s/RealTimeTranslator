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
    private ObservableCollection<SubtitleDisplayItem> _subtitles = new();

    [ObservableProperty]
    private string _fontFamily = "Yu Gothic UI";

    [ObservableProperty]
    private double _fontSize = 24;

    [ObservableProperty]
    private IBrush _backgroundBrush = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));

    // 背景色の輝度から自動派生する枠色（明るい背景には暗枠、暗い背景には明枠）。
    [ObservableProperty]
    private IBrush _borderBrush = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255));

    [ObservableProperty]
    private double _bottomMarginPercent = 10;

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
            BackgroundBrush = ParseBrush(_settings.BackgroundColor);
            BorderBrush = DeriveBorderBrush(_settings.BackgroundColor);
            BottomMarginPercent = _settings.BottomMarginPercent;
        });
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
    private string _displayText = string.Empty;

    [ObservableProperty]
    private IBrush _textBrush = Brushes.White;

    [ObservableProperty]
    private double _opacity = 1.0;

    private DateTime _displayEndTime;
    private readonly double _fadeOutDuration;

    public SubtitleDisplayItem(SubtitleItem item, OverlaySettings settings)
    {
        SegmentId = item.SegmentId;
        _fadeOutDuration = settings.FadeOutDuration;
        Update(item, settings);
    }

    public void Update(SubtitleItem item, OverlaySettings settings)
    {
        DisplayText = item.DisplayText;
        TextBrush = BrushHelper.ParseBrush(item.IsFinal ? settings.FinalTextColor : settings.PartialTextColor, Colors.White);
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
