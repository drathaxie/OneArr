using System.Text.RegularExpressions;

namespace OneArr.Infrastructure.Files;

/// <summary>
/// Sample clips shipped with a release ("sample-episode.mkv", or a "Sample" folder). They are never imported or matched:
/// they would otherwise compete with the real file for the same episode destination.
/// </summary>
public static partial class SampleFiles
{
    public static bool IsSample(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (SampleToken().IsMatch(name)) return true;
        var folder = Path.GetDirectoryName(path);
        return folder is not null && folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("sample", StringComparison.OrdinalIgnoreCase) || part.Equals("samples", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"sample" set off by a separator or the ends of the name, so a title such as "The Sample Show" is not a sample.</summary>
    [GeneratedRegex(@"(?i)(^|[-._\[(])sample($|[-._\])])")]
    private static partial Regex SampleToken();
}
