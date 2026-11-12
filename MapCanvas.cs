using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;

namespace InteractiveMap;

/// <summary>
/// The map surface: a tile pyramid, pins, clusters and place names drawn with
/// Win2D. Coordinates are native map pixels; zoom follows the web pages, where
/// zoom == World.Z shows the native level at 1:1.
/// </summary>
public sealed class MapCanvas : UserControl
{
    /// <summary>Most decoded tiles kept at once (about 1 MB each).</summary>
    const int TileCap = 96;

    readonly CanvasControl _canvas = new();
    GameMap? _map;
    World? _world;
    double _cx, _cy, _zoom;
    bool _fitPending;
    long _frame;
    int _generation;

    // ---- tiles and icons
    sealed class Slot { public CanvasBitmap? Bitmap; public bool Loading; public long LastFrame; }
    readonly Dictionary<(string World, int Z, int X, int Y), Slot> _tiles = new();
    readonly Dictionary<string, Slot> _icons = new();

    // ---- pins
    readonly record struct Item(float X, float Y, int Count, Marker Marker);
    readonly List<Item> _items = new();
    bool _itemsDirty = true;
    double _itemsZoom = double.NaN;
    Item? _hover;

    // ---- pointer
    bool _pressed, _dragging;
    Point _downAt, _lastAt;

    readonly CanvasTextFormat _countFormat = Format(14, FontWeights.Bold);
    readonly CanvasTextFormat _regionFormat = Format(16, FontWeights.SemiBold);
    readonly CanvasTextFormat _subFormat = Format(13.5f, FontWeights.SemiBold);
    readonly CanvasTextFormat _markFormat = Format(12, FontWeights.Medium);
    readonly CanvasTextFormat _tipTitleFormat = Format(13, FontWeights.SemiBold, CanvasHorizontalAlignment.Left);
    readonly CanvasTextFormat _tipSubFormat = Format(11.5f, FontWeights.Normal, CanvasHorizontalAlignment.Left);

    /// <summary>Whether a pin is on the map: its category is on and it is not hidden.</summary>
    internal Func<Marker, bool> IsShown { get; set; } = _ => true;
    /// <summary>A pin was tapped, or null for a tap on empty map.</summary>
    internal event Action<Marker?>? PinTapped;

    public Color Accent { get; set; } = Hex.Rgb(0xF2, 0xC1, 0x4E);
    public bool ShowNames { get; set; } = true;
    public bool HideMode { get; set; }
    internal Marker? Selected { get; set; }
    internal World? World => _world;

    public MapCanvas()
    {
        Content = _canvas;
        IsTabStop = false;
        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (_, _) => DropBitmaps();   // a new device: old bitmaps are gone
        SizeChanged += (_, _) => { if (_fitPending) Fit(); else Redraw(); };
        Unloaded += (_, _) => { _canvas.RemoveFromVisualTree(); };

        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCanceled += (_, _) => { _pressed = _dragging = false; };
        PointerCaptureLost += (_, _) => { _pressed = _dragging = false; };
        PointerExited += (_, _) => SetHover(null);
        PointerWheelChanged += OnWheel;
        DoubleTapped += OnDoubleTapped;
    }

    static CanvasTextFormat Format(float size, Windows.UI.Text.FontWeight weight,
        CanvasHorizontalAlignment align = CanvasHorizontalAlignment.Center) => new()
    {
        FontFamily = "Segoe UI Variable Display",
        FontSize = size,
        FontWeight = weight,
        HorizontalAlignment = align,
        VerticalAlignment = align == CanvasHorizontalAlignment.Center ? CanvasVerticalAlignment.Center : CanvasVerticalAlignment.Top,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    // ------------------------------------------------------------ public surface
    internal void SetMap(GameMap? map)
    {
        _map = map;
        _world = null;
        Selected = null;
        _hover = null;
        DropBitmaps();
        Redraw();
    }

    internal void SetWorld(World world)
    {
        _world = world;
        Selected = null;
        _hover = null;
        _itemsDirty = true;
        DropTiles();
        _canvas.ClearColor = world.Sea;
        Fit();
    }

    /// <summary>Frames the part of the world that has art.</summary>
    public void Fit()
    {
        if (_world == null) return;
        if (ActualWidth < 2 || ActualHeight < 2) { _fitPending = true; return; }
        _fitPending = false;
        var art = _world.Art;
        double scale = Math.Min((ActualWidth - 24) / art.Width, (ActualHeight - 24) / art.Height);
        _zoom = Math.Clamp(_world.Z + Math.Log2(Math.Max(scale, 1e-6)), MinZoom, MaxZoom);
        _cx = art.X + art.Width / 2;
        _cy = art.Y + art.Height / 2;
        Redraw();
    }

    public void ZoomBy(double delta) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), delta);

    public void CenterOn(double x, double y, double minZoom)
    {
        _zoom = Math.Clamp(Math.Max(_zoom, minZoom), MinZoom, MaxZoom);
        _cx = x; _cy = y;
        ClampCenter();
        Redraw();
    }

    public void CenterAt(double x, double y, double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _cx = x; _cy = y;
        ClampCenter();
        Redraw();
    }

    /// <summary>Call after filters or hidden pins change.</summary>
    public void RefreshMarkers()
    {
        _itemsDirty = true;
        Redraw();
    }

    public void Redraw() => _canvas.Invalidate();

    // ------------------------------------------------------------ view maths
    double MapScale => _world == null ? 1 : Math.Pow(2, _zoom - _world.Z);   // screen px per map px
    double MaxZoom => _world == null ? 0 : _world.Z + 2;
    double MinZoom
    {
        get
        {
            if (_world == null || ActualWidth < 2) return 0;
            double whole = Math.Min(ActualWidth / _world.W, ActualHeight / _world.H);
            return Math.Min(_world.Z + Math.Log2(Math.Max(whole, 1e-6)) - 0.5, _world.Z);
        }
    }

    Vector2 ToScreen(double x, double y, double scale) =>
        new((float)((x - _cx) * scale + ActualWidth / 2), (float)((y - _cy) * scale + ActualHeight / 2));

    void ClampCenter()
    {
        if (_world == null) return;
        _cx = Math.Clamp(_cx, 0, _world.W);
        _cy = Math.Clamp(_cy, 0, _world.H);
    }

    void ZoomAt(Point at, double delta)
    {
        if (_world == null) return;
        double before = MapScale;
        double mx = _cx + (at.X - ActualWidth / 2) / before, my = _cy + (at.Y - ActualHeight / 2) / before;
        _zoom = Math.Clamp(_zoom + delta, MinZoom, MaxZoom);
        double after = MapScale;
        _cx = mx - (at.X - ActualWidth / 2) / after;
        _cy = my - (at.Y - ActualHeight / 2) / after;
        ClampCenter();
        Redraw();
    }

    // ------------------------------------------------------------ bitmaps
    void DropTiles()
    {
        _generation++;
        foreach (var s in _tiles.Values) s.Bitmap?.Dispose();
        _tiles.Clear();
    }

    void DropBitmaps()
    {
        DropTiles();
        foreach (var s in _icons.Values) s.Bitmap?.Dispose();
        _icons.Clear();
    }

    async void LoadInto(Slot slot, string path, bool isTile)
    {
        int generation = _generation;
        slot.Loading = true;
        CanvasBitmap? bitmap = null;
        try
        {
            if (File.Exists(path))
            {
                var bytes = await File.ReadAllBytesAsync(path);
                using var stream = new MemoryStream(bytes).AsRandomAccessStream();
                bitmap = await CanvasBitmap.LoadAsync(_canvas, stream);
            }
        }
        catch (Exception)
        {
            // an unreadable or broken image is drawn as missing
        }
        slot.Loading = false;
        if (generation != _generation) { bitmap?.Dispose(); return; }   // the world changed meanwhile
        slot.Bitmap = bitmap;
        if (isTile) TrimTiles();
        Redraw();
    }

    /// <summary>The tile if decoded; starts loading it otherwise.</summary>
    CanvasBitmap? Tile(World w, int z, int x, int y, bool load)
    {
        var key = (w.Id, z, x, y);
        if (!_tiles.TryGetValue(key, out var slot))
        {
            if (!load) return null;
            _tiles[key] = slot = new Slot();
            LoadInto(slot, w.TilePath(z, x, y), true);
        }
        slot.LastFrame = _frame;
        return slot.Bitmap;
    }

    bool TilePending(World w, int z, int x, int y) =>
        _tiles.TryGetValue((w.Id, z, x, y), out var s) && s.Loading;

    void TrimTiles()
    {
        if (_tiles.Count <= TileCap) return;
        var old = new List<KeyValuePair<(string, int, int, int), Slot>>();
        foreach (var kv in _tiles) if (!kv.Value.Loading && kv.Value.LastFrame < _frame - 1) old.Add(kv);
        old.Sort((a, b) => a.Value.LastFrame.CompareTo(b.Value.LastFrame));
        int drop = Math.Min(old.Count, _tiles.Count - TileCap);
        for (int i = 0; i < drop; i++)
        {
            old[i].Value.Bitmap?.Dispose();
            _tiles.Remove(old[i].Key);
        }
    }

    CanvasBitmap? Icon(string? path)
    {
        if (path == null) return null;
        if (!_icons.TryGetValue(path, out var slot))
        {
            _icons[path] = slot = new Slot();
            LoadInto(slot, path, false);
        }
        return slot.Bitmap;
    }

    // ------------------------------------------------------------ pins
    void RebuildItems()
    {
        _items.Clear();
        _itemsDirty = false;
        if (_map == null || _world == null) return;
        int Z = _world.Z;
        // clusters only change at half-zoom steps, so they hold still while panning and easing
        double q = Math.Floor(_zoom * 2) / 2;
        _itemsZoom = q;

        if (!_map.Cluster || q >= Z + 1)
        {
            foreach (var m in _world.Markers) if (IsShown(m)) _items.Add(new Item(m.X, m.Y, 1, m));
            return;
        }

        double radius = q >= Z ? 20 : 56;
        double cell = radius * 1.5 / Math.Pow(2, q - Z);
        var cells = new Dictionary<(int, int), (double X, double Y, int N, Marker First)>();
        foreach (var m in _world.Markers)
        {
            if (!IsShown(m)) continue;
            var key = ((int)Math.Floor(m.X / cell), (int)Math.Floor(m.Y / cell));
            cells[key] = cells.TryGetValue(key, out var c) ? (c.X + m.X, c.Y + m.Y, c.N + 1, c.First) : (m.X, m.Y, 1, m);
        }
        foreach (var c in cells.Values)
            _items.Add(new Item((float)(c.X / c.N), (float)(c.Y / c.N), c.N, c.First));
    }

    float SizeOf(Item it)
    {
        if (it.Count > 1) return it.Count < 10 ? 38 : it.Count < 100 ? 46 : it.Count < 1000 ? 54 : 62;
        return it.Marker.Big ? _map!.BigPinSize : _map!.PinSize;
    }

    Vector2 ScreenOf(Item it, double scale)
    {
        var p = ToScreen(it.X, it.Y, scale);
        if (it.Count == 1) p.X += it.Marker.Dx;
        return p;
    }

    Item? HitTest(Point at)
    {
        if (_world == null || _map == null) return null;
        double scale = MapScale;
        Item? best = null;
        float bestD = float.MaxValue;
        var p = new Vector2((float)at.X, (float)at.Y);
        foreach (var it in _items)
        {
            var s = ScreenOf(it, scale);
            float d = Vector2.Distance(s, p);
            if (d <= SizeOf(it) / 2 + 3 && d < bestD) { best = it; bestD = d; }
        }
        return best;
    }

    void SetHover(Item? item)
    {
        if (Nullable.Equals(_hover, item)) return;
        _hover = item;
        ProtectedCursor = item == null ? null : InputSystemCursor.Create(HideMode && item.Value.Count == 1 ? InputSystemCursorShape.Cross : InputSystemCursorShape.Hand);
        Redraw();
    }

    // ------------------------------------------------------------ input
    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _pressed = true; _dragging = false;
        _downAt = _lastAt = pt.Position;
        CapturePointer(e.Pointer);
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var at = e.GetCurrentPoint(this).Position;
        if (!_pressed) { SetHover(HitTest(at)); return; }
        if (!_dragging && Math.Abs(at.X - _downAt.X) + Math.Abs(at.Y - _downAt.Y) > 5)
        {
            _dragging = true;
            SetHover(null);
        }
        if (_dragging && _world != null)
        {
            double scale = MapScale;
            _cx -= (at.X - _lastAt.X) / scale;
            _cy -= (at.Y - _lastAt.Y) / scale;
            ClampCenter();
            Redraw();
        }
        _lastAt = at;
    }

    void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        ReleasePointerCapture(e.Pointer);
        if (_dragging) { _dragging = false; return; }

        var at = e.GetCurrentPoint(this).Position;
        var hit = HitTest(at);
        if (hit is { Count: > 1 } cluster)
        {
            // open the cluster: zoom in on it
            double scale = MapScale;
            var s = ToScreen(cluster.X, cluster.Y, scale);
            ZoomAt(new Point(s.X, s.Y), 1.5);
            return;
        }
        PinTapped?.Invoke(hit?.Marker);
    }

    void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        ZoomAt(pt.Position, pt.Properties.MouseWheelDelta / 120.0 * 0.5);
        SetHover(HitTest(pt.Position));
        e.Handled = true;
    }

    void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var at = e.GetPosition(this);
        if (HitTest(at) == null) ZoomAt(at, 1);
    }

    // ------------------------------------------------------------ drawing
    void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        if (_world == null || _map == null) return;
        _frame++;
        var w = _world;
        double scale = MapScale, vw = ActualWidth, vh = ActualHeight;
        double left = _cx - vw / 2 / scale, top = _cy - vh / 2 / scale;
        double right = left + vw / scale, bottom = top + vh / scale;

        DrawTiles(ds, w, scale, left, top, right, bottom);

        if (_itemsDirty || Math.Floor(_zoom * 2) / 2 != _itemsZoom) RebuildItems();
        if (ShowNames) DrawLabels(ds, w, scale, left, top, right, bottom);
        DrawPins(ds, scale, vw, vh);
        if (_hover is { } h) DrawTip(ds, h, scale, vw);
    }

    void DrawTiles(CanvasDrawingSession ds, World w, double scale, double left, double top, double right, double bottom)
    {
        int Z = w.Z;
        int z = Math.Clamp((int)Math.Ceiling(_zoom - 0.3), 0, Z);
        double span = w.Tile * Math.Pow(2, Z - z);   // map px covered by one tile at this level
        int maxX = (int)Math.Ceiling(w.W / span) - 1, maxY = (int)Math.Ceiling(w.H / span) - 1;
        int x0 = Math.Max(0, (int)Math.Floor(left / span)), x1 = Math.Min(maxX, (int)Math.Floor(right / span));
        int y0 = Math.Max(0, (int)Math.Floor(top / span)), y1 = Math.Min(maxY, (int)Math.Floor(bottom / span));

        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            // half a pixel of overlap hides the seams between tiles
            var dest = new Rect((x * span - left) * scale - 0.5, (y * span - top) * scale - 0.5, span * scale + 1, span * scale + 1);
            var bmp = Tile(w, z, x, y, true);
            if (bmp != null)
            {
                ds.DrawImage(bmp, dest, bmp.Bounds, 1f, CanvasImageInterpolation.Linear);
                continue;
            }
            if (!TilePending(w, z, x, y)) continue;   // no art here
            // while it loads, stretch whatever coarser tile is already decoded
            for (int up = 1; up <= 4 && z - up >= 0; up++)
            {
                var parent = Tile(w, z - up, x >> up, y >> up, false);
                if (parent == null) continue;
                double n = 1 << up, pw = parent.Size.Width / n, ph = parent.Size.Height / n;
                var src = new Rect((x - ((x >> up) << up)) * pw, (y - ((y >> up) << up)) * ph, pw, ph);
                ds.DrawImage(parent, dest, src, 1f, CanvasImageInterpolation.Linear);
                break;
            }
        }
    }

    void DrawLabels(CanvasDrawingSession ds, World w, double scale, double left, double top, double right, double bottom)
    {
        if (w.Labels.Count == 0) return;
        double z = _zoom; int Z = w.Z;
        // countries and regions while zoomed out, districts closer in, landmarks at street level
        bool region = z >= Z - 3.5 && z < Z - 1.5, sub = z >= Z - 1.5 && z < Z + 0.5, mark = z >= Z;
        if (!region && !sub && !mark) return;
        double pad = 200 / scale;
        var shadow = Color.FromArgb(230, 0, 0, 0);
        foreach (var l in w.Labels)
        {
            bool on = l.Kind switch { "region" => region, "sub" => sub, "mark" => mark, _ => false };
            if (!on || l.X < left - pad || l.X > right + pad || l.Y < top - pad || l.Y > bottom + pad) continue;
            var (format, color, text) = l.Kind switch
            {
                "region" => (_regionFormat, Accent, l.Text.ToUpperInvariant()),
                "sub" => (_subFormat, Color.FromArgb(255, 255, 255, 255), l.Text.ToUpperInvariant()),
                _ => (_markFormat, Hex.Rgb(0xD3, 0xDB, 0xE0), l.Text),
            };
            var p = ToScreen(l.X, l.Y, scale);
            ds.DrawText(text, p + new Vector2(1, 1), shadow, format);
            ds.DrawText(text, p + new Vector2(-1, 1), shadow, format);
            ds.DrawText(text, p + new Vector2(1, -1), shadow, format);
            ds.DrawText(text, p + new Vector2(-1, -1), shadow, format);
            ds.DrawText(text, p, color, format);
        }
    }

    void DrawPins(CanvasDrawingSession ds, double scale, double vw, double vh)
    {
        var plate = Color.FromArgb(230, 11, 14, 17);
        Item? selected = null;
        // singles first, clusters on top
        for (int pass = 0; pass < 2; pass++)
        foreach (var it in _items)
        {
            if ((it.Count > 1) != (pass == 1)) continue;
            var p = ScreenOf(it, scale);
            float size = SizeOf(it);
            if (p.X < -size || p.Y < -size || p.X > vw + size || p.Y > vh + size) continue;
            bool hover = _hover is { } h && h.Equals(it);
            if (hover) size *= 1.3f;

            if (it.Count > 1)
            {
                // a diamond with the count
                float side = size * 0.7f;
                ds.Transform = Matrix3x2.CreateRotation(MathF.PI / 4, p);
                var box = new Rect(p.X - side / 2, p.Y - side / 2, side, side);
                ds.FillRectangle(box, hover ? Accent : plate);
                ds.DrawRectangle(box, Accent, 1.5f);
                ds.Transform = Matrix3x2.Identity;
                ds.DrawText(it.Count.ToString(), p, hover ? Hex.Rgb(11, 14, 17) : Accent, _countFormat);
                continue;
            }

            var m = it.Marker;
            if (ReferenceEquals(m, Selected)) selected = it;
            float opacity = hover && HideMode ? 0.45f : 1f;
            var icon = Icon(m.IconOrCat);
            if (_map!.PinPlate)
            {
                if (icon != null)
                {
                    ds.FillCircle(p, size / 2, Color.FromArgb(209, 13, 13, 18));
                    ds.DrawCircle(p, size / 2, m.Cat.Color.WithAlpha(200), 1.5f);
                    float inner = size * 0.78f;
                    ds.DrawImage(icon, new Rect(p.X - inner / 2, p.Y - inner / 2, inner, inner), icon.Bounds, opacity, CanvasImageInterpolation.Linear);
                }
                else
                {
                    ds.FillCircle(p, 6, Accent);
                    ds.DrawCircle(p, 6, Color.FromArgb(217, 255, 255, 255), 2f);
                }
            }
            else if (icon != null)
            {
                ds.FillCircle(p, size * 0.46f, Color.FromArgb(70, 0, 0, 0));
                ds.DrawImage(icon, FitRect(icon, p, size), icon.Bounds, opacity, CanvasImageInterpolation.Linear);
            }
            else
            {
                // no icon: a small diamond in the category colour
                ds.Transform = Matrix3x2.CreateRotation(MathF.PI / 4, p);
                var box = new Rect(p.X - 5.5, p.Y - 5.5, 11, 11);
                ds.FillRectangle(box, m.Cat.Color);
                ds.DrawRectangle(box, Hex.Rgb(11, 14, 17), 1.5f);
                ds.Transform = Matrix3x2.Identity;
            }
        }

        if (selected is { } s)
        {
            var p = ScreenOf(s, scale);
            ds.DrawCircle(p, SizeOf(s) / 2 + 7, Accent, 2f);
            ds.DrawCircle(p, SizeOf(s) / 2 + 13, Accent.WithAlpha(90), 1.5f);
        }
    }

    /// <summary>Largest rect of the icon's shape that fits a size x size box around p.</summary>
    static Rect FitRect(CanvasBitmap icon, Vector2 p, float size)
    {
        double iw = icon.Size.Width, ih = icon.Size.Height;
        double k = size / Math.Max(iw, ih);
        return new Rect(p.X - iw * k / 2, p.Y - ih * k / 2, iw * k, ih * k);
    }

    void DrawTip(CanvasDrawingSession ds, Item it, double scale, double vw)
    {
        string title, sub;
        if (it.Count > 1) { title = it.Count.ToString("N0") + " pins"; sub = "Click to zoom in"; }
        else
        {
            var m = it.Marker;
            title = m.Title;
            sub = string.IsNullOrEmpty(m.Place) ? m.Cat.Label : m.Cat.Label + " · " + m.Place;
            if (HideMode) sub = "Click to hide · " + sub;
        }
        using var t = new CanvasTextLayout(ds, title, _tipTitleFormat, 0, 0);
        using var s = new CanvasTextLayout(ds, sub, _tipSubFormat, 0, 0);
        float tw = (float)Math.Max(t.LayoutBounds.Width, s.LayoutBounds.Width);
        float th = (float)(t.LayoutBounds.Height + s.LayoutBounds.Height);
        var p = ScreenOf(it, scale);
        float bw = tw + 20, bh = th + 12;
        float x = Math.Clamp(p.X - bw / 2, 6, (float)Math.Max(6, vw - bw - 6));
        float y = p.Y - SizeOf(it) * 0.65f - 10 - bh;
        if (y < 6) y = p.Y + SizeOf(it) * 0.65f + 10;
        ds.FillRoundedRectangle(new Rect(x, y, bw, bh), 6, 6, Color.FromArgb(240, 11, 14, 17));
        ds.DrawRoundedRectangle(new Rect(x, y, bw, bh), 6, 6, Color.FromArgb(60, 255, 255, 255), 1f);
        ds.DrawTextLayout(t, new Vector2(x + 10, y + 6), Color.FromArgb(255, 255, 255, 255));
        ds.DrawTextLayout(s, new Vector2(x + 10, y + 6 + (float)t.LayoutBounds.Height), Hex.Rgb(0xA7, 0xB0, 0xBA));
    }
}
