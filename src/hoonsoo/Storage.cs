using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Hoonsoo;
public sealed class SettingsStore
{
    public string DirectoryPath { get; }
    public SettingsStore(string? directory = null) => DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hoonsoo");
    public Settings Load()
    {
        try { var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(DirectoryPath, "settings.json"))) ?? new(); s.Normalize(); return s; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }
    public void Save(Settings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("hoonsoo", "\"" + Environment.ProcessPath + "\" --background");
        else key.DeleteValue("hoonsoo", false);
        // Pre-rename builds wrote a "DevLingo" value pointing at the old exe. Leaving it
        // behind keeps a second autostart entry that resolves to a deleted path.
        key.DeleteValue("DevLingo", false);
    }
}
