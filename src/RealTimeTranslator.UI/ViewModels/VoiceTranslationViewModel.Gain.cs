using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.UI.ViewModels;

public partial class VoiceTranslationViewModel
{
    [ObservableProperty] private double _inputGainDb;
    [ObservableProperty] private double _outputGainDb;
    [ObservableProperty] private bool _isInputClipping;
    [ObservableProperty] private bool _isOutputClipping;
    private long _inputClipUntil;
    private long _outputClipUntil;

    public double InputLevelNorm => NormalizeLevel(InputLevel);
    public double OutputLevelNorm => NormalizeLevel(OutputLevel);
    public string InputLevelText => FormatLevel(InputLevel);
    public string OutputLevelText => FormatLevel(OutputLevel);

    private void InitializeGains()
    {
        InputGainDb = _settings.InputGainDb = VoiceTranslationSettings.ClampGainDb(_settings.InputGainDb);
        OutputGainDb = _settings.OutputGainDb = VoiceTranslationSettings.ClampGainDb(_settings.OutputGainDb);
    }

    partial void OnInputGainDbChanged(double value) => ApplyGains();
    partial void OnOutputGainDbChanged(double value) => ApplyGains();

    private void ApplyGains()
    {
        if (_initializing || _disposed) return;
        var input = double.IsFinite(InputGainDb) ? Math.Clamp(InputGainDb, -24d, 24d) : 0d;
        var output = double.IsFinite(OutputGainDb) ? Math.Clamp(OutputGainDb, -24d, 24d) : 0d;
        if (InputGainDb != input) { InputGainDb = input; return; }
        if (OutputGainDb != output) { OutputGainDb = output; return; }
        _settings.InputGainDb = (float)input;
        _settings.OutputGainDb = (float)output;
        _service.SetGains((float)input, (float)output);
        SaveSettings();
    }

    [RelayCommand]
    private void ResetInputGain() => InputGainDb = 0;

    [RelayCommand]
    private void ResetOutputGain() => OutputGainDb = 0;

    private static double PeakDb(double percent) => percent <= 0 ? -120d : 20d * Math.Log10(percent / 100d);
    private static double NormalizeLevel(double percent) => Math.Clamp((PeakDb(percent) + 60d) / 60d, 0d, 1d);
    private static string FormatLevel(double percent) => PeakDb(percent) <= -60d ? "-∞ dBFS" : $"{PeakDb(percent):0.0} dBFS";

    private void UpdateClippingIndicators()
    {
        var now = Environment.TickCount64;
        if (PeakDb(InputLevel) >= SettingsViewModel.ClipThresholdDb) _inputClipUntil = now + 1500;
        if (PeakDb(OutputLevel) >= SettingsViewModel.ClipThresholdDb) _outputClipUntil = now + 1500;
        IsInputClipping = now < _inputClipUntil;
        IsOutputClipping = now < _outputClipUntil;
    }
}
