using System.IO;
using System.Text.Json;

namespace K2AzureMigrator;

internal class AppSettings
{
    public string AzureServer       { get; set; } = "";
    public string AzureDatabase     { get; set; } = "K2";
    public string AzureUser         { get; set; } = "";
    public string AzurePassword     { get; set; } = ""; // plaintext — single-operator migration tool
    public string ImportBacpacPath  { get; set; } = "";
    public bool   DropIfExists      { get; set; } = false;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2AzureMigrator", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
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
