using System.Text.Json;

namespace StoDialogueCapture.Gui;

internal sealed class GuiSettings
{
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    public bool OverlayVisible { get; set; }
    public bool AlwaysRenameToMissionTitle { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string DefaultPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StoDialogueCapture");
        return Path.Combine(directory, "settings.json");
    }

    public static GuiSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new GuiSettings();
            }
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<GuiSettings>(json, JsonOptions);
            return settings ?? new GuiSettings();
        }
        catch
        {
            return new GuiSettings();
        }
    }

    public void Save(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best effort - never throw from settings persistence.
        }
    }
}
