namespace MultiDriveCloud.Infrastructure;

public sealed class AppPaths
{
    public string Root { get; }
    public string Database => Path.Combine(Root, "index.db");
    public string Secrets => Path.Combine(Root, "secrets");
    public string Logs => Path.Combine(Root, "logs");
    public string Temp => Path.Combine(Root, "temp");
    public string Backups => Path.Combine(Root, "backups");
    public string DefaultDownloads => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MultiDrive Cloud");

    public AppPaths(string? root = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable("MULTIDRIVE_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiDriveCloud");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Secrets);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(Backups);
    }
}
