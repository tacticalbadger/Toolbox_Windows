using Toolbox.Terminal;

namespace Toolbox.Tests;

/// <summary>What the app reads from Windows Terminal's settings file, which the user edits by hand.</summary>
public class TerminalProfileTests
{
    private const string Settings = """
        // Windows Terminal writes comments like this one.
        {
            "$schema": "https://aka.ms/terminal-profiles-schema",
            "defaultProfile": "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}",
            "profiles":
            {
                "defaults": { "font": { "size": 11 } },
                "list":
                [
                    { "guid": "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}", "name": "Windows PowerShell", "hidden": false },
                    { "guid": "{0caa0dad-35be-5f56-a8ff-afceeeaa6101}", "name": "Command Prompt" },
                    { "guid": "{b453ae62-4e3d-5e58-b989-0a998ec441b8}", "name": "Azure Cloud Shell", "hidden": true },
                    { "guid": "{2c4de342-38b7-51cf-b940-2309a097f518}", "name": "Ubuntu", "source": "Windows.Terminal.Wsl" }, // trailing comma next
                ]
            }
        }
        """;

    [Fact]
    public void VisibleProfilesAreListedInTheFilesOrder()
    {
        var names = WindowsTerminal.ParseProfiles(Settings).Select(p => p.Name).ToArray();
        string[] expected = ["Windows PowerShell", "Command Prompt", "Ubuntu"];
        Assert.Equal(expected, names);
    }

    [Fact]
    public void AnOlderFileWithProfilesAsAListIsRead()
    {
        var names = WindowsTerminal.ParseProfiles("""{ "profiles": [ { "name": "cmd" }, { "name": "cmd" }, { "name": " " } ] }""")
            .Select(p => p.Name).ToArray();
        string[] expected = ["cmd"];
        Assert.Equal(expected, names);
    }

    [Fact]
    public void NamesThatCouldNotBeShownOrPassedOnAreLeftOut()
    {
        var odd = "{ \"profiles\": { \"list\": [ { \"name\": \"two\\nlines\" }, { \"name\": 7 }, \"text\", { \"name\": \"" +
                  new string('x', 101) + "\" }, { \"name\": \"fine\" } ] } }";
        var names = WindowsTerminal.ParseProfiles(odd).Select(p => p.Name).ToArray();
        string[] expected = ["fine"];
        Assert.Equal(expected, names);
    }

    [Fact]
    public void AFileWithoutProfilesGivesNone()
    {
        Assert.Empty(WindowsTerminal.ParseProfiles("{}"));
        Assert.Empty(WindowsTerminal.ParseProfiles("""{ "profiles": "none" }"""));
    }

    [Theory]
    [InlineData("PING.EXE", "ping")]
    [InlineData("python.exe", "python")]
    [InlineData("Code.exe", "Code")]
    [InlineData("7Z.EXE", "7z")]
    public void AProgramIsNamedTheWayAPersonWould(string executable, string expected) =>
        Assert.Equal(expected, WindowsTerminal.ProgramName(executable));
}
