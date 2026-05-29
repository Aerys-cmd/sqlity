using System.Text.Json;

namespace Sqlity.Studio.Services;

public sealed class AppSettings
{
    public List<string> RecentDatabases { get; set; } = [];
    public string? LastOpenedDatabase { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sqlity.Studio",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void AddRecent(string path)
    {
        RecentDatabases.Remove(path);
        RecentDatabases.Insert(0, path);
        if (RecentDatabases.Count > 10)
            RecentDatabases.RemoveRange(10, RecentDatabases.Count - 10);
        LastOpenedDatabase = path;
        Save();
    }
}
