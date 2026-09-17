# Koogle Kerbin

A viewer for the worlds of Kerbal Space Program, in two versions:

- **[Windows desktop app](desktop/README.md)** (`desktop/`, C# and OpenGL): flat
  map, 3D globe, sky view, day and night with a physically based atmosphere, any
  celestial body (stock or Kopernicus), and the vessels from one of your saves
  rendered with their real part models. Installer in the
  [releases](https://github.com/alonso49noob/koogle-kerbin/releases).
- **Web version** (the rest of this repository), described below.

*Español: [README.es.md](README.es.md).*

Kerbal Space Program belongs to Squad and Take-Two Interactive. This is a fan
project with no connection to them.

---

## The desktop app

`desktop/` holds a native Windows application, written in C# (.NET 10, WinForms)
with OpenGL 3.3. No NuGet packages, no engine, no telemetry: it builds offline and
reads your game files where they already are.

What it does:

- **Three views of the same place.** A sliding flat map, a 3D globe and a sky view
  from a point on the surface, all sharing observer, time and selection.
- **Day and night.** Rayleigh and Mie single scattering with a Chapman function
  for the sun's optical depth, aerial perspective and a filmic tone map, so
  sunrises, the terminator and the night side look the way they should. Each body
  gets its own air colour and density; airless bodies get no shell at all.
- **Your vessels.** Point it at a `persistent.sfs` and your craft appear in orbit
  and on the ground, with a time bar using KSP's own warp steps. Select one and you
  get its orbital data, its ground track, and its **actual model**, assembled part
  by part from the game's `.mu` files — including the pose each part was saved in:
  deployed solar panels and antennas, extended landing gear, jettisoned engine
  shrouds, packed or open parachutes, built fairings.
- **Any celestial body.** The 17 stock bodies are built in, and if you have
  Kopernicus installed it reads the planet pack straight from
  `ModuleManager.ConfigCache` — OPM, RSS, JNSQ or whatever else you run — with the
  right radius, rotation period, atmosphere, sphere of influence and hierarchy.
- **English and Spanish**, both in the app and in the installer.

The detailed documentation is in **[desktop/README.md](desktop/README.md)**
(Spanish). The installer asks for .NET 10 and installs per user; no admin rights.

---

## Web version: Kerbin Maps

A Kerbin surface viewer (stock KSP) to run locally, along the lines of Kerbal
Maps: a sliding map with a graticule, a biome overlay, markers, a ruler and
orbital ground tracks.

Everything runs on your machine. No external services, no telemetry, no CDN: the
only dependency is Leaflet, which is vendored in `vendor/leaflet/`.

---

## Running it

```bash
node server.mjs
```

Then open <http://127.0.0.1:8080>. For another port: `node server.mjs 8099`.

If you don't have Node, any static server will do; Python's, for instance:

```bash
python -m http.server 8080 --bind 127.0.0.1
```

The server only listens on `127.0.0.1`, so it isn't exposed to your local network.

Any static server works; what does **not** work is opening `index.html` by double
clicking it (`file://`), because the browser blocks the `fetch` of
`data/landmarks.json` and reading pixels back from the canvas.

---

## The maps

`data/` ships two images of Kerbin so you can try the viewer as soon as you start
it, without having to export anything:

| File | What it is | Offset | Source |
|---|---|---|---|
| `Kerbin_Color_HD.jpg` | colour 4096×2048 | 0° | contributed, original source unknown |
| `Kerbin_Color.png` | colour 2048×1024 | **90°** | KSP community wiki |
| `Kerbin_Biome.png` | biomes 1800×900 | 0° | KSP community wiki |
| `Kerbin_Height.png` | heights 2048×1024 | 0° | SCANsat export (greyscale −1500…6500 m) |

**They are not mine, nor the project's.** They derive from the game's textures,
property of Squad / Private Division, and the wiki declares no licence. They are
there only so you can try the viewer on your own machine, assuming you own the
game. **Don't redistribute them.** If you'd rather not have them, delete both
files and `data/maps.json`: the viewer still starts and greets you with the
reference graticule.

The provenance and settings of each one are written down in `data/maps.json`.

### Map presets

`data/maps.json` is a catalogue: each entry says which file goes in which slot and
with what longitude offset. The one marked `predeterminado` loads by itself the
first time you open the viewer; the rest are one click away in the **Mapa
predeterminado** dropdown of the "Datos del mapa" panel.

Adding your own is one more entry:

```json
{
  "id": "mio",
  "nombre": "My KittopiaTech export",
  "color": { "file": "Kerbin_Color_HD.png", "lonOffset": "auto" },
  "biome": { "file": "Kerbin_Biome.png",    "lonOffset": 0 }
}
```

`lonOffset` takes a number of degrees or `"auto"`. With `"auto"`, the viewer
measures the rotation itself when loading the preset by comparing the outline of
the continents against the biome map, and tells you how much it rotated and with
what confidence. If the file isn't in `data/`, it names it and leaves whatever you
already had loaded.

If you already have something stored in the browser, the default preset does not
load: yours wins.

### Loading your own maps

Two ways, whichever suits you:

1. **Drag and drop** the PNG onto the "Datos del mapa" panel. It is stored in the
   browser's IndexedDB and is still there next time you open the page.
2. **Drop the file into `data/`** under one of these names and reload:
   `Kerbin_Color.png`, `Kerbin_Biome.png`, `Kerbin_Height.png`. They load by
   themselves.

The requirement is that the image be **equirectangular with a 2:1 aspect ratio**,
longitude −180° on the left and +180° on the right, latitude +90° at the top. That
is the format KSP stores its body textures in, so normally there is nothing to
reproject. If you load something that isn't 2:1, the viewer warns you.

Resolution doesn't matter: 720×360 and 8192×4096 both work.

### When the map doesn't land where it should

Not every map out there uses the same prime meridian as KSP. The included colour
map, for one, is rotated 90°: straight off the download, the KSC lands in the
middle of a desert instead of on its coast.

Each slot has a degrees field that rotates the map in longitude. And if you have
both colour and biome loaded, the **"Alinear color con el bioma"** button works the
rotation out on its own: it compares the coastline outlines of both maps and keeps
the angle that fits best. It works even when the palettes look nothing alike,
because it compares shapes, not colours. If the best fit doesn't stand out above
the average, instead of rotating blindly it tells you those two maps don't match.

Watch out for one trap: **the offset belongs to the image, not to the slot.** If
you load a new map over another one, the viewer resets the offset to 0 rather than
carrying the previous one over, and if a biome map is loaded it takes the chance to
measure the new one right there and tell you. Inheriting the previous map's
rotation draws the new one crooked for no apparent reason, which is thoroughly
confusing.

The two included colour maps are a good example that this isn't theoretical: the
4096 one is aligned (peak at 0°, 95.9%; neighbouring angles drop off) and the wiki
one needs 90° (94.7% at 90°, between 44% and 58% at any other angle).

### Biomes — the practical route

**SCANsat's biome map (720×360) works as is.** It's 2:1, and even though a pixel is
half a degree (about 5 km at the equator), biomes are flat regions, so nothing is
lost. The viewer draws it **without interpolation**: a colour averaged between two
biomes is no biome at all, and it would break the probe as well.

Biomes go in as an **overlay**, with their own opacity, on top of the colour map.
That's how you actually look at them: which biome falls on which terrain.

On load, the "Biomas" panel reads the whole legend: each colour with the
percentage of Kerbin's surface it covers. The percentage is weighted by `cos(lat)`,
because in an equirectangular projection a row of pixels next to the pole
represents far less surface than one at the equator; without that correction the
caps would come out three times larger than they are.

You supply the names, in the legend itself. The palette changes between versions
and exporters, so the viewer guesses none of them. They are stored in the browser
and you can export them to JSON to reuse them.

### Height

**Stock Kerbin's terrain is procedural.** The PQS generates it with noise at run
time; there is no height texture inside the game files you could extract. That's
why height lives in a separate panel marked optional. There are two ways to get a
real heightmap.

#### The good one: SCANsat in greyscale

It's the only route that gives current data, at a decent resolution and with a
known scale. SCANsat already stores the altimetry; you just have to make it draw
it in a way that can be read as a number:

1. Scan Kerbin with an altimetry instrument (SAR gives more resolution).
2. Open the **big map** and set it to **Altimetry**.
3. In the projection dropdown pick **Rectangular**. The others (KavrayskiyVII,
   Polar) are not equirectangular and won't fit.
4. Open the **color management** window (the palette icon) and, in the terrain tab:
   - pick the **greyscale** palette;
   - leave it on **smooth gradient**, not *discrete*, or you'll get steps;
   - **turn Clamp off**. With it on, everything below the cut is painted with the
     palette's first two colours and the grey stops being proportional to height,
     which is exactly what you need it to be.
5. **Write down the Min and Max slider values.** They are the cuts SCANsat uses to
   turn height into colour, so they are exactly the two numbers the viewer asks
   for.
6. Export with the camera icon.

In the viewer: load the PNG into the **Altura** slot and enter `grey 0 = Min` and
`grey 255 = Max` in the panel. No calibration needed, because you already know the
scale. Better still: write them into `data/maps.json` inside the preset and they
are applied on load.

```json
"height": { "file": "Kerbin_Height.png", "lonOffset": 0, "hMin": -1000, "hMax": 6800 }
```

The included heightmap comes out of SCANsat with `TrueGreyScale`. Its range is not
an estimate: `GameData/SCANsat/Resources/SCANcolors.cfg` stores `minHeightRange`
and `maxHeightRange` per body, and for Kerbin they are −1500 and 6500. Looking
there is more reliable than eyeballing the sliders.

Checked against independent data: the KSC reads 37 m (its real altitude is around
70, and each pixel covers 1.8 km), alignment comes out at 0° with a 96.6% match
against the biome map, and the ocean gives between −1090 and −935 m — real
bathymetry, not a flat plane.

**A warning about the poles.** 14.7% of the image is grey 48, which with that range
is exactly 0 m, and 98.4% of those pixels are above |lat| 60°. That's unscanned
area filled in at 0 m, not terrain. Below that latitude the map is practically
complete.

**The colour altimetry map is no good.** It's the same data, but the probe
translates luminance into metres, and in those palettes luminance doesn't grow with
altitude: the yellow of a mid slope is brighter than the red of the summit, so
peaks would come out as pits. The viewer measures the saturation of whatever you
load into the Height slot and warns you if you've fed it a palette instead of a
greyscale.

If one day you don't know where a greyscale came from, the panel's two-point
calibration is still there: press "Punto A", click somewhere whose real altitude
you know, type it in, repeat with B somewhere of a very different altitude, and the
range adjusts itself. It also works if the map is inverted (dark = high).

#### The other one: dumping the PQS

KittopiaTech or Kopernicus's in-game editor can sample the PQS and produce a
heightmap. It gives more resolution than SCANsat and doesn't depend on having
scanned anything, but the greyscale comes with no scale attached: there you do have
to calibrate with two points.

#### The one that doesn't work: the wiki figure

The wiki has a `Kerbin heightmap.jpg`. It isn't a greyscale, it's a figure coloured
in bands with a legend and axes drawn on top, from KSP 0.18.2.

It can be decoded — `tools/decode-banded-map.html` does it, and classifies 96.8% of
the pixels against the 13 bands of its legend — but the result **is not reliable
where it matters most**:

- They are **12 steps**, not a surface. The cuts are at −1000, −500, 0, 100, 200,
  500, 1000, 1500, 2000, 2500 and 3000 m.
- The global geography does still hold up: it matches the current biome map by
  **94.6%**, with no rotation. The continents of 2012 are today's.
- But **the KSC's coastline doesn't match**. In that figure the KSC falls almost a
  degree out to sea, some 90 km. Baikerbanur, Woomerang and the Dessert Site, which
  are inland, do come out right; the coastal points don't.

It's good for "is this region high or low ground?". It's no good for the altitude
of a specific spot, least of all next to the sea. That's why it **isn't preloaded**:
having the viewer say "alt +217 m" at the KSC off that data would be worse than
saying nothing.

The tool stays because it works with any banded figure that has a legend, not just
that one.

### Colour

Any equirectangular map of Kerbin you have or generate works as is. SCANsat can
export one too, and the map export in KittopiaTech / Kopernicus's in-game editor
produces colour in one go. I'm not giving you the exact menu path for each mod
because it changes between versions; check the documentation of the one you use.

### Checking that it fits

Generate the test images:

```bash
node tools/make-test-map.mjs
```

```bash
node tools/make-test-map.mjs --biome
```

The first writes `data/test-equirectangular.png` (2048×1024) with the equator in
green, the prime meridian in red and a yellow cross over the KSC's coordinates: if
the KSC's blue marker lands right on the cross, the projection is right.

The second writes `data/test-biome-720x360.png`, in flat colours, to test the
legend and the percentages at the same resolution SCANsat gives.

And for the height chain:

```bash
node tools/make-test-map.mjs --height
```

It writes `data/test-height-ramp.png`, a greyscale with a linear ramp in longitude:
`grey = round(255·(lon+180)/360)`. Since the altitude that should come out at each
point can be worked out by hand, it serves to check that the probe and the
grey→metres range are right before feeding it a real export. Realistic-looking
terrain would be prettier but wouldn't let you verify anything.

---

## What it gives you

**Base map.** Reference graticule, your colour image, your heightmap, local tiles
in `tiles/`, or your own XYZ template. With per-layer opacity and longitude offset,
and automatic rotation detection.

**Map presets.** A catalogue in `data/maps.json` with each one's offset already
worked out (or measured on the fly). Switching maps is one click.

**3D view.** The "Ver en 3D" button in the top bar switches to a globe: same
textures, same biome overlay, same markers and the orbital track, with its ground
footprint and its path at true altitude. Drag to rotate, wheel to zoom. Going back
to 2D keeps the point you were looking at.

**Biome overlay.** A layer over the relief with its own opacity, drawn without
interpolation, and a legend with the surface breakdown. See above.

**Graticule.** Parallels and meridians with a step that adapts to the zoom,
labelled once at the edge.

**Coordinates.** Latitude and longitude under the cursor, in the same convention
KSP uses (N positive, E positive). On click, a popup with the coordinates in
decimal, ready to copy, and a button to leave a marker there.

**Probe.** Under the cursor, the biome (by its colour, under whatever name you gave
it) and, if you have a calibrated heightmap, the altitude.

**Ruler.** Chain points and measure along the great circle, with the initial
bearing of each leg and the running total. Important: in an equirectangular
projection the straight line between two points is **not** the shortest path, and
the ruler takes that into account.

**Footprint / horizon.** Given an altitude, it draws how far the line of sight
reaches from there. Useful for placing comm relays or planning scanning coverage.

**Ground track.** Enter periapsis, apoapsis, inclination, LAN and argument of
periapsis, and it draws where the craft passes over the ground, accounting for
Kerbin's rotation. It returns period, semi-major axis, eccentricity, velocity at
periapsis and apoapsis, longitude drift per revolution and maximum latitude
reached. It warns you if the periapsis enters the atmosphere or goes below sea
level, and it detects synchronous orbits.

**Vessels from a save.** Drag in a `persistent.sfs` and your craft appear in orbit
around Kerbin, in 2D and in 3D (on the globe, at their true altitude). A time bar
moves them forwards and backwards with KSP's warp steps, with pause. You can draw
every orbit at once and have the camera follow one. See below.

**Markers.** Six reference points come included (KSC, runway, Island Airfield,
Baikerbanur, Woomerang, Dessert). You can add your own, export them to JSON and
import them.

**Search.** By marker name or by coordinates: `-0.0972, -74.5577` and
`0.0972 S 74.5577 W` both work.

---

## About the included coordinates

The six markers carry a `confianza` (confidence) field:

- `alta` — a much-quoted coordinate, stable across versions. The KSC only.
- `media` — approximate. It puts the point in view, but don't use it to land
  blind.

**The anomalies are not included.** Monoliths, pyramids, the crater, wreckage… none
of them ship, and that's deliberate: I don't know their coordinates precisely
enough, and making numbers up is worse than leaving them out. Add them as you find
them, or paste your own list into `data/landmarks.json` following the same format.

---

## How it works inside

**The projection.** The map uses plate carrée (equirectangular), the projection KSP
stores its body textures in: longitude and latitude convert straight into X and Y.
Leaflet already ships it as `CRS.EPSG4326`; the only change is the radius, from
Earth's 6371 km to Kerbin's 600 km, so scale and distances come out right
(`js/geo.js`).

**The tiles.** Loading an 8192×4096 image as a single element chokes when you zoom
in. Rather than forcing you to slice the texture into thousands of files,
`KM.ImageLayer` extends `L.GridLayer` and draws each tile by cropping the
corresponding piece out of an in-memory `ImageBitmap`. It behaves like a tile
server without being one (`js/layers.js`).

The colour map is interpolated when zooming in; the biome and height ones are
**not**, because there the exact pixel value is the data.

**The orbit.** KSP uses patched conics: inside Kerbin's sphere of influence the
orbit is an exact Keplerian ellipse, with no oblateness or J2 perturbing it. So it
is enough to solve Kepler's equation by Newton-Raphson, convert to inertial
coordinates and subtract the planet's rotation (`js/orbit.js`). The tracks that
come out are the game's, not an approximation.

**Kerbin constants** (`js/config.js`): radius 600 km, μ = 3.5316×10¹² m³/s²,
sidereal day 21,549.425 s, solar day exactly 6 h, atmosphere up to 70 km,
SOI 84,159,286 m.

---

## Layout

```
index.html            interface
css/app.css           styles
js/config.js          Kerbin constants and available layers
js/geo.js             Kerbin CRS and geodesy (great circle, bearings, antimeridian)
js/storage.js         IndexedDB for images, localStorage for settings
js/layers.js          image layer, graticule, edge labels, XYZ
js/globe.js           3D view in WebGL2 (sphere, atmosphere, pins, picking)
js/probe.js           pixel probe and biome legend extraction
js/orbit.js           Keplerian propagation and ground track
js/savefile.js        .sfs reader and rotation calibration from the vessels
js/markers.js         reference and user markers
js/tools.js           ruler and footprint
js/app.js             interface wiring
data/landmarks.json   reference points
data/maps.json        which file goes in which slot, its offset and its provenance
data/                 your map PNGs go here
tiles/                pre-sliced tiles (optional; see tiles/LEEME.txt)
tools/png.mjs         dependency-free PNG encoder
tools/make-test-map.mjs  generates test images (colour and biomes)
tools/decode-banded-map.html  banded elevation figure -> greyscale heightmap
server.mjs            dependency-free static server
vendor/leaflet/       Leaflet 1.9.4
desktop/              the Windows app (C# and OpenGL) — see desktop/README.md
```

---

## Vessels from a save

The "Naves de una partida" panel: drag in the `persistent.sfs` from
`saves/<your save>/`. It is read in the browser, it never leaves your machine, and
a 6 MB file takes about 200 ms.

Vessels orbiting Kerbin are drawn; those at other bodies are counted but not
painted, because their latitude and longitude belong somewhere else. Debris is
hidden by default: it's usually half the list. Click a vessel to see its track and
its figures (Pe, Ap, inclination, eccentricity, period).

The globe has a single track: clicking a vessel replaces the orbit you had drawn by
hand in its panel, and vice versa. The last one you asked for wins.

### The longitude problem, and how it was solved

For each vessel the save stores its orbital elements (`SMA`, `ECC`, `INC`, `LPE`,
`LAN`, `MNA`) referred to an epoch `EPH`. That gives you the shape of the orbit, its
period and its inclination exactly. But to know **which point of the ground** it is
over you need the angle Kerbin has rotated at that instant, and the save doesn't
store that.

A constant won't do. After hundreds of thousands of revolutions, **7·10⁻⁵ s of
error in the period already shifts things by a degree**, and the published period
gives about 2.6° of mean error in a long-running save.

What the save does store, for each vessel, is its latitude and longitude at the
moment of its epoch. That's enough to measure the rotation directly:

1. **North is the Z axis.** Checked against the 107 vessels in orbit in a real
   save: median latitude error 0.2° with Z, 43° with Y. Latitude doesn't depend on
   the rotation, so this test is independent of everything else.
2. **The save's lat/lon is the one at epoch EPH, not at the moment of saving.**
   Median 0.2° against 1.2°.
3. Each vessel whose computed latitude matches the stored one (to 0.01°) yields the
   rotation at its epoch: inertial longitude minus map longitude. They are all
   carried to the instant of the save with the sidereal day and averaged **with the
   inconsistent ones trimmed**: circular mean, anything further than 5 times the
   median dispersion is discarded (with a 2° floor), and it is recomputed. The
   panel shows how many vessels were used, their median dispersion and how many it
   threw out.
4. There's a manual **longitude adjustment** in case a save has few usable vessels.
   You don't normally need to touch it.

Why the sidereal rotation and not the solar one: with the sidereal day the
measurements from the different vessels cluster (R = 0.67 unfiltered), with the
solar one they scatter (R = 0.27).

**The method gives the same constant across different saves.** The rotation at the
instant of the save changes with every save, but once UT is discounted the same
origin has to come out every time. With two saves of the same game 31 Kerbin days
apart: 87.364° and 87.365°. Whether that origin is really 90° with a period
2·10⁻⁴ s shorter than the published one can't be told apart, because the published
value isn't that precise. The viewer doesn't care: it calibrates each save on its
own.

**Verified with a control group.** It was calibrated with half the vessels and used
to predict the longitude of the other half, which took no part in the calibration.
Across the two partitions: median error **0.02°** in the worse one (about 2 km) and
0.0004° in the better one. Calibrating and checking with the same vessels would
have proved nothing. The viewer's code was run as is in Node over the save and in
the browser, and both give the same angle.

**Inconsistent vessels.** That save has two: one comes out 43.5° off and a piece of
debris 2.9°. Most likely their lat/lon is from another instant and they passed the
latitude filter by chance. That's why the calibration trims: without trimming, the
43.5° one dragged the result almost a degree off (0.996°), and the debris was what
spoiled the first validation, which gave 0.18° instead of 0.02°.

Limits: closed orbits only (hyperbolic ones and the degenerate radial orbits KSP
assigns to landed craft are discarded), the viewer's body only, and it doesn't
connect to the running game: everything is simulated from the save.

### Simulation in time

Loading a save brings up a time bar at the bottom. The clock starts at the instant
of the save, paused.

| Control | Shortcut | What it does |
|---|---|---|
| ▶ / ❚❚ | space | resume / pause |
| ▶▶ | `.` | one step faster forwards |
| ◀◀ | `,` | one step further back |
| Guardado | | back to the instant of the save |
| Seguir | F | the camera keeps the selected vessel in view |

The steps are KSP's own time warp steps (×1, 5, 10, 50, 100, 1000, 10,000 and
100,000) and continue into negative values on a single scale: ◀◀ goes down one step
at a time (×100,000 … ×5, ×1) and then moves on to ×−1, ×−5 … ×−100,000, and ▶▶
walks back the same way, with no jumps. Changing the speed starts the clock, as in
the game. The shortcuts don't fire while you're typing in a field.

**It's pure Kepler from the saved state.** Going backwards you see where each
vessel *would have been* according to its current orbit: no manoeuvres, no later
launches and no atmospheric drag. A vessel whose orbit cuts the ground is hidden
while it is below it.

**All orbits.** The panel checkbox draws them for every visible vessel. In 2D they
are ground tracks from the simulated instant (up to 3 revolutions, on a canvas,
because dozens of lines redrawing several times a second swamp Leaflet's SVG). In
3D they are closed rings: a Keplerian orbit is an ellipse fixed in space and what
moves is Kerbin underneath, so each ring is built once and only rotated in the
shader each frame. The selected vessel also gets its ground footprint.

**Follow.** In 3D the camera sits above the vessel looking at Kerbin's centre: the
vessel stays at the centre of the screen with the ground passing underneath, and it
won't let you get inside its orbit. In 2D the map recentres on it. Dragging cancels
it.

**Zoom.** Zooming out is no longer capped at 12 radii: it reaches three times the
furthest orbit in the save. The clipping planes depend on the distance; with a
fixed near plane, at hundreds of radii the depth buffer runs out of precision and
the orbits flicker against the planet.

**The clock doesn't lose time at low frame rates.** If the tab goes to the
background, the gap is discarded on return, so it doesn't jump hours at once. But
the cap can't be per frame: an early version clipped each step to 0.25 s, and with a
heavy scene at 3 frames per second ×1 simulated 0.28 s per real second.

**Verified:**

- Each vessel sits on its rotated ring at instants from −5000 s to +250,000 s.
- Against the render itself (reading the pixels back), with the most inclined
  vessel (89.83°) and the two rotation directions 60° apart: there is ring colour
  next to the vessel with the correct rotation (34 pixels) and none with the
  inverted rotation or with no rotation. With a near-equatorial orbit this test
  doesn't discriminate: an equatorial ring rotated about the pole falls on itself.
- The markers match Kepler exactly at the simulated instant, the speed scale steps
  and saturates as described above, pause freezes the clock and "Guardado" returns
  exactly to the instant of the save.
- Follow leaves the camera exactly above the vessel in 3D, and in 2D within
  Leaflet's pixel rounding. Dragging cancels it in both modes.
- With 62 vessels, zooming out reaches the 263.5 radii the furthest orbit asks for.
- Clock: measuring each frame against its own timestamp, simulated time advances
  exactly at the chosen rate (×1, ×1000, ×−1000 and ×100,000; per-frame error of 2
  parts per million at most), even with the window limited to 2–4 frames per
  second.

## The 3D view

Plain WebGL, with no 3D engine in between (`js/globe.js`). For what's needed here —
a sphere with an equirectangular texture on it — pulling in a 600 KB library
doesn't pay off: that is precisely a sphere's natural UV mapping.

Details that aren't obvious and took some tuning:

- **The meridian seam.** The vertices of the column where `u` goes from 1 to 0 are
  duplicated; if they're stitched, the interpolation runs 1 → 0 inside a triangle
  and you get a band with the whole texture squeezed into it. And since the
  longitude offset moves `u` outside [0,1], it is wrapped with `fract()` but the
  derivatives are passed unwrapped to `textureGrad`: with a plain `texture()`, the
  mipmap sees a huge derivative right at the seam, picks the blurriest level and
  leaves a line.
- **It needs WebGL2**, because of the above and because the biome textures aren't
  powers of two (1800×900). If the browser doesn't have it, it says so and stays in
  2D.
- **Attribute indices are set by hand** before linking: the planet and the
  atmosphere share the same VAO, and if each program numbered them its own way, one
  of the two would read garbage.
- **The picking camera basis has to match the rendering one.** `lookAt` builds its X
  axis as `up × back`, and it's easy to compute it in the picking code as
  `up × forward`, which is the same vector with the sign flipped: the mouse ends up
  mirrored in X with respect to what you see. The bug is treacherous because picking
  stays consistent with itself, so any test comparing `pick` against `pick` passes
  it. The only one that catches it compares the **drawn** pixel against the colour
  the texture holds at the coordinate `pick` returns: mirrored gives a mean error of
  100, correct gives 3.
- **Dragging grabs the surface.** The degrees each pixel rotates aren't an
  eyeballed factor: they are solved from the geometry, `θ = asin(d·sin α) − α`, so
  the point you grab follows the cursor. A constant rate only works near the centre;
  as you move outwards the sphere foreshortens, the same pixel spans more and more
  arc, and linearising it is what makes the globe bolt. Verified by measurement: the
  point drifts less than 31 km over 3770 of circumference.
- **The markers are HTML** over the canvas, not geometry: sharp at any zoom and
  looking the same as in 2D. They hide themselves when they pass behind the horizon,
  and when several pile up only the first keeps its name — the dot always stays,
  because the position is the data.
- **Relief displaces vertices and recomputes the normal.** The first part is
  obvious; the second isn't, and it's what decides whether you see anything: with
  the sphere's normal the light never learns there are mountains and the relief only
  shows on the limb silhouette. Two neighbours a couple of texels away are sampled
  from the heightmap and the tangents are crossed. It comes with exaggeration
  because at true scale Kerbin's highest peak is 1% of the radius and wouldn't show.

### The orbital track on the globe

Drawing an orbit paints two things from the same points: the **ground footprint**
and the **path at its true altitude**, which moves away from the planet up to the
apoapsis and back.

Both are in the body-fixed frame, like the globe's texture. That's why the path in
altitude **doesn't close into an ellipse**: it is the trajectory as seen from the
rotating planet, and that unravelling towards the west is exactly the drift that
makes each revolution pass somewhere different. The number the panel gives as
"drift per revolution" is that same thing, measured.

Two details:

- **Nothing has to be split at the antimeridian here.** On a sphere the line is
  continuous, so all the slicing that is mandatory in 2D simply disappears. It's one
  of the few things that come out easier in 3D.
- **The footprint is lifted 0.0016 off the ground.** The sphere is an inscribed
  polyhedron: between vertices its surface falls below r=1, and a line stuck at r=1
  would sink in places. With relief enabled the line is displaced like the terrain,
  so it doesn't end up floating.

What it does **not** do: there is no real 3D terrain (only vertex displacement from
the heightmap, if you have one) and no lat/lon graticule in 3D.

---

## Known limits (web version)

- **Kerbin only.** Adding Mun, Minmus or the rest of the bodies is a matter of
  putting their parameters into `js/config.js` plus a selector; it isn't done. The
  desktop app does have every body.
- **Height depends on your getting hold of a heightmap.** There is none inside the
  game: the workable route is exporting one from SCANsat in greyscale. See above.
- **No relief shading** in 2D.
- **Footprints that wrap around a pole** are drawn crudely: the circle is split at
  the antimeridian and near the pole that shows.
- **No day/night.** The web version's lighting is flat; the terminator, the
  atmosphere and the sky view are desktop-app features.
