using System.Diagnostics;
using System.Text;

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
/// ログ出力機能を提供するクラス
/// ファイル、デバッグ出力ウィンドウ、UI ログの統一管理
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
    /// ログファイルの最大行数
    /// </summary>
    private const int MaxLogLines = 1000;

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
    /// UI ログコールバックを設定
    /// </summary>
    /// <param name="callback">ログメッセージを受け取るコールバック</param>
    public static void SetUILogCallback(Action<string> callback)
    {
        _uiLogCallback = callback;
    }

    /// <summary>
    /// ログを出力する
    /// ファイル、デバッグ出力ウィンドウ、UI ログに統一出力
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    /// <param name="level">ログレベル（デフォルト: Info）</param>
    public static void Log(string message, LogLevel level = LogLevel.Info)
    {
        if (level < MinLogLevel)
            return;

        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var formattedMessage = $"[{timestamp}] [{level}] {message}";

            // ファイルに出力
            File.AppendAllText(LogFilePath, formattedMessage + Environment.NewLine, Encoding.UTF8);
            TrimLogFile();

            // UI ログに出力（時刻は含めない：UI 側で管理）
            var uiMessage = $"[{level}] {message}";
            _uiLogCallback?.Invoke(uiMessage);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ログ出力エラー: {ex.Message}");
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

        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logLines = messages.Select(message => $"[{timestamp}] [{level}] {message}").ToArray();

            // ファイルに出力
            File.AppendAllLines(LogFilePath, logLines, Encoding.UTF8);
            TrimLogFile();

            // UI ログに出力
            foreach (var message in messages)
            {
                var uiMessage = $"[{level}] {message}";
                _uiLogCallback?.Invoke(uiMessage);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ログ出力エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// 例外情報を含むログを出力する（常にErrorレベル）
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    /// <param name="exception">例外オブジェクト</param>
    public static void LogException(string message, Exception exception)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logMessage = new StringBuilder();
            logMessage.AppendLine($"[{timestamp}] [Error] {message}");
            logMessage.AppendLine($"例外: {exception.GetType().Name} - {exception.Message}");
            logMessage.AppendLine($"スタックトレース: {exception.StackTrace}");

            // InnerExceptionも記録
            if (exception.InnerException != null)
            {
                logMessage.AppendLine($"InnerException: {exception.InnerException.GetType().Name} - {exception.InnerException.Message}");
                logMessage.AppendLine($"InnerStackTrace: {exception.InnerException.StackTrace}");
            }

            var content = logMessage.ToString();

            // ファイルに出力
            File.AppendAllText(LogFilePath, content, Encoding.UTF8);
            TrimLogFile();

            // UI ログに出力
            _uiLogCallback?.Invoke($"[Error] {message}: {exception.Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ログ出力エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// デバッグレベルのログを出力
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    public static void LogDebug(string message)
    {
        Log(message, LogLevel.Debug);
    }

    /// <summary>
    /// 情報レベルのログを出力
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    public static void LogInfo(string message)
    {
        Log(message, LogLevel.Info);
    }

    /// <summary>
    /// 警告レベルのログを出力
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    public static void LogWarning(string message)
    {
        Log(message, LogLevel.Warning);
    }

    /// <summary>
    /// エラーレベルのログを出力
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    public static void LogError(string message)
    {
        Log(message, LogLevel.Error);
    }

    /// <summary>
    /// ログファイルを最大行数に制限する
    /// </summary>
    private static void TrimLogFile()
    {
        try
        {
            if (File.Exists(LogFilePath))
            {
                var lines = File.ReadAllLines(LogFilePath, Encoding.UTF8);
                if (lines.Length > MaxLogLines)
                {
                    var trimmedLines = lines.Skip(lines.Length - MaxLogLines).ToArray();
                    File.WriteAllLines(LogFilePath, trimmedLines, Encoding.UTF8);
                }
            }
        }
        catch (Exception ex)
        {
            // ログファイル整理エラーの場合はデバッグ出力ウィンドウに最終手段として出力
            Debug.WriteLine($"ログファイル整理エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// ログファイルをクリア
    /// </summary>
    public static void ClearLogFile()
    {
        try
        {
            if (File.Exists(LogFilePath))
            {
                File.Delete(LogFilePath);
            }
        }
        catch (Exception ex)
        {
            // ログファイル削除エラーの場合はデバッグ出力ウィンドウに最終手段として出力
            Debug.WriteLine($"ログファイル削除エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// ログファイルのパスを取得
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
}
