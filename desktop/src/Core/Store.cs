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

    /* Ajustes que se recuerdan entre sesiones: lo mismo que la web guardaba en
       localStorage, más la ventana. */
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
        public string VolcadoCalibrado;           // el mapa de alturas de Kerbin ya calibrado como volcado
        public bool MapasDelJuego;                // ya se eligió una vez el preset de la instalación
        public bool VerCuerpos = true;            // lunas, planetas y el Sol en el globo 3D
        public bool View3D;
        public bool BiomeOn, BiomeTouched;
        public double BiomeOpacity = BiomeConfig.DefaultOpacity;
        public double CenterLat = MapConfig.InitialLat, CenterLon = MapConfig.InitialLon, Zoom = MapConfig.InitialZoom;
        public bool BannerDismissed, OffsetTouched;
        public bool SidebarHidden;
        public string ViewMode;                   // "2d", "3d" o "sky"; si falta, manda View3D
        public double ObsLat = -0.0972, ObsLon = -74.5577, ObsAlt = 70;   // la plataforma del KSC
        public double SkyAz = 90, SkyEl = 25, SkyFov = 70;
        public bool SkyGrid = true, SkyForceNight;
        public string Lang;                       // «es», «en»; si falta, el del sistema
        public string BodyName;                   // el cuerpo que se ve; si falta, Kerbin
        public bool DayNight = true;              // luz del Sol en el instante de la barra de tiempo
        public bool ShowOrbitInfo = true;         // panel con la órbita de la nave seleccionada
        public string SavePath;                   // el persistent.sfs original de la copia guardada
        public double? SimT;                      // el instante de la barra de tiempo al cerrar
        public bool ShowVesselModels = true;      // montar la nave con las piezas de KSP al acercarse
        public string KspPath;                    // carpeta de KSP elegida a mano, si no se encuentra sola
        public string BodyMapsDir;                // carpeta con las texturas de los cuerpos
        public double BodyMapOffset = 90;         // giro que necesitan esas texturas
        public bool BodyMapMirror = true;         // y su espejo horizontal
        public Dictionary<string, bool> Sections = new();   // secciones del panel abiertas o cerradas
        public double? FreeLat, FreeLon;          // cámara libre: dónde se quedó
        public double FreeAlt = 150, FreeAz = 90, FreeSpeed = 120;
        public bool FreeRelief = true;            // relieve del terreno al volar
        public bool FreeDetail = true;            // texturas de suelo del juego
        public bool UseParallax = true;           // las de Parallax si está instalado
        public bool TextureVariation = true;      // que el mosaico de las texturas de suelo no se vea repetido
        public bool Scatters = true;              // hierba, árboles y rocas de Parallax
        public double ScatterDensity = 1;         // fracción de los que pone Parallax
        public bool Wind = true;                  // la vegetación se mueve con el viento
        public bool ShowStatics = true;           // edificios de Kerbal Konstructs
        public bool DetailTiles = true;           // alturas de detalle bajo la cámara (Parallax)
        public int FreeDebug;                     // diagnóstico del relieve (0 = normal)
        public bool Clouds = true;                // nubes del juego en cielo y vuelo
        public double CloudAlt = 5200;            // altura de la capa, m
        public bool GlobeClouds = true;           // y en el globo 3D
        public string TransferTo;                 // destino de la calculadora de ventanas
        public double TransferPark = 100000;      // órbita de aparcamiento, m
        public double TransferCapture = 100000;   // órbita a la que capturar, m
        public bool TransferCapturar = true;
        public double TransferSpan = 500;         // plazo de búsqueda, en días
        public double? LandLat, LandLon;          // objetivo del asistente de aterrizaje
        public double LandPe = 0;                 // periapsis al que frenar, m
        public double LandBc = 200;               // coeficiente balístico, kg/m2
        public bool AltFilter;                    // filtro de altimetría
        public double? AltMin, AltMax;            // franja; si falta, el rango del cuerpo
        public double AltOpacity = 0.85;
        public bool Progresion;                   // enseñar solo lo que la partida ha descubierto
        public bool ShowScan = true;              // cobertura de SCANsat sobre el mapa
        public bool ShowAnomalies = true;         // anomalías del catálogo
        // «bajo», «medio», «alto» o «personalizado» (en cuanto se toca algo a mano);
        // ViewDistance es la distancia de dibujado de scatters, edificios y naves en tierra
        public string GraphicsPreset = "alto";
        public double ViewDistance = 25000;
        public bool FaccionesOn = true;           // países y facciones en el mapa y el globo
        public double FacRelleno = 0.45;          // opacidad del relleno de los territorios
        public bool FacNombres = true;            // nombres sobre cada territorio
        public bool FacRespetar = true;           // no pisar el territorio de otras facciones
        public double FacPincelKm = 25;           // radio del pincel
        public bool Olas = true;                  // olas y espuma en el mar
        public bool AutoUpdate = true;            // buscar versiones nuevas al abrir
        public DateTime? LastUpdateCheck;         // UTC de la última consulta a GitHub
        public string SkippedVersion;             // versión que se dijo «omitir»
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

    /* Persistencia local. Ajustes, marcadores y nombres de bioma en JSON bajo
       %APPDATA%; las imágenes que sueltas, copiadas bajo %LOCALAPPDATA%, que pueden
       pesar decenas de MB. Guardar es una comodidad: si falla, el visor sigue. */
    public static class Store
    {
        /* Con KOOGLE_PERFIL, todo va a esa carpeta: para probar una primera ejecución sin
           tocar los ajustes de verdad. */
        static readonly string perfil = Environment.GetEnvironmentVariable("KOOGLE_PERFIL");
        public static readonly string RoamingDir = !string.IsNullOrEmpty(perfil) ? Path.Combine(perfil, "roaming")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KoogleKerbin");
        public static readonly string LocalDir = !string.IsNullOrEmpty(perfil) ? Path.Combine(perfil, "local")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoogleKerbin");

        /* La app se llamaba Kerbin Maps: si quedan sus carpetas y aún no hay de las
           nuevas, se renombran para no perder ajustes, marcadores ni mapas cargados. */
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

        /* Copia de la última partida cargada, para recuperarla al volver a abrir. */
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

        /* La deja aparte en slots/anteriores/<fecha> en lugar de borrarla: al cambiar a un
           preset, el mapa que se había cargado a mano no se pierde. */
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

        /* La carpeta data/ junto al .exe. Mientras se desarrolla, el .exe vive en
           bin/ y los mapas en la raíz del proyecto: se buscan subiendo carpetas. */
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
        public string Juego;                      // sale de la instalación de KSP: «color», «biome» o «height»
    }

    public sealed class Preset
    {
        public string Id, Nombre, Fuente;
        public SlotSpec Color, Biome, Height;
        public SlotSpec Get(string slot) => slot switch { "color" => Color, "biome" => Biome, _ => Height };
    }

    /* Catálogo de mapas predeterminados (data/maps.json) y sitios de referencia
       (data/landmarks.json). Los mismos ficheros que usa la versión web. */
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
