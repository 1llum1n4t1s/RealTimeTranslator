using System.Diagnostics;
using System.Reflection;

using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// ログレベルを表す列挙型
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// log4net を利用したログ出力のファサード
/// ファイル・デバッグ出力・UI コールバックを統一して利用する
/// </summary>
public static class LoggerService
{
    /// <summary>
    /// ログファイルのパス
    /// </summary>
    private static readonly string LogFilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "RealTimeTranslator.log");

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
    /// UI ログ出力のコールバック
    /// </summary>
    private static Action<string>? _uiLogCallback;

    /// <summary>
    /// UI ログ用のカスタム Appender（設定後に参照を保持）
    /// </summary>
    private static UILogAppender? _uiAppender;

    /// <summary>
    /// log4net の logger
    /// </summary>
    private static readonly ILog LogImpl = LogManager.GetLogger("RealTimeTranslator");

    /// <summary>
    /// 初期化済みフラグ
    /// </summary>
    private static bool _initialized;

    /// <summary>
    /// 初期化用ロック
    /// </summary>
    private static readonly object InitLock = new();

    /// <summary>
    /// log4net を設定し、未初期化なら一度だけ初期化する
    /// </summary>
    private static void EnsureInitialized()
    {
        if (_initialized)
            return;
        lock (InitLock)
        {
            if (_initialized)
                return;
            try
            {
                GlobalContext.Properties["LogPath"] = LogFilePath;
                var entryAssembly = Assembly.GetEntryAssembly();
                var baseDir = !string.IsNullOrEmpty(entryAssembly?.Location)
                    ? Path.GetDirectoryName(entryAssembly.Location)
                    : null;
                var configDir = !string.IsNullOrEmpty(baseDir) ? baseDir : AppDomain.CurrentDomain.BaseDirectory;
                var configPath = Path.Combine(configDir, "log4net.config");
                var repository = LogManager.GetRepository(typeof(LoggerService).Assembly);
                if (File.Exists(configPath))
                    XmlConfigurator.Configure(repository, new FileInfo(configPath));
                else
                    XmlConfigurator.Configure(repository);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"log4net config error: {ex.Message}");
            }
            _initialized = true;
        }
    }

    /// <summary>
    /// UI ログコールバックを設定
    /// </summary>
    /// <param name="callback">ログメッセージを受け取るコールバック</param>
    public static void SetUILogCallback(Action<string> callback)
    {
        _uiLogCallback = callback;
        EnsureInitialized();
        if (_uiAppender != null)
            return;
        try
        {
            var hierarchy = (Hierarchy)LogManager.GetRepository();
            _uiAppender = new UILogAppender(() => _uiLogCallback);
            _uiAppender.Layout = new PatternLayout("%message");
            _uiAppender.ActivateOptions();
            hierarchy.Root.AddAppender(_uiAppender);
            hierarchy.Configured = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UILogAppender add error: {ex.Message}");
        }
    }

    /// <summary>
    /// アプリケーション終了時に呼び出してログをフラッシュ
    /// </summary>
    public static void Shutdown()
    {
        LogManager.Shutdown();
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
        EnsureInitialized();
        switch (level)
        {
            case LogLevel.Debug:
                LogImpl.Logger.Log(typeof(LoggerService), Level.Debug, message, null);
                break;
            case LogLevel.Info:
                LogImpl.Logger.Log(typeof(LoggerService), Level.Info, message, null);
                break;
            case LogLevel.Warning:
                LogImpl.Logger.Log(typeof(LoggerService), Level.Warn, message, null);
                break;
            case LogLevel.Error:
                LogImpl.Logger.Log(typeof(LoggerService), Level.Error, message, null);
                break;
        }
    }

    /// <summary>
    /// 複数行のログを出力する
    /// </summary>
    /// <param name="messages">ログメッセージの配列</param>
    /// <param name="level">ログレベル（デフォルト: Info）</param>
    public static void LogLines(string[] messages, LogLevel level = LogLevel.Info)
    {
        if (level < MinLogLevel)
            return;
        EnsureInitialized();
        foreach (var message in messages)
        {
            switch (level)
            {
                case LogLevel.Debug:
                    LogImpl.Logger.Log(typeof(LoggerService), Level.Debug, message, null);
                    break;
                case LogLevel.Info:
                    LogImpl.Logger.Log(typeof(LoggerService), Level.Info, message, null);
                    break;
                case LogLevel.Warning:
                    LogImpl.Logger.Log(typeof(LoggerService), Level.Warn, message, null);
                    break;
                case LogLevel.Error:
                    LogImpl.Logger.Log(typeof(LoggerService), Level.Error, message, null);
                    break;
            }
        }
    }

    /// <summary>
    /// 例外情報を含むログを出力する（常にErrorレベル）
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    /// <param name="exception">例外オブジェクト</param>
    public static void LogException(string message, Exception exception)
    {
        EnsureInitialized();
        LogImpl.Logger.Log(typeof(LoggerService), Level.Error, message, exception);
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
            if (File.Exists(LogFilePath))
                File.Delete(LogFilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ログファイル削除エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// ログファイルのパスを取得する
    /// </summary>
    /// <returns>ログファイルのパス</returns>
    public static string GetLogFilePath()
    {
        return LogFilePath;
    }

    /// <summary>
    /// アプリケーション起動時のログを出力する（Debugレベル）
    /// </summary>
    public static void LogStartup()
    {
        var messages = new List<string>
        {
            "=== RealTimeTranslator 起動ログ ===",
            $"起動時刻: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}",
            $"実行ファイルパス: {Environment.ProcessPath}",
            $".NET Version: {Environment.Version}",
            $"OS Version: {Environment.OSVersion}",
            $"Processor Count: {Environment.ProcessorCount}"
        };
        LogLines(messages.ToArray(), LogLevel.Debug);
    }

    /// <summary>
    /// UI に転送するための log4net カスタム Appender
    /// </summary>
    private sealed class UILogAppender : AppenderSkeleton
    {
        private readonly Func<Action<string>?> _getCallback;

        public UILogAppender(Func<Action<string>?> getCallback)
        {
            _getCallback = getCallback;
        }

        protected override void Append(LoggingEvent loggingEvent)
        {
            var cb = _getCallback();
            if (cb == null)
                return;
            var msg = RenderLoggingEvent(loggingEvent);
            var level = loggingEvent.Level?.DisplayName ?? "Info";
            try
            {
                cb.Invoke($"[{level}] {msg}".TrimEnd());
            }
            catch
            {
                // UI コールバック内の例外は無視
            }
        }
    }
}
