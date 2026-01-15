using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>
/// 設定画面のViewModel
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private AppSettings _settings;
    private readonly ISettingsService _settingsService;
    private readonly OverlayViewModel _overlayViewModel;

    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    public SettingsViewModel(IOptionsSnapshot<AppSettings> options, ISettingsService settingsService, OverlayViewModel overlayViewModel)
    {
        _settings = options.Value;
        _settingsService = settingsService;
        _overlayViewModel = overlayViewModel;
        GpuTypes = new ReadOnlyCollection<GPUType>(Enum.GetValues<GPUType>());
        Languages = new ReadOnlyCollection<LanguageType>(Enum.GetValues<LanguageType>());
        SampleRates = new ReadOnlyCollection<int>(new[] { 8000, 16000, 44100, 48000 });
        FontSizes = new ReadOnlyCollection<int>(new[] { 12, 14, 16, 18, 20, 24, 28, 32, 36, 40 });
        BeamSizes = new ReadOnlyCollection<int>(new[] { 1, 3, 5, 10, 15, 20 });
        DeviceIds = new ReadOnlyCollection<int>(new[] { 0, 1, 2, 3 });
        CacheSizes = new ReadOnlyCollection<int>(new[] { 100, 500, 1000, 2000, 5000, 10000 });
        MaxLinesList = new ReadOnlyCollection<int>(new[] { 1, 2, 3, 4, 5 });
        GameProfiles = new ObservableCollection<GameProfile>(_settings.GameProfiles);
        SelectedGameProfile = GameProfiles.FirstOrDefault();
    }

    public AppSettings Settings => _settings;

    public ReadOnlyCollection<GPUType> GpuTypes { get; }

    public ReadOnlyCollection<LanguageType> Languages { get; }

    public ReadOnlyCollection<int> SampleRates { get; }

    public ReadOnlyCollection<int> FontSizes { get; }

    public ReadOnlyCollection<int> BeamSizes { get; }

    public ReadOnlyCollection<int> DeviceIds { get; }

    public ReadOnlyCollection<int> CacheSizes { get; }

    public ReadOnlyCollection<int> MaxLinesList { get; }

    public ObservableCollection<GameProfile> GameProfiles { get; }

    [ObservableProperty]
    private GameProfile? _selectedGameProfile;

    [ObservableProperty]
    private string _hotwordsText = string.Empty;

    [ObservableProperty]
    private string _asrCorrectionDictionaryText = string.Empty;

    [ObservableProperty]
    private string _preTranslationDictionaryText = string.Empty;

    [ObservableProperty]
    private string _postTranslationDictionaryText = string.Empty;

    [ObservableProperty]
    private string _initialPromptText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    partial void OnSelectedGameProfileChanged(GameProfile? value)
    {
        if (value is null)
        {
            HotwordsText = string.Empty;
            AsrCorrectionDictionaryText = string.Empty;
            PreTranslationDictionaryText = string.Empty;
            PostTranslationDictionaryText = string.Empty;
            InitialPromptText = string.Empty;
            return;
        }

        HotwordsText = string.Join(Environment.NewLine, value.Hotwords);
        AsrCorrectionDictionaryText = SerializeDictionary(value.ASRCorrectionDictionary);
        PreTranslationDictionaryText = SerializeDictionary(value.PreTranslationDictionary);
        PostTranslationDictionaryText = SerializeDictionary(value.PostTranslationDictionary);
        InitialPromptText = value.InitialPrompt;
    }

    partial void OnHotwordsTextChanged(string value)
    {
        if (SelectedGameProfile is null)
            return;

        SelectedGameProfile.Hotwords = ParseHotwords(value);
    }

    partial void OnAsrCorrectionDictionaryTextChanged(string value)
    {
        if (SelectedGameProfile is null)
            return;
        SelectedGameProfile.ASRCorrectionDictionary = ParseDictionary(value);
    }

    partial void OnPreTranslationDictionaryTextChanged(string value)
    {
        if (SelectedGameProfile is null)
            return;
        SelectedGameProfile.PreTranslationDictionary = ParseDictionary(value);
    }

    partial void OnPostTranslationDictionaryTextChanged(string value)
    {
        if (SelectedGameProfile is null)
            return;
        SelectedGameProfile.PostTranslationDictionary = ParseDictionary(value);
    }

    partial void OnInitialPromptTextChanged(string value)
    {
        if (SelectedGameProfile is null)
            return;
        SelectedGameProfile.InitialPrompt = value;
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new GameProfile { Name = "新規プロファイル" };
        GameProfiles.Add(profile);
        SelectedGameProfile = profile;
    }

    [RelayCommand]
    private void RemoveProfile()
    {
        if (SelectedGameProfile is null)
            return;

        var index = GameProfiles.IndexOf(SelectedGameProfile);
        GameProfiles.Remove(SelectedGameProfile);
        SelectedGameProfile = GameProfiles.ElementAtOrDefault(Math.Max(0, index - 1));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        _settings.GameProfiles = GameProfiles.ToList();
        await _settingsService.SaveAsync(_settings);
        _overlayViewModel.ReloadSettings();
        SettingsSaved?.Invoke(this, new SettingsSavedEventArgs(_settings));
        StatusMessage = $"設定を保存しました: {DateTime.Now:HH:mm:ss}";
    }

    private static List<string> ParseHotwords(string text)
    {
        return text.Split(Environment.NewLine)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static Dictionary<string, string> ParseDictionary(string text)
    {
        var result = new Dictionary<string, string>();
        var lines = text.Split(Environment.NewLine);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = line.Substring(0, separatorIndex).Trim();
            var value = line.Substring(separatorIndex + 1).Trim();

            if (string.IsNullOrWhiteSpace(key))
                continue;

            result[key] = value;
        }

        return result;
    }

    private static string SerializeDictionary(IReadOnlyDictionary<string, string> dictionary)
    {
        return string.Join(Environment.NewLine, dictionary.Select(entry => $"{entry.Key}={entry.Value}"));
    }
}

public class SettingsSavedEventArgs : EventArgs
{
    public AppSettings Settings { get; }

    public SettingsSavedEventArgs(AppSettings settings)
    {
        Settings = settings;
    }
}
