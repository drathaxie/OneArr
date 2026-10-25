namespace OneArr.Tray;

/// <summary>Everything the tray app keeps lives under %LOCALAPPDATA%\OneArr.</summary>
internal static class Paths
{
    public const string RepoUrl = "https://github.com/drathaxie/OneArr.git";
    public const string Branch = "main";
    public const string Url = "http://127.0.0.1:8780";

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneArr");
    public static string Repo => Path.Combine(Root, "repo");
    public static string App => Path.Combine(Root, "app");
    public static string Config => Path.Combine(Root, "config");
    public static string Logs => Path.Combine(Root, "logs");
    public static string InstalledExe => Path.Combine(Root, "OneArr.Tray.exe");
    public static string ApiExe => Path.Combine(App, "OneArr.Api.exe");
}
