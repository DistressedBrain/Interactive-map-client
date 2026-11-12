# Interactive Map: Windows client

A native Windows app for the interactive game maps. It shows the same maps and
data as the website, in one window, without a browser.

Games so far: **Wuthering Waves** (10 maps) and **Neverness to Everness**.

## Running it

Download the release zip, unzip it anywhere and start `InteractiveMap.exe`.
Nothing needs installing: the .NET and Windows App SDK runtimes and the map data
are in the folder. Keep the exe together with the rest of the folder.

Windows 10 or 11, 64-bit. The exe is not code-signed, so SmartScreen may ask
before the first run.

## Using it

| | |
|---|---|
| Game button (icon + name, top left) | switch game |
| Tabs next to it | switch world |
| Left pane | turn pin categories on and off; "All / None" per group |
| `☰` | show or hide the left pane |
| Drag, mouse wheel, double-click | pan and zoom |
| Click a pin | details |
| Click a numbered diamond | zoom into that cluster |
| "Tap pins to hide them" | clicking a pin hides it; Undo appears at the bottom |
| Search box, or `Ctrl+F` | find places and pins in any world |
| `Esc` | close the detail card |
| Gear | Dark, Light or Dim style |

Your choices (style, last game and world, filters, hidden pins) are kept in
`%LOCALAPPDATA%\InteractiveMap\settings.json`. Delete that file to reset.

## Where the map data comes from

The client has no data format of its own. It reads the files the website's
generators already write:

| Game | Data | Tiles | Icons |
|---|---|---|---|
| Wuthering Waves | `js/data.js` | `tiles/<world>/z<level>/<x>_<y>.jpg` | `assets/icons` |
| Neverness to Everness | `mapdata.js` | `assets/map/tiles/<level>/<x>_<y>.png` | `assets/mapicons`, `anomalies`, `items` |

At start it looks for the folder that holds one sub-folder per game, in this
order:

1. the `INTERACTIVE_MAP_DATA` environment variable
2. a `maps` folder beside the exe (this is what a release contains)
3. `pages\gacha games\maps` of the website checkout, in or next to any parent
   folder (this is what a development build uses)

The gear menu shows which folder was found.

## Project layout

```
InteractiveMap.csproj   WinUI 3 (Windows App SDK) + Win2D, .NET 10, unpackaged
App.xaml                the app's colour brushes
MainWindow.xaml(.cs)    the shell: title bar, filter pane, detail card, search, settings
MapCanvas.cs            the map itself: tiles, pins, clusters, place names, input
Loaders.cs              one loader per game, plus the list of games
Models.cs               GameMap, World, Category, Marker, MapLabel; finding the data
Theme.cs                Dark / Light / Dim palettes
Settings.cs             settings.json
Assets/games/<id>.png   each game's icon, shown beside its name
make-release.ps1        builds the release folder and zip
nuget.config            adds an optional local package folder (see Building)
```

The map keeps at most 96 decoded tiles in memory (about 1 MB each) and drops the
least recently drawn ones.


## Not there yet

- Neverness to Everness has one switch per category; the website's switches per
  icon type are not in the client.
- The exe has the default Windows icon.
- A pin cannot be shared as a link.
