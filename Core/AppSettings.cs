using System.IO;
using System.Text.Json;

namespace TheoTransfer.Core;

public sealed class AppSettings
{
    public string? ReceiveFolder { get; set; }
    public int Port { get; set; } = 8421;
    public string? StaticKey { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TheoTransfer", "settings.json");

    private static readonly string[] LegacyPaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TheoFileTransfer", "settings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanFileTransfer", "settings.json"),
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            foreach (var legacy in LegacyPaths)
                if (File.Exists(legacy))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(legacy)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
