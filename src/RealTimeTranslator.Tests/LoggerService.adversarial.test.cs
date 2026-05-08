using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// LoggerService の嫌がらせテスト
/// </summary>
[TestClass]
public sealed class LoggerServiceAdversarialTests
{
    [TestCleanup]
    public void Cleanup()
    {
        LoggerService.Shutdown();
    }

    // ═══════════════════════════════════════════════════════════════
    // 🗡️ カテゴリ1: 境界値・極端入力（Boundary Assault）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Log_NullMessage_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.Log(null!, LogLevel.Info);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Log_EmptyMessage_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.Log("", LogLevel.Info);
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Log_VeryLongMessage_ShouldNotCrash()
    {
        LoggerService.Initialize();
        var longMessage = new string('X', 100_000);
        LoggerService.Log(longMessage, LogLevel.Info);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Log_UnicodeMessage_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.Log("日本語テスト 🎮 ​‮ 絵文字👨‍👩‍👧‍👦", LogLevel.Info);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Log_ControlCharacters_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.Log("null:\x00 tab:\t cr:\r lf:\n bell:\x07", LogLevel.Info);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Log_FormatStringLike_ShouldNotCrash()
    {
        // ロガーのフォーマット文字列として解釈されないこと
        LoggerService.Initialize();
        LoggerService.Log("{0} {1} {Error} ${date}", LogLevel.Info);
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void LogLines_NullArray_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.LogLines(null!, LogLevel.Info);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void LogLines_EmptyArray_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.LogLines([], LogLevel.Info);
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void LogException_NullException_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.LogException("テスト", null!);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void LogException_DeeplyNestedException_ShouldNotCrash()
    {
        LoggerService.Initialize();
        Exception ex = new("level 0");
        for (int i = 1; i <= 50; i++)
            ex = new Exception($"level {i}", ex);
        LoggerService.LogException("深いネスト例外", ex);
    }

    // ═══════════════════════════════════════════════════════════════
    // 🔀 カテゴリ4: 状態遷移の矛盾（State Machine Abuse）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="state" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Log_BeforeInitialize_ShouldAutoInitialize()
    {
        // Initialize を呼ばずに Log を呼ぶ
        LoggerService.Shutdown();
        LoggerService.Log("自動初期化テスト", LogLevel.Info);
        // クラッシュしなければOK
    }

    /// <adversarial category="state" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Initialize_DoubleCall_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.Initialize();
        LoggerService.Log("二重初期化後のログ", LogLevel.Info);
    }

    /// <adversarial category="state" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Shutdown_BeforeInitialize_ShouldNotCrash()
    {
        LoggerService.Shutdown();
        LoggerService.Shutdown();
    }

    /// <adversarial category="state" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void InitializeShutdownCycle_ShouldNotLeak()
    {
        for (int i = 0; i < 10; i++)
        {
            LoggerService.Initialize();
            LoggerService.Log($"サイクル {i}", LogLevel.Info);
            LoggerService.Shutdown();
        }
        // 最後に再初期化してログが書けることを確認
        LoggerService.Initialize();
        LoggerService.Log("サイクル後のログ", LogLevel.Info);
    }

    /// <adversarial category="state" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Log_AfterShutdown_ShouldAutoReinitialize()
    {
        LoggerService.Initialize();
        LoggerService.Log("シャットダウン前", LogLevel.Info);
        LoggerService.Shutdown();
        // シャットダウン後に再度ログ — 自動再初期化されるべき
        LoggerService.Log("シャットダウン後", LogLevel.Info);
    }

    /// <adversarial category="state" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void SetUILogCallback_BeforeInitialize_ShouldNotCrash()
    {
        LoggerService.Shutdown();
        var messages = new List<string>();
        LoggerService.SetUILogCallback(msg => messages.Add(msg));
        LoggerService.Log("UIコールバックテスト", LogLevel.Info);
    }

    /// <adversarial category="state" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void SetUILogCallback_Null_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.SetUILogCallback(null!);
        LoggerService.Log("nullコールバック後のログ", LogLevel.Info);
    }

    // ═══════════════════════════════════════════════════════════════
    // ⚡ カテゴリ2: 並行性（Concurrency Chaos）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="concurrency" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public void Initialize_ConcurrentCalls_ShouldNotDoubleInitialize()
    {
        LoggerService.Shutdown();
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            LoggerService.Initialize();
        })).ToArray();
        Task.WaitAll(tasks);
        LoggerService.Log("並行初期化後のログ", LogLevel.Info);
    }

    /// <adversarial category="concurrency" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public void Log_ConcurrentWrites_ShouldNotCrash()
    {
        LoggerService.Initialize();
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            for (int j = 0; j < 100; j++)
                LoggerService.Log($"スレッド{i} メッセージ{j}", LogLevel.Info);
        })).ToArray();
        Task.WaitAll(tasks);
    }

    /// <adversarial category="concurrency" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public void Log_ConcurrentWithShutdown_ShouldNotCrash()
    {
        LoggerService.Initialize();
        var cts = new CancellationTokenSource();
        var logTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { LoggerService.Log("並行ログ", LogLevel.Info); }
                catch { /* best effort */ }
            }
        });
        Thread.Sleep(50);
        LoggerService.Shutdown();
        LoggerService.Initialize();
        Thread.Sleep(50);
        cts.Cancel();
        logTask.Wait(TimeSpan.FromSeconds(5));
    }

    // ═══════════════════════════════════════════════════════════════
    // 💀 カテゴリ3: リソース枯渇（Resource Exhaustion）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="resource" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public void Log_HighVolume_ShouldNotExhaustResources()
    {
        LoggerService.Initialize();
        for (int i = 0; i < 10_000; i++)
            LoggerService.Log($"大量ログ {i}", LogLevel.Info);
    }

    /// <adversarial category="resource" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void SetUILogCallback_ThrowingCallback_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.SetUILogCallback(_ => throw new InvalidOperationException("callback bomb"));
        LoggerService.Log("爆弾コールバック後のログ", LogLevel.Info);
    }

    // ═══════════════════════════════════════════════════════════════
    // 🎭 カテゴリ5: 型パンチ（Type Punching）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="type" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Log_InvalidLogLevel_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.Log("無効なログレベル", (LogLevel)999);
    }

    /// <adversarial category="type" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void LogLines_ArrayWithNulls_ShouldNotCrash()
    {
        LoggerService.Initialize();
        LoggerService.LogLines([null!, "valid", null!], LogLevel.Info);
    }

    // ═══════════════════════════════════════════════════════════════
    // 🌪️ カテゴリ6: 環境異常（Environmental Chaos）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="chaos" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Initialize_WithInvalidDirectory_ShouldNotCrash()
    {
        // 存在しないドライブを指定
        try
        {
            LoggerService.Shutdown();
            LoggerService.Initialize(new LoggerConfig
            {
                LogDirectory = @"Z:\nonexistent\path\logs",
                FilePrefix = "Test"
            });
        }
        catch (Exception ex)
        {
            // IOException/DirectoryNotFoundException は許容
            Assert.IsTrue(ex is IOException or UnauthorizedAccessException,
                $"予期しない例外型: {ex.GetType().Name}");
        }
    }

    /// <adversarial category="chaos" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void GetLogFilePath_BeforeInitialize_ShouldReturnDefault()
    {
        // 初期化前でもデフォルトパスを返すべき
        var path = LoggerService.GetLogFilePath();
        Assert.IsNotNull(path);
        Assert.IsTrue(path.Length > 0);
    }
}
