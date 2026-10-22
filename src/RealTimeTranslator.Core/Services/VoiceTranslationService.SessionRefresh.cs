using SuperLightLogger;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

public sealed partial class VoiceTranslationService
{
    private static readonly ILog Logger = LogManager.GetLogger<VoiceTranslationService>();
    private static readonly TimeSpan SessionRefreshInterval = TimeSpan.FromMinutes(55);
    private Timer? _sessionRefreshTimer;

    private void ScheduleSessionRefresh(IRealtimeSpeechTranslator client, CancellationToken ct)
    {
        lock (_routing)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_client, client) || _sessionCancellation is not { } session) return;
            // モード切替用の短命な linked token ではなく、送話セッションの寿命を捕捉する。
            var sessionToken = session.Token;
            _sessionRefreshTimer?.Dispose();
            _sessionRefreshTimer = new Timer(state =>
            {
                _ = RefreshSessionAsync(client, session, sessionToken);
            }, null, SessionRefreshInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RefreshSessionAsync(IRealtimeSpeechTranslator client,
        CancellationTokenSource session, CancellationToken ct)
    {
        try
        {
            await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_routing)
                {
                    if (!ReferenceEquals(_sessionCancellation, session) || !ReferenceEquals(_client, client)
                        || State != VoiceTranslationState.Running || Mode != VoiceSendMode.Translated
                        || _faultScheduled != 0 || ct.IsCancellationRequested) return;
                }

                Logger.Info("ボイス通訳の接続が約55分に到達 — 時間上限前にセッションを更新します");
                try
                {
                    // 機器と試聴のオン状態を保ち、旧接続の音声・VAD・PTT保持だけを破棄する。
                    var oldClient = MuteRouting(preserveMonitoring: true);
                    if (oldClient is not null) await oldClient.DisposeAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    InitializeVoiceVad(ct);
                    await ConnectClientAsync(ct).ConfigureAwait(false);
                    lock (_routing)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (_faultScheduled != 0)
                            throw new IOException("接続更新中に音声送信の異常を検出しました。");
                        // 更新待ちに取得されたフレームと DSP 履歴も、新接続へ持ち越さない。
                        _epoch++;
                        _mode = (int)VoiceSendMode.Translated;
                    }
                    Logger.Info("ボイス通訳のセッション更新が完了しました");
                }
                catch
                {
                    var failedClient = MuteRouting(preserveMonitoring: true);
                    if (failedClient is not null) await failedClient.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Logger.Error("ボイス通訳のセッション更新に失敗しました", ex);
                ScheduleFault(new IOException("ボイス通訳の接続更新に失敗しました。再開始してください。", ex), session);
            }
            finally { _lifecycle.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
