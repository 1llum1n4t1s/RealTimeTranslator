using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// ログレベルを表す列挙型
/// </summary>
public enum LogLevel
{
    /// <summary>デバッグレベル</summary>
    Debug,

    /// <summary>情報レベル</summary>
    Info,

    /// <summary>警告レベル</summary>
    Warning,

    /// <summary>エラーレベル</summary>
    Error
}

/// <summary>
/// ログ初期化設定
/// </summary>
public sealed class LoggerConfig
{
    /// <summary>ログ出力ディレクトリ</summary>
    public required string LogDirectory { get; init; }

    /// <summary>ログファイル名のプレフィックス（例: "RealTimeTranslator"）</summary>
    public required string FilePrefix { get; init; }

    /// <summary>ローリングサイズ上限（MB）</summary>
    public int MaxSizeMB { get; init; } = 10;

    /// <summary>アーカイブファイルの最大保持数</summary>
    public int MaxArchiveFiles { get; init; } = 10;

    /// <summary>ログファイルの保持日数（0以下の場合は削除しない）</summary>
    public int RetentionDays { get; init; } = 7;
}

/// <summary>
/// NLogを使用したログ出力クラス。ファイル・コンソール・UIコールバックを統一して利用する。
/// </summary>
public static class LoggerService
{
    /// <summary>NLogロガーインスタンス</summary>
    private static NLog.Logger? _logger;

    /// <summary>初期化済みフラグ</summary>
    private static bool _isConfigured;

    /// <summary>アプリケーション名</summary>
    private static string _appName = "RealTimeTranslator";

    /// <summary>ログ出力ディレクトリ</summary>
    private static string _logDirectory = AppDomain.CurrentDomain.BaseDirectory;

    /// <summary>ログファイル名のプレフィックス</summary>
    private static string _filePrefix = "RealTimeTranslator";

    /// <summary>UIログ出力のコールバック</summary>
    private static Action<string>? _uiLogCallback;

    /// <summary>UIログターゲットが追加済みかどうか</summary>
    private static bool _uiTargetAdded;

    /// <summary>
    /// 最小ログレベル（これ以上のレベルのログのみ出力）
    /// </summary>
    private static readonly LogLevel MinLogLevel =
#if DEBUG
        LogLevel.Debug;
#else
        LogLevel.Info;
#endif

    /// <summary>
    /// ロガーを初期化する
    /// </summary>
    /// <param name="config">ログ設定（nullの場合はデフォルト設定を使用）</param>
    public static void Initialize(LoggerConfig? config = null)
    {
        if (_isConfigured) return;

        var effectiveConfig = config ?? new LoggerConfig
        {
            LogDirectory = AppDomain.CurrentDomain.BaseDirectory,
            FilePrefix = "RealTimeTranslator"
        };

        _appName = effectiveConfig.FilePrefix;
        _logDirectory = effectiveConfig.LogDirectory;
        _filePrefix = effectiveConfig.FilePrefix;

        if (!Directory.Exists(effectiveConfig.LogDirectory))
        {
            Directory.CreateDirectory(effectiveConfig.LogDirectory);
        }

        var nlogConfig = new LoggingConfiguration();

        var fileTarget = new FileTarget("file")
        {
            FileName = Path.Combine(effectiveConfig.LogDirectory, $"{effectiveConfig.FilePrefix}_${{date:format=yyyyMMdd}}.log"),
            ArchiveAboveSize = effectiveConfig.MaxSizeMB * 1024 * 1024,
            ArchiveFileName = Path.Combine(effectiveConfig.LogDirectory, $"{effectiveConfig.FilePrefix}_${{date:format=yyyyMMdd}}_{{##}}.log"),
            ArchiveNumbering = ArchiveNumberingMode.Rolling,
            MaxArchiveFiles = effectiveConfig.MaxArchiveFiles,
            Layout = "${longdate} [${uppercase:${level}}] ${message}${onexception:inner=${newline}${exception:format=tostring}}",
            Encoding = System.Text.Encoding.UTF8
        };

        var consoleTarget = new ConsoleTarget("console")
        {
            Layout = "${longdate} [${uppercase:${level}}] ${message}${onexception:inner=${newline}${exception:format=tostring}}"
        };

        nlogConfig.AddTarget(fileTarget);
        nlogConfig.AddTarget(consoleTarget);

        nlogConfig.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, fileTarget);
        nlogConfig.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, consoleTarget);

        LogManager.Configuration = nlogConfig;
        _logger = LogManager.GetLogger(effectiveConfig.FilePrefix);
        _isConfigured = true;

        Log("Logger initialized with NLog (RollingFile)", LogLevel.Debug);

        // 過去のバグで作成された不要な "0" ファイルを削除
        CleanupStaleFile(Path.Combine(effectiveConfig.LogDirectory, "0"));

        // 保持期間を超えた古いログファイルを削除
        CleanupOldLogFiles(effectiveConfig.LogDirectory, effectiveConfig.FilePrefix, effectiveConfig.RetentionDays);
    }

    /// <summary>
    /// UIログコールバックを設定する。ログメッセージがUIにも転送されるようになる。
    /// </summary>
    /// <param name="callback">ログメッセージを受け取るコールバック</param>
    public static void SetUILogCallback(Action<string> callback)
    {
        _uiLogCallback = callback;

        // 初期化がまだの場合は先に初期化
        if (!_isConfigured) Initialize();
        if (_uiTargetAdded) return;

        try
        {
            var nlogConfig = LogManager.Configuration;
            if (nlogConfig == null) return;

            var uiTarget = new UILogTarget(() => _uiLogCallback)
            {
                Layout = "${message}"
            };

            nlogConfig.AddTarget("ui", uiTarget);
            nlogConfig.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, uiTarget);
            LogManager.ReconfigExistingLoggers();
            _uiTargetAdded = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UILogTarget add error: {ex.Message}");
        }
    }

    /// <summary>
    /// アプリケーション終了時に呼び出してログをフラッシュする
    /// </summary>
    public static void Shutdown()
    {
        LogManager.Shutdown();
        _isConfigured = false;
    }

    /// <summary>
    /// 過去のバグで作成された不要ファイルを削除する
    /// </summary>
    /// <param name="filePath">削除対象のファイルパス</param>
    private static void CleanupStaleFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Log($"不要なファイルを削除しました: {Path.GetFileName(filePath)}", LogLevel.Debug);
            }
        }
        catch (Exception ex)
        {
            Log($"不要ファイルの削除に失敗しました: {filePath} - {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>
    /// 保持期間を超えた古いログファイルを削除する
    /// </summary>
    /// <param name="logDirectory">ログディレクトリ</param>
    /// <param name="filePrefix">ログファイル名のプレフィックス</param>
    /// <param name="retentionDays">保持日数（0以下の場合は削除しない）</param>
    private static void CleanupOldLogFiles(string logDirectory, string filePrefix, int retentionDays)
    {
        if (retentionDays <= 0) return;

        try
        {
            var cutoffDate = DateTime.Now.Date.AddDays(-retentionDays);
            var logFiles = Directory.GetFiles(logDirectory, $"{filePrefix}_*.log");

            foreach (var file in logFiles)
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var parts = fileName.Split('_');
                    if (parts.Length >= 2 && parts[1].Length == 8 &&
                        DateTime.TryParseExact(parts[1], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var fileDate))
                    {
                        if (fileDate < cutoffDate)
                        {
                            File.Delete(file);
                            Log($"古いログファイルを削除しました: {Path.GetFileName(file)}", LogLevel.Debug);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"ログファイルの削除に失敗しました: {Path.GetFileName(file)} - {ex.Message}", LogLevel.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"ログファイルのクリーンアップ中にエラーが発生しました: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>
    /// ログを出力する
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    /// <param name="level">ログレベル（デフォルト: Info）</param>
    public static void Log(string message, LogLevel level = LogLevel.Info)
    {
        if (level < MinLogLevel)
            return;

        if (!_isConfigured) Initialize();
        _logger?.Log(ToNLogLevel(level), message);
    }

    /// <summary>
    /// 複数行のログを出力する
    /// </summary>
    /// <param name="messages">ログメッセージの配列</param>
    /// <param name="level">ログレベル（デフォルト: Info）</param>
    public static void LogLines(string[] messages, LogLevel level = LogLevel.Info)
    {
        if (messages == null || messages.Length == 0) return;
        if (level < MinLogLevel) return;

        if (!_isConfigured) Initialize();
        var nlogLevel = ToNLogLevel(level);
        foreach (var message in messages)
        {
            _logger?.Log(nlogLevel, message);
        }
    }

    /// <summary>
    /// 例外情報を含むログを出力する（常にErrorレベル）
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    /// <param name="exception">例外オブジェクト</param>
    public static void LogException(string message, Exception exception)
    {
        if (!_isConfigured) Initialize();
        _logger?.Error(exception, message);
    }

    /// <summary>
    /// デバッグレベルのログを出力する
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    public static void LogDebug(string message)
    {
        Log(message, LogLevel.Debug);
    }

    /// <summary>
    /// 情報レベルのログを出力する
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    public static void LogInfo(string message)
    {
        Log(message, LogLevel.Info);
    }

    /// <summary>
    /// 警告レベルのログを出力する
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    public static void LogWarning(string message)
    {
        Log(message, LogLevel.Warning);
    }

    /// <summary>
    /// エラーレベルのログを出力する
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    public static void LogError(string message)
    {
        Log(message, LogLevel.Error);
    }

    /// <summary>
    /// ログファイルをクリアする
    /// </summary>
    public static void ClearLogFile()
    {
        try
        {
            var logFiles = Directory.GetFiles(_logDirectory, $"{_filePrefix}_*.log");
            foreach (var file in logFiles)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ログファイル削除エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// ログ出力ディレクトリのパスを取得する
    /// </summary>
    /// <returns>ログ出力ディレクトリのパス</returns>
    public static string GetLogFilePath()
    {
        return _logDirectory;
    }

    /// <summary>
    /// アプリケーション起動時のログを出力する（Debugレベル）
    /// </summary>
    public static void LogStartup()
    {
        var messages = new List<string>
        {
            $"=== {_appName} 起動ログ ===",
            $"起動時刻: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}",
            $"実行ファイルパス: {Environment.ProcessPath}",
            $".NET Version: {Environment.Version}",
            $"OS Version: {Environment.OSVersion}",
            $"Processor Count: {Environment.ProcessorCount}"
        };
        LogLines([.. messages], LogLevel.Debug);
    }

    /// <summary>
    /// 独自LogLevelをNLogのLogLevelに変換
    /// </summary>
    /// <param name="level">独自LogLevel</param>
    /// <returns>NLogのLogLevel</returns>
    private static NLog.LogLevel ToNLogLevel(LogLevel level) => level switch
    {
        LogLevel.Debug => NLog.LogLevel.Debug,
        LogLevel.Info => NLog.LogLevel.Info,
        LogLevel.Warning => NLog.LogLevel.Warn,
        LogLevel.Error => NLog.LogLevel.Error,
        _ => NLog.LogLevel.Info
    };

    /// <summary>
    /// UIにログを転送するためのNLogカスタムTarget
    /// </summary>
    private sealed class UILogTarget : TargetWithLayout
    {
        /// <summary>UIコールバック取得用デリゲート</summary>
        private readonly Func<Action<string>?> _getCallback;

        /// <summary>
        /// UILogTargetのコンストラクタ
        /// </summary>
        /// <param name="getCallback">UIコールバック取得用デリゲート</param>
        public UILogTarget(Func<Action<string>?> getCallback)
        {
            _getCallback = getCallback;
            Name = "UILog";
        }

        /// <summary>
        /// ログイベントをUIコールバックに転送する
        /// </summary>
        /// <param name="logEvent">NLogのログイベント情報</param>
        protected override void Write(LogEventInfo logEvent)
        {
            var cb = _getCallback();
            if (cb == null) return;

            var msg = Layout.Render(logEvent);
            var level = logEvent.Level.Name.ToUpperInvariant();
            try
            {
                cb($"[{level}] {msg}".TrimEnd());
            }
            catch
            {
                // UIコールバック内の例外は無視
            }
        }
    }
}
