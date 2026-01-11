using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>
/// オーバーレイウィンドウのViewModel
/// </summary>
public partial class OverlayViewModel : ObservableObject, IDisposable
{
    private const int CleanupIntervalMs = 100; // クリーンアップ間隔（ミリ秒）

    private readonly OverlaySettings _settings;
    private readonly DispatcherTimer _cleanupTimer;
    private readonly object _subtitlesLock = new();
    private bool _isDisposed;

    [ObservableProperty]
    private ObservableCollection<SubtitleDisplayItem> _subtitles = new();

    [ObservableProperty]
    private string _fontFamily = "Yu Gothic UI";

    [ObservableProperty]
    private double _fontSize = 24;

    [ObservableProperty]
    private Brush _backgroundBrush = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));

    [ObservableProperty]
    private double _bottomMarginPercent = 10;

    public OverlayViewModel(OverlaySettings? settings = null)
    {
        _settings = settings ?? new OverlaySettings();

        FontFamily = _settings.FontFamily;
        FontSize = _settings.FontSize;
        BackgroundBrush = ParseBrush(_settings.BackgroundColor);
        BottomMarginPercent = _settings.BottomMarginPercent;

        // 定期的に古い字幕を削除
        _cleanupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(CleanupIntervalMs)
        };
        _cleanupTimer.Tick += CleanupOldSubtitles;
        _cleanupTimer.Start();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _cleanupTimer.Stop();
        _cleanupTimer.Tick -= CleanupOldSubtitles;
        _isDisposed = true;
    }

    /// <summary>
    /// 字幕を追加または更新
    /// </summary>
    public void AddOrUpdateSubtitle(SubtitleItem item)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            lock (_subtitlesLock)
            {
                // 同じSegmentIdの字幕を検索
                var existing = Subtitles.FirstOrDefault(s => s.SegmentId == item.SegmentId);

                if (existing != null)
                {
                    // 既存の字幕を更新
                    existing.Update(item, _settings);
                }
                else
                {
                    // 新しい字幕を追加
                    var displayItem = new SubtitleDisplayItem(item, _settings);
                    Subtitles.Add(displayItem);

                    // 最大行数を超えた場合、古いものを削除
                    while (Subtitles.Count > _settings.MaxLines)
                    {
                        Subtitles.RemoveAt(0);
                    }
                }
            }
        });
    }

    /// <summary>
    /// 古い字幕を削除
    /// </summary>
    private void CleanupOldSubtitles(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            lock (_subtitlesLock)
            {
                var now = DateTime.Now;
                // 逆順にループして直接削除することで効率化
                for (var i = Subtitles.Count - 1; i >= 0; i--)
                {
                    if (Subtitles[i].ShouldRemove(now))
                    {
                        Subtitles.RemoveAt(i);
                    }
                }
            }
        });
    }

    /// <summary>
    /// すべての字幕をクリア
    /// </summary>
    public void ClearSubtitles()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            lock (_subtitlesLock)
            {
                Subtitles.Clear();
            }
        });
    }

    /// <summary>
    /// 設定変更を反映
    /// </summary>
    public void ReloadSettings()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            FontFamily = _settings.FontFamily;
            FontSize = _settings.FontSize;
            BackgroundBrush = ParseBrush(_settings.BackgroundColor);
            BottomMarginPercent = _settings.BottomMarginPercent;
        });
    }

    private static Brush ParseBrush(string colorString)
    {
        return BrushHelper.ParseBrush(colorString, Colors.Black);
    }
}

/// <summary>
/// Brush変換のヘルパークラス
/// </summary>
internal static class BrushHelper
{
    public static Brush ParseBrush(string colorString, Color fallbackColor)
    {
        if (string.IsNullOrWhiteSpace(colorString))
        {
            System.Diagnostics.Debug.WriteLine($"Color string is null or empty, using fallback: {fallbackColor}");
            return new SolidColorBrush(fallbackColor);
        }

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(colorString);
            return new SolidColorBrush(color);
        }
        catch (FormatException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Invalid color format '{colorString}': {ex.Message}. Using fallback: {fallbackColor}");
            return new SolidColorBrush(fallbackColor);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error parsing color '{colorString}': {ex.Message}. Using fallback: {fallbackColor}");
            return new SolidColorBrush(fallbackColor);
        }
    }
}

/// <summary>
/// 表示用の字幕アイテム
/// </summary>
public partial class SubtitleDisplayItem : ObservableObject
{
    public string SegmentId { get; }

    [ObservableProperty]
    private string _displayText = string.Empty;

    [ObservableProperty]
    private Brush _textBrush = Brushes.White;

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

        // フェードアウト中
        var fadeProgress = (now - _displayEndTime).TotalSeconds / _fadeOutDuration;
        if (fadeProgress < 1.0)
        {
            Opacity = 1.0 - fadeProgress;
            return false;
        }

        return true;
    }
}
