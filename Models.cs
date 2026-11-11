using System;
using System.Collections.Generic;
using System.IO;
using Windows.Foundation;
using Windows.UI;

namespace InteractiveMap;

/// <summary>A game the client can show. Load() runs off the UI thread.</summary>
public sealed class GameInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Shown on the logo plate when the game ships no logo image.</summary>
    public required string Initials { get; init; }
    /// <summary>The game's folder under the maps root.</summary>
    public required string Root { get; init; }
    public string? LogoPath { get; init; }
    public Color Accent { get; init; }
    public required Func<GameInfo, GameMap> Load { get; init; }
}

public sealed class GameMap
{
    public required GameInfo Info { get; init; }
    public List<World> Worlds { get; } = new();
    public List<CatGroup> Groups { get; } = new();
    public List<Category> Cats { get; } = new();
    public string? DefaultWorld { get; set; }
    /// <summary>Group nearby pins into a counted cluster while zoomed out.</summary>
    public bool Cluster { get; set; }
    /// <summary>Draw each pin on a round dark plate with a coloured ring.</summary>
    public bool PinPlate { get; set; }
    public float PinSize { get; set; } = 30;
    public float BigPinSize { get; set; } = 38;
}

public sealed class World
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Size in native map pixels (the top pyramid level).</summary>
    public double W { get; init; }
    public double H { get; init; }
    /// <summary>Pyramid levels; level Levels-1 is native.</summary>
    public int Levels { get; init; }
    public int Tile { get; init; } = 512;
    public Color Sea { get; init; }
    /// <summary>The part that has art, so "fit" frames the land.</summary>
    public Rect Art { get; set; }
    /// <summary>Format string with {0}=level, {1}=x, {2}=y.</summary>
    public required string TilePattern { get; init; }
    public List<Marker> Markers { get; } = new();
    public List<MapLabel> Labels { get; } = new();

    public int Z => Levels - 1;
    public string TilePath(int z, int x, int y) => string.Format(TilePattern, z, x, y);
}

public sealed class CatGroup
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>Collapsed the first time the game is opened.</summary>
    public bool StartClosed { get; init; }
}

public sealed class Category
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Label { get; init; }
    public Color Color { get; init; }
    public string? Icon { get; set; }
    public bool DefaultOn { get; init; }
    public bool On { get; set; }
}

public sealed class Marker
{
    public required string Key { get; init; }
    public required World World { get; init; }
    public required Category Cat { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    /// <summary>Sideways shift in screen pixels, for pins that share one spot.</summary>
    public float Dx { get; set; }
    public string? Name { get; init; }
    public string? Desc { get; init; }
    public string? Place { get; init; }
    public string? Icon { get; init; }
    /// <summary>A larger picture for the detail card, if there is one.</summary>
    public string? Portrait { get; init; }
    /// <summary>Extra detail lines for the card: tags, rewards, position.</summary>
    public List<string>? Lines { get; init; }
    public bool Big { get; init; }

    public string Title => string.IsNullOrEmpty(Name) ? Cat.Label : Name!;
    public string? IconOrCat => Icon ?? Cat.Icon;
    public string Uid => World.Id + ":" + Key;
}

public sealed class MapLabel
{
    public required World World { get; init; }
    public required string Text { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    /// <summary>"region", "sub" or "mark".</summary>
    public required string Kind { get; init; }
}

public static class Hex
{
    public static Color Parse(string? s, Color fallback)
    {
        if (string.IsNullOrEmpty(s) || s[0] != '#' || s.Length != 7) return fallback;
        try
        {
            return Color.FromArgb(255,
                Convert.ToByte(s.Substring(1, 2), 16),
                Convert.ToByte(s.Substring(3, 2), 16),
                Convert.ToByte(s.Substring(5, 2), 16));
        }
        catch (FormatException) { return fallback; }
    }

    public static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
    public static Color WithAlpha(this Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

public static class MapsRoot
{
    /// <summary>
    /// Finds the folder that holds one sub-folder per game. Looks for a "maps"
    /// folder beside the exe, then for the website checkout ("interactive-map")
    /// in or next to any parent folder.
    /// </summary>
    public static string? Find()
    {
        var env = Environment.GetEnvironmentVariable("INTERACTIVE_MAP_DATA");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string[] tries =
            {
                Path.Combine(dir.FullName, "maps"),
                Path.Combine(dir.FullName, "pages", "gacha games", "maps"),
                Path.Combine(dir.FullName, "interactive-map", "pages", "gacha games", "maps"),
            };
            foreach (var t in tries) if (Directory.Exists(t)) return t;
        }
        return null;
    }
}
