using System.Text.Json;

namespace AirPrinter.Services;

public static class IdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetOrCreate(string printerName)
    {
        Dictionary<string, string> map = Load();
        if (!map.TryGetValue(printerName, out string? id) || !Guid.TryParse(id, out _))
        {
            id = Guid.NewGuid().ToString("D");
            map[printerName] = id;
            Save(map);
        }

        return "urn:uuid:" + id;
    }

    private static Dictionary<string, string> Load()
    {
        string path = FilePath();
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save(Dictionary<string, string> map)
    {
        File.WriteAllText(FilePath(), JsonSerializer.Serialize(map, JsonOptions));
    }

    private static string FilePath() => Path.Combine(AppPaths.Root, "identities.json");
}

public sealed class UserSettings
{
    public string? PrinterName { get; set; }
    public bool Share { get; set; } = true;
    public string? ShareName { get; set; }
}

public static class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static UserSettings Load()
    {
        string path = FilePath();
        if (!File.Exists(path))
        {
            return new UserSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new UserSettings();
        }
        catch (JsonException)
        {
            return new UserSettings();
        }
    }

    public static void Save(UserSettings settings)
    {
        File.WriteAllText(FilePath(), JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static string FilePath() => Path.Combine(AppPaths.Root, "settings.json");
}
