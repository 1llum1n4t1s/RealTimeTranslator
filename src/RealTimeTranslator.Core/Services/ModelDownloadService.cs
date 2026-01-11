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

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.LoadFailed,
                "モデルパスが未設定のためダウンロードをスキップしました。"));
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
            return null;
        }

        var resolvedPath = ResolveModelPath(modelPath, defaultFileName);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.LoadFailed,
                "モデルパスの解決に失敗しました。"));
            return null;
        }

        if (File.Exists(resolvedPath))
        {
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.Info,
                "モデルファイルを検出しました。"));
            return resolvedPath;
        }

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
            await DownloadModelAsync(resolvedPath, downloadUrl, serviceName, modelLabel, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.DownloadFailed,
                "モデルのダウンロードがキャンセルされました。"));
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            OnStatusChanged(new ModelStatusChangedEventArgs(
                serviceName,
                modelLabel,
                ModelStatusType.DownloadFailed,
                "モデルのダウンロードに失敗しました。",
                ex));
            return null;
        }

        return File.Exists(resolvedPath) ? resolvedPath : null;
    }

    private async Task DownloadModelAsync(
        string targetPath,
        string downloadUrl,
        string serviceName,
        string modelLabel,
        CancellationToken cancellationToken)
    {
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

        var rootPath = Path.IsPathRooted(modelPath)
            ? modelPath
            : Path.Combine(AppContext.BaseDirectory, modelPath);

        if (Directory.Exists(rootPath) || !Path.HasExtension(rootPath))
        {
            return Path.Combine(rootPath, defaultFileName);
        }

        return rootPath;
    }

    /// <summary>
    /// パスの安全性を検証（パストラバーサル対策）
    /// </summary>
    private static bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            // パストラバーサル攻撃のチェック
            var normalizedPath = Path.GetFullPath(path);
            var basePath = Path.GetFullPath(AppContext.BaseDirectory);

            // 相対パスの場合、BaseDirectory配下であることを確認
            if (!Path.IsPathRooted(path) && !normalizedPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 不正な文字のチェック
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or NotSupportedException)
        {
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
            "www.argosopentech.com"
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
