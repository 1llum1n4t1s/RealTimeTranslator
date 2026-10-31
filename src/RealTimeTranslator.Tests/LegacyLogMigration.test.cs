using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

[TestClass]
public sealed class LegacyLogMigrationTests
{
    [TestMethod]
    public void Move_DoesNotFollowDirectoryLinks()
    {
        var root = Path.Combine(TestOutputEnvironment.RootDirectory, $"migration-{Guid.NewGuid():N}");
        var old = Path.Combine(root, "old");
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(current);
        var log = Path.Combine(old, "RealTimeTranslator_link.log");
        File.WriteAllText(log, "old");
        var oldLink = Path.Combine(root, "old-link");
        CreateDirectoryLink(oldLink, old);
        var destinationLink = Path.Combine(root, "current-link");
        CreateDirectoryLink(destinationLink, current);

        LegacyLogMigration.Move(oldLink, current);
        LegacyLogMigration.Move(old, destinationLink);
        Assert.IsTrue(File.Exists(log));
        Assert.AreEqual(0, Directory.GetFiles(current).Length);
        LegacyLogMigration.RemoveEmptyDirectory(oldLink);
        Assert.IsTrue(Directory.Exists(oldLink));
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode);
    }

    [TestMethod]
    public void Move_PreservesHistoryConflictsAndRetriesLockedFiles()
    {
        var root = Path.Combine(TestOutputEnvironment.RootDirectory, $"migration-{Guid.NewGuid():N}");
        var old = Path.Combine(root, "old");
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(Path.Combine(old, "translations"));
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(old, "translations", "history.tsv"), "history");
        var name = "RealTimeTranslator_20261031.log";
        File.WriteAllText(Path.Combine(old, name), "old");
        File.WriteAllText(Path.Combine(current, name), "current");
        var locked = Path.Combine(old, "RealTimeTranslator_20261030.log");
        File.WriteAllText(locked, "locked");

        using (new FileStream(locked, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            LegacyLogMigration.Move(old, current);
            Assert.IsTrue(File.Exists(locked));
        }
        Assert.AreEqual("current", File.ReadAllText(Path.Combine(current, name)));
        Assert.AreEqual("old", File.ReadAllText(Directory.GetFiles(current, "*_legacy_*.log").Single()));
        LegacyLogMigration.Move(old, current);
        Assert.IsFalse(File.Exists(locked));
        Assert.AreEqual("locked", File.ReadAllText(Path.Combine(current, Path.GetFileName(locked))));
        Assert.AreEqual("history", File.ReadAllText(Path.Combine(old, "translations", "history.tsv")));
        LegacyLogMigration.RemoveEmptyDirectory(old);
        Assert.IsTrue(Directory.Exists(old));
    }

    [TestMethod]
    public void Move_ConcurrentCallsPreserveEveryLogAndRemoveEmptySource()
    {
        var root = Path.Combine(TestOutputEnvironment.RootDirectory, $"migration-{Guid.NewGuid():N}");
        var old = Path.Combine(root, "old");
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(old);
        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(old, $"RealTimeTranslator_{i}.log"), i.ToString());

        Parallel.For(0, 4, _ => LegacyLogMigration.Move(old, current));
        LegacyLogMigration.Move(old, current);
        Assert.IsFalse(Directory.Exists(old));
        LegacyLogMigration.RemoveEmptyDirectory(root);
        Assert.IsTrue(Directory.Exists(root));
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        LegacyLogMigration.RemoveEmptyDirectory(empty);
        Assert.IsFalse(Directory.Exists(empty));
        Assert.AreEqual(20, Directory.GetFiles(current, "*.log").Length);
        for (var i = 0; i < 20; i++)
            Assert.AreEqual(i.ToString(), File.ReadAllText(Path.Combine(current, $"RealTimeTranslator_{i}.log")));
    }
}
