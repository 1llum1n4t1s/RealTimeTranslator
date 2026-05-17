using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>
/// メインウィンドウのViewModel
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private const int MaxLogLines = 1000;
    private static readonly System.Text.RegularExpressions.Regex s_numericPattern =
        new(@"[\d.]+%?", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly ITranslationPipelineService _pipelineService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly OverlayViewModel _overlayViewModel;
    private AppSettings _settings;
    private readonly IUpdateService _updateService;
    private readonly SettingsViewModel _settingsViewModel;
    // rere B1-003 完遂: DPAPI 復号は ISettingsService 経由に統一 (Service Locator アンチパターン解消)。
    private readonly ISettingsService _settingsService;
    private string _lastApiKey;
    private string _lastOutputLanguage;
    private readonly IDisposable? _settingsChangeSubscription;
    private readonly Queue<string> _logLines = new();
    private readonly object _logLock = new();
    private readonly object _cancellationLock = new();
    private string? _lastLogMessage;
    private CancellationTokenSource? _processingCancellation;

    [ObservableProperty]
    private ObservableCollection<ProcessInfo> _processes = new();

    [ObservableProperty]
    private ProcessInfo? _selectedProcess;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "停止中";

    [ObservableProperty]
    private IBrush _statusColor = Brushes.Gray;

    [ObservableProperty]
    private double _processingLatency;

    [ObservableProperty]
    private double _translationLatency;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadingMessage = "初期化中...";

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _downloadStatus = string.Empty;

    [ObservableProperty]
    private string _downloadReason = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>
    /// OpenAI API キーが設定済みかどうか（空白のみは未設定扱い）。
    /// 未設定だと CanStart が常に false になり、UI 側で警告メッセージを表示する。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isApiKeyConfigured;

    /// <summary>
    /// バージョンタブの「更新の確認」ボタン押下中フラグ。
    /// ボタンの IsEnabled / 連打防止に使う。
    /// </summary>
    [ObservableProperty]
    private bool _isCheckingUpdate;

    /// <summary>
    /// バージョンタブに表示する更新チェックのステータスメッセージ。
    /// 既存の OnUpdateStatusChanged 経由で更新する。
    /// </summary>
    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    // ───────── 送信統計 / コスト見える化 (案 G) ─────────
    // OpenAI Realtime API の input audio token を「いま使ってる量 / 推定コスト」として
    // メイン画面に表示する。 ダラダラ垂れ流しによる課金事故防止が目的。

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CumulativeStatsText))]
    private long _inputAudioTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CumulativeStatsText))]
    private decimal _estimatedCostUsd;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CumulativeStatsText))]
    private TimeSpan _sessionDuration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CumulativeStatsText))]
    private double _skippedSecondsByVad;

    /// <summary>
    /// メイン画面に出す統計サマリ。 IsRunning にかかわらず Pipeline の StatsUpdated を反映する
    /// (Stop 後も「今回のセッションで何 tokens 使ったか」が見える)。
    /// </summary>
    public string CumulativeStatsText
    {
        get
        {
            if (SessionDuration == TimeSpan.Zero && InputAudioTokens == 0)
            {
                return string.Empty;
            }
            var savedText = SkippedSecondsByVad > 0
                ? $" / 🚫 VAD 節約 {FormatDuration(TimeSpan.FromSeconds(SkippedSecondsByVad))}"
                : string.Empty;
            return $"⏱ {FormatDuration(SessionDuration)} / 🪙 {InputAudioTokens:N0} tokens / 💰 ${EstimatedCostUsd:F4}{savedText}";
        }
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>
    /// バージョンタブのコピーライト表示。AssemblyCopyrightAttribute から取得。
    /// </summary>
    public string CopyrightText { get; } = LoadCopyrightText();

    private static string LoadCopyrightText()
    {
        try
        {
            var assembly = typeof(MainViewModel).Assembly;
            return assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
                   ?? "Copyright © 2024-2026 ゆろち";
        }
        catch
        {
            return "Copyright © 2024-2026 ゆろち";
        }
    }

    /// <summary>
    /// タイトルバーに表示するアプリバージョン（Lhamiel と統一）。
    /// AssemblyInformationalVersion から取得し、'+' 以降のビルドメタを除去。
    /// </summary>
    public string VersionText { get; } = LoadVersionText();

    private static string LoadVersionText()
    {
        try
        {
            var assembly = typeof(MainViewModel).Assembly;
            var raw = assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString(3)
                      ?? "0.0.0";
            return raw.Contains('+') ? raw.Split('+')[0] : raw;
        }
        catch
        {
            return "0.0.0";
        }
    }

    /// <summary>
    /// 開始ボタンが有効かどうか
    /// </summary>
    public bool CanStart => SelectedProcess != null && !IsRunning && !IsLoading && IsApiKeyConfigured;

    public SettingsViewModel SettingsVM => _settingsViewModel;

    /// <summary>
    /// MainViewModel コンストラクタ
    /// </summary>
    public MainViewModel(
        ITranslationPipelineService pipelineService,
        IAudioCaptureService audioCaptureService,
        OverlayViewModel overlayViewModel,
        IOptionsMonitor<AppSettings> optionsMonitor,
        IUpdateService updateService,
        SettingsViewModel settingsViewModel,
        ISettingsService settingsService)
    {
        _pipelineService = pipelineService;
        _audioCaptureService = audioCaptureService;
        _overlayViewModel = overlayViewModel;
        _settingsService = settingsService;
        _settings = optionsMonitor.CurrentValue;
        _lastApiKey = _settings.OpenAIRealtime.ApiKey;
        _lastOutputLanguage = _settings.OpenAIRealtime.OutputLanguage;
        IsApiKeyConfigured = !string.IsNullOrWhiteSpace(_settings.OpenAIRealtime.ApiKey);

        // 設定変更のイベントを購読。settings.json は DPAPI 暗号化済み API キーで保存されているため、
        // hot-reload された AppSettings も in-place で復号してから消費する。
        _settingsChangeSubscription = optionsMonitor.OnChange(newSettings =>
        {
            LoggerService.LogInfo("Settings updated detected in MainViewModel.");
            _settingsService.DecryptApiKey(newSettings);
            _settings = newSettings;
            // API キーの設定状態を UI スレッドで反映（CanStart 再評価が走る）
            Dispatcher.UIThread.Post(() =>
            {
                IsApiKeyConfigured = !string.IsNullOrWhiteSpace(newSettings.OpenAIRealtime.ApiKey);
            });
        });

        _updateService = updateService;
        _settingsViewModel = settingsViewModel;

        _pipelineService.SubtitleGenerated += OnSubtitleGenerated;
        _pipelineService.StatsUpdated += OnPipelineStatsUpdated;
        _pipelineService.ErrorOccurred += OnPipelineError;

        _audioCaptureService.CaptureStatusChanged += OnCaptureStatusChanged;

        _settingsViewModel.SettingsSaved += OnSettingsSaved;

        _updateService.StatusChanged += OnUpdateStatusChanged;
        // UpdateAvailable イベントは廃止 (v1.0.12 から VelopackUpdateDialog.Avalonia に置換)。
        // 検出 → DL → Apply → Restart まで UpdateService 内のダイアログで完結する。
        _updateService.UpdateSettings(_settings.Update);

        // RefreshProcesses は Process.GetProcesses + 全 audio session 列挙で 100-500ms かかるため、
        // 起動クリティカルパス（コンストラクタ同期実行）から外して MainWindow 表示後に走らせる。
        Dispatcher.UIThread.Post(() =>
        {
            RefreshProcesses();
            RestoreLastSelectedProcess();
            Log("アプリケーションを起動しました");
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 字幕が生成されたときのハンドラー
    /// </summary>
    private void OnSubtitleGenerated(object? sender, SubtitleItem item)
    {
        RunOnUiThread(() =>
        {
            _overlayViewModel.AddOrUpdateSubtitle(item);
            // partial(delta) と final(done) を区別してログ表示する。
            // partial 中に蓄積されているのは OriginalText、final 確定時は TranslatedText（SubtitleItem コメント参照）。
            var prefix = item.IsFinal ? "[確定]" : "[途中]";
            var text = item.IsFinal ? item.TranslatedText : item.OriginalText;
            var logMessage = $"{prefix} →{_settings.OpenAIRealtime.OutputLanguage} {text}";
            // ログ I/O 削減のため、途中字幕は LoggerService.LogInfo に出さず UI Log のみ。
            if (item.IsFinal)
            {
                // ⭐ rere P1 #2 / A3-001 / F-3 修正: PII 漏洩経路を抑制。
                // 翻訳テキスト全文をログファイル化すると、 Issue 添付時に視聴コンテンツのセリフ
                // や会議の機微発話が公開リポに残る経路がある。 Core 側 (TranslationPipelineService /
                // OpenAIRealtimeClient) と同じ 40 文字 truncate をここでも適用する。
                // UI Log() (Logs タブ) はフル文字列のまま (画面表示のみで永続化されない)。
                var truncated = text.Length <= 40 ? text : text[..40] + "...";
                LoggerService.LogInfo($"{prefix} →{_settings.OpenAIRealtime.OutputLanguage} {truncated}");
            }
            Log(logMessage);
        });
    }

    /// <summary>
    /// パイプライン統計が更新されたときのハンドラー
    /// </summary>
    private void OnPipelineStatsUpdated(object? sender, PipelineStatsEventArgs e)
    {
        RunOnUiThread(() =>
        {
            ProcessingLatency = e.ProcessingLatency;
            TranslationLatency = e.TranslationLatency;
            if (!string.IsNullOrEmpty(e.StatusText) && IsRunning)
            {
                StatusText = e.StatusText;
            }
            // 統計は IsRunning に関係なく反映 (Stop 後も最終値を残してユーザーが確認できるように)。
            // 0 で上書きされるケース (途中で Pipeline が誤って 0 を送る) は今のところ無いはずだが、
            // 念のため StartAsync の最初は明示的に 0 を送るので問題ない。
            InputAudioTokens = e.InputAudioTokensEstimate;
            EstimatedCostUsd = e.EstimatedCostUsd;
            SessionDuration = e.SessionDuration;
            SkippedSecondsByVad = e.SkippedSecondsByVad;
        });
    }

    /// <summary>
    /// パイプラインでエラーが発生したときのハンドラー
    /// </summary>
    private void OnPipelineError(object? sender, Exception ex)
    {
        RunOnUiThread(() =>
        {
            Log($"パイプラインエラー: {ex.Message}");
            LoggerService.LogException($"TranslationPipelineService Error: {ex.Message}", ex);

            // 2026-05-17 ゆろさんクォータ超過ログ対応:
            // OpenAI API の致命エラー (Quota / InvalidApiKey / Forbidden) は UI に警告バナーで表示する。
            // 一過性エラー (RateLimit / BadRequest / Unknown) はログのみで、 バナーは出さない (自動再接続で復旧する想定)。
            if (ex is OpenAIApiException apiEx && apiEx.IsFatal)
            {
                ErrorBannerMessage = apiEx.FriendlyMessage;
                IsErrorBannerVisible = true;
            }
        });
    }

    /// <summary>UI 上部に表示する重大エラー警告バナーの表示状態。</summary>
    [ObservableProperty]
    private bool _isErrorBannerVisible;

    /// <summary>警告バナーに表示するユーザー向けメッセージ (日本語、 OpenAIApiException.FriendlyMessage 由来)。</summary>
    [ObservableProperty]
    private string _errorBannerMessage = string.Empty;

    /// <summary>バナーを閉じる (ユーザーが内容を確認した後)。</summary>
    [RelayCommand]
    private void DismissErrorBanner()
    {
        IsErrorBannerVisible = false;
        ErrorBannerMessage = string.Empty;
    }

    private async void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        try
        {
            // 表示設定（Overlay）は常に反映（パイプライン停止不要）
            _audioCaptureService.ApplySettings(e.Settings.AudioCapture);
            _updateService.UpdateSettings(e.Settings.Update);

            // API設定が変更されたかを、前回保存した値と比較して判定
            // （SettingsViewModel が同一 AppSettings インスタンスを編集するため、
            //   e.Settings と _settings は同一参照になる。別途保持した前回値と比較する）
            var newApiKey = e.Settings.OpenAIRealtime.ApiKey;
            var newOutputLang = e.Settings.OpenAIRealtime.OutputLanguage;
            var apiSettingsChanged = _lastApiKey != newApiKey || _lastOutputLanguage != newOutputLang;

            // rere レビュー B1-007: ApplySettingsAsync は dead code として削除済み。
            // 設定変更は IOptionsMonitor.OnChange 経由で各サービスに自動伝播するため呼び出し不要。
            _lastApiKey = newApiKey;
            _lastOutputLanguage = newOutputLang;

            // API設定変更時のみパイプラインを停止（表示設定だけの変更では停止しない）
            if (apiSettingsChanged && IsRunning)
            {
                await StopAsync();
                StatusText = "API設定変更のため停止しました。再開時に新しい設定が反映されます。";
                StatusColor = Brushes.Orange;
                Log($"API設定変更を検知したため停止しました。再開時に新しい設定が反映されます。翻訳先: {newOutputLang}");
                return;
            }

            Log($"設定変更を反映しました。翻訳先: {newOutputLang}");
        }
        catch (Exception ex)
        {
            // {ex.Message} だけだと例外型と stack が消えて事後解析できない。
            // LogException で full dump して、 UI には短いメッセージだけ出す (rere P1 #14)。
            LoggerService.LogException("OnSettingsSaved: 設定適用エラー", ex);
            Log($"設定の適用中にエラーが発生しました: {ex.GetType().Name}: {ex.Message}");
            StatusText = "設定の適用中にエラーが発生しました";
            StatusColor = Brushes.Red;
        }
    }

    private void OnUpdateStatusChanged(object? sender, UpdateStatusChangedEventArgs e)
    {
        RunOnUiThread(() =>
        {
            Log($"更新: {e.Message}");
            // バージョンタブの「更新の確認」結果表示にもメッセージを流す。
            // 既存の Log（ログタブ）はそのまま、バージョンタブのテキストブロックに UpdateStatusText が出る。
            UpdateStatusText = e.Message;
            if (!IsRunning && e.Status == UpdateStatus.Failed)
            {
                StatusText = "更新エラー";
                StatusColor = Brushes.Red;
            }
        });
    }

    /// <summary>
    /// バージョンタブの「更新の確認」ボタン。
    /// 手動チェック結果は UpdateService 内部で VelopackUpdateDialog.Avalonia の
    /// UpdateDialogWindow を開いて表示する (DL/Apply/Restart 全部委譲)。
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        if (IsCheckingUpdate) return;

        IsCheckingUpdate = true;
        UpdateStatusText = "更新を確認しています...";
        try
        {
            await _updateService.CheckForUpdateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"CheckForUpdateAsync 失敗: {ex.Message}");
            RunOnUiThread(() => UpdateStatusText = $"更新確認でエラー: {ex.Message}");
        }
        finally
        {
            RunOnUiThread(() => IsCheckingUpdate = false);
        }
    }

    /// <summary>
    /// バージョンタブの「ログフォルダを開く」ボタン (rere P2 F-2 修正)。
    /// ユーザーが Issue 提出時に該当日付のログを添付しやすくする。
    /// </summary>
    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealTimeTranslator", "logs");
            if (!Directory.Exists(logsDir))
                Directory.CreateDirectory(logsDir);
            Process.Start(new ProcessStartInfo { FileName = logsDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggerService.LogException("OpenLogsFolder 失敗", ex);
            UpdateStatusText = $"ログフォルダを開けませんでした: {ex.Message}";
        }
    }

    /// <summary>
    /// バージョンタブの「設定フォルダを開く」ボタン (rere P2 F-2 修正)。
    /// API キーの紛失 / 移行時に settings.json の場所を素早く見せる。
    /// </summary>
    [RelayCommand]
    private void OpenSettingsFolder()
    {
        try
        {
            var settingsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealTimeTranslator");
            if (!Directory.Exists(settingsDir))
                Directory.CreateDirectory(settingsDir);
            Process.Start(new ProcessStartInfo { FileName = settingsDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggerService.LogException("OpenSettingsFolder 失敗", ex);
            UpdateStatusText = $"設定フォルダを開けませんでした: {ex.Message}";
        }
    }

    /// <summary>
    /// バージョンタブの「ご意見・ご要望」ボタン。GitHub Issues を既定ブラウザで開く。
    /// </summary>
    [RelayCommand]
    private void OpenFeedbackLink()
    {
        const string url = "https://github.com/1llum1n4t1s/RealTimeTranslator/issues";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"OpenFeedbackLink 失敗: {ex.Message}");
            UpdateStatusText = $"ブラウザを起動できませんでした: {ex.Message}";
        }
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }
        return Dispatcher.UIThread.InvokeAsync(action);
    }

    /// <summary>
    /// プロセス一覧を更新する
    /// </summary>
    [RelayCommand]
    private void RefreshProcesses()
    {
        var activeProcessIds = GetActiveAudioProcessIds();
        var currentProcessId = Environment.ProcessId;
        IEnumerable<ProcessInfo> processes;

        LoggerService.LogInfo($"RefreshProcesses: オーディオセッションを持つプロセスID = {activeProcessIds.Count}個");

        if (activeProcessIds.Count > 0)
        {
            // 旧実装は「セッションを持つプロセス名」で同名プロセスも一覧に出していたが、
            // Chrome の数十個の子プロセス・RtkAudUService64 等のシステム常駐サービスまで全部表示される問題があった。
            // Process Loopback は「セッション所有者の PID」を指定すれば子プロセスの音声も含めて取れるため、
            // 実際にオーディオセッションを持っているプロセスだけに絞る。
            var allRawProcesses = Process.GetProcesses();
            try
            {
                var allProcesses = allRawProcesses
                    .Where(p => p.Id != currentProcessId && activeProcessIds.Contains(p.Id))
                    .OrderBy(p => p.ProcessName)
                    .ThenBy(p => p.Id)
                    .ToList();

                LoggerService.LogInfo($"RefreshProcesses: オーディオセッションを持つプロセス {allProcesses.Count} 個を特定（自分自身を除外）");

                var processList = new List<ProcessInfo>();
                var processNames = new Dictionary<string, int>();

                foreach (var p in allProcesses)
                {
                    try
                    {
                        var title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle;
                        var name = p.ProcessName;

                        if (!processNames.ContainsKey(name))
                        {
                            processNames[name] = 0;
                        }
                        processNames[name]++;

                        var displayTitle = processNames[name] > 1
                            ? $"{title} (PID: {p.Id})"
                            : title;

                        // セッション所有者がそのまま CaptureProcessId。子プロセスの音声も含まれる。
                        processList.Add(new ProcessInfo
                        {
                            Id = p.Id,
                            CaptureProcessId = p.Id,
                            Name = name,
                            Title = displayTitle
                        });
                        LoggerService.LogInfo($"RefreshProcesses: プロセス追加 - {name} (PID: {p.Id}, Title: {displayTitle})");
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        LoggerService.LogError($"RefreshProcesses: プロセス情報取得エラー (PID: {p.Id}): {ex.Message}");
                    }
                }

                processes = processList;
            }
            finally
            {
                foreach (var p in allRawProcesses)
                    p.Dispose();
            }
        }
        else
        {
            LoggerService.LogInfo("RefreshProcesses: オーディオセッションを持つプロセスがないため、フォールバック処理を実行（メインウィンドウを持つプロセスを表示）");
            var fallbackRawProcesses = Process.GetProcesses();
            try
            {
                var fallbackProcesses = fallbackRawProcesses
                    .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle) && p.Id != currentProcessId)
                    .OrderBy(p => p.ProcessName)
                    .ThenBy(p => p.Id)
                    .ToList();

                LoggerService.LogInfo($"RefreshProcesses: フォールバック処理で{fallbackProcesses.Count}個のプロセスを検出");

                // Select を即時評価して ProcessInfo リストを確定させる（Process の Dispose 前にプロパティを読み取る）
                processes = fallbackProcesses.Select(p => new ProcessInfo
                {
                    Id = p.Id,
                    CaptureProcessId = p.Id,
                    Name = p.ProcessName,
                    Title = p.MainWindowTitle
                }).ToList();
            }
            finally
            {
                foreach (var p in fallbackRawProcesses)
                    p.Dispose();
            }
        }

        Dispatcher.UIThread.Invoke(() =>
        {
            Processes.Clear();
            foreach (var process in processes)
            {
                Processes.Add(process);
            }
            RestoreLastSelectedProcess();
        });

        LoggerService.LogInfo($"RefreshProcesses: プロセス一覧を更新しました（{Processes.Count}件）");
        Log($"プロセス一覧を更新しました（{Processes.Count}件）");
    }

    /// <summary>
    /// 翻訳処理を開始する
    /// </summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        if (SelectedProcess == null)
            return;

        try
        {
            // Process Loopback は STA（UI）スレッドにバインドするため、キャプチャ開始は必ず UI の SynchronizationContext で実行する。
            // ボタンクリックで呼ばれる想定なので Current は通常 WPF のコンテキスト。null の場合は Dispatcher から取得して確実に UI で実行する。
            var uiContext = SynchronizationContext.Current ?? new AvaloniaSynchronizationContext();
            if (SynchronizationContext.Current == null)
                LoggerService.LogDebug("[Capture] StartAsync: SynchronizationContext.Current was null, using Dispatcher-based context");
            // スレッドセーフにCancellationTokenSourceを置き換え
            lock (_cancellationLock)
            {
                _processingCancellation?.Cancel();
                _processingCancellation?.Dispose();
                _processingCancellation = new CancellationTokenSource();
            }
            IsRunning = true;
            StatusText = "起動中...";
            StatusColor = Brushes.Orange;

            // GameProfile（ホットワード / 辞書 / InitialPrompt）は OpenAI Realtime API 移行で削除済み。
            // 必要なら API の `instructions` フィールドにマップして復活させる。

            Log($"翻訳開始（翻訳先: {_settings.OpenAIRealtime.OutputLanguage}）");

            await _pipelineService.StartAsync(_processingCancellation.Token);

            StatusText = "音声の再生を待機中...";
            StatusColor = Brushes.Orange;
            var selectedPid = SelectedProcess.Id;
            var capturePid = SelectedProcess.CaptureProcessId;
            Log($"'{SelectedProcess.DisplayName}' (PID: {selectedPid}) の音声再生を待機しています...");
            LoggerService.LogInfo($"[キャプチャ開始] 表示PID={selectedPid}, CaptureProcessId={capturePid}, Name={SelectedProcess.Name}, DisplayName={SelectedProcess.DisplayName}");
            // Minimal で実音が取れた条件に合わせ、まず選択行の ProcessId（ウィンドウ／子プロセス）で試す。失敗時のみ CaptureProcessId（セッション所有者）でリトライする。
            var pidForCapture = selectedPid;
            LoggerService.LogDebug($"StartAsync: Starting audio capture for process: {SelectedProcess.Name} (PID: {pidForCapture}, Title: {SelectedProcess.Title})");
            var captureStarted = await _audioCaptureService.StartCaptureWithRetryAsync(
                pidForCapture,
                _processingCancellation.Token,
                uiContext);

            if (!captureStarted && capturePid != selectedPid)
            {
                LoggerService.LogInfo($"[キャプチャ] 選択PID={selectedPid} で開始できなかったため、セッション所有者PID={capturePid} でリトライします");
                captureStarted = await _audioCaptureService.StartCaptureWithRetryAsync(
                    capturePid,
                    _processingCancellation.Token,
                    uiContext);
                if (captureStarted)
                    pidForCapture = capturePid;
            }

            if (!captureStarted)
            {
                await _pipelineService.StopAsync();
                IsRunning = false;
                StatusText = "停止中";
                StatusColor = Brushes.Gray;
                Log("音声キャプチャがキャンセルされました。");
                return;
            }

            StatusText = "実行中";
            StatusColor = Brushes.Green;
            Log($"'{SelectedProcess.DisplayName}' の音声キャプチャを開始しました");

            // セッション所有者PIDでキャプチャしている場合のみ、持続無音時に選択PIDへフォールバックする監視を開始する。
            if (captureStarted && pidForCapture == capturePid && capturePid != selectedPid)
            {
                var ct = _processingCancellation?.Token ?? CancellationToken.None;
                _ = TryFallbackToWindowPidAfterSilenceAsync(selectedPid, ct);
            }
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StatusText = "エラー";
            StatusColor = Brushes.Red;
            Log($"エラー: {ex.GetType().Name}: {ex.Message}");
            LoggerService.LogException($"StartAsync Error: {ex.GetType().FullName}: {ex.Message}", ex);
            if (ex.InnerException != null)
            {
                Log($"内部エラー: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                LoggerService.LogException($"InnerException: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}", ex.InnerException);
            }
        }
    }

    /// <summary>
    /// セッション所有者 PID でキャプチャ開始後、一定時間無音なら選択ウィンドウの PID で再キャプチャを試行する。
    /// </summary>
    private async Task TryFallbackToWindowPidAfterSilenceAsync(int windowPid, CancellationToken cancellationToken)
    {
        const int silenceCheckDelayMs = 2500;
        try
        {
            await Task.Delay(silenceCheckDelayMs, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await RunOnUiThreadAsync(async () =>
        {
            if (!IsRunning || cancellationToken.IsCancellationRequested)
                return;
            if (_audioCaptureService.HasReceivedNonSilentDataSinceStart)
                return;
            LoggerService.LogInfo($"[キャプチャ] 持続無音のため選択ウィンドウPID={windowPid} で再キャプチャを試行します");
            Log("音声が検出されないため、別のプロセスで再キャプチャを試行します…");
            _audioCaptureService.StopCapture();
            var uiCtx = SynchronizationContext.Current ?? new AvaloniaSynchronizationContext();
            var started = await _audioCaptureService.StartCaptureWithRetryAsync(
                windowPid,
                cancellationToken,
                uiCtx);
            if (started)
            {
                LoggerService.LogInfo($"[キャプチャ] 選択ウィンドウPID={windowPid} でキャプチャを開始しました");
                Log("再キャプチャを開始しました");
            }
        });
    }

    /// <summary>
    /// 翻訳処理を停止する
    /// </summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        // スレッドセーフにCancellationTokenSourceをクリーンアップ
        lock (_cancellationLock)
        {
            _processingCancellation?.Cancel();
            _processingCancellation?.Dispose();
            _processingCancellation = null;
        }

        // pipelineService.StopAsync は内部で WASAPI 停止 (Task.Run+3s) + audio task (2s) +
        // WS close (3s) の wait を順次含む。 全体で最大 ~8s だが通常は数百ms 以下で完了する。
        // 何らかの異常で詰まった場合に UI を永久に固まらせないよう外側 10s でタイムアウトさせ、
        // 超過したらバックグラウンドで完了させて UI は先に進める。
        // ⚠️ _audioCaptureService.StopCapture() を UI スレッドから直接呼ぶと NAudio/WASAPI の
        // native callback 完了待ちでフリーズするため (2026-05-17 観測)、 ここでは呼ばない。
        // pipelineService.StopAsync の内部で Task.Run 経由で停止する設計に変更済み。
        try
        {
            await _pipelineService.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            LoggerService.LogWarning("StopAsync: pipelineService.StopAsync が 10 秒を超えたため UI を先に進めます（後始末はバックグラウンド継続）");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"StopAsync: pipelineService.StopAsync で例外: {ex}");
        }

        _overlayViewModel.ClearSubtitles();

        IsRunning = false;
        StatusText = "停止中";
        StatusColor = Brushes.Gray;
        Log("音声キャプチャを停止しました");
    }

    private void Log(string message, bool suppressDuplicate = false)
    {
        if (suppressDuplicate)
        {
            var baseMessage = ExtractBaseMessage(message);
            var lastBaseMessage = _lastLogMessage != null ? ExtractBaseMessage(_lastLogMessage) : null;

            if (baseMessage == lastBaseMessage)
            {
                return;
            }
        }

        _lastLogMessage = message;

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var logLine = $"[{timestamp}] {message}";

        // LogText を `+=` で組み立てると、1000 行に達するまで毎回 string 全体を再構築して
        // 累計で O(N²) のヒープを通過する。常に Queue から StringBuilder で組み立てて
        // 1 呼び出しを O(N)（N = 現在の行数, 上限 MaxLogLines）に抑える。
        string newText;
        lock (_logLock)
        {
            _logLines.Enqueue(logLine);
            while (_logLines.Count > MaxLogLines)
            {
                _logLines.Dequeue();
            }
            var sb = new StringBuilder(_logLines.Count * 80);
            foreach (var line in _logLines)
            {
                sb.AppendLine(line);
            }
            newText = sb.ToString();
        }
        LogText = newText;
    }

    /// <summary>
    /// メッセージから数値・パーセント部分を除去してベース部分を抽出
    /// </summary>
    private static string ExtractBaseMessage(string message)
    {
        return s_numericPattern.Replace(message, "").Trim();
    }


    private void OnCaptureStatusChanged(object? sender, CaptureStatusEventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (e.IsWaiting)
            {
                StatusText = e.Message;
                StatusColor = Brushes.Orange;
            }
            Log(e.Message, suppressDuplicate: e.IsWaiting);
        });
    }


    // OnSelectedProcessChanged は SaveLastSelectedProcess の副作用のため partial void を残す。
    // CanStart の通知は [NotifyPropertyChangedFor] が SelectedProcess 側で行う想定だが、
    // SelectedProcess は元々 [ObservableProperty] 由来ではなく manual な setter 経由のため、
    // 既存挙動維持として明示通知を残す。
    partial void OnSelectedProcessChanged(ProcessInfo? value)
    {
        OnPropertyChanged(nameof(CanStart));
        SaveLastSelectedProcess(value);
    }
    // IsRunning / IsLoading / IsApiKeyConfigured の CanStart 通知は
    // [NotifyPropertyChangedFor(nameof(CanStart))] でフィールド側に集約済み (手動 OnPropertyChanged 排除)。

    private void RestoreLastSelectedProcess()
    {
        if (Processes.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.LastSelectedProcessName))
        {
            SelectFirstProcessIfNeeded();
            return;
        }

        // プロセスIDが保存されていれば、同名が複数ある場合でも正しいプロセス（音声を出している方）を復元する
        if (_settings.LastSelectedProcessId > 0)
        {
            var matchById = Processes.FirstOrDefault(p => p.Id == _settings.LastSelectedProcessId);
            if (matchById != null && !Equals(SelectedProcess, matchById))
            {
                SelectedProcess = matchById;
                Log($"前回選択したプロセス '{matchById.DisplayName}' を復元しました（PID 一致）");
                return;
            }
        }

        var matchByName = Processes.FirstOrDefault(p =>
            p.Name.Equals(_settings.LastSelectedProcessName, StringComparison.OrdinalIgnoreCase));
        if (matchByName != null && !Equals(SelectedProcess, matchByName))
        {
            SelectedProcess = matchByName;
            Log($"前回選択したプロセス '{matchByName.DisplayName}' を復元しました");
            return;
        }

        SelectFirstProcessIfNeeded();
    }

    private void SelectFirstProcessIfNeeded()
    {
        if (SelectedProcess != null || Processes.Count == 0)
        {
            return;
        }

        SelectedProcess = Processes[0];
        Log($"最初のプロセス '{SelectedProcess.DisplayName}' を既定選択しました");
    }

    private void SaveLastSelectedProcess(ProcessInfo? process)
    {
        if (process == null)
        {
            return;
        }

        _settings.LastSelectedProcessName = process.Name;
        _settings.LastSelectedProcessId = process.Id;
        Log($"選択プロセス '{process.DisplayName}' を設定に保存しました");
    }

    /// <summary>
    /// 現在オーディオセッションを持つプロセスのIDを取得
    /// </summary>
    private static HashSet<int> GetActiveAudioProcessIds()
    {
        var processIds = new HashSet<int>();
        try
        {
            LoggerService.LogInfo("オーディオセッション列挙を開始");
            using var enumerator = new MMDeviceEnumerator();

            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            LoggerService.LogInfo($"アクティブなオーディオデバイス数: {devices.Count}");

            var deviceIndex = 0;
            foreach (var device in devices)
            {
                try
                {
                    LoggerService.LogInfo($"デバイス[{deviceIndex}]: {device.FriendlyName} (ID: {device.ID})");

                    var sessionManager = device.AudioSessionManager;
                    var sessions = sessionManager.Sessions;
                    LoggerService.LogInfo($"デバイス[{deviceIndex}] オーディオセッション総数: {sessions.Count}");

                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        try
                        {
                            var stateValue = (int)session.State;
                            LoggerService.LogInfo($"デバイス[{deviceIndex}] セッション[{i}]: State={stateValue} (0=Inactive, 1=Active, 2=Expired)");

                            if (stateValue != 2)
                            {
                                uint processId = 0;

                                try
                                {
                                    processId = session.GetProcessID;
                                    LoggerService.LogInfo($"デバイス[{deviceIndex}] セッション[{i}]: ProcessID={processId}を取得");
                                }
                                catch (InvalidOperationException ex)
                                {
                                    LoggerService.LogWarning($"デバイス[{deviceIndex}] セッション[{i}]: ProcessIDの取得失敗（IAudioSessionControl2未サポート）: {ex.Message}");
                                }
                                catch (System.Runtime.InteropServices.COMException ex)
                                {
                                    LoggerService.LogWarning($"デバイス[{deviceIndex}] セッション[{i}]: ProcessIDの取得失敗（COMエラー）: HResult=0x{ex.HResult:X}, Message={ex.Message}");
                                }
                                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                                {
                                    LoggerService.LogWarning($"デバイス[{deviceIndex}] セッション[{i}]: ProcessIDの取得失敗: {ex.GetType().Name}: {ex.Message}");
                                }

                                if (processId > 0)
                                {
                                    var stateName = stateValue switch
                                    {
                                        0 => "Inactive",
                                        1 => "Active",
                                        _ => "Unknown"
                                    };

                                    if (processIds.Add((int)processId))
                                    {
                                        LoggerService.LogInfo($"デバイス[{deviceIndex}] セッション[{i}]: ProcessID={processId} (State={stateName}, Device={device.FriendlyName}) を検出");
                                    }
                                    else
                                    {
                                        LoggerService.LogInfo($"デバイス[{deviceIndex}] セッション[{i}]: ProcessID={processId} (State={stateName}) は既に検出済み");
                                    }
                                }
                                else
                                {
                                    LoggerService.LogWarning($"デバイス[{deviceIndex}] セッション[{i}]: ProcessIDが取得できませんでした（State={stateValue}）");
                                }
                            }
                            else
                            {
                                LoggerService.LogInfo($"デバイス[{deviceIndex}] セッション[{i}]: Expired状態のため除外");
                            }
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                        {
                            LoggerService.LogError($"デバイス[{deviceIndex}] セッション[{i}]の情報取得に失敗: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    deviceIndex++;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    LoggerService.LogError($"デバイス[{deviceIndex}]の処理に失敗: {ex.GetType().Name}: {ex.Message}");
                    deviceIndex++;
                }
            }

            LoggerService.LogInfo($"オーディオセッションを持つプロセスを{processIds.Count}個検出: [{string.Join(", ", processIds)}]");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LoggerService.LogError($"オーディオセッション列挙に失敗: {ex.GetType().Name}: {ex.Message}\nStackTrace: {ex.StackTrace}");
        }

        return processIds;
    }

    /// <summary>
    /// MainViewModel のディスポーズ
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        LoggerService.LogDebug("MainViewModel.Dispose: 開始");

        try
        {
            // UIスレッドでのデッドロックを回避するため Task.Run 経由で呼び出す
            Task.Run(() => _pipelineService.StopAsync()).GetAwaiter().GetResult();
            LoggerService.LogInfo("MainViewModel.Dispose: パイプライン停止完了");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"MainViewModel.Dispose: パイプライン停止エラー: {ex.Message}");
        }

        try
        {
            _audioCaptureService.StopCapture();
            LoggerService.LogInfo("MainViewModel.Dispose: 音声キャプチャ停止完了");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"MainViewModel.Dispose: 音声キャプチャ停止エラー: {ex.Message}");
        }

        lock (_cancellationLock)
        {
            _processingCancellation?.Cancel();
            _processingCancellation?.Dispose();
            _processingCancellation = null;
        }

        _settingsChangeSubscription?.Dispose();

        _pipelineService.SubtitleGenerated -= OnSubtitleGenerated;
        _pipelineService.StatsUpdated -= OnPipelineStatsUpdated;
        _pipelineService.ErrorOccurred -= OnPipelineError;
        _audioCaptureService.CaptureStatusChanged -= OnCaptureStatusChanged;
        _settingsViewModel.SettingsSaved -= OnSettingsSaved;
        _updateService.StatusChanged -= OnUpdateStatusChanged;
        // UpdateAvailable イベント解除も不要 (v1.0.12 で廃止)

        LoggerService.LogInfo("MainViewModel.Dispose: イベントハンドラ解除完了");

        _disposed = true;
        GC.SuppressFinalize(this);
        LoggerService.LogInfo("MainViewModel.Dispose: 完了");
    }
}

/// <summary>
/// プロセス情報
/// </summary>
public class ProcessInfo
{
    /// <summary>
    /// プロセスID（表示・選択用）
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Process Loopback キャプチャに使うプロセスID（オーディオセッションを持つプロセス。同名の子プロセスの場合はセッション所有者の PID）
    /// </summary>
    public int CaptureProcessId { get; set; }

    /// <summary>
    /// プロセス名
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// ウィンドウタイトル
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 表示用の名前（プロセス名 (PID: xxx) - タイトル）
    /// </summary>
    public string DisplayName => $"{Name} (PID: {Id}) - {Title}";
}
