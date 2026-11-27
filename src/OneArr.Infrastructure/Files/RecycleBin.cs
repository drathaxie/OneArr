namespace OneArr.Infrastructure.Files;

/// <summary>
/// Moves a replaced library file aside instead of deleting it. Only a same-drive move (a rename) is allowed, so the file's
/// data is never copied and deleted: if anything goes wrong it is still there, under the recycle bin or where it was.
/// </summary>
public static class RecycleBin
{
    /// <summary>The problem preventing a move into this recycle bin, or null when it can be used for this file.</summary>
    public static string? Problem(string? recycleRoot, string file)
    {
        if (string.IsNullOrWhiteSpace(recycleRoot)) return "No recycle bin folder is set (Settings → Media management)";
        if (!Path.IsPathFullyQualified(recycleRoot)) return "The recycle bin folder must be a full path";
        if (!SafeImporter.IsSameVolume(file, ExistingAncestor(recycleRoot))) return "The recycle bin is on a different drive from the file";
        return null;
    }

    /// <summary>Moves the file to recycleRoot\yyyy-MM-dd\name, adding a number if that name is taken. Returns the new path.</summary>
    public static string Move(string recycleRoot, string file, DateTimeOffset now)
    {
        if (Problem(recycleRoot, file) is string problem) throw new IOException(problem);
        var folder = Path.Combine(recycleRoot, now.UtcDateTime.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        var target = Path.Combine(folder, name + ext);
        for (var i = 2; File.Exists(target); i++) target = Path.Combine(folder, $"{name} ({i}){ext}");
        File.Move(file, target, overwrite: false);
        return target;
    }

    private static string ExistingAncestor(string path)
    {
        var p = path;
        while (!Directory.Exists(p) && Path.GetDirectoryName(p) is { } parent && parent != p) p = parent;
        return p;
    }
}
