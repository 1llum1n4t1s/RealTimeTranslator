using System;
using System.Collections.Generic;
using Avalonia.Input;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>Avalonia のキーと、ゲームが前面にある間も確認する Windows 仮想キーを対応させる。</summary>
internal static class VoiceShortcutKey
{
    public static int ToVirtualKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return 0x41 + (int)key - (int)Key.A;
        if (key is >= Key.D0 and <= Key.D9) return 0x30 + (int)key - (int)Key.D0;
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return 0x60 + (int)key - (int)Key.NumPad0;
        if (key is >= Key.F1 and <= Key.F24) return 0x70 + (int)key - (int)Key.F1;
        return key switch
        {
            Key.Back => 0x08, Key.Tab => 0x09, Key.Enter => 0x0D, Key.Pause => 0x13, Key.CapsLock => 0x14,
            Key.Space => 0x20, Key.PageUp => 0x21, Key.PageDown => 0x22, Key.End => 0x23, Key.Home => 0x24,
            Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
            Key.PrintScreen => 0x2C, Key.Insert => 0x2D, Key.Delete => 0x2E, Key.Apps => 0x5D,
            Key.Multiply => 0x6A, Key.Add => 0x6B, Key.Separator => 0x6C, Key.Subtract => 0x6D,
            Key.Decimal => 0x6E, Key.Divide => 0x6F, Key.NumLock => 0x90, Key.Scroll => 0x91,
            Key.OemSemicolon => 0xBA, Key.OemPlus => 0xBB, Key.OemComma => 0xBC, Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE, Key.OemQuestion => 0xBF, Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB, Key.OemPipe => 0xDC, Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE, Key.Oem8 => 0xDF, Key.OemBackslash => 0xE2,
            _ => 0
        };
    }

    public static int ToVirtualKey(KeyEventArgs args)
    {
        var key = ToVirtualKey(args.Key);
        if (key != 0) return key;
        // IME 処理中も、取得できた物理キーを使って英数字やファンクションキーを登録できる。
        if (args.PhysicalKey is >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9)
            return 0x30 + (int)args.PhysicalKey - (int)PhysicalKey.Digit0;
        return Enum.TryParse<Key>(args.PhysicalKey.ToString(), out var physical) ? ToVirtualKey(physical) : 0;
    }

    public static VoiceShortcutModifiers Modifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? VoiceShortcutModifiers.Control : 0) |
        (modifiers.HasFlag(KeyModifiers.Shift) ? VoiceShortcutModifiers.Shift : 0) |
        (modifiers.HasFlag(KeyModifiers.Alt) ? VoiceShortcutModifiers.Alt : 0) |
        (modifiers.HasFlag(KeyModifiers.Meta) ? VoiceShortcutModifiers.Windows : 0);

    public static string Format(int virtualKey, VoiceShortcutModifiers modifiers)
    {
        var names = new List<string>();
        if (modifiers.HasFlag(VoiceShortcutModifiers.Control)) names.Add("Ctrl");
        if (modifiers.HasFlag(VoiceShortcutModifiers.Shift)) names.Add("Shift");
        if (modifiers.HasFlag(VoiceShortcutModifiers.Alt)) names.Add("Alt");
        if (modifiers.HasFlag(VoiceShortcutModifiers.Windows)) names.Add("Win");
        var key = Key.None;
        foreach (var candidate in Enum.GetValues<Key>())
            if (ToVirtualKey(candidate) == virtualKey) { key = candidate; break; }
        names.Add(virtualKey is >= 0x30 and <= 0x39 ? ((char)virtualKey).ToString() : key.ToString());
        return string.Join(" + ", names);
    }
}
