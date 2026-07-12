using System.IO;
using System.Text.Json;

namespace UsageWidget.Services;

public sealed class WidgetSettings
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool Locked { get; set; }
    public int PollMinutes { get; set; } = 5;
    public bool NotificationsEnabled { get; set; } = true;
    public string Theme { get; set; } = "dark";
}

public static class SettingsService
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageWidget");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static WidgetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(WidgetSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
