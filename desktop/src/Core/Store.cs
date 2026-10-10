using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KerbinMaps.Core
{
    public sealed class LonOffsets
    {
        public double Color, Biome, Height;

        public double Get(string slot) => slot switch { "color" => Color, "biome" => Biome, _ => Height };

        public void Set(string slot, double v)
        {
            switch (slot)
            {
                case "color": Color = v; break;
                case "biome": Biome = v; break;
                default: Height = v; break;
            }
        }
    }

    /* Settings remembered between sessions: the same as the web version kept in localStorage,
       plus the window. */
    public sealed class AppState
    {
        public string BaseId = "grid";
        public string CustomUrl = "";
        public double Opacity = 1;
        public bool Grid = true;
        public bool Landmarks = true;
        public double HMin = HeightRange.Min, HMax = HeightRange.Max;
        public LonOffsets LonOffset = new();
        public string PresetId;
        public string VolcadoCalibrado;           // Kerbin's height map already calibrated as a dump
        public bool MapasDelJuego;                // the installation preset has already been chosen once
        public bool VerCuerpos = true;            // moons, planets and the Sun on the 3D globe
        public bool View3D;
        public bool BiomeOn, BiomeTouched;
        public double BiomeOpacity = BiomeConfig.DefaultOpacity;
        public double CenterLat = MapConfig.InitialLat, CenterLon = MapConfig.InitialLon, Zoom = MapConfig.InitialZoom;
        public bool BannerDismissed, OffsetTouched;
        public bool SidebarHidden;
        public string ViewMode;                   // "2d", "3d" or "sky"; if missing, View3D decides
        public double ObsLat = -0.0972, ObsLon = -74.5577, ObsAlt = 70;   // the KSC launch pad
        public double SkyAz = 90, SkyEl = 25, SkyFov = 70;
        public bool SkyGrid = true, SkyForceNight;
        public string Lang;                       // «es», «en»; if missing, the system's
        public string Theme;                      // the interface palette (UI.Theme.Palettes); if missing, «clasico»
        public string BodyName;                   // the body being viewed; if missing, Kerbin
        public bool DayNight = true;              // sunlight at the time on the time bar
        public bool ShowOrbitInfo = true;         // panel with the selected vessel's orbit
        public string SavePath;                   // the original persistent.sfs of the saved copy
        public double? SimT;                      // the time on the time bar when closing
        public bool ShowVesselModels = true;      // assemble the vessel from KSP parts when getting close
        public string KspPath;                    // KSP folder chosen by hand, if it isn't found on its own
        public string BodyMapsDir;                // folder with the bodies' textures
        public double BodyMapOffset = 90;         // rotation those textures need
        public bool BodyMapMirror = true;         // and their horizontal mirror
        public Dictionary<string, bool> Sections = new();   // sidebar sections open or closed
        public double? FreeLat, FreeLon;          // free camera: where it was left
        public double FreeAlt = 150, FreeAz = 90, FreeSpeed = 120;
        public bool FreeRelief = true;            // terrain relief when flying
        public bool FreeDetail = true;            // the game's ground textures
        public bool UseParallax = true;           // Parallax's if it's installed
        public bool TextureVariation = true;      // keep the ground texture tiling from looking repeated
        public bool Scatters = true;              // Parallax grass, trees and rocks
        public double ScatterDensity = 1;         // fraction of the ones Parallax places
        public bool Wind = true;                  // vegetation moves with the wind
        public bool ShowStatics = true;           // Kerbal Konstructs buildings
        public bool DetailTiles = true;           // detail heights under the camera (Parallax)
        public int FreeDebug;                     // terrain diagnostics (0 = normal)
        public bool Clouds = true;                // the game's clouds in the sky and flight views
        public double CloudAlt = 5200;            // layer height, m
        public bool GlobeClouds = true;           // and on the 3D globe
        public string TransferTo;                 // destination for the window calculator
        public double TransferPark = 100000;      // parking orbit, m
        public double TransferCapture = 100000;   // orbit to capture into, m
        public bool TransferCapturar = true;
        public double TransferSpan = 500;         // search period, in days
        public double? LandLat, LandLon;          // target for the landing assistant
        public double LandPe = 0;                 // periapsis to brake to, m
        public double LandBc = 200;               // ballistic coefficient, kg/m2
        public bool AltFilter;                    // altimetry filter
        public double? AltMin, AltMax;            // band; if missing, the body's range
        public double AltOpacity = 0.85;
        public bool Progresion;                   // show only what the save has discovered
        public bool ShowScan = true;              // SCANsat coverage over the map
        public bool ShowAnomalies = true;         // anomalies from the catalog
        // «bajo», «medio», «alto» or «personalizado» (as soon as something is touched by hand);
        // ViewDistance is the draw distance for scatters, buildings and landed vessels
        public string GraphicsPreset = "alto";
        public double ViewDistance = 25000;
        public bool FaccionesOn = true;           // countries and factions on the map and the globe
        public double FacRelleno = 0.45;          // opacity of the territory fill
        public bool FacNombres = true;            // names over each territory
        public bool FacRespetar = true;           // don't overwrite other factions' territory
        public double FacPincelKm = 25;           // brush radius
        public bool Olas = true;                  // waves and foam on the sea
        public double RutaVAire = 300;            // cruise speeds for routes, m/s
        public double RutaVMar = 15;
        public double RutaVTierra = 20;
        public double RutaPendiente = 30;         // the steepest slope the rover climbs or descends, °
        public bool AutoUpdate = true;            // check for new versions on startup
        public DateTime? LastUpdateCheck;         // UTC of the last GitHub check
        public string SkippedVersion;             // version marked «skip»
        public int WinX = int.MinValue, WinY = int.MinValue, WinW = 1400, WinH = 880;
        public bool WinMax;
    }

    public sealed class Marker
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("cat")] public string Cat { get; set; }
        [JsonPropertyName("lat")] public double Lat { get; set; }
        [JsonPropertyName("lon")] public double Lon { get; set; }
        [JsonPropertyName("desc")] public string Desc { get; set; }
        [JsonPropertyName("confianza")] public string Confianza { get; set; }
    }

    public sealed class SlotMeta
    {
        public string Name;
        public int W, H;
        public long Size;
        public bool FromDisk;
        public string Ext;
    }

    /* Local persistence. Settings, markers and biome names as JSON under %APPDATA%; the images
       you drop, copied under %LOCALAPPDATA%, since they can weigh tens of MB. Saving is a
       convenience: if it fails, the viewer carries on. */
    public static class Store
    {
        /* With KOOGLE_PERFIL, everything goes to that folder: for testing a first run without
           touching the real settings. */
        static readonly string perfil = Environment.GetEnvironmentVariable("KOOGLE_PERFIL");
        public static readonly string RoamingDir = !string.IsNullOrEmpty(perfil) ? Path.Combine(perfil, "roaming")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KoogleKerbin");
        public static readonly string LocalDir = !string.IsNullOrEmpty(perfil) ? Path.Combine(perfil, "local")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoogleKerbin");

        /* The app used to be called Kerbin Maps: if its folders are still there and the new
           ones don't exist yet, they're renamed so settings, markers and loaded maps aren't
           lost. */
        public static void MigrateOldFolders()
        {
            if (!string.IsNullOrEmpty(perfil)) return;
            foreach (var (root, nuevo) in new[] { (Environment.SpecialFolder.ApplicationData, RoamingDir), (Environment.SpecialFolder.LocalApplicationData, LocalDir) })
            {
                try
                {
                    string viejo = Path.Combine(Environment.GetFolderPath(root), "KerbinMaps");
                    if (Directory.Exists(viejo) && !Directory.Exists(nuevo)) Directory.Move(viejo, nuevo);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[store] no se pudo migrar: " + ex.Message);
                }
            }
        }

        /* Copy of the last loaded save, to restore it when opening again. */
        public static string SaveCopyPath => Path.Combine(LocalDir, "partida", "persistent.sfs");

        public static readonly JsonSerializerOptions Json = new()
        {
            IncludeFields = true,
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static T Load<T>(string name, T fallback)
        {
            try
            {
                string p = Path.Combine(RoamingDir, name);
                if (!File.Exists(p)) return fallback;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(p), Json) ?? fallback;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[store] no se pudo leer " + name + ": " + ex.Message);
                return fallback;
            }
        }

        public static void Save<T>(string name, T value)
        {
            try
            {
                Directory.CreateDirectory(RoamingDir);
                string p = Path.Combine(RoamingDir, name), tmp = p + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
                File.Move(tmp, p, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[store] no se pudo guardar " + name + ": " + ex.Message);
            }
        }

        static string SlotDir => Path.Combine(LocalDir, "slots");

        public static void PutImage(string slot, byte[] bytes, SlotMeta meta)
        {
            Directory.CreateDirectory(SlotDir);
            DelImage(slot);
            string ext = string.IsNullOrEmpty(meta.Ext) ? ".img" : meta.Ext;
            File.WriteAllBytes(Path.Combine(SlotDir, slot + ext), bytes);
            File.WriteAllText(Path.Combine(SlotDir, slot + ".json"), JsonSerializer.Serialize(meta, Json));
        }

        public static (byte[] bytes, SlotMeta meta)? GetImage(string slot)
        {
            string metaPath = Path.Combine(SlotDir, slot + ".json");
            if (!File.Exists(metaPath)) return null;
            var meta = JsonSerializer.Deserialize<SlotMeta>(File.ReadAllText(metaPath), Json);
            string img = Path.Combine(SlotDir, slot + (string.IsNullOrEmpty(meta?.Ext) ? ".img" : meta.Ext));
            if (!File.Exists(img)) return null;
            return (File.ReadAllBytes(img), meta);
        }

        /* Sets it aside in slots/anteriores/<date> instead of deleting it: when switching to a
           preset, the map that had been loaded by hand isn't lost. */
        public static void ArchiveImage(string slot)
        {
            if (!Directory.Exists(SlotDir)) return;
            var ficheros = Directory.GetFiles(SlotDir, slot + ".*");
            if (ficheros.Length == 0) return;
            string dest = Path.Combine(SlotDir, "anteriores", DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
            Directory.CreateDirectory(dest);
            foreach (var f in ficheros) File.Move(f, Path.Combine(dest, Path.GetFileName(f)), true);
        }

        public static void DelImage(string slot)
        {
            if (!Directory.Exists(SlotDir)) return;
            foreach (var f in Directory.GetFiles(SlotDir, slot + ".*")) File.Delete(f);
        }

        static string dataDir;

        /* The data/ folder next to the .exe. During development the .exe lives in bin/ and the
           maps at the project root: they're found by walking up folders. */
        public static string DataDir
        {
            get
            {
                if (dataDir != null) return dataDir;
                string here = AppContext.BaseDirectory;
                var dir = new DirectoryInfo(here);
                for (int i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
                {
                    string cand = Path.Combine(dir.FullName, "data");
                    if (File.Exists(Path.Combine(cand, "maps.json")) || File.Exists(Path.Combine(cand, "landmarks.json")))
                        return dataDir = cand;
                }
                return dataDir = Path.Combine(here, "data");
            }
        }

        public static string TilesDir => Path.Combine(Path.GetDirectoryName(DataDir) ?? AppContext.BaseDirectory, "tiles");
    }

    public sealed class SlotSpec
    {
        public string File;
        public bool Auto;
        public double LonOffset;
        public double? HMin, HMax;
        public string Juego;                      // comes from the KSP installation: «color», «biome» or «height»
    }

    public sealed class Preset
    {
        public string Id, Nombre, Fuente;
        public SlotSpec Color, Biome, Height;
        public SlotSpec Get(string slot) => slot switch { "color" => Color, "biome" => Biome, _ => Height };
    }

    /* Catalog of default maps (data/maps.json) and reference sites (data/landmarks.json). */
    public sealed class MapsCatalog
    {
        public string Predeterminado;
        public List<Preset> Presets = new();

        public Preset Find(string id) => Presets.FirstOrDefault(p => p.Id == id);

        public static MapsCatalog Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var root = doc.RootElement;
                if (!root.TryGetProperty("presets", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
                var cat = new MapsCatalog { Predeterminado = Str(root, "predeterminado") };
                foreach (var p in arr.EnumerateArray())
                {
                    var preset = new Preset
                    {
                        Id = Str(p, "id"), Nombre = Str(p, "nombre"), Fuente = Str(p, "fuente"),
                        Color = Spec(p, "color"), Biome = Spec(p, "biome"), Height = Spec(p, "height")
                    };
                    if (!string.IsNullOrEmpty(preset.Id)) cat.Presets.Add(preset);
                }
                return cat.Presets.Count > 0 ? cat : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[catalogo] " + ex.Message);
                return null;
            }
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static SlotSpec Spec(JsonElement p, string slot)
        {
            if (!p.TryGetProperty(slot, out var s) || s.ValueKind != JsonValueKind.Object) return null;
            var spec = new SlotSpec { File = Str(s, "file") };
            if (s.TryGetProperty("lonOffset", out var off))
            {
                if (off.ValueKind == JsonValueKind.String && off.GetString() == "auto") spec.Auto = true;
                else if (off.ValueKind == JsonValueKind.Number) spec.LonOffset = Math.Truncate(off.GetDouble());
            }
            if (s.TryGetProperty("hMin", out var mn) && mn.ValueKind == JsonValueKind.Number) spec.HMin = mn.GetDouble();
            if (s.TryGetProperty("hMax", out var mx) && mx.ValueKind == JsonValueKind.Number) spec.HMax = mx.GetDouble();
            return spec;
        }

        public static List<Marker> LoadLandmarks(string path)
        {
            try
            {
                if (!File.Exists(path)) return new List<Marker>();
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("items", out var items)) return new List<Marker>();
                return JsonSerializer.Deserialize<List<Marker>>(items.GetRawText(), Store.Json) ?? new List<Marker>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[sitios] " + ex.Message);
                return new List<Marker>();
            }
        }
    }
}
