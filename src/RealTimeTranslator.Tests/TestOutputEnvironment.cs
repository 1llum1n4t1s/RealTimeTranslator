using System.Reflection;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>ロガーとデバッグ録音の副作用をテスト実行ごとの Temp に閉じ込める。</summary>
[TestClass]
public sealed class TestOutputEnvironment
{
    internal static string RootDirectory { get; } = CreateRootDirectory();
    internal static string LogDirectory => Path.Combine(RootDirectory, "logs");
    internal static string DebugDirectory => Path.Combine(RootDirectory, "debug");

    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        context.WriteLine($"テスト出力先: {RootDirectory}");
        RestoreLoggerDirectory();
    }

    internal static void RestoreLoggerDirectory()
    {
        LoggerService.Shutdown();
        LoggerService.Initialize(new LoggerConfig { LogDirectory = LogDirectory, FilePrefix = "RealTimeTranslator" });
        // Shutdown は出力先を保持する。初期化前・自動再初期化のテストも Temp を使う。
        LoggerService.Shutdown();
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        LoggerService.Shutdown();
    }

    private static string CreateRootDirectory()
    {
        var configuredRoot = typeof(TestOutputEnvironment).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(item => item.Key == "RealTimeTranslatorDevelopmentWorkRoot")?.Value;
        var localHost = OperatingSystem.IsWindows()
            && string.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"C:\Users\IMT", StringComparison.OrdinalIgnoreCase)
            && !new[] { "GITHUB_ACTIONS", "TF_BUILD", "CI" }.Any(name =>
                string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase));
        if (localHost && string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException("ローカル検証の正本設定がありません。scripts/Dev.ps1を使用してください。");
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Codex", "RealTimeTranslator"));
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var canonicalRoot = Path.GetFullPath(configuredRoot);
            if (!Directory.Exists(Path.GetPathRoot(canonicalRoot)))
                throw new InvalidOperationException("作業ドライブが使用できません。Cへのfallbackは禁止です。");
            var work = Environment.GetEnvironmentVariable("RTT_WORK_DIRECTORY");
            if (string.IsNullOrWhiteSpace(work) || !Path.IsPathFullyQualified(work))
                throw new InvalidOperationException("RTT_WORK_DIRECTORYには正本の作業別directoryが必要です。scripts/Dev.ps1を使用してください。");
            allowedRoot = Path.GetFullPath(work);
            if (!IsWithin(allowedRoot, canonicalRoot) || string.Equals(allowedRoot, canonicalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("検証work directoryが正本の作業別directoryではありません。");
            foreach (var name in new[] { "TEMP", "TMP" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || !IsWithin(Path.GetFullPath(value), allowedRoot))
                    throw new InvalidOperationException($"{name}は正本のwork directory配下に必要です。");
                AssertNoLinks(Path.GetFullPath(value));
            }
            if (!IsWithin(Path.GetFullPath(Path.GetTempPath()), allowedRoot))
                throw new InvalidOperationException(".NET Tempが正本のwork directory配下ではありません。");
        }
        var requestedRoot = Environment.GetEnvironmentVariable("RTT_TEST_OUTPUT_ROOT");
        if (!string.IsNullOrWhiteSpace(requestedRoot) && !Path.IsPathFullyQualified(requestedRoot))
            throw new InvalidOperationException("RTT_TEST_OUTPUT_ROOTには絶対pathが必要です。");
        var outputRoot = string.IsNullOrWhiteSpace(requestedRoot) ? allowedRoot : Path.GetFullPath(requestedRoot);
        if (!IsWithin(outputRoot, allowedRoot))
            throw new InvalidOperationException("RTT_TEST_OUTPUT_ROOTは検証work directory配下に指定してください。");

        var runRoot = Path.Combine(outputRoot, $"test-run-{Guid.NewGuid():N}");
        var codexRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var configuredCodexRoot = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (IsWithin(runRoot, codexRoot)
            || (!string.IsNullOrWhiteSpace(configuredCodexRoot) && IsWithin(runRoot, Path.GetFullPath(configuredCodexRoot))))
            throw new InvalidOperationException("テスト出力先を Codex ユーザーディレクトリ内には作成できません。");

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if ((File.Exists(Path.Combine(directory.FullName, "RealTimeTranslator.slnx"))
                || Directory.Exists(Path.Combine(directory.FullName, ".git"))) && IsWithin(runRoot, directory.FullName))
                throw new InvalidOperationException("テスト出力先をリポジトリ内には作成できません。");
        }

        // 既存の祖先がリンクなら、実体が許可範囲外へ向く可能性があるため作成前に拒否する。
        for (var directory = new DirectoryInfo(runRoot); directory is not null; directory = directory.Parent)
        {
            if ((directory.Exists || File.Exists(directory.FullName))
                && (File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("テスト出力先の祖先にリンクを使用できません。");
        }

        AssertNoLinks(runRoot);
        Directory.CreateDirectory(runRoot);
        return runRoot;
    }

    private static void AssertNoLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory.FullName); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new InvalidOperationException("テスト出力先の祖先にリンク/通常fileは使用できません。");
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
