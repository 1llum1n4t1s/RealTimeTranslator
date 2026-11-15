using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.Tests;

/// <summary>
/// ボイス通訳画面の ViewModel が、サービスの一時状態を利用者の設定として保存しないことを検証する。
/// </summary>
[TestClass]
public sealed class VoiceTranslationViewModelTests
{
    [TestMethod]
    public void 送信モード切替に失敗しても一時的なミュートを保存せず切替前のモードへ戻す()
    {
        // 送信モード切替の失敗処理でサービスは一時的に Muted へ閉じる。それを保存すると、
        // 利用者が選んでいない「ミュート」で次回の開始が無送信になる。
        var appSettings = new AppSettings();
        appSettings.VoiceTranslation.Mode = VoiceSendMode.Original;
        var monitor = new StubOptionsMonitor(appSettings);
        var settingsVm = new SettingsViewModel(monitor, new TestSettingsService(), new OverlayViewModel(monitor));
        var service = new FailingModeService();
        using var vm = new VoiceTranslationViewModel(service, new EmptyAudio(), settingsVm, _ => { });

        vm.SelectedMode = vm.ModeOptions.First(x => x.Value == VoiceSendMode.Translated);

        Assert.AreEqual(1, service.SetModeCalls);
        Assert.AreEqual(VoiceSendMode.Original, settingsVm.Settings.VoiceTranslation.Mode);
        Assert.AreEqual(VoiceSendMode.Original, vm.SelectedMode?.Value);
        Assert.IsFalse(string.IsNullOrEmpty(vm.ErrorText));
    }

    private sealed class FailingModeService : IVoiceTranslationService
    {
        public int SetModeCalls { get; private set; }
        public VoiceTranslationState State => VoiceTranslationState.Running;
        // 失敗処理後のサービスと同じく、一時的なミュートを返す。
        public VoiceSendMode Mode => VoiceSendMode.Muted;
        public bool IsMonitoring => false;
#pragma warning disable CS0067
        public event Action<VoiceTranslationState>? StateChanged;
        public event Action<string>? TranscriptReceived;
        public event Action<float, float>? LevelsChanged;
        public event Action<Exception>? ErrorReceived;
        public event Action<bool>? MonitoringChanged;
        public event Action<Exception>? MonitoringErrorReceived;
#pragma warning restore CS0067
        public Task StartAsync(VoiceTranslationSettings settings, OpenAIRealtimeSettings openAi, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetModeAsync(VoiceSendMode mode, CancellationToken ct = default)
        {
            SetModeCalls++;
            return Task.FromException(new IOException("接続失敗"));
        }
        public Task SetMonitoringAsync(string? outputDeviceId, CancellationToken ct = default) => Task.CompletedTask;
        public void SetGains(float inputGainDb, float outputGainDb) { }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyAudio : IVoiceAudioService
    {
        public long SessionId => 0;
#pragma warning disable CS0067
        public event Action<VoiceMicrophoneFrame>? MicrophoneFrame;
        public event Action<long, Exception>? ErrorReceived;
#pragma warning restore CS0067
        public IReadOnlyList<VoiceAudioDevice> GetMicrophones() => [];
        public IReadOnlyList<VoiceAudioDevice> GetOutputs() => [];
        public Task StartAsync(string microphoneId, string outputDeviceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public void WriteOutput(byte[] pcm16, int sampleRate) { }
        public void ClearOutput() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
