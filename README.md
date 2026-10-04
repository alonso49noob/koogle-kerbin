# Koogle Kerbin

A viewer for the worlds of Kerbal Space Program: a **Windows desktop app** (C# and
OpenGL) with a flat map, a 3D globe, a sky view and free flight over the terrain, your
vessels with their real part models, any celestial body (stock or Kopernicus) and much
more. Installer in the [releases](https://github.com/alonso49noob/koogle-kerbin/releases).

*Español: [README.es.md](README.es.md).*

Kerbal Space Program belongs to Squad and Take-Two Interactive. This is a fan
project with no connection to them.

Happy to take your suggestions! Leave them on a PR or open an Issue! You can also contact me in discord as @perritoinfame

---

## The desktop app

`desktop/` holds a native Windows application, written in C# (.NET 10, WinForms)
with OpenGL 3.3. No NuGet packages, no engine, no telemetry: it builds offline and
reads your game files where they already are.

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/globe.jpg" alt="The 3D globe with the cloud layer from your cloud mod."><br><sub>The 3D globe with the cloud layer from your cloud mod.</sub></td>
    <td width="50%"><img src="docs/screenshots/descent.jpg" alt="Zooming down from orbit: at 3 km the camera tilts and the KSC, trees and buildings load."><br><sub>Zooming down from orbit: at 3 km the camera tilts and the KSC, trees and buildings load.</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/screenshots/ksc.jpg" alt="The stock KSC, read straight from the game's data files."><br><sub>The stock KSC, read straight from the game's data files.</sub></td>
    <td width="50%"><img src="docs/screenshots/crawlerway.jpg" alt="At ground level on the crawlerway, looking at the VAB and the SPH."><br><sub>At ground level on the crawlerway, looking at the VAB and the SPH.</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/screenshots/kerbal-konstructs.jpg" alt="Tundra Space Center (Kerbal Konstructs) with Parallax trees and grass."><br><sub>Tundra Space Center (Kerbal Konstructs) with Parallax trees and grass.</sub></td>
    <td width="50%"><img src="docs/screenshots/scatters.jpg" alt="Parallax scatters up close: grass, daisies and flowers, swaying in the wind."><br><sub>Parallax scatters up close: grass, daisies and flowers, swaying in the wind.</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/screenshots/relief.jpg" alt="Detailed relief: a full-resolution Parallax height tile streamed around the camera."><br><sub>Detailed relief: a full-resolution Parallax height tile streamed around the camera.</sub></td>
    <td width="50%"><img src="docs/screenshots/coast.jpg" alt="Shores: shallows, foam and wet sand where land meets the sea."><br><sub>Shores: shallows, foam and wet sand where land meets the sea.</sub></td>
  </tr>
</table>

*Screenshots rendered by the app itself, with KSP and the Parallax, Kerbal Konstructs, KSC Extended, Tundra Space Center and Stock Volumetric Clouds mods installed.*

What it does:

- **The rest of the solar system on the 3D globe.** Moons, planets and the Sun sit where
  they are for the time on the time bar, at true scale, lit by the Sun (the Mun shows its
  phases) and with their Parallax colour maps.
- **Four views of the same place.** A sliding flat map, a 3D globe, a sky view from a
  point on the surface and a free camera you fly at ground level, all sharing observer,
  time and selection. The flight view ray-marches the body's height map, so the horizon
  and the mountain silhouettes are exact, and it dresses the ground with the game's own
  terrain textures and cloud maps when you have them installed (the cloud layer shows on
  the 3D globe too, drifting west at the speed in the cloud mod's config and changing shape
  over time). If it finds KSP, it switches Kerbin to the game's own maps: the 8192 colour
  map, the 4096 biome map and Parallax's 8192 height map. With Parallax
  Continued, each body gets its own surface textures, blended by altitude and slope the
  way the mod does (biplanar mapping, influence, displacement blending, occlusion and
  normal maps, plus anti-tiling variation), and every planet's colour and height maps
  are read straight from its Unity bundle, no dumping needed.
- **Parallax scatters.** Grass, ferns, flowers, bushes, oaks, pines, palms and cacti on
  Kerbin, rocks on the Mun and elsewhere: the mod's own models and textures, placed by
  its own distribution rules (noise, slope, altitude, biome blacklists) and depth-tested
  against the ray-marched ground, so a hill hides the trees behind it. Grass and foliage
  sway in the wind the way the mod animates them.
- **Kerbal Konstructs bases, viewed and edited.** The KK statics you have installed
  (KSC Extended, Tundra Space Center, Kerbin Side...) are read from GameData and placed
  with KK's own maths, shown as markers on the map and globe and as textured models in
  the flight and sky views (the stock KSC textures they reuse come straight from the
  game's `sharedassets` files). An editor moves, rotates, scales, duplicates, deletes and
  adds buildings, and saves back to the `.cfg` files by changing only the lines involved,
  after backing each file up.
- **Countries and factions.** A political-map maker: create countries or factions, give
  them a name and a colour, and paint them on the 2D map or the globe with a brush, a fill
  (a whole island, or the gap left by borders), a polygon or an eraser. Only land gets
  painted — the drawing is clipped at the coastline — and borders keep the same width at
  any zoom, with each territory's name at its deepest point. One map per body, saved
  automatically, with undo, JSON export/import and a transparent PNG export.
- **A living sea.** Sixteen wave trains with deep-water dispersion, a Beckmann/Cox–Munk
  sun glint that widens with distance into the glitter path and the broad glint seen from
  orbit, the real sky reflected with Fresnel, shallows that show the sea floor (sand turns
  turquoise, then the map's open-sea colour) and foam that rolls in along the shore.
- **From orbit to the ground with the mouse wheel.** The 3D globe zooms all the way down
  to the terrain, tilting towards the horizon as it descends, and switches to the flight
  renderer near the ground, loading close-up textures, vegetation and buildings on the
  way. Level of detail follows distance: a full-resolution Parallax height tile streams in
  around the camera (four times the detail of the base map on Kerbin), and scatters are
  generated by distance from the eye, not from the ground below it.
- **A flat map that keeps its detail up close.** Zooming past level 10 the 2D map fades into
  the flight renderer looking straight down, in the map's own projection: detailed relief,
  the game's terrain textures, shores and every building in plan view, down to about half
  a metre per pixel, with the grid, tracks and markers on top.
- **The stock KSC, straight from the game's data.** The space center's buildings have no
  `.mu` files: they are Unity prefabs inside `sharedassets9.assets`, read here without
  type trees (meshes, materials, hierarchy, cross-file references), including the Unity
  Crunch-compressed masks that say where the ground is tarmac or grass. Each facility shows at
  the level your save has reached, on a flattened plateau like the game's terrain decal,
  and KK's copies of stock buildings use the same models.
- **Optional extra textures in the installer.** If you don't have Parallax, the
  installer can fetch its planet maps (234 MB), surface textures (1.9 GB) and scatters
  (1.1 GB) from the
  author's official GitHub release on your machine. They are Gameslinx's work (all
  rights reserved), so they are never bundled or re-hosted here.
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
  right radius, rotation period, atmosphere, sphere of influence and hierarchy. Point
  it at a folder of KSP textures dumped to PNG and every body gets its real map,
  heights and biomes, with the mirror and the 90° those textures need applied for you.
- **What your save has discovered.** It reads SCANsat coverage out of the save, so
  progression mode hides what you haven't scanned and shows only the anomalies your
  anomaly scanner has found; sandbox mode shows everything. Plus an altimetry filter
  that paints a height band with a SCANsat-style palette and dims the rest.
- **Things to fly with.** A landing planner that works out when and how much to burn
  to set down on a target, a launch-window calculator (patched conics, Lambert, a
  single ballistic burn with no mid-course corrections), and waypoints written
  straight into the save so KSP shows them on the map and the navball.
- **Routes by air, sea and land.** A sat-nav for Kerbin: pick two points and get the
  travel time by plane (great circle), by boat (only over water) and by rover (only
  over land, going around slopes steeper than the maximum and slower on steep ground),
  drawn on the map and the globe with the fastest one highlighted.
- **Automatic updates.** On startup the app checks the latest GitHub release and offers to update; it verifies the installer's SHA-256 and reinstalls in place. Can be turned off in the panel.
- **English and Spanish**, both in the app and in the installer.

The detailed documentation is in **[desktop/README.md](desktop/README.md)**
(Spanish). The installer asks for .NET 10 and installs per user; no admin rights.

---

## The maps

`data/` ships a few images of Kerbin so the app has something to show before it finds
your KSP install (once it does, it switches to the game's own maps):

| File | What it is | Offset | Source |
|---|---|---|---|
| `Kerbin_Color_HD.jpg` | colour 4096×2048 | 0° | contributed, original source unknown |
| `Kerbin_Color.png` | colour 2048×1024 | **90°** | KSP community wiki |
| `Kerbin_Biome.png` | biomes 1800×900 | 0° | KSP community wiki |
| `Kerbin_Height.png` | heights 2048×1024 | 0° | SCANsat export (greyscale −1500…6500 m) |

**They are not mine, nor the project's.** They derive from the game's textures,
property of Squad / Private Division, and the wiki declares no licence. They are
there only so you can try the viewer on your own machine, assuming you own the
game. **Don't redistribute them.** If you'd rather not have them, delete them
and `data/maps.json`: the app still starts and greets you with the reference
graticule.

The provenance and settings of each one are written down in `data/maps.json`.

---

## The old web version

This repository used to hold a second, browser-based viewer (Kerbin Maps). It ended at
version 1.6.10 and won't be updated again; it was removed from the repository in 1.6.11.
If you want it, download `KerbinMaps-web-1.6.10.zip` from the
[web-v1.6.10 release](https://github.com/alonso49noob/koogle-kerbin/releases/tag/web-v1.6.10),
or check out the `web-v1.6.10` tag.

---

## License

Koogle Kerbin is free software, licensed under the **GNU General Public License
v3.0 only** (GPL-3.0-only). The full text is in [LICENSE](LICENSE) (shipped as
`license.txt` in the desktop download). Copyright (C) 2026 alonso cardenas.

Third-party components and their licenses are listed in
[THIRD-PARTY.md](THIRD-PARTY.md). Kerbal Space Program, its textures and any
data extracted from the game belong to Squad and Take-Two Interactive and are
not covered by this license.
