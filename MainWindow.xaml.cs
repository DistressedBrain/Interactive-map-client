using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;

namespace InteractiveMap;

/// <summary>One row of the search drop-down.</summary>
public sealed class SearchHit
{
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    public Marker? Marker { get; init; }
    public MapLabel? Label { get; init; }
    public override string ToString() => Title;
}

public sealed partial class MainWindow : Window
{
    sealed class RowUi { public required Category Cat; public required Button Button; public required TextBlock Count; }
    sealed class GroupUi
    {
        public required CatGroup Group; public required StackPanel Section; public required StackPanel List;
        public required FontIcon Chevron; public required TextBlock Count; public required HyperlinkButton All;
        public List<RowUi> Rows = new();
    }
    sealed class TabUi { public required World World; public required Button Button; public required Border Line; public required TextBlock Name; }

    readonly AppSettings _settings = AppSettings.Load();
    readonly List<GroupUi> _groups = new();
    readonly List<TabUi> _tabs = new();
    readonly DispatcherTimer _toastTimer = new();
    List<GameInfo> _games = new();
    GameMap? _map;
    GameSettings _gs = new();
    World? _world;
    Marker? _selected;
    Action? _undo;
    bool _ready;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Interactive Map";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // most of the screen, centred; sizes here are physical pixels, so this follows the display scale
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = (int)(area.Width * 0.84), h = (int)(area.Height * 0.88);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));

        Theme.Apply(Theme.Parse(_settings.Theme), Root, AppWindow);
        ThemeRadios.SelectedIndex = (int)Theme.Current;

        Map.IsShown = m => m.Cat.On && !_gs.Hidden.Contains(m.Uid);
        Map.PinTapped += OnMapTapped;
        SetPane(_settings.PaneOpen);
        TitleRow.SizeChanged += (_, _) => FitTabs();
        _toastTimer.Tick += (_, _) => CloseToast();
        Closed += (_, _) => _settings.Save();

        var root = MapsRoot.Find();
        if (root == null)
        {
            DataPathText.Text = "Not found";
            ShowMessage("No map data found. Put the website's \"maps\" folder next to InteractiveMap.exe, or keep the interactive-map folder beside this one.");
        }
        else
        {
            DataPathText.Text = root;
            _games = GameCatalog.Find(root);
            BuildGameList();
            var start = _games.FirstOrDefault(g => g.Id == _settings.Game) ?? _games.FirstOrDefault();
            if (start != null) _ = SwitchGame(start);
            else ShowMessage("The maps folder has no game with generated data yet:\n" + root);
        }
        _ready = true;
    }

    static SolidColorBrush Res(string key) => Theme.Brush(key);
    static Style FlatStyle(FrameworkElement scope) => (Style)scope.Resources["FlatButton"];
    static string N(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    static BitmapImage? Picture(string? path, int decodeWidth = 0)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
        var img = new BitmapImage(new Uri(path));
        if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
        return img;
    }

    void ShowMessage(string? text)
    {
        Message.Text = text ?? "";
        Message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------ games
    void BuildGameList()
    {
        GameList.Children.Clear();
        foreach (var g in _games)
        {
            var plate = new Border
            {
                Background = Res("LogoPlateBrush"), CornerRadius = new CornerRadius(8),
                Width = 40, Height = 40,
            };
            var logo = Picture(g.LogoPath, 96);
            plate.Child = logo != null
                ? new Image { Source = logo, Stretch = Stretch.UniformToFill }
                : new TextBlock
                {
                    Text = g.Initials, Foreground = new SolidColorBrush(g.Accent), FontWeight = FontWeights.Bold, FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(plate);
            row.Children.Add(new TextBlock { Text = g.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button
            {
                Style = FlatStyle(Root), Content = row, Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => { GameFlyout.Hide(); if (_map?.Info != g) _ = SwitchGame(g); };
            GameList.Children.Add(button);
        }
    }

    async Task SwitchGame(GameInfo info)
    {
        Select(null);
        ShowMessage(null);
        Busy.IsActive = true;
        GameMap map;
        try
        {
            map = await Task.Run(() => info.Load(info));
        }
        catch (Exception e)
        {
            Busy.IsActive = false;
            ShowMessage($"Could not read the {info.Name} data.\n{e.Message}");
            return;
        }
        Busy.IsActive = false;

        _map = map;
        _gs = _settings.For(info.Id);
        _settings.Game = info.Id;
        foreach (var c in map.Cats) if (_gs.Cats.TryGetValue(c.Id, out var on)) c.On = on;

        Theme.SetAccent(info.Accent);
        Map.Accent = info.Accent;
        GameName.Text = info.Name;
        var logo = Picture(info.LogoPath, 96);
        GameLogo.Source = logo;
        GameLogo.Visibility = logo != null ? Visibility.Visible : Visibility.Collapsed;
        GameInitials.Text = logo != null ? "" : info.Initials;
        GameInitials.Foreground = new SolidColorBrush(info.Accent);

        _ready = false;
        NamesSwitch.IsOn = _gs.Names;
        HideSwitch.IsOn = false;
        _ready = true;
        Map.ShowNames = _gs.Names;
        Map.HideMode = false;
        NamesSwitch.Visibility = map.Worlds.Any(w => w.Labels.Count > 0) ? Visibility.Visible : Visibility.Collapsed;

        Map.SetMap(map);
        BuildTabs();
        BuildFilters();
        var world = map.Worlds.FirstOrDefault(w => w.Id == _gs.World)
                    ?? map.Worlds.FirstOrDefault(w => w.Id == map.DefaultWorld)
                    ?? map.Worlds.FirstOrDefault();
        if (world != null) SwitchWorld(world);
        _settings.Save();
    }

    // ------------------------------------------------------------ worlds
    void BuildTabs()
    {
        Tabs.Children.Clear();
        _tabs.Clear();
        if (_map == null) return;
        foreach (var w in _map.Worlds)
        {
            var name = new TextBlock { Text = w.Name, FontSize = 13.5 };
            var count = new TextBlock { Text = N(w.Markers.Count), FontSize = 11.5, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) };
            var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(name);
            text.Children.Add(count);
            var line = new Border { Height = 2, Background = Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0, CornerRadius = new CornerRadius(1) };
            var content = new Grid { Height = 38 };
            content.Children.Add(text);
            content.Children.Add(line);
            var button = new Button { Style = FlatStyle(Root), Content = content, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(1, 0, 1, 0) };
            var world = w;
            button.Click += (_, _) => { if (_world != world) SwitchWorld(world); };
            Tabs.Children.Add(button);
            _tabs.Add(new TabUi { World = w, Button = button, Line = line, Name = name });
        }
        FitTabs();
    }

    /// <summary>The world tabs scroll sideways; they may not push the drag strip or search away.</summary>
    void FitTabs()
    {
        double free = TitleRow.ActualWidth - PaneToggle.ActualWidth - GameButton.ActualWidth
                      - SearchBox.ActualWidth - SettingsButton.ActualWidth - 140 - 48 - 24;
        TabsScroller.MaxWidth = Math.Max(60, free);
    }

    void SwitchWorld(World world)
    {
        Select(null);
        _world = world;
        _gs.World = world.Id;
        Map.SetWorld(world);
        foreach (var t in _tabs)
        {
            bool on = t.World == world;
            t.Line.Opacity = on ? 1 : 0;
            t.Name.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            if (on) t.Button.StartBringIntoView();
        }
        SyncRows();
        SyncHidden();
    }

    // ------------------------------------------------------------ filters
    void BuildFilters()
    {
        Filters.Children.Clear();
        _groups.Clear();
        if (_map == null) return;
        var open = _gs.OpenGroups ?? _map.Groups.Where(g => !g.StartClosed).Select(g => g.Id).ToHashSet();

        foreach (var g in _map.Groups)
        {
            var cats = _map.Cats.Where(c => c.Group == g.Id).ToList();
            if (cats.Count == 0) continue;
            bool isOpen = open.Contains(g.Id);

            var chevron = new FontIcon { Glyph = isOpen ? "" : "", FontSize = 10, Foreground = Res("TextDimBrush") };
            var count = new TextBlock { FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(chevron);
            title.Children.Add(new TextBlock { Text = g.Label, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            title.Children.Add(count);
            var nameButton = new Button
            {
                Style = FlatStyle(Root), Content = title, Padding = new Thickness(6, 7, 6, 7),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            var all = new HyperlinkButton { Content = "All", Padding = new Thickness(8, 4, 8, 4), FontSize = 12.5 };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(nameButton);
            Grid.SetColumn(all, 1);
            head.Children.Add(all);

            var list = new StackPanel { Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed };
            var section = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            section.Children.Add(head);
            section.Children.Add(list);

            var ui = new GroupUi { Group = g, Section = section, List = list, Chevron = chevron, Count = count, All = all };
            foreach (var c in cats)
            {
                FrameworkElement mark;
                var icon = Picture(c.Icon, 48);
                // the games' icons are light glyphs: a dark disc keeps them readable in the Light style
                if (icon != null) mark = new Border
                {
                    Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Res("LogoPlateBrush"),
                    Child = new Image { Source = icon, Width = 20, Height = 20, Stretch = Stretch.Uniform },
                };
                else mark = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(c.Color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var label = new TextBlock { Text = c.Label, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var n = new TextBlock { FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center };
                var grid = new Grid { ColumnSpacing = 10 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(mark);
                Grid.SetColumn(label, 1); grid.Children.Add(label);
                Grid.SetColumn(n, 2); grid.Children.Add(n);
                var row = new Button
                {
                    Style = FlatStyle(Root), Content = grid, Padding = new Thickness(8, 5, 8, 5),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                var cat = c;
                row.Click += (_, _) => SetCats(new[] { cat }, !cat.On);
                list.Children.Add(row);
                ui.Rows.Add(new RowUi { Cat = c, Button = row, Count = n });
            }

            nameButton.Click += (_, _) =>
            {
                bool now = ui.List.Visibility != Visibility.Visible;
                ui.List.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
                ui.Chevron.Glyph = now ? "" : "";
                _gs.OpenGroups = _groups.Where(x => x.List.Visibility == Visibility.Visible).Select(x => x.Group.Id).ToHashSet();
                _settings.Save();
            };
            all.Click += (_, _) =>
            {
                var here = ui.Rows.Where(r => r.Button.Visibility == Visibility.Visible).Select(r => r.Cat).ToList();
                if (here.Count > 0) SetCats(here, !here.All(c => c.On));
            };

            _groups.Add(ui);
            Filters.Children.Add(section);
        }
    }

    /// <summary>Rows and counts follow the current world; categories it does not have are hidden.</summary>
    void SyncRows()
    {
        var counts = new Dictionary<Category, int>();
        var gone = new Dictionary<Category, int>();
        if (_world != null)
            foreach (var m in _world.Markers)
            {
                counts[m.Cat] = counts.GetValueOrDefault(m.Cat) + 1;
                if (_gs.Hidden.Contains(m.Uid)) gone[m.Cat] = gone.GetValueOrDefault(m.Cat) + 1;
            }

        foreach (var g in _groups)
        {
            int total = 0, on = 0, any = 0;
            foreach (var r in g.Rows)
            {
                int have = counts.GetValueOrDefault(r.Cat);
                r.Button.Visibility = have > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (have == 0) continue;
                int n = have - gone.GetValueOrDefault(r.Cat);
                any++; total += n;
                if (r.Cat.On) on++;
                r.Button.Opacity = r.Cat.On ? 1 : 0.42;
                r.Count.Text = N(n);
            }
            g.Section.Visibility = any > 0 ? Visibility.Visible : Visibility.Collapsed;
            g.Count.Text = N(total);
            g.All.Content = on == any ? "None" : "All";
        }
    }

    void SetCats(IReadOnlyCollection<Category> cats, bool on)
    {
        foreach (var c in cats) { c.On = on; _gs.Cats[c.Id] = on; }
        if (!on && _selected != null && cats.Contains(_selected.Cat)) Select(null);
        Map.RefreshMarkers();
        SyncRows();
        _settings.Save();
    }

    // ------------------------------------------------------------ hidden pins
    void SetHidden(IReadOnlyCollection<Marker> list, bool hide)
    {
        foreach (var m in list)
        {
            if (hide) _gs.Hidden.Add(m.Uid); else _gs.Hidden.Remove(m.Uid);
        }
        Map.RefreshMarkers();
        SyncRows();
        SyncHidden();
        _settings.Save();
    }

    void HidePin(Marker m)
    {
        if (_selected == m) Select(null);
        SetHidden(new[] { m }, true);
        ShowToast($"Hidden: {m.Title}.", () => SetHidden(new[] { m }, false));
    }

    void SyncHidden()
    {
        int n = _world?.Markers.Count(m => _gs.Hidden.Contains(m.Uid)) ?? 0;
        HiddenLine.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenText.Text = n == 1 ? "1 pin hidden on this map." : $"{N(n)} pins hidden on this map.";
    }

    void OnShowHidden(object sender, RoutedEventArgs e)
    {
        if (_world == null) return;
        var list = _world.Markers.Where(m => _gs.Hidden.Contains(m.Uid)).ToList();
        if (list.Count == 0) return;
        SetHidden(list, false);
        ShowToast(list.Count == 1 ? "1 pin is back on the map." : $"{N(list.Count)} pins are back on the map.", () => SetHidden(list, true));
    }

    void OnHideToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Map.HideMode = HideSwitch.IsOn;
        if (HideSwitch.IsOn) Select(null);
    }

    void OnNamesToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _gs.Names = Map.ShowNames = NamesSwitch.IsOn;
        Map.Redraw();
        _settings.Save();
    }

    // ------------------------------------------------------------ toast
    void ShowToast(string text, Action? undo = null)
    {
        _undo = undo;
        Toast.Message = text;
        ToastUndo.Visibility = undo != null ? Visibility.Visible : Visibility.Collapsed;
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(undo != null ? 6 : 2.8);
        _toastTimer.Start();
    }

    void CloseToast()
    {
        _toastTimer.Stop();
        Toast.IsOpen = false;
        _undo = null;
    }

    void OnToastUndo(object sender, RoutedEventArgs e)
    {
        var undo = _undo;
        CloseToast();
        undo?.Invoke();
    }

    // ------------------------------------------------------------ detail card
    void OnMapTapped(Marker? m)
    {
        if (m == null) Select(null);
        else if (HideSwitch.IsOn) HidePin(m);
        else Select(m);
    }

    void Select(Marker? m)
    {
        _selected = m;
        Map.Selected = m;
        Map.Redraw();
        if (m == null) { Card.Visibility = Visibility.Collapsed; return; }

        var picture = Picture(m.Portrait) ?? Picture(m.IconOrCat);
        CardIcon.Source = picture;
        CardIcon.Visibility = picture != null ? Visibility.Visible : Visibility.Collapsed;
        CardPip.Fill = new SolidColorBrush(m.Cat.Color);
        CardPip.Visibility = picture == null ? Visibility.Visible : Visibility.Collapsed;
        CardKind.Text = m.Cat.Label.ToUpperInvariant();
        CardName.Text = m.Title;
        CardPlace.Text = string.Join(" · ", new[] { m.Place, m.World.Name }.Where(s => !string.IsNullOrEmpty(s)));
        CardDesc.Text = m.Desc ?? "";
        CardDesc.Visibility = string.IsNullOrEmpty(m.Desc) ? Visibility.Collapsed : Visibility.Visible;
        CardLines.Children.Clear();
        if (m.Lines != null)
            foreach (var line in m.Lines)
            {
                bool item = line.StartsWith("  ", StringComparison.Ordinal);
                CardLines.Children.Add(new TextBlock
                {
                    Text = line.Trim(), FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(item ? 10 : 0, 0, 0, 0),
                    Foreground = Res(item ? "TextBrush" : "TextDimBrush"),
                });
            }
        CardScroll.MaxHeight = Math.Max(200, Map.ActualHeight - 110);
        CardScroll.ChangeView(null, 0, null, true);
        Card.Visibility = Visibility.Visible;
    }

    void OnCardClose(object sender, RoutedEventArgs e) => Select(null);
    void OnCardHide(object sender, RoutedEventArgs e) { if (_selected != null) HidePin(_selected); }

    void JumpTo(Marker m)
    {
        if (m.World != _world) SwitchWorld(m.World);
        if (_gs.Hidden.Contains(m.Uid)) SetHidden(new[] { m }, false);
        if (!m.Cat.On) SetCats(new[] { m.Cat }, true);
        Map.CenterOn(m.X, m.Y, m.World.Z);
        Select(m);
    }

    // ------------------------------------------------------------ search
    void OnSearchText(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.ItemsSource = Search(sender.Text);
    }

    List<SearchHit>? Search(string text)
    {
        var q = text.Trim().ToLowerInvariant();
        if (q.Length < 2 || _map == null) return null;
        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Match(string s) => terms.All(t => s.Contains(t, StringComparison.OrdinalIgnoreCase));
        int Here(World w) => w == _world ? 0 : 1;

        var hits = _map.Worlds.SelectMany(w => w.Labels)
            .Where(l => Match(l.Text)).OrderBy(l => Here(l.World)).Take(6)
            .Select(l => new SearchHit { Title = l.Text, Sub = "Place · " + l.World.Name, Label = l })
            .ToList();

        // one result per kind of pin and world: 900 of one plant would bury everything else
        var seen = new HashSet<(World, Category, string)>();
        var pins = new List<Marker>();
        foreach (var w in _map.Worlds)
            foreach (var m in w.Markers)
            {
                if (!Match(m.Title + " " + m.Cat.Label)) continue;
                if (seen.Add((w, m.Cat, m.Title))) pins.Add(m);
            }
        hits.AddRange(pins
            .OrderBy(m => Here(m.World))
            .ThenBy(m => m.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(40)
            .Select(m => new SearchHit
            {
                Title = m.Title,
                Sub = string.Join(" · ", new[] { m.Cat.Label, m.Place, m.World.Name }.Where(s => !string.IsNullOrEmpty(s))),
                Marker = m,
            }));

        if (hits.Count == 0) hits.Add(new SearchHit { Title = $"Nothing matches “{text.Trim()}”", Sub = "Try a boss, place or material." });
        return hits;
    }

    void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var hit = args.ChosenSuggestion as SearchHit ?? Search(sender.Text)?.FirstOrDefault();
        if (hit == null || _map == null) return;
        if (hit.Marker != null) JumpTo(hit.Marker);
        else if (hit.Label != null)
        {
            var l = hit.Label;
            if (l.World != _world) SwitchWorld(l.World);
            Map.CenterAt(l.X, l.Y, l.Kind == "region" ? l.World.Z - 2 : l.World.Z - 0.5);
        }
        else return;
        sender.Text = "";
        sender.ItemsSource = null;
    }

    // ------------------------------------------------------------ chrome
    void SetPane(bool open)
    {
        _settings.PaneOpen = open;
        Pane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PaneColumn.Width = new GridLength(open ? 300 : 0);
    }

    void OnPaneToggle(object sender, RoutedEventArgs e)
    {
        SetPane(Pane.Visibility != Visibility.Visible);
        _settings.Save();
    }

    void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ThemeRadios.SelectedIndex < 0) return;
        var theme = (AppTheme)ThemeRadios.SelectedIndex;
        Theme.Apply(theme, Root, AppWindow);
        _settings.Theme = theme.ToString();
        _settings.Save();
    }

    void OnZoomIn(object sender, RoutedEventArgs e) => Map.ZoomBy(1);
    void OnZoomOut(object sender, RoutedEventArgs e) => Map.ZoomBy(-1);
    void OnZoomFit(object sender, RoutedEventArgs e) => Map.Fit();

    void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Select(null);
        args.Handled = true;
    }

    void OnFocusSearch(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }
}
