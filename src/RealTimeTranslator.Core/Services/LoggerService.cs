// System / System.Collections.Generic / System.IO / System.Linq は GlobalUsings 経由 (rere /opop Cleaner #4)。
using SuperLightLogger;

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
/// SuperLightLoggerを使用したログ出力クラス。ファイル・コンソール・UIコールバックを統一して利用する。
/// </summary>
public static class LoggerService
{
    /// <summary>ロガーインスタンス</summary>
    private static ILog? _logger;

    /// <summary>初期化済みフラグ（スレッドセーフ: 0=未, 1=済）</summary>
    private static int _isConfigured;

    /// <summary>初期化中ガード（再入防止: 0=未, 1=実行中）</summary>
    private static int _initializing;

    /// <summary>アプリケーション名</summary>
    private static string _appName = "RealTimeTranslator";

    /// <summary>
    /// ログ出力ディレクトリ。
    /// Velopack のインストールルート (%LocalAppData%/RealTimeTranslator) と衝突して
    /// 更新時に消える可能性があるため、Velopack 管理外の %APPDATA%/Roaming に置く。
    /// </summary>
    private static string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RealTimeTranslator",
        "logs");

    /// <summary>ログファイル名のプレフィックス</summary>
    private static string _filePrefix = "RealTimeTranslator";

    /// <summary>UIログ出力のコールバック</summary>
    private static Action<string>? _uiLogCallback;

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
        if (Volatile.Read(ref _isConfigured) != 0) return;
        if (Interlocked.CompareExchange(ref _initializing, 1, 0) != 0) return;

        try
        {
            var effectiveConfig = config ?? new LoggerConfig
            {
                LogDirectory = _logDirectory,
                FilePrefix = "RealTimeTranslator"
            };

            _appName = effectiveConfig.FilePrefix;
            _logDirectory = effectiveConfig.LogDirectory;
            _filePrefix = effectiveConfig.FilePrefix;

            // ディレクトリ作成失敗（権限なし / 不正パス）でも crash させない。
            // 失敗時はファイルロガーを無効化しつつ初期化済みフラグを立てて、
            // 後続の Log() 呼び出しが auto-init ループに陥らないようにする。
            try
            {
                if (!Directory.Exists(effectiveConfig.LogDirectory))
                {
                    Directory.CreateDirectory(effectiveConfig.LogDirectory);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"LoggerService.Initialize: ログディレクトリ '{effectiveConfig.LogDirectory}' へのアクセスに失敗。ファイルログを無効化します: {ex.Message}");
                // _logger は null のまま、_isConfigured を 1 に立てて auto-init を抑止する。
                Volatile.Write(ref _isConfigured, 1);
                return;
            }

            var minLevel =
#if DEBUG
                "Debug";
#else
                "Information";
#endif

            LogManager.Configure(builder =>
            {
                builder.SetMinimumLevel(minLevel);
                builder.AddSuperLightFile(opt =>
                {
                    opt.FileName = Path.Combine(effectiveConfig.LogDirectory, $"{effectiveConfig.FilePrefix}_${{shortdate}}.log");
                    opt.Layout = "${longdate} [${level:uppercase=true}] ${message}${onexception:inner=${newline}${exception:format=tostring}}";
                    opt.ArchiveAboveSize = effectiveConfig.MaxSizeMB * 1024 * 1024;
                    opt.ArchiveFileName = Path.Combine(effectiveConfig.LogDirectory, $"{effectiveConfig.FilePrefix}_${{shortdate}}_{{#}}.log");
                    opt.ArchiveNumbering = ArchiveNumbering.Sequence;
                    opt.MaxArchiveFiles = effectiveConfig.MaxArchiveFiles;
                    opt.Encoding = System.Text.Encoding.UTF8;
                    opt.MinLevelName = minLevel;
                });
            });

            _logger = LogManager.GetLogger(effectiveConfig.FilePrefix);
            Volatile.Write(ref _isConfigured, 1);

            Log("Logger initialized with SuperLightLogger (RollingFile)", LogLevel.Debug);

            CleanupStaleFile(Path.Combine(effectiveConfig.LogDirectory, "0"));
            CleanupOldLogFiles(effectiveConfig.LogDirectory, effectiveConfig.FilePrefix, effectiveConfig.RetentionDays);
        }
        catch
        {
            // 失敗時は再試行を許す
            Interlocked.Exchange(ref _initializing, 0);
            throw;
        }
        finally
        {
            // 成功時も _initializing を 0 に戻す（Shutdown → Initialize の遷移で次回ガードが意図通り効くようにする）
            Interlocked.Exchange(ref _initializing, 0);
        }
    }

    /// <summary>
    /// UIログコールバックを設定する。ログメッセージがUIにも転送されるようになる。
    /// </summary>
    /// <param name="callback">ログメッセージを受け取るコールバック</param>
    public static void SetUILogCallback(Action<string> callback)
    {
        _uiLogCallback = callback;

        // 初期化がまだの場合は先に初期化
        if (Volatile.Read(ref _isConfigured) == 0) Initialize();
    }

    /// <summary>
    /// アプリケーション終了時に呼び出してログをフラッシュする
    /// </summary>
    public static void Shutdown()
    {
        LogManager.Shutdown();
        Interlocked.Exchange(ref _isConfigured, 0);
        Interlocked.Exchange(ref _initializing, 0);
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

        if (Volatile.Read(ref _isConfigured) == 0) Initialize();

        switch (level)
        {
            case LogLevel.Debug:
                _logger?.Debug(message);
                break;
            case LogLevel.Info:
                _logger?.Info(message);
                break;
            case LogLevel.Warning:
                _logger?.Warn(message);
                break;
            case LogLevel.Error:
                _logger?.Error(message);
                break;
            default:
                _logger?.Info(message);
                break;
        }

        NotifyUI(message, level);
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

        if (Volatile.Read(ref _isConfigured) == 0) Initialize();
        foreach (var message in messages)
        {
            Log(message, level);
        }
    }

    /// <summary>
    /// 例外情報を含むログを出力する（常にErrorレベル）
    /// </summary>
    /// <param name="message">ログメッセージ</param>
    /// <param name="exception">例外オブジェクト</param>
    public static void LogException(string message, Exception exception)
    {
        if (Volatile.Read(ref _isConfigured) == 0) Initialize();
        _logger?.Error(message, exception);
        NotifyUI(message, LogLevel.Error);
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
    /// UIコールバックにログメッセージを通知する
    /// </summary>
    private static void NotifyUI(string message, LogLevel level)
    {
        var cb = _uiLogCallback;
        if (cb == null) return;

        var levelStr = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            _ => "INFO"
        };

        try
        {
            cb($"[{levelStr}] {message}".TrimEnd());
        }
        catch
        {
            // UIコールバック内の例外は無視
        }
    }
}
