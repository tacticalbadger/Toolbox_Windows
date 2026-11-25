using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Toolbox.Support;

/// <summary>One downloadable build of a version: its file, and what that file has to be.</summary>
internal sealed record UpdatePackage(string File, long Size, string Sha256);

/// <summary>
/// What a release says about itself to installed copies: <c>update.json</c>,
/// a file attached to every GitHub Release.
///
/// Nothing in it is taken on the server's word. The bytes have to carry a
/// signature made with the update key (<see cref="IsSigned"/>) before they
/// are read at all, and reading is strict: a field that is missing, of the
/// wrong kind or out of range refuses the whole feed. The package itself is
/// not signed separately: the signed feed names its length and its SHA-256.
/// </summary>
internal sealed partial record UpdateFeed(string Version, int Build, string Notes, IReadOnlyDictionary<string, UpdatePackage> Packages)
{
    /// <summary>The architectures a release is built for.</summary>
    public static readonly IReadOnlyList<string> Architectures = ["win-arm64", "win-x64"];

    public const int MaxFeedBytes = 256 * 1024;
    public const int MaxSignatureBytes = 1024;
    private const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxNotesLength = 20_000;

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    /// <summary>
    /// Whether <paramref name="data"/> carries <paramref name="signature"/>
    /// (Base64 of an ECDSA P-256 signature over its SHA-256, r then s) made
    /// with the key whose public half is <paramref name="publicKey"/> (Base64
    /// of the point's X and Y). False for anything malformed.
    /// </summary>
    public static bool IsSigned(byte[] data, string signature, string publicKey)
    {
        try
        {
            var point = Convert.FromBase64String(publicKey);
            var bytes = Convert.FromBase64String(signature.Trim());
            if (point.Length != 64 || bytes.Length != 64) return false;
            using var key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[..32], Y = point[32..] },
            });
            return key.VerifyData(data, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Reads a feed. Throws <see cref="FormatException"/> for anything that is not exactly as expected.</summary>
    public static UpdateFeed Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("the feed is not an object");
            if (root.GetProperty("format").GetInt32() != 1) throw new FormatException("the feed is of a kind this version does not know");
            var version = root.GetProperty("version").GetString() ?? "";
            if (!VersionPattern().IsMatch(version)) throw new FormatException("the feed's version is not a version");
            var build = root.GetProperty("build").GetInt32();
            if (build is < 1 or > 10_000_000) throw new FormatException("the feed's build number is out of range");
            var notes = root.TryGetProperty("notes", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
            if (notes.Length > MaxNotesLength) notes = notes[..MaxNotesLength];
            notes = CleanNotes(notes);

            var packages = new Dictionary<string, UpdatePackage>();
            foreach (var entry in root.GetProperty("packages").EnumerateObject())
            {
                // An architecture this version has not heard of is somebody else's.
                if (!Architectures.Contains(entry.Name)) continue;
                var file = entry.Value.GetProperty("file").GetString() ?? "";
                // The one name a package of that version and architecture has: nothing else is a file name here.
                if (file != PackageName(version, entry.Name)) throw new FormatException($"the package for {entry.Name} has an unexpected name");
                var size = entry.Value.GetProperty("size").GetInt64();
                if (size is < 1 or > MaxPackageBytes) throw new FormatException($"the package for {entry.Name} has an impossible size");
                var hash = entry.Value.GetProperty("sha256").GetString() ?? "";
                if (!HashPattern().IsMatch(hash)) throw new FormatException($"the package for {entry.Name} has no usable checksum");
                packages[entry.Name] = new UpdatePackage(file, size, hash);
            }
            return new UpdateFeed(version, build, notes, packages);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("the feed cannot be read: " + e.Message);
        }
    }

    /// <summary>The file name of a version's package for an architecture.</summary>
    public static string PackageName(string version, string architecture) => $"Toolbox-{version}-{architecture}.zip";

    /// <summary>
    /// Whether this feed is about a later build than <paramref name="build"/>.
    /// The build number only ever goes up (it is the commit count), so an old
    /// feed served again cannot bring an older version back.
    /// </summary>
    public bool IsNewerThan(int build) => Build > build;

    /// <summary>Release notes as text to show: no control characters besides line breaks.</summary>
    private static string CleanNotes(string notes)
    {
        var builder = new StringBuilder(notes.Length);
        foreach (var c in notes.ReplaceLineEndings("\n"))
        {
            if (c == '\n' || !char.IsControl(c)) builder.Append(c);
        }
        return builder.ToString().Trim();
    }
}
