using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.UI.Views;

public partial class VoiceTranslationView : UserControl
{
    private int _capturedKey;
    private VoiceShortcutModifiers _capturedModifiers;
    private Window? _owner;

    public VoiceTranslationView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnCaptureKeyUp, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) =>
        {
            _owner = TopLevel.GetTopLevel(this) as Window;
            if (_owner is not null) _owner.Deactivated += OnWindowDeactivated;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_owner is not null) _owner.Deactivated -= OnWindowDeactivated;
            _owner = null;
            CancelCapture();
        };
    }

    private void BeginCapture(object? sender, RoutedEventArgs args)
    {
        _capturedKey = 0;
        if (DataContext is VoiceTranslationViewModel vm) vm.BeginPttCapture();
        if (sender is Button button) button.Focus();
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is not VoiceTranslationViewModel { IsCapturingPttKey: true }) return;
        args.Handled = true;
        if (args.Key == Key.Escape) { CancelCapture(); return; }
        var modifiers = VoiceShortcutKey.Modifiers(args.KeyModifiers);
        if (_capturedKey != 0)
        {
            _capturedModifiers |= modifiers;
            return;
        }
        var key = VoiceShortcutKey.ToVirtualKey(args);
        if (key == 0) return;
        _capturedKey = key;
        _capturedModifiers = modifiers;
    }

    private void OnCaptureKeyUp(object? sender, KeyEventArgs args)
    {
        if (DataContext is not VoiceTranslationViewModel { IsCapturingPttKey: true } vm) return;
        args.Handled = true;
        if (_capturedKey == 0 || VoiceShortcutKey.ToVirtualKey(args) != _capturedKey) return;
        vm.CapturePttShortcut(_capturedKey, _capturedModifiers);
        _capturedKey = 0;
    }

    private void OnCaptureLostFocus(object? sender, RoutedEventArgs args) => CancelCapture();
    private void OnWindowDeactivated(object? sender, System.EventArgs args) => CancelCapture();

    private void CancelCapture()
    {
        _capturedKey = 0;
        if (DataContext is VoiceTranslationViewModel vm) vm.CancelPttCapture();
    }
}
