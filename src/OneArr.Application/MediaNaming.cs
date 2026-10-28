using System.Text.RegularExpressions;

namespace OneArr.Application;

/// <summary>Read-only deterministic media filename preview. Never touches disk.</summary>
public static class MediaNaming
{
    private static readonly Regex Token = new(@"\{(?<name>[A-Za-z][A-Za-z0-9:]*)\}", RegexOptions.Compiled);
    private static readonly Regex Unsafe = new(@"[\\/:*?""<>|\x00-\x1f]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Reserved = new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const string MovieTemplate = "{Title} ({Year}) - {Quality}";
    public const string EpisodeTemplate = "{Title} - S{Season:00}E{Episode:00} - {EpisodeTitle} - {Quality}";

    public sealed record Request(string Kind, string Template, string Title, string OriginalFile,
        int? Year = null, int? Season = null, int? Episode = null,
        string? EpisodeTitle = null, string? Quality = null, string? ReleaseGroup = null);

    public static string Preview(Request input)
    {
        if (input.Kind is not ("movie" or "series"))
            throw new ArgumentException("Naming preview kind must be movie or series");
        if (string.IsNullOrWhiteSpace(input.Title))
            throw new ArgumentException("Media title is required");
        if (string.IsNullOrWhiteSpace(input.Template) || input.Template.Length > 220)
            throw new ArgumentException("Filename template must be 1-220 characters");
        if (input.Template.Contains("..", StringComparison.Ordinal) ||
            input.Template.Contains('/') || input.Template.Contains('\\') ||
            input.Template.Any(char.IsControl))
            throw new ArgumentException("Filename template cannot include folders, traversal or control characters");
        if (string.IsNullOrWhiteSpace(input.OriginalFile) ||
            input.OriginalFile.Contains('/') || input.OriginalFile.Contains('\\'))
            throw new ArgumentException("Original filename must be a single file name");
        var extension = Path.GetExtension(input.OriginalFile);
        if (!Regex.IsMatch(extension, @"^\.[A-Za-z0-9]{2,6}$") ||
            !new[] { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpg", ".mpeg", ".webm" }
                .Contains(extension.ToLowerInvariant(), StringComparer.Ordinal))
            throw new ArgumentException("Original filename has an unsupported extension");
        var originalName = Path.GetFileNameWithoutExtension(input.OriginalFile);
        string Value(string token) => token switch
        {
            "Title" => Clean(input.Title),
            "Year" when input.Year is >= 1900 and <= 2100 => input.Year.Value.ToString(),
            "Season:00" when input.Kind == "series" && input.Season is >= 0 and <= 999 => input.Season.Value.ToString("00"),
            "Episode:00" when input.Kind == "series" && input.Episode is >= 1 and <= 999 => input.Episode.Value.ToString("00"),
            "EpisodeTitle" when input.Kind == "series" && !string.IsNullOrWhiteSpace(input.EpisodeTitle) => Clean(input.EpisodeTitle!),
            "Quality" when !string.IsNullOrWhiteSpace(input.Quality) => Clean(input.Quality!),
            "ReleaseGroup" when !string.IsNullOrWhiteSpace(input.ReleaseGroup) => Clean(input.ReleaseGroup!),
            "OriginalName" => Clean(originalName),
            _ => throw new ArgumentException($"Unsupported or unavailable naming token: {{{token}}}")
        };
        var name = Token.Replace(input.Template, m => Value(m.Groups["name"].Value));
        if (name.Contains('{') || name.Contains('}'))
            throw new ArgumentException("Filename template contains an unknown or malformed token");
        name = Clean(name);
        if (name.Length == 0) throw new ArgumentException("Naming template produced an empty file name");
        // Avoid truncation changing the file extension or creating an empty stem.
        if (name.Length + extension.Length > 220)
            name = name[..(220 - extension.Length)].TrimEnd(' ', '.');
        if (name.Length == 0) throw new ArgumentException("Naming template produced an empty file name");
        if (Reserved.IsMatch(name)) name = "_" + name;
        return name + extension.ToLowerInvariant();
    }

    private static string Clean(string input)
    {
        var text = Unsafe.Replace(input, " ");
        text = Regex.Replace(text, @"\.{2,}", " ");
        text = Whitespace.Replace(text, " ").Trim(' ', '.');
        return text;
    }
}
