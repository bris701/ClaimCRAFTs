using System.IO;
using System.Text.Json;

namespace ClaimCRAFTs.Services;

public sealed class UserSettings
{
    public string? ClaimId { get; set; }
    public string? ClaimName { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool LiveEnabled { get; set; } = true;
    public double BackgroundOpacity { get; set; } = 100;
    public double TextOpacity { get; set; } = 100;
    public double TextShade { get; set; }
}

public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaimCRAFTs", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception) { /* Invalid/unreadable user settings: fall back safely. */ }
        return new();
    }

    public static void Save(UserSettings settings)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, FilePath, overwrite: true);
    }
}
