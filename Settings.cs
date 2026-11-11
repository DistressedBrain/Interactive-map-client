using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace InteractiveMap;

/// <summary>What the player chose, kept in %LOCALAPPDATA%\InteractiveMap\settings.json.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string? Game { get; set; }
    public bool PaneOpen { get; set; } = true;
    public Dictionary<string, GameSettings> Games { get; set; } = new();

    public GameSettings For(string gameId)
    {
        if (!Games.TryGetValue(gameId, out var g)) Games[gameId] = g = new GameSettings();
        return g;
    }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InteractiveMap", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // a broken or unreadable file just means starting with defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // settings stay in memory for this run
        }
    }
}

public sealed class GameSettings
{
    public string? World { get; set; }
    public bool Names { get; set; } = true;
    public Dictionary<string, bool> Cats { get; set; } = new();
    public HashSet<string> Hidden { get; set; } = new();
    /// <summary>Null until the player opens or closes a group.</summary>
    public HashSet<string>? OpenGroups { get; set; }
}
