using Avalonia;
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
    // 捕捉状態に関係なく押下中のキーを追う。捕捉開始時に押下中だったキーは、その KeyUp まで
    // 登録候補にしない (Enter でボタンを起動して押し続けると、リピートの KeyDown が登録される)。
    // IME 処理中は KeyDown の Key が ImeProcessed、KeyUp が実キーになり得るため、物理キーで識別する。
    private readonly HashSet<(PhysicalKey Physical, Key Logical)> _heldKeys = [];
    private readonly HashSet<(PhysicalKey Physical, Key Logical)> _keysHeldAtCaptureStart = [];

    /// <summary>押下追跡の識別子。物理キーを優先し、取得できない場合だけ論理キーを使う。</summary>
    private static (PhysicalKey Physical, Key Logical) HeldKeyId(KeyEventArgs args) =>
        args.PhysicalKey != PhysicalKey.None ? (args.PhysicalKey, Key.None) : (PhysicalKey.None, args.Key);

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
            _heldKeys.Clear();
            CancelCapture();
        };
    }

    private void BeginCapture(object? sender, RoutedEventArgs args)
    {
        _capturedKey = 0;
        RememberKeysHeldAtCaptureStart();
        if (DataContext is VoiceTranslationViewModel vm) vm.BeginPttCapture();
        if (sender is Button button) button.Focus();
    }

    private void BeginGameCapture(object? sender, RoutedEventArgs args)
    {
        _capturedKey = 0;
        RememberKeysHeldAtCaptureStart();
        if (DataContext is VoiceTranslationViewModel vm) vm.BeginGamePttCapture();
        if (sender is Button button) button.Focus();
    }

    private void RememberKeysHeldAtCaptureStart()
    {
        _keysHeldAtCaptureStart.Clear();
        _keysHeldAtCaptureStart.UnionWith(_heldKeys);
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs args)
    {
        _heldKeys.Add(HeldKeyId(args));
        if (DataContext is not VoiceTranslationViewModel vm || (!vm.IsCapturingPttKey && !vm.IsCapturingGamePttKey)) return;
        args.Handled = true;
        if (args.Key == Key.Escape) { CancelCapture(); return; }
        // 捕捉開始前から押されているキーのリピートは、離されるまで登録しない。
        if (_keysHeldAtCaptureStart.Contains(HeldKeyId(args))) return;
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
        _heldKeys.Remove(HeldKeyId(args));
        if (DataContext is not VoiceTranslationViewModel vm || (!vm.IsCapturingPttKey && !vm.IsCapturingGamePttKey)) return;
        args.Handled = true;
        // 捕捉開始前から押されていたキーは、ここで離されたので以後は登録候補にできる。
        if (_keysHeldAtCaptureStart.Remove(HeldKeyId(args))) return;
        if (_capturedKey == 0 || VoiceShortcutKey.ToVirtualKey(args) != _capturedKey) return;
        if (vm.IsCapturingGamePttKey) vm.CaptureGamePttShortcut(_capturedKey, _capturedModifiers);
        else vm.CapturePttShortcut(_capturedKey, _capturedModifiers);
        _capturedKey = 0;
    }

    private void OnCaptureLostFocus(object? sender, RoutedEventArgs args) => CancelCapture();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // view の外へフォーカスが移ると KeyUp が届かないため、押下中の記録を捨てる
        // (残ると次の捕捉で、そのキーの押下を開始前から押されていたものとして無視してしまう)。
        if (change.Property == IsKeyboardFocusWithinProperty && !IsKeyboardFocusWithin) _heldKeys.Clear();
    }
    private void OnWindowDeactivated(object? sender, System.EventArgs args)
    {
        // 非アクティブ中の KeyUp は届かないため、押下中の記録を捨てる。
        _heldKeys.Clear();
        CancelCapture();
    }

    private void CancelCapture()
    {
        _capturedKey = 0;
        _keysHeldAtCaptureStart.Clear();
        if (DataContext is VoiceTranslationViewModel vm)
        {
            vm.CancelPttCapture();
            vm.CancelGamePttCapture();
        }
    }
}
