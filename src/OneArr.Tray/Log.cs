namespace OneArr.Tray;

/// <summary>Append-only text logs in %LOCALAPPDATA%\OneArr\logs. Writes are serialised across threads.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string file, string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Paths.Logs);
            File.AppendAllText(Path.Combine(Paths.Logs, file), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
    }
}
