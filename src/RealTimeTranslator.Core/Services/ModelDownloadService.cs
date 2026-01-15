using System.Net.Http;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// モデルファイルのダウンロードを担当する共通サービス
/// </summary>
public class ModelDownloadService : IDisposable
{
    private const int DefaultBufferSize = 1048576; // 1MB バッファ（ダウンロード速度向上）
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1);
    private bool _disposed;

    public event EventHandler<ModelDownloadProgressEventArgs>? DownloadProgress;
    public event EventHandler<ModelStatusChangedEventArgs>? StatusChanged;

    public ModelDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// モデルの存在を確認し、必要に応じてダウンロードする
    /// </summary>
    /// <param name="modelPath">モデルファイルのパスまたはディレクトリ</param>
    /// <param name="defaultFileName">デフォルトのファイル名</param>
    /// <param name="downloadUrl">ダウンロードURL</param>
    /// <param name="serviceName">サービス名（ログ用）</param>
    /// <param name="modelLabel">モデルラベル（ログ用）</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>解決されたモデルファイルのパス（失敗時はnull）</returns>
    public async Task<string?> EnsureModelAsync(
        string modelPath,
        string defaultFileName,
        string downloadUrl,
        string serviceName,
        string modelLabel,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        LoggerService.LogDebug($"[{serviceName}] EnsureModelAsync called: modelPath={modelPath}, defaultFileName={defaultFileName}, downloadUrl={downloadUrl}");

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.LoadFailed,
                "モデルパスが未設定のためダウンロードをスキップしました。"));
            LoggerService.LogWarning($"[{serviceName}] Model path is not set.");
            return null;
        }

        // パスを検証
        if (!IsValidPath(modelPath))
        {
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.LoadFailed,
                "モデルパスが不正です。"));
            LoggerService.LogWarning($"[{serviceName}] Model path is invalid: {modelPath}");
            return null;
        }

        var resolvedPath = ResolveModelPath(modelPath, defaultFileName);
        LoggerService.LogDebug($"[{serviceName}] Resolved path: {resolvedPath}");
        System.Diagnostics.Debug.WriteLine($"[{serviceName}] Resolved path: {resolvedPath}");
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.LoadFailed,
                "モデルパスの解決に失敗しました。"));
            LoggerService.LogError($"[{serviceName}] Failed to resolve model path. Input: {modelPath}, Default filename: {defaultFileName}");
            System.Diagnostics.Debug.WriteLine($"[{serviceName}] Failed to resolve model path. Input: {modelPath}, Default filename: {defaultFileName}");
            return null;
        }

        if (File.Exists(resolvedPath))
        {
            var fileInfo = new FileInfo(resolvedPath);
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.Info,
                $"モデルファイルを検出しました ({fileInfo.Length} bytes)。読み込み中..."));
            LoggerService.LogDebug($"[{serviceName}] Model file found at: {resolvedPath} (Size: {fileInfo.Length} bytes)");
            System.Diagnostics.Debug.WriteLine($"[{serviceName}] Model file found at: {resolvedPath} (Size: {fileInfo.Length} bytes)");

            // ファイルの整合性を検証
            if (await ValidateModelFileAsync(resolvedPath, downloadUrl, serviceName, modelLabel, cancellationToken))
            {
                OnStatusChanged(new ModelStatusChangedEventArgs(
                    serviceName,
                    modelLabel,
                    ModelStatusType.Info,
                    $"モデルの読み込みが完了しました。"));
                return resolvedPath;
            }

            // ファイルが破損している場合は削除して再ダウンロード
            System.Diagnostics.Debug.WriteLine($"[{serviceName}] Model file is corrupted or outdated. Deleting and re-downloading...");
            try
            {
                File.Delete(resolvedPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{serviceName}] Failed to delete corrupted file: {ex.Message}");
            }
        }

        LoggerService.LogDebug($"[{serviceName}] Model file not found at {resolvedPath}. Will attempt to download from {downloadUrl}");
        System.Diagnostics.Debug.WriteLine($"[{serviceName}] Model file not found at {resolvedPath}. Will attempt to download from {downloadUrl}");
        System.Diagnostics.Debug.WriteLine($"[{serviceName}] AppContext.BaseDirectory: {AppContext.BaseDirectory}");
        System.Diagnostics.Debug.WriteLine($"[{serviceName}] Current directory: {Directory.GetCurrentDirectory()}");

        // URLを検証
        LoggerService.LogDebug($"[{serviceName}] Validating download URL: {downloadUrl}");
        if (!IsValidDownloadUrl(downloadUrl))
        {
            LoggerService.LogError($"[{serviceName}] Download URL validation failed: {downloadUrl}");
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.LoadFailed,
                "ダウンロードURLが不正です。"));
            return null;
        }
        LoggerService.LogDebug($"[{serviceName}] Download URL validation passed");

        var targetDirectory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        await _downloadSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(resolvedPath))
            {
                if (await ValidateModelFileAsync(resolvedPath, downloadUrl, serviceName, modelLabel, cancellationToken))
                {
                    return resolvedPath;
                }

                System.Diagnostics.Debug.WriteLine($"[{serviceName}] Model file is corrupted or outdated after waiting. Deleting and re-downloading...");
                try
                {
                    File.Delete(resolvedPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[{serviceName}] Failed to delete corrupted file after waiting: {ex.Message}");
                }
            }

            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.Info,
                "モデルファイルが見つからないためダウンロードしています。"));

            try
            {
                System.Diagnostics.Debug.WriteLine($"[{serviceName}] Starting model download...");
                await DownloadModelAsync(resolvedPath, downloadUrl, serviceName, modelLabel, cancellationToken);
                System.Diagnostics.Debug.WriteLine($"[{serviceName}] Model download completed.");
            }
            catch (OperationCanceledException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{serviceName}] Download cancelled: {ex.Message}");
                OnStatusChanged(new ModelStatusChangedEventArgs(
                    serviceName,
                    modelLabel,
                    ModelStatusType.DownloadFailed,
                    "モデルのダウンロードがキャンセルされました。"));
                return null;
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{serviceName}] HTTP error during download: {ex.Message}");
                OnStatusChanged(new ModelStatusChangedEventArgs(
                    serviceName,
                    modelLabel,
                    ModelStatusType.DownloadFailed,
                    $"モデルのダウンロードに失敗しました: {ex.Message}",
                    ex));
                return null;
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{serviceName}] IO error during download: {ex.Message}");
                OnStatusChanged(new ModelStatusChangedEventArgs(
                    serviceName,
                    modelLabel,
                    ModelStatusType.DownloadFailed,
                    $"モデルのダウンロードに失敗しました: {ex.Message}",
                    ex));
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{serviceName}] Unexpected error during download: {ex.GetType().Name}: {ex.Message}");
                OnStatusChanged(new ModelStatusChangedEventArgs(
                    serviceName,
                    modelLabel,
                    ModelStatusType.DownloadFailed,
                    $"モデルのダウンロードに失敗しました: {ex.Message}",
                    ex));
                return null;
            }
        }
        finally
        {
            _downloadSemaphore.Release();
        }

        var finalCheck = File.Exists(resolvedPath);
        if (!finalCheck)
        {
            System.Diagnostics.Debug.WriteLine($"[{serviceName}] ERROR: Model file still does not exist after download attempt. Path: {resolvedPath}");
        }
        else
        {
            var fileInfo = new FileInfo(resolvedPath);
            System.Diagnostics.Debug.WriteLine($"[{serviceName}] Download completed successfully. File size: {fileInfo.Length} bytes");
        }
        return finalCheck ? resolvedPath : null;
    }

    private async Task DownloadModelAsync(
        string targetPath,
        string downloadUrl,
        string serviceName,
        string modelLabel,
        CancellationToken cancellationToken)
    {
        System.Diagnostics.Debug.WriteLine($"[{serviceName}] Starting download: URL={downloadUrl}, Target={targetPath}");

        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: DefaultBufferSize,
            useAsync: true);

        var totalBytes = response.Content.Headers.ContentLength;
        var buffer = new byte[DefaultBufferSize];
        long totalRead = 0;
        int bytesRead;

        System.Diagnostics.Debug.WriteLine($"[{serviceName}] Content-Length: {totalBytes} bytes");

        OnStatusChanged(new ModelStatusChangedEventArgs(
            serviceName,
            modelLabel,
            ModelStatusType.Downloading,
            "モデルのダウンロードを開始しました。"));

        while ((bytesRead = await httpStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;
            double? progress = totalBytes.HasValue && totalBytes.Value > 0
                ? totalRead * 100d / totalBytes.Value
                : null;
            OnDownloadProgress(new ModelDownloadProgressEventArgs(
                serviceName,
                modelLabel,
                totalRead,
                totalBytes,
                progress));
        }

        Console.WriteLine($"Downloaded model to: {targetPath}");
        System.Diagnostics.Debug.WriteLine($"[{serviceName}] Download complete: {totalRead} bytes written to {targetPath}");
        OnStatusChanged(new ModelStatusChangedEventArgs(
            serviceName,
            modelLabel,
            ModelStatusType.DownloadCompleted,
            "モデルのダウンロードが完了しました。"));
    }

    /// <summary>
    /// モデルファイルの整合性を検証（サーバーのファイルサイズと比較）
    /// </summary>
    private async Task<bool> ValidateModelFileAsync(
        string localPath,
        string downloadUrl,
        string serviceName,
        string modelLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(localPath))
            {
                return false;
            }

            var localFileInfo = new FileInfo(localPath);
            System.Diagnostics.Debug.WriteLine($"[{serviceName}] Validating file: {localPath} (Local size: {localFileInfo.Length} bytes)");

            // サーバーからファイルサイズを取得（HEADリクエスト）
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, downloadUrl);
            using var response = await _httpClient.SendAsync(headRequest, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[{serviceName}] Failed to get file info from server: {response.StatusCode}");
                return true; // サーバーに問い合わせできない場合は既存ファイルを信頼
            }

            var remoteFileSize = response.Content.Headers.ContentLength;

            if (remoteFileSize == null || remoteFileSize <= 0)
            {
                System.Diagnostics.Debug.WriteLine($"[{serviceName}] Could not determine remote file size");
                return true; // ファイルサイズが不明な場合は既存ファイルを信頼
            }

            // ローカルファイルサイズとリモートファイルサイズを比較
            if (localFileInfo.Length == remoteFileSize.Value)
            {
                System.Diagnostics.Debug.WriteLine($"[{serviceName}] File validation passed: Local size ({localFileInfo.Length}) matches remote size ({remoteFileSize.Value})");
                OnStatusChanged(new ModelStatusChangedEventArgs(
                    serviceName,
                    modelLabel,
                    ModelStatusType.Info,
                    $"ファイルの検証に成功しました。"));
                return true;
            }

            // ファイルサイズが異なる場合は破損している可能性あり
            System.Diagnostics.Debug.WriteLine($"[{serviceName}] File validation failed: Local size ({localFileInfo.Length}) != Remote size ({remoteFileSize.Value})");
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.Info,
                "ファイルが破損しているか古いバージョンのため再ダウンロードしています。"));
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[{serviceName}] Error during file validation: {ex.Message}");
            return true; // 検証中にエラーが発生した場合は既存ファイルを信頼
        }
    }

    private static string? ResolveModelPath(string modelPath, string defaultFileName)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        var isRooted = Path.IsPathRooted(modelPath);
        System.Diagnostics.Debug.WriteLine($"ResolveModelPath: modelPath={modelPath}, isRooted={isRooted}");
        System.Diagnostics.Debug.WriteLine($"ResolveModelPath: AppContext.BaseDirectory={AppContext.BaseDirectory}");

        var rootPath = isRooted
            ? modelPath
            : Path.Combine(AppContext.BaseDirectory, modelPath);

        System.Diagnostics.Debug.WriteLine($"ResolveModelPath: rootPath={rootPath}, Directory.Exists={Directory.Exists(rootPath)}, HasExtension={Path.HasExtension(rootPath)}");

        string result;
        if (Directory.Exists(rootPath) || !Path.HasExtension(rootPath))
        {
            result = Path.Combine(rootPath, defaultFileName);
            System.Diagnostics.Debug.WriteLine($"ResolveModelPath: returning combined path={result}");
        }
        else
        {
            result = rootPath;
            System.Diagnostics.Debug.WriteLine($"ResolveModelPath: returning rootPath={rootPath}");
        }

        // パス区切り文字を正規化（Windows標準に統一）
        result = Path.GetFullPath(result);
        System.Diagnostics.Debug.WriteLine($"ResolveModelPath: normalized path={result}");
        return result;
    }

    /// <summary>
    /// パスの安全性を検証（パストラバーサル対策）
    /// </summary>
    private static bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            System.Diagnostics.Debug.WriteLine($"IsValidPath: path is null or empty, returning false");
            return false;
        }

        try
        {
            // 不正な文字のチェック
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                System.Diagnostics.Debug.WriteLine($"IsValidPath: invalid characters in path, returning false");
                return false;
            }

            // パストラバーサル攻撃のチェック（相対パスのみ）
            if (!Path.IsPathRooted(path))
            {
                // 相対パスの場合、".." を含まないことを確認
                var normalizedPath = Path.GetFullPath(path);
                var basePath = Path.GetFullPath(AppContext.BaseDirectory);

                System.Diagnostics.Debug.WriteLine($"IsValidPath: relative path={path}, normalizedPath={normalizedPath}, basePath={basePath}");

                // パストラバーサル攻撃の検出（basePath配下になることを確認）
                if (!normalizedPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine($"IsValidPath: relative path not in base directory, returning false");
                    return false;
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"IsValidPath: absolute path={path}, allowing");
            }

            System.Diagnostics.Debug.WriteLine($"IsValidPath: path is valid, returning true");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine($"IsValidPath: exception {ex.GetType().Name}: {ex.Message}, returning false");
            return false;
        }
    }

    /// <summary>
    /// ダウンロードURLの安全性を検証
    /// </summary>
    private static bool IsValidDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            LoggerService.LogDebug($"IsValidDownloadUrl: URL is null or empty");
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            LoggerService.LogDebug($"IsValidDownloadUrl: Failed to parse URL: {url}");
            return false;
        }

        // HTTPSのみ許可（セキュリティ向上）
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            LoggerService.LogDebug($"IsValidDownloadUrl: URL scheme is not HTTPS: {uri.Scheme}");
            return false;
        }

        // 信頼できるホストのみ許可（オプション - 必要に応じて拡張）
        var trustedHosts = new[]
        {
            "huggingface.co",
            "www.argosopentech.com",
            "argos-net.com",
            "github.com"
        };

        var isHostTrusted = trustedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                                        uri.Host.EndsWith($".{host}", StringComparison.OrdinalIgnoreCase));
        
        if (!isHostTrusted)
        {
            LoggerService.LogDebug($"IsValidDownloadUrl: Host is not trusted: {uri.Host}");
        }
        
        return isHostTrusted;
    }

    private void OnDownloadProgress(ModelDownloadProgressEventArgs args)
    {
        DownloadProgress?.Invoke(this, args);
    }

    private void OnStatusChanged(ModelStatusChangedEventArgs args)
    {
        StatusChanged?.Invoke(this, args);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
