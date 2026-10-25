namespace OneArr.Tray;

/// <summary>
/// Text logs under %LOCALAPPDATA%\OneArr\logs. Bounded: when a file passes 2 MB it is rotated to .1 (one previous
/// generation kept), so a log never grows past about 4 MB.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 2L * 1024 * 1024;
    private static readonly object Gate = new();
    public static string Folder { get; set; } = "";

    public static void Write(string file, string message)
    {
        lock (Gate)
        {
            if (string.IsNullOrEmpty(Folder)) return;
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, file);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            {
                File.Copy(path, path + ".1", overwrite: true);
                File.Delete(path);
            }
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
    }
}
