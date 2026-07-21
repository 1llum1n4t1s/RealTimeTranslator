using RealTimeTranslator.UI.Services;
using Velopack.Windows;

namespace RealTimeTranslator.Tests;

[TestClass]
[DoNotParallelize]
public sealed class WindowsLegacyStartMenuShortcutMigratorTests
{
    private string _testDirectory = null!;
    private string _programsDirectory = null!;
    private string _rootAppDirectory = null!;
    private WindowsLegacyStartMenuShortcutMigrator.ShortcutDetails _realTimeTranslatorShortcut = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            "RealTimeTranslator.Tests",
            Guid.NewGuid().ToString("N"));
        _programsDirectory = Directory.CreateDirectory(Path.Combine(_testDirectory, "Programs")).FullName;
        var currentDirectory = Directory.CreateDirectory(
            Path.Combine(_testDirectory, "RealTimeTranslator", "current")).FullName;
        _rootAppDirectory = Directory.GetParent(currentDirectory)!.FullName;
        _realTimeTranslatorShortcut = new(
            Path.Combine(currentDirectory, "RealTimeTranslator.UI.exe"),
            currentDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void TryMigrate_旧リンクがある場合_直下へ移して空の旧フォルダを削除する()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "1llum1n4t1s"));
        var legacyShortcut = Path.Combine(legacyDirectory.FullName, "RealTimeTranslator.lnk");
        File.WriteAllText(legacyShortcut, "realtimetranslator");

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory,
            _rootAppDirectory,
            ReadShortcut);

        Assert.IsTrue(migrated);
        Assert.IsFalse(File.Exists(legacyShortcut));
        Assert.IsFalse(Directory.Exists(legacyDirectory.FullName));
        Assert.AreEqual(
            "realtimetranslator",
            File.ReadAllText(Path.Combine(_programsDirectory, "RealTimeTranslator.lnk")));
    }

    [TestMethod]
    public void TryMigrate_実際のWindowsショートカットを直下へ移動できる()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "1llum1n4t1s"));
        var legacyShortcut = Path.Combine(legacyDirectory.FullName, "RealTimeTranslator.lnk");
        File.WriteAllText(_realTimeTranslatorShortcut.TargetPath!, string.Empty);

        using (var shortcut = new ShellLink
        {
            Target = _realTimeTranslatorShortcut.TargetPath,
            WorkingDirectory = _realTimeTranslatorShortcut.WorkingDirectory,
        })
        {
            shortcut.Save(legacyShortcut);
        }

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory,
            _rootAppDirectory);

        Assert.IsTrue(migrated);
        Assert.IsFalse(File.Exists(legacyShortcut));
        Assert.IsTrue(File.Exists(Path.Combine(_programsDirectory, "RealTimeTranslator.lnk")));
        Assert.IsFalse(Directory.Exists(legacyDirectory.FullName));
    }

    [TestMethod]
    public void TryMigrate_直下に自アプリリンクがある場合_既存リンクを保持して旧リンクだけ削除する()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "1llum1n4t1s"));
        var legacyShortcut = Path.Combine(legacyDirectory.FullName, "RealTimeTranslator.lnk");
        var rootShortcut = Path.Combine(_programsDirectory, "RealTimeTranslator.lnk");
        File.WriteAllText(legacyShortcut, "realtimetranslator");
        File.WriteAllText(rootShortcut, "realtimetranslator");

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory,
            _rootAppDirectory,
            ReadShortcut);

        Assert.IsTrue(migrated);
        Assert.IsFalse(File.Exists(legacyShortcut));
        Assert.IsFalse(Directory.Exists(legacyDirectory.FullName));
        Assert.AreEqual("realtimetranslator", File.ReadAllText(rootShortcut));
    }

    [TestMethod]
    public void TryMigrate_旧フォルダに別ファイルがある場合_フォルダを残す()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "1llum1n4t1s"));
        File.WriteAllText(
            Path.Combine(legacyDirectory.FullName, "RealTimeTranslator.lnk"),
            "realtimetranslator");
        var otherFile = Path.Combine(legacyDirectory.FullName, "Other.lnk");
        File.WriteAllText(otherFile, "other");

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory,
            _rootAppDirectory,
            ReadShortcut);

        Assert.IsTrue(migrated);
        Assert.IsTrue(Directory.Exists(legacyDirectory.FullName));
        Assert.AreEqual("other", File.ReadAllText(otherFile));
        Assert.IsTrue(File.Exists(Path.Combine(_programsDirectory, "RealTimeTranslator.lnk")));
    }

    [TestMethod]
    public void TryMigrate_直下が別アプリの同名リンクの場合_何も変更しない()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "1llum1n4t1s"));
        var legacyShortcut = Path.Combine(legacyDirectory.FullName, "RealTimeTranslator.lnk");
        var rootShortcut = Path.Combine(_programsDirectory, "RealTimeTranslator.lnk");
        File.WriteAllText(legacyShortcut, "realtimetranslator");
        File.WriteAllText(rootShortcut, "other");

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory,
            _rootAppDirectory,
            ReadShortcut);

        Assert.IsFalse(migrated);
        Assert.AreEqual("realtimetranslator", File.ReadAllText(legacyShortcut));
        Assert.AreEqual("other", File.ReadAllText(rootShortcut));
    }

    [TestMethod]
    public void TryMigrate_旧リンクが自アプリを指さない場合_何も変更しない()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "1llum1n4t1s"));
        var legacyShortcut = Path.Combine(legacyDirectory.FullName, "RealTimeTranslator.lnk");
        File.WriteAllText(legacyShortcut, "other");

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory,
            _rootAppDirectory,
            ReadShortcut);

        Assert.IsFalse(migrated);
        Assert.AreEqual("other", File.ReadAllText(legacyShortcut));
        Assert.IsFalse(File.Exists(Path.Combine(_programsDirectory, "RealTimeTranslator.lnk")));
    }

    private WindowsLegacyStartMenuShortcutMigrator.ShortcutDetails? ReadShortcut(string shortcutPath) =>
        File.ReadAllText(shortcutPath) switch
        {
            "realtimetranslator" => _realTimeTranslatorShortcut,
            "other" => new WindowsLegacyStartMenuShortcutMigrator.ShortcutDetails(
                Path.Combine(_testDirectory, "Other", "Other.exe"),
                Path.Combine(_testDirectory, "Other")),
            _ => null,
        };
}
