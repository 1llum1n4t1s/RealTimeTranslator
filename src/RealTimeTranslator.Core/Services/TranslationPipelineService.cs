using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SuperLightLogger;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services.Audio;

namespace RealTimeTranslator.Core.Services;

public sealed class TranslationPipelineService : ITranslationPipelineService, IAsyncDisposable
{
    private static readonly ILog Logger = LogManager.GetLogger<TranslationPipelineService>();
    // partial 表示の更新頻度。 短いほど字幕が機敏になるが UI スレッド負荷が増える。
    // ゆろさんの「翻訳が遅れているときに加速」要望で 100ms → 50ms → 30ms → 20ms と段階的に短縮 (v1.0.33)。
    // OpenAI Realtime API のサーバー側 VAD 律速 (silence_duration_ms はクライアント設定不可) は
    // 越えられないので、ここで詰められるのは partial 描画間隔だけ。
    // 20ms = 50Hz は人間の知覚閾値 (CFF ~60Hz) に近く、 これ以上の高速化は体感差ゼロでコストだけ増える境界値。
    private static readonly TimeSpan DeltaThrottle = TimeSpan.FromMilliseconds(20);

    private readonly IAudioCaptureService _audioCaptureService;
    // プロバイダ N 実装 + 現在 active な参照。 hot-swap: StartCoreAsync で Provider を見て
    // _activeClient を選び直す (走行中は切り替わらない。 設定変更は次の Start で反映)。
    // 送信レート分岐は _activeClient.InputSampleRate (OpenAI=24000 / その他=16000) で行う。
    private readonly IRealtimeTranscriber _openAiClient;
    private readonly IRealtimeTranscriber _geminiClient;
    private readonly IRealtimeTranscriber _sonioxClient;
    private readonly IRealtimeTranscriber _speechmaticsClient;
    private readonly IRealtimeTranscriber _azureClient;
    // Provider → client の解決表 (StartCoreAsync で active を引く)。 未知 Provider は OpenAI に倒す。
    private readonly Dictionary<TranscriptionProvider, IRealtimeTranscriber> _clientsByProvider;
    private IRealtimeTranscriber _activeClient;
    // 購読/解除用の重複排除済み client 配列。 テストで openAi==gemini の同一モックを渡されたとき、
    // 同じインスタンスへ 2 回 += すると delta/done が二重発火する (emit 件数が倍) ため参照で排除する。
    private readonly IRealtimeTranscriber[] _distinctClients;
    // active セッションの provider 別設定スナップショット (Start 時に固定)。 走行中の設定変更や
    // provider 切替に引きずられないよう、 OpenAI/Gemini の同名設定 (D-7 分割閾値 / 無音 padding /
    // cost 計算用モデル名) をここに確定させる。 active セッションは _activeClient と常に整合する。
    private int _activeMaxPartialChars;
    private int _activeSilencePaddingMs;
    private string _activeModelForCost = "gpt-realtime-translate";
    private Channel<float[]>? _audioInputChannel;
    private Task? _audioProcessingTask;
    private CancellationTokenSource? _audioProcessingCts;

    private string _currentSegmentId = Guid.NewGuid().ToString();
    private readonly StringBuilder _accumulatedText = new();
    private readonly System.Threading.Lock _textLock = new();
    private DateTime _lastEmitTime = DateTime.MinValue;
    private bool _hasPendingDelta;
    private readonly Timer _throttleTimer;
    private readonly Stopwatch _latencyStopwatch = new();
    private volatile bool _isRunning;
    // rere B1-006: Dispose / DisposeAsync の二重実行を防ぐ Interlocked 占有マーク。
    // 旧 volatile bool は check + set が非原子で、 並行 Dispose 時に両方が `if (_disposed) return;` を抜け
    // ObjectDisposedException を投げる経路があった。 OpenAIRealtimeClient も同型対策済み。
    // 0=alive, 1=disposed。
    private int _disposed;
    private DateTime _lastAudioErrorLogTime = DateTime.MinValue;

    // OpenAI Realtime Translation API は transcript.done を「セッション累積の全文」で
    // 返してくる挙動が観測されている (2026-05-16)。 そのまま finalText として overlay に出すと
    // 「まあ → まあ、 → まあ、ノ → ...」と1つの subtitle が際限なく成長する UX 不具合になる。
    // _lastFinalizedTranscript で「既に確定字幕として出した累積テキスト」を保持し、
    // done のたびに差分だけを抽出 → 句点で分割 → 文ごとに新 SegmentId で emit することで
    // 「会話が途切れず長文化する」問題を回避する。
    //
    // ⚠️ rere C1-P0-001 / A2-001: `+= sentence` の immutable string 再アロケート (O(n²)) を
    // 回避するため StringBuilder で保持。 累積長は数 KB〜数十 KB に達するため、 + 連結ごとに
    // 全文を allocate する旧設計だと 30 分以上の連続セッションで GC pressure が顕著になる。
    // 比較・末尾取得は ToString() でなく専用ヘルパー (StringBuilderStartsWith / 末尾切出し)
    // で済ませて ToString 経由のコピーを避ける。
    private readonly StringBuilder _lastFinalizedTranscript = new();

    // 句点として扱う文字。 ASCII 終端も入れて英語訳出力にも対応する。
    // 「、」(読点) は文の区切りではないので含めない (一文の中で複数現れるため)。
    // 注意: 半角ピリオド '.' は IsSentenceBoundaryAt で小数点保護 (例: 「6.3インチ」「3.14」)
    //       を行うため、 直接 IndexOf するのではなく必ず IsSentenceBoundaryAt 経由で判定する。
    // /opop M-003: char[] + Array.IndexOf を SearchValues<char> + Contains に置換 (v1.0.33)。
    // 内部 bit-table で O(1) lookup、 ホットパス (IsSentenceBoundaryAt の per-char 呼び出し) で効く。
    private static readonly System.Buffers.SearchValues<char> SentenceTerminators =
        System.Buffers.SearchValues.Create(['。', '！', '？', '!', '?', '.']);

    // 観測用カウンタ: partial / 完結文 emit の累計回数を頻度抑制ログで間引きながら吐く。
    // 「字幕が来ない」「文が切れない」「字幕が成長し続ける」系の調査で経路を可視化する。
    private long _partialEmitCount;
    private long _completedEmitCount;
    // v1.0.25 デバッグ用: 直前 partial emit 時の累積長を覚えておき、 「累積長が伸びた時のみ Info ログ」に
    // 切替えるための補助。 同内容の再 emit は出さない (= 1.3 件/秒の delta 流入で爆発させない)。
    // ShouldLogAtCount の間引きより情報量が多く、 全件出力より節度がある中間ログ密度を実現。
    private int _lastLoggedPartialLength = -1;
    // /rere 第2R #C1-R2-002 (v1.0.29 候補): 同長 & 同 SegmentId の partial 再 emit を抑止するためのキャッシュ。
    // 内容が変わってない再 emit (throttle 再発火等) で ToString + SubtitleGenerated + UI binding equality を skip。
    // 60 分セッション 60k emit のうち重複分を概ね 1/3 削減見込み (GC pressure 低減)。
    // SegmentId 切替 (新セグメント) では length=0 から始まるので自動的に invalid 化される。
    private int _lastEmittedPartialLength = -1;
    private string _lastEmittedPartialSegmentId = string.Empty;

    // 直前の ConnectionState を保持。 Reconnecting → Connected 遷移を検出して
    // 字幕状態 (_lastFinalizedTranscript / _currentSegmentId) をリセットするため (rere P0 #1)。
    private ConnectionState _lastConnectionState = ConnectionState.Disconnected;

    // ───────── 類似重複抑制 ─────────
    // Translation API (intent=translation) は transcript.done を送らず delta のみで動作する
    // ケースがある (2026-05-22 観測)。 サーバー VAD が重複する音声セグメントを別レスポンスとして
    // 再翻訳し、 微妙に異なるテキストを生成するため、 近似重複が連続 emit される UX バグになる。
    // 例: "手すりって、実は取り付けるものなんだな。" → 3秒後 "手すりって、取り付けるものなんだな。"
    // Bigram Jaccard 類似度 > DuplicateSuppressionThreshold なら後発を抑制する。
    // (text, bigrams) で保持し、 IsSimilarToRecentEmission での recent 側 bigram 再計算を排除する。
    private readonly Queue<(string text, HashSet<long> bigrams)> _recentEmittedSentences = new();
    private const int MaxRecentEmissions = 8;
    // 0.7: 実運用の近似重複 (例: 「手すりって、実は取り付けるものなんだな。」↔「手すりって、取り付けるものなんだな。」= 0.8)
    // を確実に抑制しつつ、 短い文のインデックス違い (0.55〜0.67) を誤検出しない閾値。
    internal const double DuplicateSuppressionThreshold = 0.7;

    // ───────── VAD ゲート (Silence/InSpeech/Hangover 状態機) ─────────
    // BGM や効果音だけが鳴っているシーンで OpenAI 送信を抑制し token 浪費を防ぐ。
    // EnableVad=false の場合は素通し (旧挙動)。
    private enum VadState { Silence, InSpeech, Hangover }
    private readonly IVoiceActivityDetector _vad;
    // VAD 有効/無効の切替検知用 (B1-001 / D-I3 対応)。 hot-reload で切替時に _vadResampler の
    // filter state を Reset することで、 VAD 無効パスでの「戻り値捨て Resample」を不要にする。
    // 初期値は default (EnableVad=true) と一致させて、 起動直後の不要 Reset を避ける。
    private bool _lastEnableVad = true;

    // ⭐ 並列 2 系統ストリーミングリサンプラ (v1.0.36 設計):
    //   _vadResampler  (48k→16k): VAD 判定用 (Silero v5 16kHz/512sample 固定仕様)
    //   _sendResampler (48k→24k): OpenAI 送信用 (24kHz/mono PCM16、 Nyquist 12kHz 確保)
    //
    // 経緯:
    //   v1.0.23-26 は並列 2 系統 → v1.0.27〜v1.0.35 は 1 系統二段 (`48k→16k→24k`) に統合
    //   (ゆろさん提案: 「常時/準常時送信なら 2 系統分岐は無駄、 1 系統二段で簡素化」)
    //   → v1.0.36 で並列 2 系統に revert。
    //
    // ⚠️ revert 根拠と未検証部分 (rere D-I2 で議題化):
    //   - 数学的事実: 1 系統二段は中継 16k で Nyquist 8kHz の高域カットを物理的に発生させる
    //   - 仮説 (未検証): 削れた 8-12kHz 帯域が OpenAI transcribe 精度に有意な影響を持つ
    //   - 実機 A/B 比較 (同一音源・同一発話・同一 OpenAI セッションでの完結文 emit 件数比較) は未実施
    //   - 「情報損失は確実に減るので safety side として並列 2 系統を選ぶ」という消極的根拠で採用
    //   - OpenAI Realtime Translate API server 側のモデル仕様 (16kHz Whisper-like 可能性) は非公開のため
    //     利得が server 側 resampler 品質で吸収される経路もある。 実機 A/B で検証推奨。
    //
    // 両リサンプラは LatencyMargin を時間ベース (4ms) に揃えてあり、 16k 512sample = 24k 768sample
    // = 同じ 32ms フレームとして時間同期できる (= フレームペア化可能)。
    private readonly StreamingResampler _vadResampler = new(48000, 16000);
    private readonly StreamingResampler _sendResampler = new(48000, 24000);

    // ───────── 入力プリプロセス DSP 2 段 (v1.0.30 新規、 v1.0.32 LoudnessNormalizer 削除、 v1.0.36 NightModeCompressor 削除) ─────────
    // WASAPI 48kHz mono float32 を受け取った直後・リサンプル前に挟まる前処理チェーン。
    // 全 IsEnabled=false / InputGainDb=0 がデフォルトで、 完全 bypass 動作 (v1.0.29 以前と同一)。
    // 信号フロー: WASAPI → [InputGain] → [AntiClip] → _vadResampler →...
    // ステートフルなので _vadResampler と同じくシングルインスタンスで保持 + StartCoreAsync で Reset
    // (詳細は _global/systemPatterns.md の DSP 教訓と各 DSP クラスの XML doc 参照)。
    private const int CaptureSampleRate = 48000;
    private readonly InputGainStage _inputGain = new(0f);
    // レベルメーター用: ゲイン適用後のピーク振幅を ~50ms スロットルで UI へ通知する状態。
    private DateTime _lastLevelEmitUtc = DateTime.MinValue;
    private float _levelPeakAccum;
    private static readonly TimeSpan LevelEmitInterval = TimeSpan.FromMilliseconds(50);
    // 16kHz / 32ms = 512 samples ごとに切り出すための VAD用 accumulator。
    // 24kHz / 32ms = 768 samples ごとに切り出すための送信用 accumulator (同じ時間区間に対応)。
    private float[]? _frameAccumulator;
    private int _frameAccumulatorLen;
    private float[]? _frameAccumulator24k;
    private int _frameAccumulator24kLen;
    // 発話冒頭の取りこぼし防止用に直近フレームを保持するリングバッファ。 中身は送信用 24k フレーム
    // (旧 16k から変更)。 VAD 判定は 16k で行うが PreRoll に積むのは送信フレームなので 24k で持つ。
    private readonly Queue<float[]> _preRollBuffer = new();
    private VadState _vadState = VadState.Silence;
    private int _hangoverFramesRemaining;
    // v1.0.27 改修: VAD Silence 状態に遷移した時刻。 「VAD Silence 中、 SilencePaddingMs 以内は
    // 無音 PCM (ゼロ埋め PCM16) を継続送信して server に入力継続をアピール」するロジックの起点。
    // 旧 v1.0.26 戦略 (commit 送信 + 強制確定) は分断問題で削除。
    // Silence → InSpeech 遷移でリセット (DateTime.MinValue)。
    // ARC Raiders 等で OpenAI server が delta を保留する問題への client 側対策 (2026-05-24)。
    private DateTime _silenceStartUtc = DateTime.MinValue;
    // 無音 PCM 送信に使い回すフレームバッファ (24kHz/mono PCM16、 768 sample = 1536 bytes)。
    // 毎フレーム new するのを避けるため _silencePaddingPcm16 でキャッシュ。 中身はずっとゼロ。
    private byte[]? _silencePaddingPcm16;
    // 自動 Pause 判定用 (最後に speech と判定された時刻)。
    private DateTime _lastSpeechUtc = DateTime.UtcNow;
    private DateTime _sessionStartUtc = DateTime.UtcNow;
    private double _skippedSecondsByVad;
    private Task? _autoPauseTask;
    private CancellationTokenSource? _autoPauseCts;
    // リアルタイム stats tick: silence で API response が来ない間も UI の経過時間 / VAD 節約秒数を 1 秒毎に更新する。
    // 旧実装は response.done + AutoPause + Stop でしか StatsUpdated が走らず、 「Start 直後の数十秒間 stats が止まる」体感バグだった。
    private Task? _statsTickTask;
    private CancellationTokenSource? _statsTickCts;
    // StartAsync / StopAsync を直列化する semaphore (rere B1-004 対応)。
    // 旧実装は _isRunning フラグだけで状態管理しており、 「Start 中 (ConnectAsync 待機中) に
    // OnSettingsSaved 経由で Stop が呼ばれる」シナリオで pipeline 側 `if (!_isRunning) return;` が
    // 空振りして Channel / Task が orphan として残る経路があった。 全状態遷移をこの semaphore で
    // 直列化することで、 Start と Stop が交差する経路を構造的に消す。
    private readonly SemaphoreSlim _startStopLock = new(1, 1);

    public event EventHandler<SubtitleItem>? SubtitleGenerated;
    public event EventHandler<PipelineStatsEventArgs>? StatsUpdated;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler<AudioLevelEventArgs>? AudioLevelUpdated;

    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;
    // rere B1-003: SettingsService.DecryptApiKeyInPlace の static 直叩き (Service Locator) を
    // ISettingsService 注入経由に置換。 テスト時のモック差し替え可能性が回復。
    private readonly ISettingsService _settingsService;

    // デバッグ録音 (OpenAI 送信前 PCM16 を WAV に書き出す)。 ctor で DI 注入。 null OK (テスト互換)。
    // ライフサイクル: 開始/停止判定は SyncDebugRecording に一元化し、 StartCoreAsync / StopCoreAsync /
    // 設定変更 (IOptionsMonitor.OnChange) の 3 経路から呼ぶ。 これにより「翻訳開始後に WAV 保存を ON/OFF
    // しても即反映」される (旧実装は StartCoreAsync 時に 1 回しか判定せず、 走行中の切替が無視されていた)。
    // OpenAIRealtimeClient.SendAudio 側でも recorder.WritePcm16 を呼ぶが、 録音セッション未開始なら内部で no-op。
    private readonly IDebugAudioRecorder? _debugAudioRecorder;
    // SyncDebugRecording を 3 経路から呼んでも安全なよう直列化するロック。
    private readonly System.Threading.Lock _debugRecordingLock = new();
    // pipeline が録音セッションを開いて WriteFailed を購読中か (= 自分が start させた状態か)。
    private bool _debugRecordingActive;
    // 走行中のデバッグ録音 ON/OFF 切替を即反映するための設定変更購読。 Dispose で解除する。
    private IDisposable? _settingsChangeSubscription;

    // 本番 DI 用: OpenAI/Gemini/Soniox/Speechmatics/Azure の具象 client を注入する。
    public TranslationPipelineService(
        IAudioCaptureService audioCaptureService,
        OpenAIRealtimeClient openAiClient,
        GeminiLiveClient geminiClient,
        SonioxRealtimeClient sonioxClient,
        SpeechmaticsRealtimeClient speechmaticsClient,
        AzureSpeechTranslationClient azureClient,
        IOptionsMonitor<AppSettings> settingsMonitor,
        ISettingsService settingsService,
        IVoiceActivityDetector vad,
        IDebugAudioRecorder? debugAudioRecorder = null)
        : this(audioCaptureService, openAiClient, geminiClient, sonioxClient, speechmaticsClient, azureClient,
               settingsMonitor, settingsService, vad, debugAudioRecorder, sentinel: default)
    {
    }

    // テスト用: 単一の IRealtimeTranscriber モックを全 provider 兼用で受ける
    // (テストは Provider=OpenAI 既定で active=openAi のため他 provider 固有経路は通らない)。
    internal TranslationPipelineService(
        IAudioCaptureService audioCaptureService,
        IRealtimeTranscriber transcriber,
        IOptionsMonitor<AppSettings> settingsMonitor,
        ISettingsService settingsService,
        IVoiceActivityDetector vad,
        IDebugAudioRecorder? debugAudioRecorder = null)
        : this(audioCaptureService, transcriber, transcriber, transcriber, transcriber, transcriber,
               settingsMonitor, settingsService, vad, debugAudioRecorder, sentinel: default)
    {
    }

    // 共通本体 (bool sentinel で上 2 つの ctor とシグネチャを区別する)。
    private TranslationPipelineService(
        IAudioCaptureService audioCaptureService,
        IRealtimeTranscriber openAiClient,
        IRealtimeTranscriber geminiClient,
        IRealtimeTranscriber sonioxClient,
        IRealtimeTranscriber speechmaticsClient,
        IRealtimeTranscriber azureClient,
        IOptionsMonitor<AppSettings> settingsMonitor,
        ISettingsService settingsService,
        IVoiceActivityDetector vad,
        IDebugAudioRecorder? debugAudioRecorder,
        bool sentinel)
    {
        _audioCaptureService = audioCaptureService;
        _openAiClient = openAiClient;
        _geminiClient = geminiClient;
        _sonioxClient = sonioxClient;
        _speechmaticsClient = speechmaticsClient;
        _azureClient = azureClient;
        _activeClient = openAiClient; // 既定 (Start 時に Provider で再選択)
        _clientsByProvider = new Dictionary<TranscriptionProvider, IRealtimeTranscriber>
        {
            [TranscriptionProvider.OpenAI] = openAiClient,
            [TranscriptionProvider.Gemini] = geminiClient,
            [TranscriptionProvider.Soniox] = sonioxClient,
            [TranscriptionProvider.Speechmatics] = speechmaticsClient,
            [TranscriptionProvider.Azure] = azureClient,
        };
        // 購読/解除用の重複排除済み配列。 テストで同一モックを全 provider に渡された場合や、
        // 同一インスタンスが複数 provider を兼ねる場合に二重購読 (delta/done 二重発火) を防ぐ。
        _distinctClients = _clientsByProvider.Values.Distinct().ToArray();
        _settingsMonitor = settingsMonitor;
        _settingsService = settingsService;
        _vad = vad;
        _debugAudioRecorder = debugAudioRecorder;
        var initialOpenAi = settingsMonitor.CurrentValue.OpenAIRealtime;
        _activeMaxPartialChars = initialOpenAi.MaxPartialChars;
        _activeSilencePaddingMs = initialOpenAi.SilencePaddingMs;
        _activeModelForCost = initialOpenAi.Model;
        _throttleTimer = new Timer(OnThrottleTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

        // 両 client のイベントを購読する。 非 active client は Connect しないのでイベントを発火しない
        // (provider 切替は Start を跨ぐため active/非active のイベント混線は起きない)。
        foreach (var client in _distinctClients)
        {
            client.TranscriptDeltaReceived += OnTranscriptDelta;
            client.TranscriptCompleted += OnTranscriptCompleted;
            client.ErrorReceived += OnClientError;
            client.StateChanged += OnConnectionStateChanged;
        }

        // 翻訳開始後にデバッグ録音 (WAV 保存) を ON/OFF しても即反映されるよう設定変更を購読する。
        // 走行中なら SyncDebugRecording が録音セッションを開始/停止する (非走行中は no-op)。
        _settingsChangeSubscription = _settingsMonitor.OnChange((_, _) => SyncDebugRecording());
    }


    public async Task StartAsync(CancellationToken token)
    {
        // rere B1-004: Start ↔ Stop の TOCTOU を消すため semaphore で全初期化を直列化。
        // Stop が並行に走った場合は Stop が完了するまでブロック → Start は全部完了済み状態から走り直す。
        await _startStopLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_isRunning) return;
            await StartCoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken token)
    {
        // settings.json で変更したばかりの内容 (Provider / OutputLanguage 等) が反映されるよう、
        // 起動直前に IOptionsMonitor から最新値を取り直す。 DPAPI 暗号化された API キーも復号して使う。
        _settingsService.DecryptApiKey(_settingsMonitor.CurrentValue);
        var appSettings = _settingsMonitor.CurrentValue;
        var provider = appSettings.Provider;

        // hot-swap: Provider に応じて active client を選ぶ。 走行中は切り替わらない
        // (設定変更は次の Start で反映)。 送信レート分岐は _activeClient.InputSampleRate で行う。
        _activeClient = _clientsByProvider.TryGetValue(provider, out var picked) ? picked : _openAiClient;

        var freshSettings = appSettings.OpenAIRealtime;
        // active provider の設定 (APIキー / 出力言語 / D-7 分割閾値 / 無音 padding / cost モデル名) を
        // Start 時にスナップショット。 走行中の設定変更や provider 切替で現セッションが引きずられないように
        // する (CodeRabbit 指摘)。 cost モデル名は CostEstimator のキーワード判定に合わせた文字列
        // (Speechmatics / Azure は専用 Model フィールドを持たないため合成キーを渡す)。
        var (apiKey, outputLanguage, modelForCost, maxPartialChars, silencePaddingMs, providerLabel) = provider switch
        {
            TranscriptionProvider.Gemini => (appSettings.Gemini.ApiKey, appSettings.Gemini.OutputLanguage,
                appSettings.Gemini.Model, appSettings.Gemini.MaxPartialChars, appSettings.Gemini.SilencePaddingMs, "Gemini"),
            TranscriptionProvider.Soniox => (appSettings.Soniox.ApiKey, appSettings.Soniox.OutputLanguage,
                appSettings.Soniox.Model, appSettings.Soniox.MaxPartialChars, appSettings.Soniox.SilencePaddingMs, "Soniox"),
            TranscriptionProvider.Speechmatics => (appSettings.Speechmatics.ApiKey, appSettings.Speechmatics.OutputLanguage,
                "speechmatics-rt", appSettings.Speechmatics.MaxPartialChars, appSettings.Speechmatics.SilencePaddingMs, "Speechmatics"),
            TranscriptionProvider.Azure => (appSettings.Azure.ApiKey, appSettings.Azure.OutputLanguage,
                "azure-translation", appSettings.Azure.MaxPartialChars, appSettings.Azure.SilencePaddingMs, "Azure"),
            _ => (freshSettings.ApiKey, freshSettings.OutputLanguage,
                freshSettings.Model, freshSettings.MaxPartialChars, freshSettings.SilencePaddingMs, "OpenAI"),
        };
        _activeMaxPartialChars = maxPartialChars;
        _activeSilencePaddingMs = silencePaddingMs;
        _activeModelForCost = modelForCost;

        // API キー検証 (provider 別)。
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var ex = new InvalidOperationException($"{providerLabel} APIキーが設定されていません。設定画面でキーを入力してください。");
            ErrorOccurred?.Invoke(this, ex);
            throw ex;
        }

        Logger.Info($"翻訳パイプライン開始 (Provider={provider}, OutputLanguage='{outputLanguage}', Model='{modelForCost}')");

        // 前セッションの累積 transcript / SegmentId をリセットしないと、 再 Start 時に
        // 「前回 done で確定したテキストを prefix として保持」した状態から始まり、
        // 新セッションの最初の done で全文が emit されない (or 重複検知される) 不具合になる。
        lock (_textLock)
        {
            _lastFinalizedTranscript.Clear();
            _accumulatedText.Clear();
            _recentEmittedSentences.Clear();
            _currentSegmentId = Guid.NewGuid().ToString();
            _lastEmitTime = DateTime.MinValue;
            _hasPendingDelta = false;
        }

        // 接続 (provider 別)。 各 provider は具象 client の固有メソッドで専用 Settings を直接渡す
        // (OpenAIRealtimeSettings 経由のマッピングだと Endpoint/Model/源言語等が落ちるため)。 テスト時は
        // モック (具象型ではない) なので default 経路に落ち、 active モックへ OpenAIRealtimeSettings で接続する
        // (テストは Provider=OpenAI 既定)。 rere B1-011: Core layer 統一で ConfigureAwait(false) 付与。
        switch (provider)
        {
            case TranscriptionProvider.Gemini when _geminiClient is GeminiLiveClient gemini:
                await gemini.ConnectAsync(appSettings.Gemini, token).ConfigureAwait(false);
                break;
            case TranscriptionProvider.Soniox when _sonioxClient is SonioxRealtimeClient soniox:
                await soniox.ConnectAsync(appSettings.Soniox, token).ConfigureAwait(false);
                break;
            case TranscriptionProvider.Speechmatics when _speechmaticsClient is SpeechmaticsRealtimeClient speechmatics:
                await speechmatics.ConnectAsync(appSettings.Speechmatics, token).ConfigureAwait(false);
                break;
            case TranscriptionProvider.Azure when _azureClient is AzureSpeechTranslationClient azure:
                await azure.ConnectAsync(appSettings.Azure, token).ConfigureAwait(false);
                break;
            default:
                await _activeClient.ConnectAsync(freshSettings, token).ConfigureAwait(false);
                break;
        }

        // WASAPI コールバックスレッドで重い変換を行うと audio glitch の原因になるため、
        // Channel に raw float[] を投入だけして変換は専用タスクで行う。
        // 容量 15: 1 chunk ≒ 80ms 想定で約 1.2 秒分のバッファ。
        // 送信チャンネル (30) と合わせて合計 ~3.6 秒の最大パイプライン遅延に抑える。
        // VAD 処理 (~1ms/frame) や GC pause (~10ms) に対して十分な余裕がある。
        _audioInputChannel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(15)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        _audioProcessingCts = new CancellationTokenSource();
        _audioProcessingTask = Task.Run(() => ProcessAudioLoopAsync(_audioProcessingCts.Token));

        // VAD ゲート初期化 (新セッション開始時に状態を完全リセット)。
        // 旧セッションの hidden state / preroll が残っていると、 開始直後の判定が
        // ブレるので必ずここで初期化する。
        _vad.Reset();
        // 2 系統リサンプラのフィルタ状態 + 未消費入力を完全クリア (前セッションの残響を持ち越さない)。
        _vadResampler.Reset();
        _sendResampler.Reset();
        // VAD 切替検知の基準値を初期化 (B1-001 / D-I3 対応)。 起動直後の不要 Reset を避ける。
        _lastEnableVad = _settingsMonitor.CurrentValue.AudioCapture.EnableVad;
        // 入力プリプロセス DSP の envelope follower を同様にクリア
        // (前セッション末尾の大音量に追従していた gain が新セッションに漏れないように)。
        _inputGain.Reset();
        _preRollBuffer.Clear();
        _vadState = VadState.Silence;
        _hangoverFramesRemaining = 0;
        _silenceStartUtc = DateTime.MinValue;
        _frameAccumulator = null;
        _frameAccumulatorLen = 0;
        _frameAccumulator24k = null;
        _frameAccumulator24kLen = 0;
        // provider 切替で送信フレーム長 (16k=1024B / 24k=1536B) が変わるため、 無音 padding バッファの
        // キャッシュを破棄して次フレームで正しいサイズに作り直させる (Plan 検証リスク 1)。
        _silencePaddingPcm16 = null;
        _skippedSecondsByVad = 0;
        _sessionStartUtc = DateTime.UtcNow;
        _lastSpeechUtc = DateTime.UtcNow;

        // 自動 Pause 監視ループ起動 (5 秒間隔ポーリング、 設定で AutoPauseOnSilenceSec=0 なら no-op)。
        _autoPauseCts = new CancellationTokenSource();
        _autoPauseTask = Task.Run(() => AutoPauseLoopAsync(_autoPauseCts.Token));

        // リアルタイム stats tick 起動 (1 秒周期で UI に経過時間 / トークン / VAD 節約秒数を反映)。
        _statsTickCts = new CancellationTokenSource();
        _statsTickTask = Task.Run(() => StatsTickLoopAsync(_statsTickCts.Token));

        _audioCaptureService.AudioDataAvailable += OnAudioDataAvailable;
        _isRunning = true;
        _latencyStopwatch.Start();

        // デバッグ録音 (WAV 保存): ON なら録音セッションを開く。 走行中の ON/OFF 切替にも追従できるよう、
        // 開始/停止判定は SyncDebugRecording に一元化している (設定変更の OnChange からも呼ばれる)。
        SyncDebugRecording();

        StatsUpdated?.Invoke(this, new PipelineStatsEventArgs
        {
            StatusText = "API接続完了",
            SessionDuration = TimeSpan.Zero,
            InputAudioTokensEstimate = 0,
            EstimatedCostUsd = 0m,
            SkippedSecondsByVad = 0,
        });
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // rere B1-004: Start 中の Stop 呼び出し (OnSettingsSaved 経由等) も含めて直列化。
        // Start が走っている最中は完了を待ってから Stop を実行する。
        await _startStopLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isRunning) return;
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Logger.Info("翻訳パイプライン停止");
        // 未確定のまま残っている trailing を確定字幕として emit + ログ記録してから停止する
        // (停止時に「未確定の文字が確定されず消える」データロスを防ぐ)。
        FinalizePendingPartial("停止");
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;
        _isRunning = false;
        _latencyStopwatch.Stop();
        _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);

        // ⭐ WASAPI ネイティブ解放を UI スレッドから外す。
        // AudioCaptureService.StopCapture() は内部で NAudio の WasapiCapture.StopRecording +
        // Dispose を同期実行する。 これらは native callback スレッド完了待ち (WaitForSingleObject 系)
        // を含むため、 UI スレッドから直接呼ぶと「停止ボタン押下でアプリ全体フリーズ」になる
        // (2026-05-17 ゆろさん環境で観測)。 Task.Run + WaitAsync(3s) で別スレッドに逃がし、
        // タイムアウト時もログを残してフリーズを防ぐ。
        try
        {
            await Task.Run(() => _audioCaptureService.StopCapture())
                      .WaitAsync(TimeSpan.FromSeconds(3))
                      .ConfigureAwait(false);
            Logger.Info("audio キャプチャ停止 完了");
        }
        catch (TimeoutException)
        {
            Logger.Warn("audio キャプチャ停止が 3 秒を超過 (バックグラウンドで継続)");
        }
        catch (Exception ex)
        {
            Logger.Warn("audio キャプチャ停止中の例外", ex);
        }

        // audio 処理タスクの停止: writer 完了 → cts キャンセル → タスク完了待ち
        _audioInputChannel?.Writer.TryComplete();
        _audioProcessingCts?.Cancel();
        if (_audioProcessingTask is { } task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { Logger.Warn("audio 処理ループ停止がタイムアウト"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warn("audio 処理ループ停止中の例外", ex); }
        }
        _audioProcessingTask = null;
        _audioProcessingCts?.Dispose();
        _audioProcessingCts = null;
        _audioInputChannel = null;

        // 自動 Pause 監視ループの停止 (audio 処理停止と独立にキャンセル → 完了待ち)。
        _autoPauseCts?.Cancel();
        if (_autoPauseTask is { } apTask)
        {
            try { await apTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { Logger.Warn("自動 Pause タスク停止がタイムアウト"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warn("自動 Pause タスク停止中の例外", ex); }
        }
        _autoPauseTask = null;
        _autoPauseCts?.Dispose();
        _autoPauseCts = null;

        // リアルタイム stats tick の停止 (Stop ボタン押下後は更新不要)。
        _statsTickCts?.Cancel();
        if (_statsTickTask is { } stTask)
        {
            try { await stTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { Logger.Warn("stats tick タスク停止がタイムアウト"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warn("stats tick タスク停止中の例外", ex); }
        }
        _statsTickTask = null;
        _statsTickCts?.Dispose();
        _statsTickCts = null;

        // WebSocket 切断も外部待機を入れて UI スレッドに戻る前に確実に完了させる
        try
        {
            await _activeClient.DisconnectAsync()
                  .WaitAsync(TimeSpan.FromSeconds(3))
                  .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logger.Warn("WebSocket 切断が 3 秒を超過 (バックグラウンドで継続)");
        }
        catch (Exception ex)
        {
            Logger.Warn("WebSocket 切断中の例外", ex);
        }

        // ⭐ rere P2 F-7: セッション統計を確実にログに残し、 NW 詰まり指標を可視化。
        // DroppedAudioChunkCount はキャプチャ → API 送信の Channel<byte[]>(100) で
        // BoundedChannelFullMode.DropOldest によって捨てられた音声チャンク累計。
        // 「字幕が抜ける」報告時に「ローカル NW 詰まりか OpenAI 側遅延か」を切り分ける。
        Logger.Info($"セッション統計: DroppedAudioChunks={_activeClient.DroppedAudioChunkCount} 完結文emit={Interlocked.Read(ref _completedEmitCount)} partial emit={Interlocked.Read(ref _partialEmitCount)}");

        // デバッグ録音セッションを必ず閉じる: ここでは _isRunning は既に false なので SyncDebugRecording が
        // StopSession (WAV ヘッダのサイズ確定書き換え) + WriteFailed 購読解除を行う (idempotent、 未開始なら no-op)。
        SyncDebugRecording();

        Logger.Info("翻訳パイプライン停止 完了");

        StatsUpdated?.Invoke(this, BuildCurrentStats("停止"));
    }

    private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (!_isRunning) return;

        // WASAPI コールバックスレッド（MMCSS）で重い処理を行うと音声バッファが overflow して
        // audio glitch / Silent パケット化を起こすため、Channel に投入するだけで即座に戻る。
        // BoundedChannel(25, DropOldest) で詰まり時は古いものを捨てる（再接続復帰後は新しい音声を優先）。
        _audioInputChannel?.Writer.TryWrite(e.AudioData);
    }

    /// <summary>
    /// WASAPI とは別のスレッドで audio chunks を消費し、resample + PCM16 変換 + 送信を行う。
    /// </summary>
    private async Task ProcessAudioLoopAsync(CancellationToken ct)
    {
        var reader = _audioInputChannel?.Reader;
        if (reader is null) return;

        try
        {
            await foreach (var audioData in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var audioCaptureSettings = _settingsMonitor.CurrentValue.AudioCapture;

                    // /rere B1-001 / D-I3 対応: VAD 有効/無効の hot-reload 切替を検知して
                    // _vadResampler の filter state を Reset する。 これにより VAD 無効パスでの
                    // 「戻り値捨て Resample」 (CPU 浪費) が不要になる。 切替直後の数フレームは
                    // VAD 判定がやや不安定だが、 PreRoll (default 800ms) で吸収される。
                    if (audioCaptureSettings.EnableVad != _lastEnableVad)
                    {
                        // OpenAI は VAD 無効パスで _vadResampler を回さない (送信は _sendResampler) ため、
                        // 有効へ戻したとき filter state が古い → Reset する。 Gemini は VAD 有効/無効
                        // どちらも _vadResampler を送信に使うので Reset 不要 (連続性を保つ)。
                        if (_activeClient.InputSampleRate != 16000)
                            _vadResampler.Reset();
                        _lastEnableVad = audioCaptureSettings.EnableVad;
                    }

                    // ─── 入力プリプロセス DSP (v1.0.30 新規、 v1.0.32 で 4 段 → 3 段、 v1.0.36 で 3 段 → 2 段に削減) ───
                    // 設定 InputGainDb / EnableAntiClip を毎 chunk 同期 (hot-reload 対応)。
                    // 全 default (false / 0dB) なら各 Process は内部の IsEnabled で即 return するため
                    // CPU オーバーヘッドはチェック分のみ。 信号フローは InputGain → AntiClip。
                    var preproc = audioCaptureSettings.Preprocessing;
                    _inputGain.GainDb = preproc.InputGainDb;
                    var preprocSpan = audioData.AsSpan();
                    _inputGain.Process(preprocSpan);

                    // ゲイン適用後のピークレベルを UI のレベルメーターへ通知 (約 50ms スロットル)。
                    ReportAudioLevel(preprocSpan);

                    if (!audioCaptureSettings.EnableVad)
                    {
                        // VAD 無効 素通し送信。 provider 別の送信レート:
                        //   OpenAI: 48k → 24k 直 (_sendResampler、 Nyquist 12kHz)
                        //   Gemini: 48k → 16k (_vadResampler、 Gemini 入力 16kHz 固定)
                        var resampledSend = _activeClient.InputSampleRate == 16000
                            ? _vadResampler.Resample(audioData)
                            : _sendResampler.Resample(audioData);
                        if (resampledSend.Length > 0)
                        {
                            var pcm16 = AudioFormatConverter.Float32ToPcm16(resampledSend);
                            _activeClient.SendAudio(pcm16);
                        }
                    }
                    else
                    {
                        // VAD 有効: 48kHz raw audio を 16k (VAD判定用) + 24k (送信用) に同時リサンプル
                        // → 32ms 単位でフレームペア化 → VAD speech 判定区間のみ 24k フレーム送信。
                        // PreRoll + Hangover で発話冒頭/末尾の取りこぼし防止。
                        ProcessAudioWithVadGate(audioData, audioCaptureSettings);
                    }
                }
                catch (Exception ex)
                {
                    // エラーログを1秒に1回に制限（高頻度の音声イベントでログが溢れることを防止）
                    var now = DateTime.UtcNow;
                    if ((now - _lastAudioErrorLogTime).TotalSeconds >= 1.0)
                    {
                        _lastAudioErrorLogTime = now;
                        Logger.Error("音声データ変換エラー", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* StopAsync 経由の停止 */ }
        catch (Exception ex)
        {
            Logger.Error("audio 処理ループ予期しないエラー", ex);
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private void OnTranscriptDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;

        // 完結文をロック外で emit するため、 ロック内ではプランだけ作って退出する。
        // 完結文が出るのは句点到達時だけ (大多数の delta は空のまま)。 lazy 確保で
        // 毎 delta の空 List alloc を排除する。
        List<(string segmentId, string sentence)>? completedSentences = null;
        bool emitPartial = false;

        lock (_textLock)
        {
            // O(n²) → O(n) 最適化: 新規追加部分のみを走査する。
            // appendStart = 追加前の累積長。 scanFrom = 走査開始位置 (直前文字がピリオド小数点保護に必要なため -1)。
            int appendStart = _accumulatedText.Length;
            _accumulatedText.Append(delta);
            int totalLen = _accumulatedText.Length;

            // v1.0.27: 新セグメントの最初の delta を受信した瞬間を明示ログ (デバッグ用)。
            // 旧 v1.0.24-26 の partial 連結方式 (_segmentStartUtc + 最大寿命タイマー) は server gap 対応の
            // 副作用 (分断) があり削除。 v1.0.27 では「無音 PCM 送信で server を起こす」根本対策に置換。
            if (appendStart == 0 && totalLen > 0)
            {
                _lastLoggedPartialLength = -1; // 新セグメントで partial ログをリセット
                Logger.Info($"新セグメント開始: SegmentId={ShortSegmentId(_currentSegmentId)}");
            }

            if (!_latencyStopwatch.IsRunning)
                _latencyStopwatch.Restart();

            // ⭐ OpenAI Realtime Translation API は transcript.done を送ってこない
            // ケースがある (2026-05-17 観測: delta のみで会話が進み done が来ない)。
            // done を待つ設計だと SegmentId が永久に固定され「字幕が無限成長する」UX バグ
            // になるため、 delta 受信時にも _accumulatedText 内の句点を検出して
            // 完結文ごとに emit + SegmentId 切り替えを行う。
            //
            // 走査範囲: appendStart - 1 から totalLen まで (直前文字のピリオド小数点保護のため -1)。
            // StringBuilder.indexer は O(1) なので ToString() 不要。
            int start = 0;
            int scanFrom = Math.Max(0, appendStart - 1);
            for (int i = scanFrom; i < totalLen; i++)
            {
                if (IsSentenceBoundaryAt(_accumulatedText, i, totalLen, isFinalContext: false))
                {
                    // StringBuilder から部分文字列を切り出す (完結文 1 件分のみなので小さい)
                    var sentence = _accumulatedText.ToString(start, i + 1 - start);
                    if (!string.IsNullOrWhiteSpace(sentence))
                    {
                        // 類似重複抑制: Translation API が重複音声セグメントを再翻訳した際の
                        // 近似テキスト (例: "実は" が有無だけ異なる文) を抑制する。
                        if (!IsSimilarToRecentEmission(sentence))
                        {
                            (completedSentences ??= new()).Add((_currentSegmentId, sentence));
                            RecordEmission(sentence);
                        }
                        else
                        {
                            Logger.Info($"OnTranscriptDelta: 類似重複抑制 SegmentId={ShortSegmentId(_currentSegmentId)} 内容='{TruncateForLog(sentence)}'");
                        }
                        _currentSegmentId = Guid.NewGuid().ToString();
                        _lastFinalizedTranscript.Append(sentence);
                    }
                    start = i + 1;
                }
            }

            // v1.0.31 fix: 文境界が見つかった (start > 0) なら、 emit / 抑制問わず必ず
            // _accumulatedText から「処理済み区間」を削除する。
            //
            // 旧実装は `if (completedSentences is { Count: > 0 })` の中でだけ Remove していたため、
            // **類似重複抑制 only** で完結文が 1 件も emit されないケースで _accumulatedText に
            // 「抑制された文」が残り、 次回 delta で「抑制文 + 続きの partial」が新 SegmentId の
            // partial として漏れる UX バグになっていた (2026-05-26 23:53 実機観測、
            // 音量正規化 ON で server VAD が短い発話を複数イベントで返す頻度上昇により顕在化:
            // 「ベン・ウォルターズでさえ、...限度がある。」を 3 連続抑制してから「言い...」と続き、
            // 字幕として「同じ翻訳が複数 SegmentId で重複表示」される状態だった)。
            //
            // D-7 fallback 経路 (下の while ループ) は line 653 で常時 Remove しているため
            // 同型バグなし。 本ブロックを D-7 と同じ流儀に揃える。
            if (start > 0)
            {
                _accumulatedText.Remove(0, start);
                _lastEmitTime = DateTime.UtcNow;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                emitPartial = _accumulatedText.Length > 0;
            }
            else
            {
                // 文境界が一つも見つからなかった → 従来の throttled partial emit 経路
                var now = DateTime.UtcNow;
                if (now - _lastEmitTime >= DeltaThrottle)
                {
                    emitPartial = true;
                }
                else if (!_hasPendingDelta)
                {
                    _hasPendingDelta = true;
                    _throttleTimer.Change(DeltaThrottle, Timeout.InfiniteTimeSpan);
                }
            }

            // === D-7 fallback (v1.0.28 復活、 /rere #D-005 対策) ===
            // 句点を一切入れない server 出力 (ARC Raiders のセリフ等で 2026-05-24 実機観測) で
            // partial が永遠に伸び続けて「育ちすぎて途切れない」UX 破綻を防ぐ最終安全弁。
            // 完結文 emit の後に判定するため、 既に句点で切れている trailing 部分のみが対象。
            // _cachedRealtimeSettings.MaxPartialChars (default 80) 文字を超えたら強制分割。
            //
            // while ループ化の理由: 1 delta で巨大な (例: 100,000 文字) テキストが流れ込んだ場合、
            // 1 回切るだけだと残り 99,920 文字が partial として残り「育ちすぎる」問題が残存する。
            // ループで _accumulatedText.Length が maxChars 未満になるまで切り続ける。
            // 各 iteration は O(1) のスキャン + O(分割サイズ) の Remove なので、 全体は O(N)。
            int maxChars = _activeMaxPartialChars;
            while (maxChars > 0 && _accumulatedText.Length >= maxChars)
            {
                int splitIdx = FindForcedSplitIndex(_accumulatedText, maxChars);
                if (splitIdx <= 0 || splitIdx > _accumulatedText.Length)
                {
                    // 防御: FindForcedSplitIndex は本来 1〜sb.Length を返すが、
                    // 万一 0 や範囲外を返したら無限ループを防ぐため break。
                    break;
                }

                var forcedSentence = _accumulatedText.ToString(0, splitIdx);
                if (string.IsNullOrWhiteSpace(forcedSentence))
                {
                    // 空白のみの fragment は emit せず捨てる。
                    // /rere 第2R #B1-R2-003 (v1.0.29 候補): _lastFinalizedTranscript.Append を必ず実行する。
                    // 旧実装は emit skip と同時に Append も skip していたが、 server transcript には空白も含まれて
                    // 累積されているため、 client 側で append しないと OnTranscriptCompleted の prefix 比較で
                    // StringBuilderIsPrefixOf が false → done 経路全 skip → 字幕完全停止リスク。
                    // emit はしないが累積整合性のため Append は必須。
                    _lastFinalizedTranscript.Append(forcedSentence);
                    _accumulatedText.Remove(0, splitIdx);
                    continue;
                }

                if (!IsSimilarToRecentEmission(forcedSentence))
                {
                    (completedSentences ??= new()).Add((_currentSegmentId, forcedSentence));
                    RecordEmission(forcedSentence);
                    Logger.Info($"D-7 fallback: 句点なし {forcedSentence.Length} 文字超過 (>= {maxChars}) のため強制分割。 SegmentId={ShortSegmentId(_currentSegmentId)} 末尾='{TruncateForLog(forcedSentence)}'");
                }
                else
                {
                    Logger.Info($"D-7 fallback (類似重複抑制): SegmentId={ShortSegmentId(_currentSegmentId)} 内容='{TruncateForLog(forcedSentence)}'");
                }
                _currentSegmentId = Guid.NewGuid().ToString();
                _lastFinalizedTranscript.Append(forcedSentence);
                _accumulatedText.Remove(0, splitIdx);
                _lastEmitTime = DateTime.UtcNow;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                emitPartial = _accumulatedText.Length > 0;
            }
        }

        // ロック外で完結文を emit。
        if (completedSentences is not null)
        {
            foreach (var (segmentId, sentence) in completedSentences)
            {
                var n = Interlocked.Increment(ref _completedEmitCount);
                Logger.Info($"完結文 emit (delta経路) #{n}: SegmentId={ShortSegmentId(segmentId)} 長さ={sentence.Length} 内容='{TruncateForLog(sentence)}'");

                var subtitle = new SubtitleItem
                {
                    SegmentId = segmentId,
                    OriginalText = string.Empty,
                    TranslatedText = sentence,
                    IsFinal = true,
                };
                SubtitleGenerated?.Invoke(this, subtitle);
            }
        }

        if (emitPartial)
        {
            lock (_textLock)
            {
                EmitPartialSubtitle();
            }
        }
    }

    private void OnThrottleTimerElapsed(object? state)
    {
        lock (_textLock)
        {
            if (_hasPendingDelta)
                EmitPartialSubtitle();
        }
    }

    private void EmitPartialSubtitle()
    {
        _lastEmitTime = DateTime.UtcNow;
        _hasPendingDelta = false;

        var partialText = _accumulatedText.ToString();
        var segmentId = _currentSegmentId;

        // /rere 第2R #C1-R2-002 (v1.0.29 候補): 同長 & 同 SegmentId の partial 再 emit を skip する。
        // 30ms throttle + OnThrottleTimerElapsed の組み合わせで「内容が変わってないのに再 emit」が発火する
        // ケース (D-7 fallback で partial 経路増加した v1.0.28 以降特に顕著) で、 ToString() の O(L) コピー +
        // SubtitleGenerated 発火 + 下流 UI binding equality 比較 (フル走査) を全部 skip して GC pressure を削減。
        // 60 分セッション 60k emit のうち重複分を概ね 1/3 削減見込み。
        if (partialText.Length == _lastEmittedPartialLength && segmentId == _lastEmittedPartialSegmentId)
        {
            return;
        }
        _lastEmittedPartialLength = partialText.Length;
        _lastEmittedPartialSegmentId = segmentId;

        var subtitle = new SubtitleItem
        {
            SegmentId = segmentId,
            OriginalText = partialText,
            TranslatedText = "",
            IsFinal = false
        };

        SubtitleGenerated?.Invoke(this, subtitle);

        // v1.0.25 デバッグ用ログ密度: 「累積長が伸びた時のみ Info」+「ShouldLogAtCount に該当する節目」も合わせて出力。
        // partial 連結方式の挙動 (「そ → そし → そして → ...」の成長過程) を画面 ↔ ログで突き合わせ可能にする。
        // 同内容の再 emit (throttle で再発火など) はログから除外して爆発を防止。
        var count = Interlocked.Increment(ref _partialEmitCount);
        bool grew = partialText.Length != _lastLoggedPartialLength;
        if (grew || ShouldLogAtCount(count))
        {
            Logger.Info($"partial emit #{count}: SegmentId={ShortSegmentId(segmentId)} 累積長={partialText.Length} 内容='{TruncateForLog(partialText)}'");
            _lastLoggedPartialLength = partialText.Length;
        }
    }

    /// <summary>
    /// 未確定の trailing (_accumulatedText) を確定字幕 (IsFinal=true) として emit + ログ記録し、 状態を進める。
    /// v1.0.27 から 停止時 (StopCoreAsync) のみから呼ばれる (旧 v1.0.18 アイドル確定 / v1.0.24 最大寿命タイマーは削除済み)。
    /// 空 / 空白のみのときは何もしない。 SegmentId は partial 表示時と同一にして overlay 側の partial を確定表示に置換させる。
    /// </summary>
    private void FinalizePendingPartial(string reason)
    {
        string sentence;
        string segmentId;
        bool shouldEmit;
        lock (_textLock)
        {
            sentence = _accumulatedText.ToString();
            if (string.IsNullOrWhiteSpace(sentence)) return;

            segmentId = _currentSegmentId;

            // 類似重複抑制
            shouldEmit = !IsSimilarToRecentEmission(sentence);
            if (shouldEmit) RecordEmission(sentence);

            // 確定累積へ反映 (後続 done の prefix 一致を維持するため、 delta 経路の確定と同じ扱い)。
            _lastFinalizedTranscript.Append(sentence);
            _accumulatedText.Clear();
            _currentSegmentId = Guid.NewGuid().ToString();
            _hasPendingDelta = false;
            _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (!shouldEmit)
        {
            Logger.Info($"未確定文の類似重複抑制 ({reason}): SegmentId={ShortSegmentId(segmentId)} 長さ={sentence.Length} 内容='{TruncateForLog(sentence)}'");
            return;
        }

        Logger.Info($"未確定文を確定 emit ({reason}): SegmentId={ShortSegmentId(segmentId)} 長さ={sentence.Length} 内容='{TruncateForLog(sentence)}'");

        // ロック外で emit (SubtitleGenerated ハンドラ側の再入によるデッドロックを避ける)。
        SubtitleGenerated?.Invoke(this, new SubtitleItem
        {
            SegmentId = segmentId,
            OriginalText = string.Empty,
            TranslatedText = sentence,
            IsFinal = true,
        });
    }

    private void OnTranscriptCompleted(string transcript)
    {
        // 確定字幕として出すべき「新規ぶん」と、 各文の SegmentId を計算して
        // ロックの外で emit するため、 lock 内ではプランだけ作って終わる。
        List<(string segmentId, string text, bool isSentenceEnd)> emissions;
        double latencyMs;
        int newPortionLength;

        lock (_textLock)
        {
            latencyMs = _latencyStopwatch.Elapsed.TotalMilliseconds;

            // API から transcript が空で done が来た場合 (response.done fallback 経路など) は
            // 直前まで delta で蓄積してきた _accumulatedText を確定字幕として使う。
            // transcript が空でなければ「セッション累積の全文」が来ている前提で扱う。
            string effective;
            if (string.IsNullOrEmpty(transcript))
            {
                // fallback 経路 (response.done で transcript が空): 既存の確定累積 + 直前の delta 蓄積。
                // この経路は稀なので ToString() の O(N) コストは許容。
                effective = _lastFinalizedTranscript.ToString() + _accumulatedText.ToString();
            }
            else
            {
                effective = transcript;
            }

            // ⚠️ rere レビュー P0 #1 修正: prefix 不一致時の「effective 全体を newPortion 扱い」
            // を廃止し、 skip + Warn ログに倒す。
            //
            // 旧設計では `effective.StartsWith(_lastFinalizedTranscript) ? slice : effective` で
            // 不一致時に effective 全体を newPortion として再 emit していたが、 これは
            // 「_lastFinalizedTranscript と effective に重複する部分」を含めて再 emit し、
            // 新 SegmentId で overlay に追加されるため重複字幕を作る経路があった。
            // 「重複表示よりも欠落のほうがマシ」方針で skip に倒す。
            // 再接続経由の不一致は OnConnectionStateChanged のリセットで吸収するので、
            // ここに来るのは「API 側で transcript 正規化が入った」「順序入れ替わり」等の
            // 稀ケースのみで実質ゼロ件のはず。
            if (!StringBuilderIsPrefixOf(_lastFinalizedTranscript, effective))
            {
                Logger.Warn($"OnTranscriptCompleted: prefix 不整合検出 — done 経路スキップ (finalized 長={_lastFinalizedTranscript.Length} effective 長={effective.Length} finalized 末尾='{TruncateForLog(TailOf(_lastFinalizedTranscript, 20), 20)}' effective 先頭='{TruncateForLog(effective, 30)}')");
                _accumulatedText.Clear();
                _lastEmitTime = DateTime.MinValue;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _latencyStopwatch.Reset();
                return;
            }

            string newPortion = effective[_lastFinalizedTranscript.Length..];
            newPortionLength = newPortion.Length;

            if (string.IsNullOrEmpty(newPortion))
            {
                // 累積 done が来たが新規ぶんが無い (同 transcript を 2 回受信した等)
                // → emit すべきものがない。 状態だけリセットして抜ける。
                _accumulatedText.Clear();
                _lastEmitTime = DateTime.MinValue;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _latencyStopwatch.Reset();
                Logger.Debug("OnTranscriptCompleted: 新規ぶんなし。emit スキップ");
                return;
            }

            // newPortion を句点で分割して、 完結した文ごとに emit する。
            emissions = new List<(string, string, bool)>();
            int start = 0;
            for (int i = 0; i < newPortion.Length; i++)
            {
                // done は確定文脈なので isFinalContext=true: 末尾ピリオド + 直前数字でも遠慮なく区切る
                // (次に来る delta はないため保留する意味がない)。 ただし「6.3インチ」のような
                // 中間の数字小数点は引き続き保護される。
                if (IsSentenceBoundaryAt(newPortion, i, isFinalContext: true))
                {
                    // start..i が 1 文 (句点含む) として完結
                    var sentence = newPortion[start..(i + 1)];
                    if (!string.IsNullOrWhiteSpace(sentence))
                    {
                        if (!IsSimilarToRecentEmission(sentence))
                        {
                            emissions.Add((_currentSegmentId, sentence, true));
                            RecordEmission(sentence);
                        }
                        else
                        {
                            Logger.Info($"OnTranscriptCompleted: 類似重複抑制 内容='{TruncateForLog(sentence)}'");
                        }
                        _currentSegmentId = Guid.NewGuid().ToString();
                        // 確定累積を完結文ぶんだけ進める (trailing は次回更新時に再登場させる)
                        _lastFinalizedTranscript.Append(sentence);
                    }
                    start = i + 1;
                }
            }
            // 残りの未完結部分 (trailing) は _currentSegmentId で emit。
            if (start < newPortion.Length)
            {
                var trailing = newPortion[start..];
                if (!string.IsNullOrWhiteSpace(trailing))
                {
                    emissions.Add((_currentSegmentId, trailing, false));
                }
            }

            // partial 表示用の delta 蓄積はクリア (次の done までの partial 用に再使用)
            _accumulatedText.Clear();
            _lastEmitTime = DateTime.MinValue;
            _hasPendingDelta = false;
            _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _latencyStopwatch.Reset();
        }

        // 概況ログ: done 経路で何が起きたかをまとめて 1 行で出す。
        int completedCount = emissions.Count(e => e.isSentenceEnd);
        int trailingCount = emissions.Count(e => !e.isSentenceEnd);
        Logger.Info($"OnTranscriptCompleted: newPortion長={newPortionLength} 完結文={completedCount}件 trailing={trailingCount}件 latency={latencyMs:F0}ms");

        // ロック外で emit。
        foreach (var (segmentId, text, isSentenceEnd) in emissions)
        {
            if (isSentenceEnd)
            {
                var n = Interlocked.Increment(ref _completedEmitCount);
                Logger.Info($"完結文 emit (done経路) #{n}: SegmentId={ShortSegmentId(segmentId)} 長さ={text.Length} 内容='{TruncateForLog(text)}'");
            }

            var subtitle = new SubtitleItem
            {
                SegmentId = segmentId,
                OriginalText = string.Empty,
                TranslatedText = text,
                IsFinal = true,
            };
            SubtitleGenerated?.Invoke(this, subtitle);
        }

        // 累積フィールド (Tokens/Cost/SessionDuration/SkippedSecondsByVad) を 0 上書きしないよう BuildCurrentStats で埋める。
        var doneStats = BuildCurrentStats($"翻訳完了 ({latencyMs:F0}ms)");
        doneStats.ProcessingLatency = latencyMs;
        doneStats.TranslationLatency = latencyMs;
        StatsUpdated?.Invoke(this, doneStats);
    }

    private void OnClientError(Exception ex)
    {
        Logger.Error("OpenAI Realtime クライアントエラー", ex);
        ErrorOccurred?.Invoke(this, ex);

        // 2026-05-17 ゆろさんログ起点の修正:
        // OpenAI API の致命的エラー (Quota / InvalidApiKey / Forbidden) は再接続しても回復しないため、
        // パイプラインを止めて音声キャプチャも即停止する。 OS から音声を取りっぱなしになる経路や、
        // 無駄な Channel 蓄積を避ける。 UI 側は ErrorOccurred イベント経由で警告バナーを出す。
        if (ex is OpenAIApiException { IsFatal: true } apiEx)
        {
            try
            {
                Logger.Info($"OnClientError: 致命的 API エラー ({apiEx.Kind}) を検知、 AudioCapture を即停止");
                _audioCaptureService.StopCapture();
            }
            catch (Exception stopEx)
            {
                Logger.Warn($"OnClientError: AudioCapture 停止に失敗 — {stopEx.Message}");
            }

            // UI のステータス文字列も警告に切替え (StatsUpdated 経由)。
            // 累積統計は BuildCurrentStats で必ず埋める (Fatal エラー時も「これまで何 token 使ったか」を維持)。
            StatsUpdated?.Invoke(this, BuildCurrentStats(apiEx.FriendlyMessage));
        }
    }

    /// <summary>
    /// デバッグ録音 (WAV 保存) の「あるべき状態」を現在の設定 + 走行状態 (<c>_isRunning &amp;&amp;
    /// DebugRecordSentAudio</c>) から再計算し、 録音セッションを開始/停止する。 StartCoreAsync /
    /// StopCoreAsync / 設定変更 (<c>IOptionsMonitor.OnChange</c>) の 3 経路から呼ばれ、
    /// <see cref="_debugRecordingLock"/> で直列化する。 これで「翻訳開始後に WAV 保存を ON/OFF しても即反映」される。
    /// 二重 StartSession / WriteFailed 二重購読は <see cref="_debugRecordingActive"/> と -= → += で防ぐ。
    /// ErrorOccurred はハンドラ再入によるデッドロックを避けるためロック解放後に発火する。
    /// </summary>
    private void SyncDebugRecording()
    {
        if (_debugAudioRecorder is null) return;

        Exception? startError = null;
        lock (_debugRecordingLock)
        {
            bool want = _isRunning && _settingsMonitor.CurrentValue.AudioCapture.DebugRecordSentAudio;
            if (want && !_debugRecordingActive)
            {
                // 書き込み失敗 (ディスク full / 権限不足 / AV ブロック) を UI バナーに伝播するため購読。
                // 二重購読を避けるため一旦 -= してから += する (-= は未購読でも無害)。
                _debugAudioRecorder.WriteFailed -= OnDebugRecorderWriteFailed;
                _debugAudioRecorder.WriteFailed += OnDebugRecorderWriteFailed;
                try
                {
                    // StartSession は silent-fail 設計だが、 将来別実装が例外を投げても翻訳本体を止めないようガード。
                    _debugAudioRecorder.StartSession(Guid.NewGuid().ToString("N")[..8]);
                    _debugRecordingActive = true;
                }
                catch (Exception ex)
                {
                    _debugAudioRecorder.WriteFailed -= OnDebugRecorderWriteFailed;
                    Logger.Warn("DebugAudioRecorder 開始中の例外 (翻訳は継続)", ex);
                    startError = ex;
                }
            }
            else if (!want && _debugRecordingActive)
            {
                // WAV ヘッダのサイズフィールドを実値に書き直してから FileStream を Dispose。 購読も解除して leak 防止。
                _debugAudioRecorder.WriteFailed -= OnDebugRecorderWriteFailed;
                try { _debugAudioRecorder.StopSession(); }
                catch (Exception ex) { Logger.Warn("DebugAudioRecorder 停止中の例外", ex); }
                _debugRecordingActive = false;
            }
        }

        if (startError is not null) ErrorOccurred?.Invoke(this, startError);
    }

    /// <summary>
    /// ゲイン適用後のピーク振幅を算出し、 ~50ms スロットルで <see cref="AudioLevelUpdated"/> を発火する。
    /// スロットル窓内はピークホールド (窓内最大) して瞬間ピークの取りこぼしを防ぐ。 UI の入力レベルメーター用。
    /// </summary>
    private void ReportAudioLevel(ReadOnlySpan<float> samples)
    {
        float peak = _levelPeakAccum;
        for (int i = 0; i < samples.Length; i++)
        {
            float a = MathF.Abs(samples[i]);
            if (a > peak) peak = a;
        }
        _levelPeakAccum = peak;

        var now = DateTime.UtcNow;
        if (now - _lastLevelEmitUtc < LevelEmitInterval) return;
        _lastLevelEmitUtc = now;
        float db = DspMath.AmplitudeToDb(_levelPeakAccum);
        _levelPeakAccum = 0f; // スロットル窓をリセット
        AudioLevelUpdated?.Invoke(this, new AudioLevelEventArgs { PeakDb = db });
    }

    /// <summary>
    /// DebugAudioRecorder の WriteFailed イベント (ディスク full / 権限不足 / AV ブロック等で書き込み失敗) を
    /// ErrorOccurred 経由で UI バナーに伝播する (rere F-005 対応)。
    /// </summary>
    private void OnDebugRecorderWriteFailed(Exception ex)
    {
        Logger.Warn($"DebugAudioRecorder 書き込み失敗 → UI 通知: {ex.Message}");
        ErrorOccurred?.Invoke(this, new InvalidOperationException(
            $"デバッグ録音の書き込みに失敗しました ({ex.GetType().Name}: {ex.Message})。 ディスク容量・書き込み権限・アンチウイルスのブロックを確認してください。 録音は自動停止しました。",
            ex));
    }

    private void OnConnectionStateChanged(ConnectionState state)
    {
        var previousState = _lastConnectionState;
        _lastConnectionState = state;

        // /rere 第2R #D-R2-012 (v1.0.29 候補、 シナリオ X1 対策): Reconnecting 遷移時に _silenceStartUtc をリセットする。
        // 背景: VAD Silence 中の無音 PCM (SilencePaddingMs=5000ms 内) は SendAudio の `State != Connected` 経路で
        // silently drop されるため (OpenAIRealtimeClient.cs:177)、 wall-clock ベースの padding 経過判定が偽陽性になる。
        // 例: Wi-Fi 3 秒切断 → 再接続復帰 → 直後の Silence 判定で `silenceMs > paddingMs` → 無音 PCM 送信即停止
        //     → #D-001 戦略 (server gap 対策) が無効化される。
        // 修正: Reconnecting 遷移時に _silenceStartUtc をリセットして、 復帰後の次の Silence 突入で padding をやり直し。
        if (state == ConnectionState.Reconnecting && previousState != ConnectionState.Reconnecting)
        {
            _silenceStartUtc = DateTime.MinValue;
            Logger.Info($"OnConnectionStateChanged: Reconnecting 遷移 ({previousState} → Reconnecting) — _silenceStartUtc リセット (#D-R2-012 padding 偽陽性予防)");
        }

        // /rere #D-002 (v1.0.28 拡張): Connected 遷移時に字幕状態をリセットする。
        //
        // 旧 v1.0.27 実装は `Reconnecting → Connected` 経路でのみリセットしていたが、
        // NW スタック種別 (Wi-Fi/有線/VPN) によっては `Disconnected → Connected` /
        // `Failed → Connected` / `Connecting → Connected` のように Reconnecting を経由しない
        // 直接遷移パターンが存在する。 これらの経路ではリセットされず、 旧セッションの
        // `_lastFinalizedTranscript` が残ったままで新セッションの累積 0 始まりと
        // prefix mismatch → OnTranscriptCompleted で skip 連発 → **字幕完全停止 + 再起動以外復旧不能**
        // という最悪シナリオを起こしうる。
        //
        // 修正: `state == Connected` で `previousState != Connected` なら **常時 Clear**。
        // 接続瞬間は必ずリセットして「server transcript の累積 0 始まり」と整合させる。
        // 初回接続 (Disconnected → Connecting → Connected) でも走るが、 _lastFinalizedTranscript は
        // 既に空のため Clear() は no-op で問題なし。
        if (state == ConnectionState.Connected && previousState != ConnectionState.Connected)
        {
            // /rere 第2R #B1-R2-006 (v1.0.29 候補): Clear 前に partial 表示中の文を確定 emit して UX 損失を防ぐ。
            // 55 分プロアクティブ再接続 (_sessionRefreshTimer) 中の発話中央で「partial 字幕が消える」UX 違和感を回避。
            // FinalizePendingPartial は内部で _textLock を取って lock 外 emit するので、 ここから lock 外呼び出しで安全。
            // 確定 emit 後の _lastFinalizedTranscript.Append は直後の Clear() で消えるが副作用なし。
            // 「空 partial」(IsNullOrWhiteSpace) のときは早期 return するので通常時は no-op。
            FinalizePendingPartial("再接続前確定");

            lock (_textLock)
            {
                Logger.Info($"OnConnectionStateChanged: Connected 遷移 ({previousState} → Connected) — 字幕状態をリセット (旧 finalized 長={_lastFinalizedTranscript.Length})");
                _lastFinalizedTranscript.Clear();
                _accumulatedText.Clear();
                _recentEmittedSentences.Clear();
                _currentSegmentId = Guid.NewGuid().ToString();
                _lastEmitTime = DateTime.MinValue;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        var statusText = state switch
        {
            ConnectionState.Connecting => "接続中...",
            ConnectionState.Connected => "API接続完了",
            ConnectionState.Reconnecting => "再接続中...",
            ConnectionState.Failed => "接続失敗 — APIキーとネットワークを確認してください",
            ConnectionState.Disconnected => "切断",
            _ => ""
        };

        // 接続状態変化時も累積統計を維持 (再接続中も「これまで何 token 使ったか」を消さない)。
        StatsUpdated?.Invoke(this, BuildCurrentStats(statusText));
    }

    public async ValueTask DisposeAsync()
    {
        // rere B1-006: Interlocked で並行 Dispose を排他制御。
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        try
        {
            // rere B1-011: Core layer の await は ConfigureAwait(false) で統一。
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error("TranslationPipelineService.DisposeAsync: 停止エラー", ex);
        }

        _throttleTimer.Dispose();
        foreach (var client in _distinctClients)
        {
            client.TranscriptDeltaReceived -= OnTranscriptDelta;
            client.TranscriptCompleted -= OnTranscriptCompleted;
            client.ErrorReceived -= OnClientError;
            client.StateChanged -= OnConnectionStateChanged;
        }
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;
        _settingsChangeSubscription?.Dispose();
        _startStopLock.Dispose();
    }

    public void Dispose()
    {
        // rere B1-006: Interlocked で並行 Dispose を排他制御。
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        // 同期版: UIスレッドでのデッドロックを回避するため Task.Run 経由で呼び出す
        try
        {
            Task.Run(() => StopAsync()).GetAwaiter().GetResult();
        }
        catch { /* DisposeAsync を推奨 */ }

        _throttleTimer.Dispose();
        foreach (var client in _distinctClients)
        {
            client.TranscriptDeltaReceived -= OnTranscriptDelta;
            client.TranscriptCompleted -= OnTranscriptCompleted;
            client.ErrorReceived -= OnClientError;
            client.StateChanged -= OnConnectionStateChanged;
        }
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;
        _settingsChangeSubscription?.Dispose();
        _startStopLock.Dispose();
    }

    // TruncateForLog / ShouldLogAtCount は Core/Services/LogFormatting.cs に集約 (rere D1 修正)。
    // 呼び出し名はそのまま維持し、 中身を委譲する形で 1 箇所管理に統一。
    private static string TruncateForLog(string? text, int maxLength = LogFormatting.DefaultTruncateLength)
        => LogFormatting.TruncateForLog(text, maxLength);

    private static bool ShouldLogAtCount(long count)
        => LogFormatting.ShouldLogAtCount(count);

    // SegmentId 専用の短縮ヘルパー (GUID 先頭 8 文字)。 LogFormatting に置く程の汎用性なし。
    private static string ShortSegmentId(string? segmentId, int prefixLength = 8)
    {
        if (string.IsNullOrEmpty(segmentId)) return string.Empty;
        return segmentId.Length <= prefixLength ? segmentId : segmentId[..prefixLength];
    }

    // StringBuilder が string の prefix と一致するかを ToString() 経由せず char ごとに比較する。
    // rere C1-P0-001 / A2-001: _lastFinalizedTranscript の StartsWith 比較を ToString() 抜きで実現。
    // O(min(sb.Length, text.Length)) で、 不一致が早期発見できれば更に短縮される。
    private static bool StringBuilderIsPrefixOf(StringBuilder sb, string text)
    {
        if (sb.Length > text.Length) return false;
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] != text[i]) return false;
        }
        return true;
    }

    // StringBuilder の末尾 maxLength 文字を string として取り出す (ログ用)。
    // sb.Length <= maxLength なら全部、 それ以上なら末尾 maxLength のみ。
    private static string TailOf(StringBuilder sb, int maxLength)
    {
        if (sb.Length == 0) return string.Empty;
        if (sb.Length <= maxLength) return sb.ToString();
        return sb.ToString(sb.Length - maxLength, maxLength);
    }

    /// <summary>
    /// 指定位置の文字が「文の区切り (= 句点)」か判定する。
    /// ピリオド '.' は数字に挟まれている場合 (例: 「6.3インチ」「3.14」) は小数点として保護し、
    /// 区切らない。 また、 delta 受信中 (<paramref name="isFinalContext"/>=false) かつ末尾ピリオド
    /// 直前が数字の場合は「次の delta で続きの数字が来るかも」と判断して保留する
    /// (false を返す)。 done 受信時 (isFinalContext=true) は遠慮なく区切る。
    /// </summary>
    /// <remarks>
    /// テスト容易化のため internal (InternalsVisibleTo=RealTimeTranslator.Tests)。
    /// </remarks>
    internal static bool IsSentenceBoundaryAt(string text, int i, bool isFinalContext)
    {
        if (i < 0 || i >= text.Length) return false;
        char c = text[i];
        if (!SentenceTerminators.Contains(c)) return false;

        // ピリオド以外 (。 ！ ？ ! ?) は問答無用で区切り
        if (c != '.') return true;

        bool prevIsDigit = i > 0 && text[i - 1] >= '0' && text[i - 1] <= '9';
        bool nextIsDigit = i + 1 < text.Length && text[i + 1] >= '0' && text[i + 1] <= '9';

        // 小数点 (例: 6.3 / 3.14): 区切らない
        if (prevIsDigit && nextIsDigit) return false;

        // delta 受信中の「末尾ピリオド + 直前数字」: 次の delta で「3インチ」みたいな
        // 続きが来る可能性 → 区切らず保留 (続きが来てから判定)。
        // done 確定時 (isFinalContext=true) は遠慮なく区切る。
        if (!isFinalContext && prevIsDigit && i == text.Length - 1) return false;

        return true;
    }

    /// <summary>
    /// StringBuilder 版オーバーロード。 OnTranscriptDelta の O(n²) → O(n) 最適化で
    /// ToString() を回避して StringBuilder.indexer (O(1)) で直接判定するために追加。
    /// ロジックは string 版と完全に同一。
    /// </summary>
    internal static bool IsSentenceBoundaryAt(StringBuilder sb, int i, int length, bool isFinalContext)
    {
        if (i < 0 || i >= length) return false;
        char c = sb[i];
        if (!SentenceTerminators.Contains(c)) return false;

        if (c != '.') return true;

        bool prevIsDigit = i > 0 && sb[i - 1] >= '0' && sb[i - 1] <= '9';
        bool nextIsDigit = i + 1 < length && sb[i + 1] >= '0' && sb[i + 1] <= '9';

        if (prevIsDigit && nextIsDigit) return false;
        if (!isFinalContext && prevIsDigit && i == length - 1) return false;

        return true;
    }


    /// <summary>
    /// D-7 fallback (v1.0.28): 句点なしで partial が <paramref name="maxChars"/> 文字以上に成長したときの
    /// 強制分割位置を決定する。 OnTranscriptDelta の lock 内から呼ばれる前提で、 外部副作用なしの純粋関数。
    /// </summary>
    /// <remarks>
    /// 優先順位:
    ///   1. 末尾 30 文字以内の「、」「,」の直後で切る (自然な節目優先)
    ///   2. なければ末尾 30 文字以内の半角/全角空白の直後で切る
    ///   3. それでもなければ maxChars 位置で強制切断 (最終手段)
    /// 戻り値は分割位置 (0 = 分割不要、 sb.Length 以下)。 呼び出し側で sb.ToString(0, idx) で切り出せる。
    /// </remarks>
    internal static int FindForcedSplitIndex(StringBuilder sb, int maxChars)
    {
        if (sb is null) throw new ArgumentNullException(nameof(sb));
        if (maxChars <= 0 || sb.Length < maxChars) return 0;

        const int LookbackWindow = 30;
        int scanStart = Math.Max(0, maxChars - LookbackWindow);
        int scanEnd = sb.Length;

        // 1. 末尾「、」「,」を探す (新しいものから)
        for (int i = scanEnd - 1; i >= scanStart; i--)
        {
            char c = sb[i];
            if (c == '、' || c == ',')
            {
                return i + 1;
            }
        }

        // 2. 末尾「 」「　」を探す
        for (int i = scanEnd - 1; i >= scanStart; i--)
        {
            char c = sb[i];
            if (c == ' ' || c == '　')
            {
                return i + 1;
            }
        }

        // 3. 最終手段: maxChars 位置で強制切断
        return maxChars;
    }

    // ═══════════════════════════════════════════════════════════════
    // 類似重複抑制 — Bigram Jaccard 類似度
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 2つの文字列間の Bigram Jaccard 類似度を計算する (0.0〜1.0)。
    /// Bigram = 隣接2文字のペア。 Jaccard = |A∩B| / |A∪B|。
    /// </summary>
    internal static double BigramJaccardSimilarity(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b ? 1.0 : 0.0;
        return JaccardOf(BuildBigrams(a), BuildBigrams(b));
    }

    // bigram 集合を構築する。 容量を (長さ-1) で先行確保して rehash を抑える。
    private static HashSet<long> BuildBigrams(string s)
    {
        var set = new HashSet<long>(s.Length - 1);
        for (int i = 0; i < s.Length - 1; i++)
            set.Add(((long)s[i] << 16) | s[i + 1]);
        return set;
    }

    // 2 つの bigram 集合の Jaccard 係数。 集合構築と分離して再利用可能にする。
    private static double JaccardOf(HashSet<long> a, HashSet<long> b)
    {
        int intersection = 0;
        foreach (var bg in a)
            if (b.Contains(bg)) intersection++;

        int union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    /// 直近の完結文と類似度が高い場合 true を返す。 _textLock 内から呼ぶこと。
    /// </summary>
    private bool IsSimilarToRecentEmission(string sentence)
    {
        if (_recentEmittedSentences.Count == 0) return false;

        // sentence の bigram は 1 回だけ構築する (旧実装は recent 件数ぶん再構築していた)。
        // 2 文字未満は bigram が作れないので文字列等価で判定 (BigramJaccardSimilarity と同挙動)。
        var bigramsS = sentence.Length >= 2 ? BuildBigrams(sentence) : null;
        foreach (var (text, bigrams) in _recentEmittedSentences)
        {
            double sim = (bigramsS is null || text.Length < 2)
                ? (sentence == text ? 1.0 : 0.0)
                : JaccardOf(bigramsS, bigrams);
            if (sim > DuplicateSuppressionThreshold)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 完結文を直近バッファに記録する。 _textLock 内から呼ぶこと。
    /// </summary>
    private void RecordEmission(string sentence)
    {
        // bigram も一緒に保持して IsSimilarToRecentEmission での再計算を排除する。
        HashSet<long> bigrams = sentence.Length >= 2 ? BuildBigrams(sentence) : [];
        _recentEmittedSentences.Enqueue((sentence, bigrams));
        while (_recentEmittedSentences.Count > MaxRecentEmissions)
            _recentEmittedSentences.Dequeue();
    }

    // ═══════════════════════════════════════════════════════════════
    // VAD ゲート + 自動 Pause + 統計計算 (案 D + 案 G 実装)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// VAD 有効時の音声処理: 16kHz raw audio を VAD フレームサイズ単位 (512 サンプル/32ms)
    /// に切り出して順次 <see cref="ProcessVadFrame"/> へ流す。 audio chunk 境界とフレーム境界が
    /// ズレるため、 _frameAccumulator に残り部分を保持して次 chunk と結合する。
    /// テスト目的で internal (InternalsVisibleTo=RealTimeTranslator.Tests)。
    /// </summary>
    internal void ProcessAudioWithVadGate(float[] audio48kHz, AudioCaptureSettings settings)
    {
        // 送信レート分岐 (provider 依存):
        //   OpenAI (InputSampleRate=24000): VAD 判定 16k + 送信 24k の並列 2 系統 (v1.0.36 設計)。
        //   Gemini (InputSampleRate=16000): Gemini 入力は 16kHz 固定なので VAD 判定フレーム = 送信フレーム。
        //     24k 系統 (_sendResampler) は回さず、 16k フレーム 1 本を判定にも送信にも使う。
        bool sendAt16k = _activeClient.InputSampleRate == 16000;
        var resampled16k = _vadResampler.Resample(audio48kHz);

        int frameSize16k = _vad.RequiredFrameSize; // Silero VAD v5 仕様: 512 sample @ 16kHz (32ms)
        _frameAccumulator ??= new float[frameSize16k];

        if (sendAt16k)
        {
            // Gemini: 16k フレームが揃うたびに (VAD 判定フレーム = 送信フレーム = 同一参照) で処理。
            // ProcessVadFrame は送信フレームを PreRoll に Array.Copy で積むので参照共有でも安全。
            int offG = 0;
            while (true)
            {
                int copy = Math.Min(frameSize16k - _frameAccumulatorLen, resampled16k.Length - offG);
                if (copy > 0)
                {
                    Array.Copy(resampled16k, offG, _frameAccumulator, _frameAccumulatorLen, copy);
                    _frameAccumulatorLen += copy;
                    offG += copy;
                }
                if (_frameAccumulatorLen == frameSize16k)
                {
                    ProcessVadFrame(_frameAccumulator, _frameAccumulator, settings);
                    _frameAccumulatorLen = 0;
                    continue;
                }
                if (copy == 0) break;
            }
            return;
        }

        // OpenAI: 48k → 24k 直リサンプル (16k 中継を挟まない = Nyquist 12kHz 確保)。
        // 両リサンプラは LatencyMargin を時間ベース (4ms) に揃えてあるので出力は時間同期し、
        // 16k 512sample (32ms) と 24k 768sample (32ms) を「同じ時間区間のフレームペア」として取り出せる。
        var resampled24k = _sendResampler.Resample(audio48kHz);
        const int frameSize24k = 768; // 同じ 32ms を 24kHz サンプルで表すと 768
        _frameAccumulator24k ??= new float[frameSize24k];

        int off16 = 0, off24 = 0;
        while (true)
        {
            // 16k 側を accumulator に積む
            int copy16 = Math.Min(frameSize16k - _frameAccumulatorLen, resampled16k.Length - off16);
            if (copy16 > 0)
            {
                Array.Copy(resampled16k, off16, _frameAccumulator, _frameAccumulatorLen, copy16);
                _frameAccumulatorLen += copy16;
                off16 += copy16;
            }
            // 24k 側も同様
            int copy24 = Math.Min(frameSize24k - _frameAccumulator24kLen, resampled24k.Length - off24);
            if (copy24 > 0)
            {
                Array.Copy(resampled24k, off24, _frameAccumulator24k, _frameAccumulator24kLen, copy24);
                _frameAccumulator24kLen += copy24;
                off24 += copy24;
            }

            // 両方のフレームが揃ったらペアで処理
            if (_frameAccumulatorLen == frameSize16k && _frameAccumulator24kLen == frameSize24k)
            {
                ProcessVadFrame(_frameAccumulator, _frameAccumulator24k, settings);
                _frameAccumulatorLen = 0;
                _frameAccumulator24kLen = 0;
                continue; // 次のフレームペアを処理
            }
            // 進捗無し (src 消費しきり、 両アキュも未満) → 次の chunk まで待つ
            if (copy16 == 0 && copy24 == 0) break;
        }
    }

    /// <summary>
    /// 1 フレーム (512 サンプル / 32ms) を VAD で判定し、 Silence/InSpeech/Hangover 状態機を回す。
    /// テスト目的で internal (InternalsVisibleTo=RealTimeTranslator.Tests)。
    /// </summary>
    internal void ProcessVadFrame(float[] frame16kHz, float[] frameForSend, AudioCaptureSettings settings)
    {
        float prob;
        try
        {
            prob = _vad.DetectSpeechProb(frame16kHz);
        }
        catch (Exception ex)
        {
            // VAD 推論失敗時は安全側 (= 送信) に倒し、 誤って発話を捨てない。
            Logger.Warn($"VAD 推論失敗 (発話と見なして素通し): {ex.Message}");
            SendFrameToClient(frameForSend);
            _lastSpeechUtc = DateTime.UtcNow;
            return;
        }

        bool isSpeech = prob >= settings.VadThreshold;
        double frameSeconds = (double)frame16kHz.Length / _vad.SampleRate;
        // ms 値をフレーム数換算 (最低 1 / 0 を保証)。 frameMs は VAD の RequiredFrameSize と
        // SampleRate から動的計算するため、 別 VAD (例: v4 / 8kHz モード) に差し替えても破綻しない。
        double frameMs = frameSeconds * 1000.0;
        int preRollCapacity = Math.Max(1, (int)(settings.VadPreRollMs / frameMs));
        int hangoverFrames = Math.Max(0, (int)(settings.VadHangoverMs / frameMs));


        switch (_vadState)
        {
            case VadState.Silence:
                if (isSpeech)
                {
                    // PreRoll バッファ (24k フレーム) を丸ごと送信して発話冒頭の取りこぼしを補う。
                    while (_preRollBuffer.Count > 0)
                    {
                        var preRollFrame = _preRollBuffer.Dequeue();
                        SendFrameToClient(preRollFrame);
                    }
                    SendFrameToClient(frameForSend);
                    _vadState = VadState.InSpeech;
                    _lastSpeechUtc = DateTime.UtcNow;
                    // v1.0.27: Silence → InSpeech 遷移 = 発話再開。 Silence カウントをリセット。
                    _silenceStartUtc = DateTime.MinValue;
                }
                else
                {
                    // v1.0.27 ★ 無音 PCM 継続送信 (server gap 対策):
                    //
                    // OpenAI Realtime Translate API は continuous streaming model 前提で、 入力音声が
                    // 来ない区間は server が delta 出力を保留する (2026-05-24 ログから事実確証)。
                    // ゆろさん観察 (実機): VAD OFF で BGM が押し出してるから途切れない。
                    // → VAD Silence 中も「無音 PCM (ゼロ埋め)」を送って入力継続をアピール → server が
                    //   保留してた delta を吐き出す動きを期待。 これで「繰...」「り返す、10分」のような
                    //   分断 (v1.0.26 で観測) も発生しない (同 SegmentId のまま partial が伸びる)。
                    //
                    // 送信時間上限: SilencePaddingMs (default 5000ms)。 超えたら送信停止して token 節約。
                    // Silero VAD が次の発話を検知した瞬間に Silence → InSpeech 再遷移して通常送信に戻る。
                    var paddingMs = _activeSilencePaddingMs;
                    if (paddingMs > 0 && _silenceStartUtc != DateTime.MinValue)
                    {
                        var silenceMs = (DateTime.UtcNow - _silenceStartUtc).TotalMilliseconds;
                        if (silenceMs <= paddingMs)
                        {
                            // 無音 PCM を送信 (ゼロ埋め PCM16、 24kHz/768 sample = 1536 bytes)。
                            // バイト配列を 1 度だけ確保して使い回す (中身ゼロのまま不変)。
                            _silencePaddingPcm16 ??= new byte[frameForSend.Length * 2];
                            _activeClient.SendAudio(_silencePaddingPcm16);
                            // 旧 _skippedSecondsByVad は加算しない (実際には送信してるため UI 表示も「送信中」のまま)
                            break;
                        }
                    }

                    // 無音 PCM 送信時間を超過 (or 機能無効) → 従来通り PreRoll に積む。
                    while (_preRollBuffer.Count >= preRollCapacity)
                    {
                        _preRollBuffer.Dequeue();
                        _skippedSecondsByVad += frameSeconds;
                    }
                    var copy = new float[frameForSend.Length];
                    Array.Copy(frameForSend, copy, frameForSend.Length);
                    _preRollBuffer.Enqueue(copy);
                }
                break;

            case VadState.InSpeech:
                SendFrameToClient(frameForSend);
                if (isSpeech)
                {
                    _lastSpeechUtc = DateTime.UtcNow;
                }
                else
                {
                    _vadState = VadState.Hangover;
                    _hangoverFramesRemaining = hangoverFrames;
                }
                break;

            case VadState.Hangover:
                SendFrameToClient(frameForSend);
                if (isSpeech)
                {
                    _vadState = VadState.InSpeech;
                    _lastSpeechUtc = DateTime.UtcNow;
                    _hangoverFramesRemaining = 0;
                }
                else
                {
                    _hangoverFramesRemaining--;
                    if (_hangoverFramesRemaining <= 0)
                    {
                        _vadState = VadState.Silence;
                        _preRollBuffer.Clear();
                        // v1.0.27: Hangover → Silence 遷移 = 発話終了候補。 Silence 開始時刻を記録して
                        // 「SilencePaddingMs 以内は無音 PCM 継続送信」のカウント開始。
                        _silenceStartUtc = DateTime.UtcNow;
                        // LSTM state は連続性が高い方が誤検出が減るためここでは Reset しない
                        // (Start 時にだけ Reset を呼ぶ)。
                    }
                }
                break;
        }
    }

    /// <summary>VAD ゲート通過分のフレームを PCM16 に変換して active client へ送信。</summary>
    private void SendFrameToClient(float[] frameForSend)
    {
        // フレームは既に provider の送信レート (OpenAI=24k / Gemini=16k) でリサンプル済みなので、
        // ここでは float→PCM16 変換だけ行う。
        var pcm16 = AudioFormatConverter.Float32ToPcm16(frameForSend);
        _activeClient.SendAudio(pcm16);
    }

    /// <summary>
    /// 5 秒間隔で「最後に speech 検出してから AutoPauseOnSilenceSec 経過したか」を監視し、
    /// 超過していたらキャプチャを自動停止して UI に通知する。
    /// 設定で AutoPauseOnSilenceSec=0 (default) なら何もしない。
    /// 1 回発火したら自身を終了 (ユーザーが Start 押し直したら新タスクが起動される)。
    /// </summary>
    private async Task AutoPauseLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (!_isRunning) continue;

                var settings = _settingsMonitor.CurrentValue.AudioCapture;
                if (!settings.EnableVad || settings.AutoPauseOnSilenceSec <= 0) continue;

                var elapsed = (DateTime.UtcNow - _lastSpeechUtc).TotalSeconds;
                if (elapsed < settings.AutoPauseOnSilenceSec) continue;

                Logger.Info($"自動 Pause 発火: {elapsed:F0}秒間 speech 未検出のためキャプチャを停止します");
                try
                {
                    _audioCaptureService.StopCapture();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"自動 Pause で StopCapture 失敗: {ex.Message}");
                }

                StatsUpdated?.Invoke(this, BuildCurrentStats($"⏸️ 約{(int)elapsed}秒間 speech 未検出のため自動停止しました"));

                // 1 回発火で抜ける (ユーザーが Start 押し直すと新タスクが起動)。
                break;
            }
        }
        catch (OperationCanceledException) { /* StopAsync 経由の停止 */ }
        catch (Exception ex)
        {
            Logger.Warn($"自動 Pause ループで予期しないエラー: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 1 秒周期で StatsUpdated を発火する tick ループ。
    /// API response が来ない silence 区間でも UI の経過時間 / 累計トークン / VAD 節約秒数を
    /// リアルタイム表示するために存在する。 StatusText は空文字を渡し (MainViewModel 側で空なら
    /// 既存値を維持する設計) Status ラベルを上書きしない。
    /// </summary>
    private async Task StatsTickLoopAsync(CancellationToken ct)
    {
        // rere F-005: ループ全体を 1 度の try/catch で包むと、 ハンドラ側の例外で stats tick 全体が止まり
        // UI が「時計止まった = アプリ死んだ」と誤解される。 try/catch を内側に入れて 1 周スキップで継続させる。
        // ErrorOccurred を発火して UI 側にバナー通知する経路も追加。 ただし 1 秒周期で連発を避けるため、
        // 前回エラーから 60 秒以上経過時のみ ErrorOccurred を発火する rate-limit を入れる。
        DateTime lastErrorReportUtc = DateTime.MinValue;
        int tickCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                if (!_isRunning) continue;
                StatsUpdated?.Invoke(this, BuildCurrentStats(string.Empty));

                // /rere F-001 対応: 1 分周期で並列 2 系統リサンプラの同期状態を健康診断ログに残す。
                // `_frameAccumulatorLen` (16k VAD 側) と `_frameAccumulator24kLen` (24k 送信側) が
                // 期待値 0 (= ペア処理直後) で停滞すべきところ、 一方だけ恒常的に > 0 で蓄積していたら
                // LatencyMargin の時間ベース揃え (4ms) が破綻しているサイン。 「字幕が遅れて出る」 報告時の
                // 原因切り分けで使う。 lock 取らず int 読み取りは atomic (race read は health 用途で許容)。
                tickCount++;
                if (tickCount % 60 == 0)
                {
                    Logger.Info($"リサンプラ同期状態: VADacc={_frameAccumulatorLen}/{_vad.RequiredFrameSize}, 送信24kacc={_frameAccumulator24kLen} (Provider={_settingsMonitor.CurrentValue.Provider}; 値が << フレームサイズなら正常。 Gemini は 24kacc 未使用)");
                }
            }
            catch (OperationCanceledException)
            {
                // StopAsync 経由の正常終了
                return;
            }
            catch (Exception ex)
            {
                Logger.Error($"stats tick ループで予期しないエラー (1 周スキップ): {ex.Message}", ex);
                if ((DateTime.UtcNow - lastErrorReportUtc).TotalSeconds >= 60)
                {
                    lastErrorReportUtc = DateTime.UtcNow;
                    ErrorOccurred?.Invoke(this, ex);
                }
            }
        }
    }

    /// <summary>
    /// 現セッションの完全な統計スナップショットを構築する。 StatusText のみイベント特有値、
    /// 他フィールド (SessionDuration / Tokens / Cost / SkippedSecondsByVad) は累積値で必ず埋める。
    /// rere C2-001 / B2-005 対応: 個別 invoke で累積フィールドを忘れて UI を 0 に巻き戻す経路を構造的に消す。
    /// ProcessingLatency / TranslationLatency が必要な箇所は呼び出し側で result.X = ... と追加 set する。
    /// </summary>
    private PipelineStatsEventArgs BuildCurrentStats(string statusText = "")
    {
        return new PipelineStatsEventArgs
        {
            StatusText = statusText,
            SessionDuration = DateTime.UtcNow - _sessionStartUtc,
            InputAudioTokensEstimate = ComputeCurrentTokens(),
            EstimatedCostUsd = ComputeCurrentCostUsd(),
            SkippedSecondsByVad = _skippedSecondsByVad,
        };
    }

    /// <summary>
    /// 現時点の audio input tokens 累計を返す (サーバー報告値優先、 取れない場合は送信秒数推定)。
    /// </summary>
    private long ComputeCurrentTokens()
    {
        var serverTokens = _activeClient.ServerReportedAudioInputTokens;
        if (serverTokens > 0) return serverTokens;
        return CostEstimator.EstimateTokensFromSamples(
            _activeClient.TotalAudioInputSamples24kHz, 24000);
    }

    /// <summary>現時点の推定コスト (USD)。 モデル名は Start 時にスナップショットした active provider の値。</summary>
    private decimal ComputeCurrentCostUsd()
    {
        // active セッションの model スナップショット (_activeModelForCost) を使う。 走行中の
        // provider/model 変更に引きずられず、 実際に動いている _activeClient と整合する (CodeRabbit 指摘)。
        return CostEstimator.EstimateUsd(_activeModelForCost, ComputeCurrentTokens());
    }
}
