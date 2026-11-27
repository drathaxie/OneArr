namespace OneArr.Infrastructure.Files;

/// <summary>
/// Finds the subtitle files that belong to one video in a download, and names them to sit next to the imported video
/// ("Movie (2021).en.forced.srt"), the way Plex, Jellyfin and Kodi pick them up.
/// </summary>
public static class Subtitles
{
    public static readonly string[] Extensions = [".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup"];
    private static readonly string[] SubFolders = ["Subs", "Subtitles", "Sub"];

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["eng"] = "en", ["en"] = "en", ["spanish"] = "es", ["spa"] = "es", ["es"] = "es",
        ["french"] = "fr", ["fre"] = "fr", ["fra"] = "fr", ["fr"] = "fr", ["german"] = "de", ["ger"] = "de", ["deu"] = "de", ["de"] = "de",
        ["italian"] = "it", ["ita"] = "it", ["it"] = "it", ["portuguese"] = "pt", ["por"] = "pt", ["pt"] = "pt", ["brazilian"] = "pt-BR",
        ["dutch"] = "nl", ["nld"] = "nl", ["nl"] = "nl", ["japanese"] = "ja", ["jpn"] = "ja", ["ja"] = "ja", ["korean"] = "ko", ["kor"] = "ko",
        ["chinese"] = "zh", ["chi"] = "zh", ["zho"] = "zh", ["zh"] = "zh", ["russian"] = "ru", ["rus"] = "ru", ["ru"] = "ru",
        ["arabic"] = "ar", ["ara"] = "ar", ["swedish"] = "sv", ["swe"] = "sv", ["norwegian"] = "no", ["nor"] = "no", ["danish"] = "da",
        ["dan"] = "da", ["finnish"] = "fi", ["fin"] = "fi", ["polish"] = "pl", ["pol"] = "pl", ["turkish"] = "tr", ["tur"] = "tr",
    };

    /// <summary>Subtitles that sit beside an imported library video under its name ("Movie.mkv" → "Movie.en.srt", "Movie.srt").</summary>
    public static IReadOnlyList<string> Sidecars(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (dir is null || !Directory.Exists(dir)) return [];
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        return Directory.EnumerateFiles(dir, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(f => IsSubtitle(f) && (Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public static bool IsSubtitle(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Subtitles beside the video or in a Subs folder next to it. With one video in the download every subtitle there is its
    /// own; with several, only those named after the video, or inside Subs\&lt;video name&gt;\.
    /// </summary>
    public static IReadOnlyList<string> For(string videoPath, bool onlyVideoInDownload)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (dir is null || !Directory.Exists(dir)) return [];
        var videoBase = Path.GetFileNameWithoutExtension(videoPath);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var found = Directory.EnumerateFiles(dir, "*", options).Where(IsSubtitle).ToList();
        foreach (var name in SubFolders)
        {
            var sub = Path.Combine(dir, name);
            if (Directory.Exists(sub))
                found.AddRange(Directory.EnumerateFiles(sub, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(IsSubtitle));
        }
        if (onlyVideoInDownload) return found.Distinct().ToList();
        return found.Distinct().Where(s =>
            Path.GetFileName(s).StartsWith(videoBase, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(Path.GetDirectoryName(s)), videoBase, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>"Movie (2021)" + "Some.Movie.2021.eng.forced.srt" → "Movie (2021).en.forced.srt". Unknown language: no tag.</summary>
    public static string DestinationName(string newVideoBase, string subtitlePath, string originalVideoBase)
    {
        var ext = Path.GetExtension(subtitlePath).ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(subtitlePath);
        var rest = name.StartsWith(originalVideoBase, StringComparison.OrdinalIgnoreCase) ? name[originalVideoBase.Length..] : name;
        var tokens = rest.Split(['.', '_', ' ', '-', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        var language = tokens.Select(t => Languages.GetValueOrDefault(t)).LastOrDefault(l => l is not null);
        var flags = tokens.Where(t => t.Equals("forced", StringComparison.OrdinalIgnoreCase) || t.Equals("sdh", StringComparison.OrdinalIgnoreCase) || t.Equals("cc", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.ToLowerInvariant()).Distinct();
        var parts = new List<string> { newVideoBase };
        if (language is not null) parts.Add(language);
        parts.AddRange(flags);
        return string.Join('.', parts) + ext;
    }
}
