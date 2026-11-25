namespace Toolbox.Support;

internal static class Errors
{
    /// <summary>A readable message for errors coming out of the core or the app.</summary>
    public static string Describe(Exception error)
    {
        // Awaited core calls surface the CoreException directly; anything that
        // went through Task.Wait or a continuation may be wrapped.
        if (error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
        {
            return Describe(aggregate.InnerExceptions[0]);
        }
        return error.Message;
    }
}

internal static class StringExtensions
{
    /// <summary>Null for null, empty or whitespace-only strings; otherwise the string unchanged.</summary>
    public static string? NonEmpty(this string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Null for blank strings; otherwise the string trimmed.</summary>
    public static string? BlankToNull(this string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>Folder paths are slash-separated, like <c>ACME/Jump Boxes</c>.</summary>
internal static class FolderPath
{
    public static string Leaf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    /// <summary>The containing folder, or null at the top level.</summary>
    public static string? Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? null : path[..index];
    }

    public static string Join(string? parent, string name) =>
        string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";

    /// <summary>
    /// The path the way the store keeps it: each part trimmed, empty parts
    /// dropped; null when nothing is left.
    /// </summary>
    public static string? Normalize(string path)
    {
        var parts = path.Split('/').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? null : string.Join("/", parts);
    }

    /// <summary>
    /// <paramref name="path"/> with a leading <paramref name="from"/> folder
    /// replaced by <paramref name="to"/>, or null when it is not inside it.
    /// </summary>
    public static string? Moved(string path, string from, string to)
    {
        if (path == from) return to;
        return path.StartsWith(from + "/", StringComparison.Ordinal) ? to + path[from.Length..] : null;
    }
}
