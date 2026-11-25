using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Toolbox.Support;

namespace Toolbox.Tests;

/// <summary>What an installed copy believes about an update, and what it refuses.</summary>
public class UpdateFeedTests
{
    // Made with tools\update-key.ps1 and a key that was deleted afterwards:
    // the script signs in Windows PowerShell, the app checks in .NET.
    private const string TestKey = "PuGTT0Fe6q9G98IAcM8IcHClx+sXYypZ05Uu1uBhjsX0VSOMuUpDhhDukWg/GzywIvNsAO540/u4CKiUiI7eLw==";
    private const string TestSignature = "4b/ULtzCUcJWXTRolnHk2xGFF6pvmLyYSv+vJMJRrjm4mZWfFwlDNqtoBDynJZQesLMWfKLlsMWEqQhcZhPh+w==";
    private static readonly byte[] TestMessage = Encoding.UTF8.GetBytes("{\"format\":1,\"version\":\"9.9.2\"}\n");

    private static readonly string Hash = new('a', 64);

    private static string Feed(string version = "0.3.1", string build = "12", string file = "Toolbox-0.3.1-win-arm64.zip",
                               string size = "1000", string? hash = null, string extra = "") =>
        $$"""
        {
          "format": 1, "version": "{{version}}", "build": {{build}}, "notes": "- One thing\n- Another",
          "packages": {
            "win-arm64": { "file": "{{file}}", "size": {{size}}, "sha256": "{{hash ?? Hash}}" },
            "win-x64": { "file": "Toolbox-{{version}}-win-x64.zip", "size": 2000, "sha256": "{{Hash}}" }{{extra}}
          }
        }
        """;

    private static UpdateFeed Parse(string json) => UpdateFeed.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ASignatureMadeByTheReleaseScriptIsAccepted() =>
        Assert.True(UpdateFeed.IsSigned(TestMessage, TestSignature, TestKey));

    [Fact]
    public void ASignatureOnlyCoversTheBytesItWasMadeFor()
    {
        var altered = (byte[])TestMessage.Clone();
        altered[^2] ^= 1;
        Assert.False(UpdateFeed.IsSigned(altered, TestSignature, TestKey));
        Assert.False(UpdateFeed.IsSigned(TestMessage, TestSignature, Updates.PublicKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")]
    public void AMalformedSignatureIsNoSignature(string signature) =>
        Assert.False(UpdateFeed.IsSigned(TestMessage, signature, TestKey));

    [Fact]
    public void TheAppsOwnKeyIsAPointOnTheCurve()
    {
        // A key that cannot be loaded would refuse every update without saying why.
        using var key = ECDsa.Create();
        var point = Convert.FromBase64String(Updates.PublicKey);
        Assert.Equal(64, point.Length);
        key.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[..32], Y = point[32..] } });
    }

    [Fact]
    public void AFeedIsReadAsWritten()
    {
        var feed = Parse(Feed());
        Assert.Equal("0.3.1", feed.Version);
        Assert.Equal(12, feed.Build);
        Assert.Equal("- One thing\n- Another", feed.Notes);
        Assert.Equal(new UpdatePackage("Toolbox-0.3.1-win-arm64.zip", 1000, Hash), feed.Packages["win-arm64"]);
        Assert.Equal(2000, feed.Packages["win-x64"].Size);
    }

    [Fact]
    public void OnlyALaterBuildIsAnUpdate()
    {
        var feed = Parse(Feed());
        Assert.True(feed.IsNewerThan(11));
        Assert.False(feed.IsNewerThan(12));
        Assert.False(feed.IsNewerThan(13));
    }

    [Fact]
    public void AnArchitectureNobodyHasHeardOfIsPassedOver()
    {
        var feed = Parse(Feed(extra: ", \"win-riscv\": { \"file\": \"x\", \"size\": 1, \"sha256\": \"y\" }"));
        Assert.Equal(2, feed.Packages.Count);
    }

    [Theory]
    [InlineData("..\\..\\evil.zip")]
    [InlineData("https://example.com/Toolbox-0.3.1-win-arm64.zip")]
    [InlineData("Toolbox-0.3.2-win-arm64.zip")]
    [InlineData("Toolbox-0.3.1-win-x64.zip")]
    public void APackageHasOneNameAndNoOther(string file) =>
        Assert.Throws<FormatException>(() => Parse(Feed(file: file.Replace("\\", "\\\\"))));

    [Theory]
    [InlineData("1.0")]
    [InlineData("0.3.1; rm")]
    [InlineData("v0.3.1")]
    [InlineData("0.3.1/../..")]
    public void AVersionIsThreeNumbers(string version) => Assert.Throws<FormatException>(() => Parse(Feed(version: version)));

    [Theory]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("99999999")]
    [InlineData("\"12\"")]
    public void ABuildNumberIsAPlausibleNumber(string build) => Assert.Throws<FormatException>(() => Parse(Feed(build: build)));

    [Fact]
    public void SizesAndChecksumsHaveToBeUsable()
    {
        Assert.Throws<FormatException>(() => Parse(Feed(size: "0")));
        Assert.Throws<FormatException>(() => Parse(Feed(size: "99999999999999")));
        Assert.Throws<FormatException>(() => Parse(Feed(hash: "abc")));
        Assert.Throws<FormatException>(() => Parse(Feed(hash: new string('A', 64))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ \"format\": 2 }")]
    [InlineData("{ \"format\": 1, \"version\": \"0.3.1\" }")]
    [InlineData("<html>Not found</html>")]
    public void AnythingElseIsNotAFeed(string json) => Assert.Throws<FormatException>(() => Parse(json));

    [Fact]
    public void NotesAreKeptToTextThatCanBeShown()
    {
        var feed = Parse(Feed().Replace("- One thing\\n- Another", "line\\r\\nnext\\u0007\\u001b[31m"));
        Assert.Equal("line\nnext[31m", feed.Notes);
    }
}

/// <summary>The file work of an update, in folders made for the test.</summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "toolbox-update-test-" + Guid.NewGuid().ToString("N"));

    public UpdateInstallerTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a scanner has a file open; the folder is in Temp */ }
    }

    private string Folder(string name, params (string File, string Text)[] files)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        foreach (var (file, text) in files)
        {
            var path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return folder;
    }

    private static string Read(string folder, string file) => File.ReadAllText(Path.Combine(folder, file));

    [Fact]
    public void ADownloadHasToBeTheFileTheFeedDescribed()
    {
        var file = Path.Combine(root, "package.zip");
        File.WriteAllText(file, "the package");
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        Assert.True(UpdateInstaller.Matches(file, new UpdatePackage("p.zip", 11, hash)));
        Assert.False(UpdateInstaller.Matches(file, new UpdatePackage("p.zip", 12, hash)));
        Assert.False(UpdateInstaller.Matches(file, new UpdatePackage("p.zip", 11, new string('0', 64))));
        File.WriteAllText(file, "the packagE");
        Assert.False(UpdateInstaller.Matches(file, new UpdatePackage("p.zip", 11, hash)));
        Assert.False(UpdateInstaller.Matches(Path.Combine(root, "missing.zip"), new UpdatePackage("p.zip", 11, hash)));
    }

    [Fact]
    public void APackageIsUnpackedToItsOneFolder()
    {
        var source = Folder("source", ("Toolbox-9.9.2-win-x64/Toolbox.exe", "exe"), ("Toolbox-9.9.2-win-x64/toolbox_core.dll", "dll"),
                            ("Toolbox-9.9.2-win-x64/Assets/page.html", "page"));
        var zip = Path.Combine(root, "package.zip");
        ZipFile.CreateFromDirectory(source, zip);
        var app = UpdateInstaller.Unpack(zip, Path.Combine(root, "unpacked"));
        Assert.Equal("Toolbox-9.9.2-win-x64", Path.GetFileName(app));
        Assert.Equal("page", Read(app, "Assets/page.html"));
    }

    [Fact]
    public void AZipWithoutTheAppIsRefused()
    {
        var source = Folder("source", ("readme.txt", "nothing here"));
        var zip = Path.Combine(root, "package.zip");
        ZipFile.CreateFromDirectory(source, zip);
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Unpack(zip, Path.Combine(root, "unpacked")));
    }

    [Fact]
    public void AnEntryThatPointsOutsideTheFolderIsRefused()
    {
        var zip = Path.Combine(root, "package.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open());
            writer.Write("escaped");
        }
        Assert.ThrowsAny<IOException>(() => UpdateInstaller.Unpack(zip, Path.Combine(root, "unpacked")));
        Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
    }

    [Fact]
    public void TheNewVersionTakesThePlaceOfTheOld()
    {
        var installed = Folder("installed", ("Toolbox.exe", "old exe"), ("toolbox_core.dll", "old dll"), ("gone-in-new.dll", "old only"),
                               ("Assets/page.html", "old page"), ("my-notes.txt", "the user's own"),
                               ("files.txt", "Toolbox.exe\ntoolbox_core.dll\ngone-in-new.dll\nAssets/page.html\nfiles.txt\n"));
        var fresh = Folder("fresh", ("Toolbox.exe", "new exe"), ("toolbox_core.dll", "new dll"), ("Assets/page.html", "new page"),
                           ("new-in-new.dll", "new only"), ("files.txt", "Toolbox.exe\ntoolbox_core.dll\nAssets/page.html\nnew-in-new.dll\nfiles.txt\n"));
        var aside = Path.Combine(root, "previous");

        UpdateInstaller.Replace(installed, fresh, aside);

        Assert.Equal("new exe", Read(installed, "Toolbox.exe"));
        Assert.Equal("new page", Read(installed, "Assets/page.html"));
        Assert.Equal("new only", Read(installed, "new-in-new.dll"));
        Assert.False(File.Exists(Path.Combine(installed, "gone-in-new.dll")));
        // What neither version put there is not the update's to touch.
        Assert.Equal("the user's own", Read(installed, "my-notes.txt"));
        Assert.Equal("old exe", Read(aside, "Toolbox.exe"));
        Assert.Contains("new-in-new.dll", Read(installed, "files.txt"));
    }

    [Fact]
    public void AFailedSwapLeavesTheOldVersionInPlace()
    {
        var installed = Folder("installed", ("Toolbox.exe", "old exe"), ("toolbox_core.dll", "old dll"), ("b-locked.dll", "old locked"),
                               ("files.txt", "Toolbox.exe\ntoolbox_core.dll\nfiles.txt\n"));
        // A file in the way that the old version's list does not name, held open: copying the new one over it fails.
        var fresh = Folder("fresh", ("Toolbox.exe", "new exe"), ("toolbox_core.dll", "new dll"), ("zz-unlisted/blocked.dll", "new"));
        Directory.CreateDirectory(Path.Combine(installed, "zz-unlisted"));
        Directory.CreateDirectory(Path.Combine(installed, "zz-unlisted", "blocked.dll"));

        Assert.ThrowsAny<Exception>(() => UpdateInstaller.Replace(installed, fresh, Path.Combine(root, "previous")));

        Assert.Equal("old exe", Read(installed, "Toolbox.exe"));
        Assert.Equal("old dll", Read(installed, "toolbox_core.dll"));
        Assert.Equal("old locked", Read(installed, "b-locked.dll"));
    }

    [Fact]
    public void AManifestCannotNameFilesOutsideItsFolder()
    {
        var installed = Folder("installed", ("Toolbox.exe", "exe"),
                               ("files.txt", "Toolbox.exe\n..\\outside.txt\nC:\\Windows\\win.ini\n/etc/passwd\nsub/../../outside.txt\n\nAssets/page.html\n"));
        var files = UpdateInstaller.ReadManifest(installed).ToArray();
        string[] expected = ["Toolbox.exe", Path.Combine("Assets", "page.html")];
        Assert.Equal(expected, files);
    }
}
