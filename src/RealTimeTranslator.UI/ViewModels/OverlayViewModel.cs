using System;
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
        FontFamily = _settings.FontFamily;
        FontSize = _settings.FontSize;
        BackgroundBrush = ParseBrush(_settings.BackgroundColor);
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
            FontFamily = _settings.FontFamily;
            FontSize = _settings.FontSize;
            BackgroundBrush = ParseBrush(_settings.BackgroundColor);
            BottomMarginPercent = _settings.BottomMarginPercent;
        });
    }

    private static IBrush ParseBrush(string colorString)
    {
        return BrushHelper.ParseBrush(colorString, Colors.Black);
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
