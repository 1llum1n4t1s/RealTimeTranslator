using System.Collections.ObjectModel;
using System.Linq;

namespace RealTimeTranslator.UI.ViewModels;

public partial class VoiceTranslationViewModel
{
    public ReadOnlyCollection<VadThresholdOption> VadThresholdOptions { get; private set; } = null!;
    public ReadOnlyCollection<VadDurationOption> VadPreRollOptions { get; private set; } = null!;
    public ReadOnlyCollection<VadDurationOption> VadHangoverOptions { get; private set; } = null!;

    public bool CanEditVadSettings => CanEditSettings && EnableVad;

    public bool EnableVad
    {
        get => _settings.EnableVad;
        set
        {
            if (!CanEditSettings || value == _settings.EnableVad) return;
            _settings.EnableVad = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEditVadSettings));
            SaveSettings();
        }
    }

    public VadThresholdOption? SelectedVadThreshold
    {
        get => VadThresholdOptions.First(option => option.Value == _settings.VadThreshold);
        set
        {
            if (!CanEditVadSettings || value is null || !VadThresholdOptions.Contains(value)
                || value.Value == _settings.VadThreshold) return;
            _settings.VadThreshold = value.Value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public VadDurationOption? SelectedVadPreRoll
    {
        get => VadPreRollOptions.First(option => option.Milliseconds == _settings.VadPreRollMs);
        set
        {
            if (!CanEditVadSettings || value is null || !VadPreRollOptions.Contains(value)
                || value.Milliseconds == _settings.VadPreRollMs) return;
            _settings.VadPreRollMs = value.Milliseconds;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public VadDurationOption? SelectedVadHangover
    {
        get => VadHangoverOptions.First(option => option.Milliseconds == _settings.VadHangoverMs);
        set
        {
            if (!CanEditVadSettings || value is null || !VadHangoverOptions.Contains(value)
                || value.Milliseconds == _settings.VadHangoverMs) return;
            _settings.VadHangoverMs = value.Milliseconds;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    private void InitializeVad()
    {
        _settings.NormalizeVadSettings();
        VadThresholdOptions = VadOptionFactory.CreateThresholdOptions(_settings.VadThreshold);
        VadPreRollOptions = VadOptionFactory.CreatePreRollOptions(_settings.VadPreRollMs);
        VadHangoverOptions = VadOptionFactory.CreateHangoverOptions(_settings.VadHangoverMs);
    }
}
