using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Foundation;
using Windows.UI;

namespace InteractiveMap;

/// <summary>
/// Reads the same generated data files the web pages use. Each is one
/// "window.NAME = {json};" assignment, so the JSON is cut out and parsed as is.
/// </summary>
public static class GameCatalog
{
    public static List<GameInfo> Find(string mapsRoot)
    {
        var games = new List<GameInfo>();

        var ww = Path.Combine(mapsRoot, "Wuthering Waves");
        if (File.Exists(Path.Combine(ww, "js", "data.js")))
        {
            games.Add(new GameInfo
            {
                Id = "wuwa", Name = "Wuthering Waves", Initials = "WW", Root = ww,
                LogoPath = GameIcon("wuwa"),
                Accent = Hex.Rgb(0xF2, 0xC1, 0x4E),
                Load = LoadWuwa,
            });
        }

        var nte = Path.Combine(mapsRoot, "Neverness to Everness");
        if (File.Exists(Path.Combine(nte, "mapdata.js")))
        {
            games.Add(new GameInfo
            {
                Id = "nte", Name = "Neverness to Everness", Initials = "NTE", Root = nte,
                LogoPath = GameIcon("nte"),
                Accent = Hex.Rgb(0xFF, 0x3D, 0x7F),
                Load = LoadNte,
            });
        }
        return games;
    }

    /// <summary>The game's app icon, shipped with the client as Assets\games\{id}.png.</summary>
    static string? GameIcon(string id)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "games", id + ".png");
        return File.Exists(path) ? path : null;
    }

    // ------------------------------------------------------------ helpers
    static JsonDocument ReadJs(string path)
    {
        var text = File.ReadAllText(path);
        // skip the leading /* comment */, which may hold braces of its own
        int from = 0;
        if (text.StartsWith("/*", StringComparison.Ordinal))
        {
            int end = text.IndexOf("*/", StringComparison.Ordinal);
            if (end > 0) from = end + 2;
        }
        int a = text.IndexOf('{', from), b = text.LastIndexOf('}');
        if (a < 0 || b <= a) throw new InvalidDataException("No data object in " + path);
        return JsonDocument.Parse(text.AsMemory(a, b - a + 1));
    }

    static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    static double Num(JsonElement e, string name, double fallback = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
    }

    static bool Flag(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static IEnumerable<JsonElement> Items(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray()) yield return x;
    }

    static string N(double v) => Math.Round(v).ToString("N0", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------ Wuthering Waves
    static GameMap LoadWuwa(GameInfo info)
    {
        using var doc = ReadJs(Path.Combine(info.Root, "js", "data.js"));
        var root = doc.RootElement;
        var map = new GameMap { Info = info, Cluster = true, PinPlate = false, PinSize = 30, BigPinSize = 38 };
        string IconPath(string name) => Path.Combine(info.Root, "assets", "icons", name + ".png");

        var worlds = new Dictionary<string, World>();
        foreach (var w in Items(root, "worlds"))
        {
            var id = Str(w, "id")!;
            double src = Num(w, "src", 1024);
            var world = new World
            {
                Id = id,
                Name = Str(w, "name") ?? id,
                W = Num(w, "w"), H = Num(w, "h"),
                Levels = (int)Num(w, "levels", 1),
                Tile = (int)Num(w, "tile", 512),
                Sea = Hex.Parse(Str(w, "sea"), Hex.Rgb(0x04, 0x26, 0x3A)),
                TilePattern = Path.Combine(info.Root, "tiles", id, "z{0}", "{1}_{2}.jpg"),
            };
            // cells are the game's 1024 px grid cells that hold art
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var c in Items(w, "cells"))
            {
                double cx = c[0].GetDouble(), cy = c[1].GetDouble();
                x0 = Math.Min(x0, cx); y0 = Math.Min(y0, cy);
                x1 = Math.Max(x1, cx + 1); y1 = Math.Max(y1, cy + 1);
            }
            world.Art = x1 > x0
                ? new Rect(x0 * src, y0 * src, (x1 - x0) * src, (y1 - y0) * src)
                : new Rect(0, 0, world.W, world.H);
            worlds[id] = world;
            map.Worlds.Add(world);
        }
        map.DefaultWorld = worlds.ContainsKey("8") ? "8" : map.Worlds.FirstOrDefault()?.Id;

        foreach (var g in Items(root, "groups"))
        {
            var id = Str(g, "id")!;
            map.Groups.Add(new CatGroup { Id = id, Label = Str(g, "label") ?? id, StartClosed = id is "plants" or "minerals" });
        }

        var cats = new Dictionary<string, Category>();
        foreach (var c in Items(root, "cats"))
        {
            var id = Str(c, "id")!;
            var icon = Str(c, "icon");
            var cat = new Category
            {
                Id = id,
                Group = Str(c, "group") ?? "",
                Label = Str(c, "label") ?? id,
                Color = Hex.Parse(Str(c, "color"), Hex.Rgb(0xEC, 0xD9, 0xA0)),
                Icon = icon != null ? IconPath(icon) : null,
                DefaultOn = Flag(c, "on"),
            };
            cat.On = cat.DefaultOn;
            cats[id] = cat;
            map.Cats.Add(cat);
        }

        var places = Items(root, "places").Select(p => p.GetString() ?? "").ToList();

        foreach (var m in Items(root, "markers"))
        {
            var wid = Str(m, "m");
            var cid = Str(m, "c");
            if (wid == null || cid == null || !worlds.TryGetValue(wid, out var world) || !cats.TryGetValue(cid, out var cat)) continue;

            string? place = null;
            if (m.TryGetProperty("p", out var p))
            {
                if (p.ValueKind == JsonValueKind.Number) { int i = p.GetInt32(); if (i >= 0 && i < places.Count) place = places[i]; }
                else if (p.ValueKind == JsonValueKind.String) place = p.GetString();
            }
            List<string>? lines = null;
            if (m.TryGetProperty("w", out var pos) && pos.ValueKind == JsonValueKind.Array && pos.GetArrayLength() >= 3)
                lines = new List<string> { $"In-game position: X {N(pos[0].GetDouble())}, Y {N(pos[1].GetDouble())}, Z {N(pos[2].GetDouble())}" };

            var icon = Str(m, "i");
            world.Markers.Add(new Marker
            {
                Key = Str(m, "k") ?? "",
                World = world, Cat = cat,
                X = (float)Num(m, "x"), Y = (float)Num(m, "y"),
                Name = Str(m, "n"), Desc = Str(m, "d"), Place = place,
                Icon = icon != null ? IconPath(icon) : null,
                Lines = lines,
                Big = cid == "boss",
            });
        }

        foreach (var l in Items(root, "labels"))
        {
            var wid = Str(l, "m");
            var text = Str(l, "t");
            var kind = Str(l, "k");
            if (wid == null || string.IsNullOrEmpty(text) || kind == null || !worlds.TryGetValue(wid, out var world)) continue;
            world.Labels.Add(new MapLabel { World = world, Text = text, Kind = kind, X = (float)Num(l, "x"), Y = (float)Num(l, "y") });
        }
        return map;
    }

    // ------------------------------------------------------------ Neverness to Everness
    // The page keeps its layer table in map.js; this is the same table.
    static readonly (string Id, string Label, string Group, bool On, string? Ring)[] NteLayers =
    {
        ("boss", "Anomaly Hunt", "index", true, "#FF9F3D"),
        ("vision", "Anomaly Commission", "index", true, "#35C9F0"),
        ("Clone", "Anomaly Zone", "index", true, null),
        ("ProfessionalServices", "Recommended Loc", "index", true, null),
        ("FeaturedBusiness", "City Shops", "index", true, null),
        ("SpecialPlace", "Special Location", "index", true, null),
        ("quest", "Quests", "index", true, null),
        ("fishing", "Fishing Spots", "index", true, null),
        ("fleet", "Racing Fleets", "index", true, null),
        ("property", "Properties", "index", true, null),
        ("monster", "Monster Locations", "index", false, null),
        ("viewpoint", "Viewpoints", "coll", true, null),
        ("oracle", "Oracle Stones", "coll", false, "#FF3D7F"),
        ("treasure", "Gifts from “21”", "coll", false, "#F3C14B"),
        ("supply", "Supply Boxes", "coll", false, null),
    };

    static GameMap LoadNte(GameInfo info)
    {
        using var doc = ReadJs(Path.Combine(info.Root, "mapdata.js"));
        var root = doc.RootElement;
        var map = new GameMap { Info = info, Cluster = false, PinPlate = true, PinSize = 26, BigPinSize = 32 };

        // 51 x 51 native tiles of 512 px at level 6, with a pyramid down to level 0
        const int grid = 51, tile = 512, levels = 7;
        double size = grid * tile;
        var world = new World
        {
            Id = "hethereau", Name = "Hethereau",
            W = size, H = size, Levels = levels, Tile = tile,
            Sea = Hex.Rgb(0x05, 0x05, 0x07),
            Art = new Rect(0, 0, size, size),
            TilePattern = Path.Combine(info.Root, "assets", "map", "tiles", "{0}", "{1}_{2}.png"),
        };
        map.Worlds.Add(world);
        map.DefaultWorld = world.Id;

        map.Groups.Add(new CatGroup { Id = "index", Label = "Map Index" });
        map.Groups.Add(new CatGroup { Id = "coll", Label = "Collectibles" });
        var cats = new Dictionary<string, Category>();
        foreach (var l in NteLayers)
        {
            var cat = new Category
            {
                Id = l.Id, Group = l.Group, Label = l.Label, DefaultOn = l.On, On = l.On,
                Color = Hex.Parse(l.Ring, Color.FromArgb(255, 0xD8, 0xD8, 0xE2)),
            };
            cats[l.Id] = cat;
            map.Cats.Add(cat);
        }

        string? Asset(string folder, string? name) =>
            string.IsNullOrEmpty(name) ? null : Path.Combine(info.Root, "assets", folder, name);

        // what a shop sells: [item index, price] pairs into a shared item list
        var shopItems = Items(root, "shopItems").ToList();
        root.TryGetProperty("shops", out var shops);

        Marker Make(JsonElement m, Category cat, string? desc, List<string> lines, string? portrait)
        {
            var pos = m.GetProperty("pos");
            var mk = new Marker
            {
                Key = Str(m, "id") ?? "",
                World = world, Cat = cat,
                X = (float)(Num(pos, "u") * size), Y = (float)(Num(pos, "v") * size),
                Name = Str(m, "name"), Desc = desc, Place = Str(m, "area"),
                Icon = Asset("mapicons", Str(m, "icon")),
                Portrait = portrait,
                Lines = lines.Count > 0 ? lines : null,
                Big = cat.Id == "boss",
            };
            cat.Icon ??= mk.Icon;
            return mk;
        }

        foreach (var m in Items(root, "markers"))
        {
            var cid = Str(m, "categoryId");
            if (cid == null || !cats.TryGetValue(cid, out var cat) || !m.TryGetProperty("pos", out _)) continue;
            var lines = new List<string>();
            if (Flag(m, "teleport")) lines.Add("Teleport");
            var id = Str(m, "id");
            if (id != null && shops.ValueKind == JsonValueKind.Object && shops.TryGetProperty(id, out var stock) && stock.ValueKind == JsonValueKind.Array)
            {
                bool head = false;
                foreach (var pair in stock.EnumerateArray())
                {
                    int i = pair[0].GetInt32();
                    if (i < 0 || i >= shopItems.Count) continue;
                    if (!head) { lines.Add("Sells (prices in Fons)"); head = true; }
                    lines.Add($"  {shopItems[i][1].GetString()}  {N(pair[1].GetDouble())}");
                }
            }
            world.Markers.Add(Make(m, cat, null, lines, null));
        }

        foreach (var m in Items(root, "collectibles"))
        {
            var layer = Str(m, "layer");
            if (layer == null || !cats.TryGetValue(layer, out var cat) || !m.TryGetProperty("pos", out _)) continue;
            var lines = new List<string>();
            var code = Str(m, "code");
            if (!string.IsNullOrEmpty(code)) lines.Add(code);
            var kind = Str(m, "kind");
            if (!string.IsNullOrEmpty(kind)) lines.Add(kind);
            var diff = Str(m, "diffText") ?? Str(m, "difficulty");
            if (!string.IsNullOrEmpty(diff)) lines.Add("Difficulty " + diff);
            if (Flag(m, "teleport")) lines.Add("Teleport");
            if (Flag(m, "locked")) lines.Add("Requires unlock");
            var value = Str(m, "value");
            if (!string.IsNullOrEmpty(value)) lines.Add($"+{value} Footprint");

            bool head = false;
            foreach (var d in Items(m, "loot"))
            {
                if (!head) { lines.Add(Str(m, "lootTitle") ?? "Possible rewards"); head = true; }
                var count = Str(d, "count");
                var price = Str(d, "price");
                lines.Add("  " + Str(d, "name") + (price != null ? "  " + price : count != null ? "  ×" + count : ""));
            }

            string? portrait = Asset("anomalies", Str(m, "portrait"));
            if (portrait == null && m.TryGetProperty("item", out var item)) portrait = Asset("items", Str(item, "icon"));
            world.Markers.Add(Make(m, cat, Str(m, "desc"), lines, portrait));
        }

        // Several quests can start from one spot: spread those pins sideways.
        foreach (var spot in world.Markers.Where(m => m.Cat.Id == "quest").GroupBy(m => (m.X, m.Y)))
        {
            var list = spot.ToList();
            for (int i = 0; i < list.Count; i++) list[i].Dx = (i - (list.Count - 1) / 2f) * 30f;
        }
        return map;
    }
}
