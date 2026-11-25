using System.IO;
using Microsoft.Win32;
using Toolbox.Support;

namespace Toolbox.Tests;

/// <summary>
/// The version Windows shows for an installed copy, kept right by the app
/// after it has updated itself. Every test works on a registry key of its
/// own, never on the real entry.
/// </summary>
public sealed class InstalledCopyTests : IDisposable
{
    private readonly string keyPath = @"Software\Toolbox.Tests\" + Guid.NewGuid().ToString("N");
    private const string Folder = @"C:\Users\jdoe\AppData\Local\Programs\Toolbox";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);

    /// <summary>An entry as the installer leaves it.</summary>
    private void Install(string version, string location = Folder + @"\")
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue("InstallLocation", location);
        key.SetValue("DisplayName", "Toolbox");
        key.SetValue("DisplayVersion", version);
        key.SetValue("MajorVersion", 0, RegistryValueKind.DWord);
        key.SetValue("MinorVersion", 3, RegistryValueKind.DWord);
        key.SetValue("VersionMajor", 0, RegistryValueKind.DWord);
        key.SetValue("VersionMinor", 3, RegistryValueKind.DWord);
    }

    private object? Value(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(name);
    }

    [Fact]
    public void ACopyThatUpdatedItselfSaysSo()
    {
        Install("0.3.1");
        Assert.True(InstalledCopy.RecordVersion(Registry.CurrentUser, keyPath, Folder, "1.4.2"));
        Assert.Equal("1.4.2", Value("DisplayVersion"));
        Assert.Equal(1, Value("MajorVersion"));
        Assert.Equal(1, Value("VersionMajor"));
        Assert.Equal(4, Value("MinorVersion"));
        Assert.Equal(4, Value("VersionMinor"));
        Assert.Equal("Toolbox", Value("DisplayName"));
    }

    [Fact]
    public void TheFolderIsTheSameHoweverItIsWritten()
    {
        Install("0.3.1", Folder.ToUpperInvariant());
        Assert.True(InstalledCopy.RecordVersion(Registry.CurrentUser, keyPath, Folder + @"\", "0.3.2"));
        Assert.Equal("0.3.2", Value("DisplayVersion"));
    }

    [Fact]
    public void NothingIsWrittenWhenTheVersionIsAlreadyRight()
    {
        Install("0.3.1");
        Assert.False(InstalledCopy.RecordVersion(Registry.CurrentUser, keyPath, Folder, "0.3.1"));
    }

    [Fact]
    public void ACopyElsewhereLeavesTheInstalledOnesEntryAlone()
    {
        Install("0.3.1");
        Assert.False(InstalledCopy.RecordVersion(Registry.CurrentUser, keyPath, @"C:\Users\jdoe\Downloads\Toolbox", "0.3.2"));
        Assert.Equal("0.3.1", Value("DisplayVersion"));
    }

    [Fact]
    public void ACopyThatWasNeverInstalledMakesNoEntry()
    {
        Assert.False(InstalledCopy.RecordVersion(Registry.CurrentUser, keyPath, Folder, "0.3.2"));
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        Assert.Null(key);
    }

    [Fact]
    public void AnEntryWithoutAFolderIsNotThisCopys()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) key.SetValue("DisplayVersion", "0.3.1");
        Assert.False(InstalledCopy.RecordVersion(Registry.CurrentUser, keyPath, Folder, "0.3.2"));
        Assert.Equal("0.3.1", Value("DisplayVersion"));
    }

    [Fact]
    public void TheAppAndTheInstallerAgreeOnTheAppId()
    {
        // The installer names the registry key after its AppId; the app
        // looks for the key by the same id. A change to one alone would
        // leave every installed copy showing the version it was installed as.
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "installer", "Toolbox.iss"));
        Assert.Contains($"#define AppId \"{InstalledCopy.AppId}\"", script);
        Assert.EndsWith(InstalledCopy.AppId + "_is1", InstalledCopy.UninstallKey);
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "VERSION")) && Directory.Exists(Path.Combine(folder.FullName, "tools"))) return folder.FullName;
        }
        throw new InvalidOperationException("the tests do not run from inside the repository");
    }
}
