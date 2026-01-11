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

        System.Diagnostics.Debug.WriteLine($"[{serviceName}] EnsureModelAsync called: modelPath={modelPath}, defaultFileName={defaultFileName}, downloadUrl={downloadUrl}");

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.LoadFailed,
                "モデルパスが未設定のためダウンロードをスキップしました。"));
            System.Diagnostics.Debug.WriteLine($"[{serviceName}] Model path is not set.");
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
            System.Diagnostics.Debug.WriteLine($"[{serviceName}] Model path is invalid: {modelPath}");
            return null;
        }

        var resolvedPath = ResolveModelPath(modelPath, defaultFileName);
        System.Diagnostics.Debug.WriteLine($"[{serviceName}] Resolved path: {resolvedPath}");
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.LoadFailed,
                "モデルパスの解決に失敗しました。"));
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
                $"モデルファイルを検出しました ({fileInfo.Length} bytes)。"));
            System.Diagnostics.Debug.WriteLine($"[{serviceName}] Model file found at: {resolvedPath} (Size: {fileInfo.Length} bytes)");
            return resolvedPath;
        }

        System.Diagnostics.Debug.WriteLine($"[{serviceName}] Model file not found at {resolvedPath}. Will attempt to download from {downloadUrl}");
        System.Diagnostics.Debug.WriteLine($"[{serviceName}] AppContext.BaseDirectory: {AppContext.BaseDirectory}");
        System.Diagnostics.Debug.WriteLine($"[{serviceName}] Current directory: {Directory.GetCurrentDirectory()}");

        // URLを検証
        if (!IsValidDownloadUrl(downloadUrl))
        {
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.LoadFailed,
                "ダウンロードURLが不正です。"));
            return null;
        }

        var targetDirectory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

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
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // HTTPSのみ許可（セキュリティ向上）
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // 信頼できるホストのみ許可（オプション - 必要に応じて拡張）
        var trustedHosts = new[]
        {
            "huggingface.co",
            "www.argosopentech.com",
            "argos-net.com"
        };

        return trustedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                                        uri.Host.EndsWith($".{host}", StringComparison.OrdinalIgnoreCase));
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
