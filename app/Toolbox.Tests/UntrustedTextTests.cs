using Toolbox.Files;
using Toolbox.Model;
using Toolbox.Support;
using Toolbox.Terminal;

namespace Toolbox.Tests;

/// <summary>Text that comes from the other end of a connection.</summary>
public class UntrustedTextTests
{
    [Theory]
    [InlineData("report.pdf")]
    [InlineData("archive.tar.gz")]
    [InlineData(".bashrc")]
    [InlineData("name with spaces.txt")]
    [InlineData("console.log")]
    [InlineData("COM10")]
    [InlineData("nulls.txt")]
    public void OrdinaryFileNamesCanBeSaved(string name) => Assert.True(LocalNames.IsSafe(name));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    // Would leave the chosen folder, or name a drive or a stream.
    [InlineData(@"..\startup.bat")]
    [InlineData("../startup.bat")]
    [InlineData(@"sub\file.txt")]
    [InlineData(@"C:\Windows\x.dll")]
    [InlineData("C:x.dll")]
    [InlineData("file.txt:stream")]
    // Windows trims these, so they could land on another file.
    [InlineData("file.txt.")]
    [InlineData("file.txt ")]
    // Devices, with or without an extension.
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("CON")]
    [InlineData("COM1")]
    [InlineData("com1.tar.gz")]
    [InlineData("LPT1.log")]
    [InlineData("aux")]
    [InlineData("PRN .txt")]
    [InlineData("bad\tname")]
    [InlineData("what?.txt")]
    public void NamesWindowsReadsAsSomethingElseAreRefused(string name) => Assert.False(LocalNames.IsSafe(name));

    [Fact]
    public void AVeryLongNameIsRefused() => Assert.False(LocalNames.IsSafe(new string('a', 300)));

    [Fact]
    public void ATerminalTitleIsKeptToOneShortLine()
    {
        Assert.Equal("", TerminalPane.CleanTitle(null));
        Assert.Equal("", TerminalPane.CleanTitle(""));
        Assert.Equal("user@host: ~", TerminalPane.CleanTitle("user@host: ~"));
        Assert.Equal("one two", TerminalPane.CleanTitle("one\ntwo"));
        Assert.Equal("a b", TerminalPane.CleanTitle("a\u001bb"));
        Assert.Equal(200, TerminalPane.CleanTitle(new string('x', 5_000_000)).Length);
    }

    [Fact]
    public void ALogEntryStaysOnOneLine()
    {
        Assert.Equal("ssh: connected (jump01)", Log.OneLine("ssh: connected (jump01)"));
        var forged = Log.OneLine("ssh: demo: rejected\r\n2026-11-20 10:00:00.000 INFO  vault: unlocked");
        Assert.DoesNotContain('\r', forged);
        Assert.DoesNotContain('\n', forged);
        Assert.Contains("\\r\\n2026-11-20", forged);
        Assert.Equal("bell\\u0007", Log.OneLine("bell\u0007"));
        Assert.Equal("tab\tkept", Log.OneLine("tab\tkept"));
    }

    [Fact]
    public void AWindowsUsernameSplitsIntoUserAndDomain()
    {
        var down = CredentialResolver.Split(@"CORP\jdoe", "secret");
        Assert.Equal(("jdoe", "CORP", @"CORP\jdoe"), (down.Username, down.Domain, down.FullUsername));
        var upn = CredentialResolver.Split("jdoe@corp.example", "secret");
        Assert.Equal(("jdoe", "corp.example", "jdoe@corp.example"), (upn.Username, upn.Domain, upn.FullUsername));
        var plain = CredentialResolver.Split("demo", "secret");
        Assert.Equal(("demo", null, "demo"), (plain.Username, plain.Domain, plain.FullUsername));
    }
}
