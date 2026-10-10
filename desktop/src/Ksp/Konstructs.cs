using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KerbinMaps.Ksp
{
    /* Kerbal Konstructs buildings: the models (STATIC nodes with their .mu), the group centers
       (KK_GroupCenter) and the instances (Instances nodes), read from the GameData .cfg files
       as KK leaves them, and placed with its same math.

       How KK places each thing (GroupCenter.cs and StaticInstance.cs in its code):
       - A group center goes to its latitude and longitude, RadiusOffset meters above the
         terrain (or above sea level with SeaLevelAsReference). Its rotation is
         LookRotation(vertical) · Euler(0, 0, Heading) · Euler(-90, -90, -90); in old ones,
         without Heading, PQSCity's: from «up» to the vertical and RotationAngle degrees around
         «up».
       - An instance is a child of its group: localPosition = RelativePosition, localEulerAngles
         = Orientation, localScale = ModelScale.
       - Old ones, without RelativePosition, go on their own: RadialPosition, RadiusOffset above
         sea level (or above the terrain if IsRelativeToTerrain = 2), with PQSCity's rotation.
       - The «KSC_Builtin» groups and the like are the game's sites (Kerbin's PQSCity): they
         aren't in any .cfg and here they go with the unmodded game's values.

       Everything is computed in KSP's body frame (Unity, left-handed, Y toward the north pole,
       x = cos lat cos lon, z = cos lat sin lon) and in meters. To go to the viewer's frame it's
       enough to swap X and Z, as with vessels. */
    public sealed class KkModel
    {
        public string Name, Title, Category, Author;
        public string Mesh;                       // full path of the .mu, or null
        public string CfgPath;
        public long StockRoot;                    // stock building: its prefab in the game data
        // AdvancedTextures modules: which objects of the model («Any»: all) get their texture changed
        public readonly List<(HashSet<string> Transforms, string MainTex)> TexSwaps = new();
        public bool Stock => StockRoot != 0;
        public bool HasMesh => Stock || (Mesh != null && File.Exists(Mesh));

        // launch site defaults from the STATIC node: the spawn transform and the pad size
        public string DefaultLaunchPadTransform;
        public double DefaultSiteLength, DefaultSiteWidth;

        List<string> spawnTransforms;

        /* Transforms of the model a vessel can spawn on, best first: the declared default and
           then any whose name says spawn or launch. KK looks the transform up by name and, if
           it isn't in the model, the site is never registered («Launch pad transform … missing»),
           so only names that really exist in the .mu are offered. Empty: this model can't be a
           launch site. */
        public IReadOnlyList<string> SpawnTransforms()
        {
            if (spawnTransforms != null) return spawnTransforms;
            var nombres = new List<string>();
            try
            {
                if (!Stock && HasMesh)
                {
                    var pila = new Stack<MuNode>();
                    pila.Push(MuFile.Load(Mesh).Root);
                    while (pila.Count > 0)
                    {
                        var n = pila.Pop();
                        if (n == null) continue;
                        if (!string.IsNullOrEmpty(n.Name)) nombres.Add(n.Name);
                        foreach (var h in n.Children) pila.Push(h);
                    }
                }
            }
            catch { }
            var res = new List<string>();
            if (!string.IsNullOrWhiteSpace(DefaultLaunchPadTransform) && nombres.Contains(DefaultLaunchPadTransform.Trim()))
                res.Add(DefaultLaunchPadTransform.Trim());
            foreach (var n in nombres)
                if (!res.Contains(n) && (n.Contains("spawn", StringComparison.OrdinalIgnoreCase) || n.Contains("launch", StringComparison.OrdinalIgnoreCase)))
                    res.Add(n);
            return spawnTransforms = res;
        }

        /* The assembled model, with the textures KK changes on it. `_MainTex` is a GameData
           path without extension or «BUILTIN:/name», a texture from the game itself. */
        public AssembledVessel Build(string gameData, StockAssets stock)
        {
            if (Stock) return StockPrefabs.For(gameData)?.Build(StockRoot) ?? new AssembledVessel();
            var a = VesselAssembler.BuildModel(Mesh, stock);
            foreach (var (tr, tex) in TexSwaps)
            {
                string ruta = tex.StartsWith("BUILTIN:", StringComparison.OrdinalIgnoreCase)
                    ? stock?.Find(tex.Substring(tex.IndexOf(':') + 1).TrimStart('/'))
                    : TextureFile.Find(Path.Combine(gameData, tex.Replace('/', Path.DirectorySeparatorChar)));
                if (ruta == null) continue;
                foreach (var it in a.Items)
                    if (tr == null || tr.Contains(it.Node)) it.TexturePath = ruta;
            }
            return a;
        }
    }

    public sealed class KkGroup
    {
        public string Name, Body, CfgPath;
        public double Lat, Lon, RadiusOffset, Heading = 361, RotationAngle, Scale = 1;
        public bool Nuevo, Cambiado;              // created or touched in the editor, unsaved
        public double[] Up = { 0, 1, 0 };
        public bool SeaLevel, Builtin, Implicit;
        public ConfigNode Node;

        // computed by Place
        public double Alt;                        // m above sea level
        public double[] M;                        // 4x4 column-major, in KSP's body frame
        public int Count;

        public string Key => Body + "_" + Name;
    }

    /* A KK launch site: the LaunchSite node inside an instance. With it, the building shows
       up in KK's launch site selector in the VAB and SPH and vessels spawn on its transform. */
    public sealed class KkLaunchSite
    {
        public string Name = "", Transform = "", Type = "VAB", Category = "RocketPad", Description = "", Author = "";
        public double Length = 30, Width = 30, Height = 50, MaxMass, MaxParts, OpenCost, CloseValue, CameraRotation = 90;
        public string State = "Open";
        public bool Hidden;
        public bool Legacy;                       // flat LaunchSite* keys in the instance (old KK): read only

        public KkLaunchSite Clone() => (KkLaunchSite)MemberwiseClone();
    }

    public sealed class KkInstance
    {
        public string Model, Body, Group = "Ungrouped", Uuid, CfgPath, LaunchSite;
        public KkLaunchSite Sitio;
        public bool SitioCambiado;                // the launch site was added, edited or removed in the editor
        public int FileIndex = -1;                // which Instances node it is within its file
        public bool DelJuego;                     // a stock KSC facility: it isn't in any .cfg
        public string Instalacion;                // which KSC facility it is («SpaceCenter/LaunchPad»)
        public string[] ModelosNivel;             // its models, from level 1 to the last
        public bool Nuevo, Cambiado, Borrado;     // state in the editor, unsaved
        public double[] Rel = { 0, 0, 0 }, Euler = { 0, 0, 0 };
        public double Scale = 1, Visibility = 25000;
        // old format
        public double[] Radial = { 0, 0, 0 }, OrientUp = { 0, 1, 0 };
        public double RadiusOffset, RotationAngle;
        public int HeightRef;
        public ConfigNode Node;

        // computed by Place
        public KkGroup GroupRef;
        public KkModel ModelRef;
        public double[] M;                        // model → KSP body frame, in meters
        public double Lat, Lon, Alt;
        public bool Placed;

        public bool Legacy => Rel[0] == 0 && Rel[1] == 0 && Rel[2] == 0;
        public string Label => ModelRef?.Title is string t && t.Length > 0 ? t : Model;
    }

    public sealed class KkDatabase
    {
        public readonly Dictionary<string, KkModel> Models = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, KkGroup> Groups = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<KkInstance> Instances = new();
        public int Files;
        public string GameData;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /* The game's sites that KK uses as group centers (BuiltinCenters.cs), with the values of
           Kerbin's PQSCity: radial position, final rotation and height above the sea. */
        static readonly (string Name, double[] Radial, double Angle, double Offset)[] Builtins =
        {
            ("KSC", new double[] { 157000, -1000, -570000 }, -15, 42.7000007629395),
            ("KSC2", new double[] { -468960.406, 211164.703, -310261.688 }, -117.65, 201),
            ("Pyramids", new double[] { -468635.094, -68111.1016, -370297.094 }, 0, 98),
            ("IslandAirfield", new double[] { 186253.594, -16135.6797, -570176.813 }, 150, 28),
        };
        // the PQSCity2 of the Making History DLC: latitude, longitude, rotation and height
        static readonly (string Name, double Lat, double Lon, double Angle, double Alt)[] Builtins2 =
        {
            ("Desert_Airfield", -6.51999963320189, -144.039999478851, -125, 822.840140053537),
            ("Woomerang_Launch_Site", 45.2899990947616, 136.1100029881, 135, 734.788969624788),
        };

        public static KkDatabase Load(string gameData, string homeWorld)
        {
            var db = new KkDatabase { GameData = gameData };
            foreach (var b in Builtins)
            {
                var n = Norm(b.Radial);
                db.Groups[homeWorld + "_" + b.Name + "_Builtin"] = new KkGroup
                {
                    Name = b.Name + "_Builtin", Body = homeWorld, Builtin = true, SeaLevel = true,
                    Lat = Math.Asin(n[1]) * 180 / Math.PI, Lon = Math.Atan2(n[2], n[0]) * 180 / Math.PI,
                    RotationAngle = b.Angle, RadiusOffset = b.Offset,
                };
            }
            if (Directory.Exists(Path.Combine(gameData, "SquadExpansion", "MakingHistory")))
                foreach (var b in Builtins2)
                    db.Groups[homeWorld + "_" + b.Name + "_Builtin"] = new KkGroup
                    {
                        Name = b.Name + "_Builtin", Body = homeWorld, Builtin = true, SeaLevel = true,
                        Lat = b.Lat, Lon = b.Lon, RotationAngle = b.Angle, RadiusOffset = b.Alt,
                    };

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(gameData, "*.cfg", SearchOption.AllDirectories).ToList(); }
            catch { return db; }
            var pendientes = new List<(ConfigNode Node, string Path)>();
            foreach (var path in files)
            {
                string text;
                try { text = File.ReadAllText(path); }
                catch { continue; }
                // most .cfg files have nothing to do with KK: they're discarded without parsing
                if (!text.Contains("STATIC", StringComparison.Ordinal) && !text.Contains("KK_GroupCenter", StringComparison.Ordinal)) continue;
                ConfigNode root;
                try { root = ConfigNode.Parse(text.Split('\n')); }
                catch { continue; }
                bool alguno = false;
                foreach (var n in root.Nodes)
                {
                    // «@STATIC», «+STATIC:NEEDS[...]»... are ModuleManager patches, not definitions
                    if (n.Name == "STATIC") { pendientes.Add((n, path)); alguno = true; }
                    else if (n.Name == "KK_GroupCenter") { db.AddGroup(n, path); alguno = true; }
                }
                if (alguno) db.Files++;
            }
            // all the models first, because an instance can be in another file
            foreach (var (n, path) in pendientes)
                if (n.Get("mesh") != null) db.AddModel(n, path);
            var porFichero = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (n, path) in pendientes)
            {
                string modelo = n.Get("pointername") is string pn && pn.Length > 0 && !pn.Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? pn : n.Get("name");
                if (string.IsNullOrEmpty(modelo))
                {
                    porFichero[path] = porFichero.GetValueOrDefault(path) + n.Children("Instances").Count();
                    continue;
                }
                foreach (var inst in n.Children("Instances"))
                {
                    var i = ParseInstance(inst, modelo, path);
                    i.FileIndex = porFichero.GetValueOrDefault(path);
                    porFichero[path] = i.FileIndex + 1;
                    db.Instances.Add(i);
                }
            }
            db.AddStock(homeWorld);
            foreach (var i in db.Instances)
                i.ModelRef = db.Models.GetValueOrDefault(i.Model);
            return db;
        }

        /* The stock buildings: the models with the name KK gives them (for the instances that
           use them and to place them from the editor) and the KSC itself, with each facility at
           its highest level, inside the KSC_Builtin group as in the game. */
        void AddStock(string homeWorld)
        {
            var sp = StockPrefabs.For(GameData);
            if (sp == null) return;
            foreach (var (nombre, raiz) in sp.PorNombreKK)
            {
                if (Models.ContainsKey(nombre)) continue;
                string titulo = nombre.StartsWith("KSC_") ? "KSC " + nombre.Substring(4).Replace("_level_", " lv ").Replace('_', ' ') : nombre;
                Models[nombre] = new KkModel { Name = nombre, Title = titulo, Category = "Squad KSC", Author = "Squad", StockRoot = raiz };
            }
            string grupo = homeWorld + "_KSC_Builtin";
            if (!Groups.ContainsKey(grupo)) return;
            foreach (var f in sp.Ksc)
            {
                bool camino = !sp.PorNombreKK.ContainsKey("KSC_" + f.Name + "_level_1");   // the Grounds ones
                string prefijo = camino ? "KSC_Grounds_" + f.Name : "KSC_" + f.Name;
                var modelos = new string[f.Niveles.Length];
                for (int n = 0; n < modelos.Length; n++)
                {
                    modelos[n] = prefijo + "_level_" + (n + 1);
                    if (!Models.ContainsKey(modelos[n]))
                        Models[modelos[n]] = new KkModel { Name = modelos[n], Title = "KSC " + f.Name + " lv " + (n + 1), Category = "Squad KSC", Author = "Squad", StockRoot = f.Niveles[n] };
                }
                Instances.Add(new KkInstance
                {
                    Model = modelos[^1], Body = homeWorld, Group = "KSC_Builtin", DelJuego = true,
                    Instalacion = (camino ? "SpaceCenter/Grounds/" : "SpaceCenter/") + f.Name, ModelosNivel = modelos,
                    Rel = new[] { f.M[12], f.M[13], f.M[14] }, Euler = EulerDe(f.M),
                    LaunchSite = f.Name == "Runway" || f.Name == "LaunchPad" ? f.Name : null,
                });
            }
        }

        /* The level of each KSC facility according to the save (0 to 1, as the game stores it:
           with three levels, 0, 0.5 and 1). Without data, the highest, as in sandbox. Says
           whether anything changed. */
        public bool NivelesKsc(Func<string, double?> nivel)
        {
            bool cambio = false;
            foreach (var i in Instances)
            {
                if (!i.DelJuego || i.ModelosNivel == null) continue;
                int n = i.ModelosNivel.Length;
                int k = nivel(i.Instalacion) is double v ? (int)Math.Round(Math.Clamp(v, 0, 1) * (n - 1)) : n - 1;
                string m = i.ModelosNivel[k];
                if (m == i.Model) continue;
                i.Model = m;
                i.ModelRef = Models.GetValueOrDefault(m);
                cambio = true;
            }
            return cambio;
        }

        /* Unity Euler angles (Z, then X, then Y) of a rotation matrix. */
        public static double[] EulerDe(double[] m)
        {
            double sx = Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
            double sy = Math.Sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]);
            double sz = Math.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);
            double r12 = m[9] / sz, r02 = m[8] / sz, r22 = m[10] / sz, r10 = m[1] / sx, r11 = m[5] / sy;
            double x = Math.Asin(Math.Clamp(-r12, -1, 1));
            double y, z;
            if (Math.Abs(r12) < 0.999999)
            {
                y = Math.Atan2(r02, r22);
                z = Math.Atan2(r10, r11);
            }
            else
            {
                // gimbal lock: with X at ±90° only the sum of Y and Z counts
                y = Math.Atan2(-m[2] / sx, m[0] / sx);
                z = 0;
            }
            double D(double v) => ((v * 180 / Math.PI) % 360 + 360) % 360;
            return new[] { D(x), D(y), D(z) };
        }

        void AddModel(ConfigNode n, string path)
        {
            string name = n.Get("name");
            if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(path);
            string mesh = n.Get("mesh");
            if (mesh.Contains('.')) mesh = mesh.Substring(0, mesh.LastIndexOf('.'));
            Models[name] = new KkModel
            {
                Name = name,
                Title = n.Get("title"),
                Category = n.Get("category"),
                Author = n.Get("author"),
                Mesh = Path.Combine(Path.GetDirectoryName(path), mesh.Replace('/', Path.DirectorySeparatorChar) + ".mu"),
                CfgPath = path,
                DefaultLaunchPadTransform = n.Get("DefaultLaunchPadTransform"),
                DefaultSiteLength = D(n.Get("DefaultLaunchSiteLength"), 0),
                DefaultSiteWidth = D(n.Get("DefaultLaunchSiteWidth"), 0),
            };
            foreach (var mod in n.Children("MODULE"))
            {
                if (mod.Get("name") != "AdvancedTextures" || string.IsNullOrWhiteSpace(mod.Get("_MainTex"))) continue;
                string tr = mod.Get("transforms") ?? "Any";
                var set = tr.Trim().Equals("Any", StringComparison.OrdinalIgnoreCase) ? null
                    : new HashSet<string>(tr.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                Models[name].TexSwaps.Add((set, mod.Get("_MainTex").Trim()));
            }
        }

        void AddGroup(ConfigNode n, string path)
        {
            var g = new KkGroup
            {
                Name = n.Get("Group") ?? "",
                Body = n.Get("CelestialBody") ?? "Kerbin",
                CfgPath = path,
                Node = n,
                RadiusOffset = D(n.Get("RadiusOffset"), 0),
                Heading = D(n.Get("Heading"), 361),
                RotationAngle = D(n.Get("RotationAngle"), 0),
                Scale = D(n.Get("ModelScale"), 1),
                SeaLevel = Bool(n.Get("SeaLevelAsReference")),
                Up = V(n.Get("Orientation")) ?? new double[] { 0, 1, 0 },
            };
            double lat = D(n.Get("RefLatitude"), 361), lon = D(n.Get("RefLongitude"), 361);
            if ((lat == 361 || lon == 361) && V(n.Get("RadialPosition")) is double[] rp && Len(rp) > 0)
            {
                var u = Norm(rp);
                lat = Math.Asin(u[1]) * 180 / Math.PI;
                lon = Math.Atan2(u[2], u[0]) * 180 / Math.PI;
            }
            if (lat == 361 || lon == 361) return;
            g.Lat = lat; g.Lon = lon;
            Groups[g.Key] = g;
        }

        static KkInstance ParseInstance(ConfigNode n, string modelo, string path)
        {
            var i = new KkInstance
            {
                Model = modelo,
                Body = n.Get("CelestialBody") ?? "Kerbin",
                Group = n.Get("Group") is string gr && gr.Length > 0 ? gr : "Ungrouped",
                Uuid = n.Get("UUID"),
                CfgPath = path,
                Node = n,
                Rel = V(n.Get("RelativePosition")) ?? new double[3],
                Euler = V(n.Get("Orientation")) ?? new double[3],
                Scale = D(n.Get("ModelScale"), 1),
                Visibility = D(n.Get("VisibilityRange"), 25000),
                Radial = V(n.Get("RadialPosition")) ?? new double[3],
                RadiusOffset = D(n.Get("RadiusOffset"), 0),
                RotationAngle = D(n.Get("RotationAngle"), 0),
                HeightRef = (int)D(n.Get("IsRelativeToTerrain"), 0),
            };
            // in the old format «Orientation» is the model's «up» vector
            if (i.Legacy) i.OrientUp = Len(i.Euler) > 0 ? (double[])i.Euler.Clone() : new double[] { 0, 1, 0 };
            if (n.Children("LaunchSite").FirstOrDefault() is ConfigNode ls) i.Sitio = ParseSitio(ls, false);
            else if (!string.IsNullOrEmpty(n.Get("LaunchSiteName"))) i.Sitio = ParseSitio(n, true);
            i.LaunchSite = i.Sitio?.Name;
            return i;
        }

        static KkLaunchSite ParseSitio(ConfigNode n, bool legacy) => new()
        {
            Name = n.Get("LaunchSiteName") ?? "",
            Transform = n.Get("LaunchPadTransform") ?? "",
            Type = n.Get("LaunchSiteType") is string t && t.Length > 0 ? t : "Any",
            Category = n.Get("Category") is string c && c.Length > 0 ? c : "Other",
            Description = n.Get("LaunchSiteDescription") ?? "",
            Author = n.Get("LaunchSiteAuthor") ?? "",
            Length = D(n.Get("LaunchSiteLength"), 0),
            Width = D(n.Get("LaunchSiteWidth"), 0),
            Height = D(n.Get("LaunchSiteHeight"), 0),
            MaxMass = D(n.Get("MaxCraftMass"), 0),
            MaxParts = D(n.Get("MaxCraftParts"), 0),
            OpenCost = D(n.Get("OpenCost"), 0),
            CloseValue = D(n.Get("CloseValue"), 0),
            CameraRotation = D(n.Get("InitialCameraRotation"), 90),
            State = n.Get("OpenCloseState") is string s && s.Length > 0 ? s : "Closed",
            Hidden = Bool(n.Get("LaunchSiteIsHidden")),
            Legacy = legacy,
        };

        /* Launch site names already in use on any body (KK needs them unique), plus the stock ones. */
        public HashSet<string> NombresDeSitios(KkInstance salvo = null)
        {
            var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "KSC", "LaunchPad", "Runway", "KSC LaunchPad", "KSC Runway" };
            foreach (var i in Instances)
                if (i != salvo && !i.Borrado && !string.IsNullOrEmpty(i.Sitio?.Name)) res.Add(i.Sitio.Name);
            return res;
        }

        /* Places the groups and instances of a body. `ground` gives the terrain height (m) at a
           latitude and longitude: that of the height map the viewer paints, so buildings sit on
           the same ground you see. */
        public void Place(string body, double radius, Func<double, double, double> ground)
        {
            foreach (var g in Groups.Values.Where(g => g.Body == body && g.Implicit).ToList()) Groups.Remove(g.Key);
            foreach (var g in Groups.Values)
            {
                if (g.Body != body) continue;
                PlaceGroup(g, radius, ground);
                g.Count = 0;
            }
            foreach (var i in Instances)
            {
                i.Placed = false;
                if (i.Body != body || i.Borrado) continue;
                double[] m;
                if (i.Legacy)
                {
                    if (Len(i.Radial) == 0) continue;
                    var n = Norm(i.Radial);
                    double lat = Math.Asin(n[1]) * 180 / Math.PI, lon = Math.Atan2(n[2], n[0]) * 180 / Math.PI;
                    double alt = i.RadiusOffset + (i.HeightRef == 2 ? ground(lat, lon) : 0);
                    m = Mat.Mul(Mat.Translate(Scale(n, radius + alt)),
                        Mat.Mul(Mat.Rotate(Mat.QMul(FromTo(i.OrientUp, n), AngleAxis(i.RotationAngle, new double[] { 0, 1, 0 }))),
                                Mat.Scale(i.Scale, i.Scale, i.Scale)));
                    i.GroupRef = Groups.GetValueOrDefault(body + "_" + i.Group);
                }
                else
                {
                    if (!Groups.TryGetValue(body + "_" + i.Group, out var g))
                    {
                        // KK creates the center wherever RadialPosition says, if there is one
                        if (Len(i.Radial) == 0) continue;
                        var n = Norm(i.Radial);
                        g = new KkGroup
                        {
                            Name = i.Group, Body = body, Implicit = true,
                            Lat = Math.Asin(n[1]) * 180 / Math.PI, Lon = Math.Atan2(n[2], n[0]) * 180 / Math.PI,
                        };
                        Groups[g.Key] = g;
                        PlaceGroup(g, radius, ground);
                    }
                    i.GroupRef = g;
                    m = Mat.Mul(g.M, Mat.Mul(Mat.Translate(i.Rel),
                        Mat.Mul(Mat.Rotate(Mat.Euler(i.Euler)), Mat.Scale(i.Scale, i.Scale, i.Scale))));
                }
                i.M = m;
                double x = m[12], y = m[13], z = m[14];
                double r = Math.Sqrt(x * x + y * y + z * z);
                i.Lat = Math.Asin(y / r) * 180 / Math.PI;
                i.Lon = Math.Atan2(z, x) * 180 / Math.PI;
                i.Alt = r - radius;
                i.Placed = true;
                if (i.GroupRef != null) i.GroupRef.Count++;
            }
        }

        public static void PlaceGroup(KkGroup g, double radius, Func<double, double, double> ground)
        {
            var n = NVec(g.Lat, g.Lon);
            g.Alt = g.SeaLevel ? g.RadiusOffset : ground(g.Lat, g.Lon) + g.RadiusOffset;
            double[] rot;
            if (g.Heading < 361)
                rot = Mat.Mul(LookRotation(n), Mat.Mul(Mat.Rotate(Mat.Euler(new[] { 0, 0, g.Heading })), Mat.Rotate(Mat.Euler(new double[] { -90, -90, -90 }))));
            else
                rot = Mat.Rotate(Mat.QMul(FromTo(g.Up, n), AngleAxis(g.RotationAngle, g.Up)));
            g.M = Mat.Mul(Mat.Translate(Scale(n, radius + g.Alt)), Mat.Mul(rot, Mat.Scale(g.Scale, g.Scale, g.Scale)));
        }

        /* ------------------------------------------------ editing */

        /* East, north and vertical of a point, in KSP's frame. */
        public static (double[] E, double[] N, double[] U) Enu(double lat, double lon)
        {
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
            return (new[] { -Math.Sin(lo), 0, Math.Cos(lo) },
                    new[] { -Math.Sin(la) * Math.Cos(lo), Math.Cos(la), -Math.Sin(la) * Math.Sin(lo) },
                    NVec(lat, lon));
        }

        /* Moves an instance a few meters east, north and up (from where it is). What changes is
           its position within the group. */
        public static bool Move(KkInstance i, double dE, double dN, double dU)
        {
            var g = i.GroupRef;
            if (g?.M == null || i.Legacy || !i.Placed) return false;
            var (e, n, u) = Enu(i.Lat, i.Lon);
            var d = new double[3];
            for (int k = 0; k < 3; k++) d[k] = e[k] * dE + n[k] * dN + u[k] * dU;
            var local = ToLocal(g, d);
            for (int k = 0; k < 3; k++) i.Rel[k] += local[k];
            i.Cambiado = true;
            return true;
        }

        /* Rotates around the group's vertical (its Y axis), which is what Orientation's Y angle
           does. Positive: clockwise seen from above. */
        public static void Rotate(KkInstance i, double deg)
        {
            i.Euler[1] = ((i.Euler[1] + deg) % 360 + 360) % 360;
            i.Cambiado = true;
        }

        /* Heading the model faces (its Z axis), in degrees from north. */
        public static double Heading(KkInstance i)
        {
            if (i.M == null) return 0;
            var f = new[] { i.M[8], i.M[9], i.M[10] };
            var (e, n, _) = Enu(i.Lat, i.Lon);
            double h = Math.Atan2(f[0] * e[0] + f[1] * e[1] + f[2] * e[2], f[0] * n[0] + f[1] * n[1] + f[2] * n[2]) * 180 / Math.PI;
            return (h + 360) % 360;
        }

        /* A vector from KSP's frame to the group's inner frame (no translation). */
        static double[] ToLocal(KkGroup g, double[] d)
        {
            // the columns of the group matrix are its axes, with their scale
            var r = new double[3];
            for (int c = 0; c < 3; c++)
            {
                double ax = g.M[c * 4], ay = g.M[c * 4 + 1], az = g.M[c * 4 + 2];
                double l2 = ax * ax + ay * ay + az * az;
                r[c] = (d[0] * ax + d[1] * ay + d[2] * az) / l2;
            }
            return r;
        }

        /* A new building at a point: it goes to the nearest group (within 25 km, like KK's) or
           to a new one, facing the given heading. */
        public KkInstance Add(KkModel model, string body, double radius, double lat, double lon, double alt, double heading,
                              Func<double, double, double> ground, string grupoNuevo)
        {
            var pos = Scale(NVec(lat, lon), radius + alt);
            KkGroup g = null;
            double mejor = 25000;
            foreach (var c in Groups.Values)
            {
                if (c.Body != body || c.M == null || c.Implicit) continue;
                double dx = c.M[12] - pos[0], dy = c.M[13] - pos[1], dz = c.M[14] - pos[2];
                double dd = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dd < mejor) { mejor = dd; g = c; }
            }
            if (g == null)
            {
                string nombre = grupoNuevo;
                for (int k = 1; Groups.ContainsKey(body + "_" + nombre); k++) nombre = grupoNuevo + "_" + k;
                g = new KkGroup { Name = nombre, Body = body, Lat = lat, Lon = lon, Heading = 0, Nuevo = true, Cambiado = true };
                Groups[g.Key] = g;
                PlaceGroup(g, radius, ground);
            }
            var d = new[] { pos[0] - g.M[12], pos[1] - g.M[13], pos[2] - g.M[14] };
            var i = new KkInstance
            {
                Model = model.Name, ModelRef = model, Body = body, Group = g.Name, GroupRef = g,
                Uuid = Guid.NewGuid().ToString(), Rel = ToLocal(g, d), Nuevo = true, Cambiado = true,
            };
            Instances.Add(i);
            PlaceOne(i, radius);
            // make it face the requested heading
            Rotate(i, heading - Heading(i));
            PlaceOne(i, radius);
            g.Count++;
            return i;
        }

        /* Copy of an instance a few meters to the east, to move it afterwards. */
        public KkInstance Duplicate(KkInstance src, double radius)
        {
            // a copy of a stock building is a normal KK instance with that model
            var i = new KkInstance
            {
                Model = src.Model, ModelRef = src.ModelRef, Body = src.Body, Group = src.Group, GroupRef = src.GroupRef,
                Uuid = Guid.NewGuid().ToString(), Rel = (double[])src.Rel.Clone(), Euler = (double[])src.Euler.Clone(),
                Scale = src.Scale, Visibility = src.Visibility, Nuevo = true, Cambiado = true,
            };
            Instances.Add(i);
            PlaceOne(i, radius);
            Move(i, 20, 0, 0);
            PlaceOne(i, radius);
            if (i.GroupRef != null) i.GroupRef.Count++;
            return i;
        }

        /* Repositions a single instance with its group already placed (after editing it). */
        public void PlaceOne(KkInstance i, double radius)
        {
            var g = i.GroupRef;
            if (g?.M == null || i.Legacy) return;
            var m = Mat.Mul(g.M, Mat.Mul(Mat.Translate(i.Rel),
                Mat.Mul(Mat.Rotate(Mat.Euler(i.Euler)), Mat.Scale(i.Scale, i.Scale, i.Scale))));
            i.M = m;
            double x = m[12], y = m[13], z = m[14];
            double r = Math.Sqrt(x * x + y * y + z * z);
            i.Lat = Math.Asin(y / r) * 180 / Math.PI;
            i.Lon = Math.Atan2(z, x) * 180 / Math.PI;
            i.Alt = r - radius;
            i.Placed = true;
        }

        /* ------------------------------------------------ Unity math */

        public static double[] NVec(double lat, double lon)
        {
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
            return new[] { Math.Cos(la) * Math.Cos(lo), Math.Sin(la), Math.Cos(la) * Math.Sin(lo) };
        }

        // Quaternion.LookRotation(f) with «up» as the Y axis: columns right, up, forward
        static double[] LookRotation(double[] f)
        {
            var r = Norm(Cross(new double[] { 0, 1, 0 }, f));
            var u = Cross(f, r);
            return new double[] { r[0], r[1], r[2], 0, u[0], u[1], u[2], 0, f[0], f[1], f[2], 0, 0, 0, 0, 1 };
        }

        static double[] AngleAxis(double deg, double[] axis)
        {
            var a = Norm(axis);
            double h = deg * Math.PI / 360, s = Math.Sin(h);
            return new[] { a[0] * s, a[1] * s, a[2] * s, Math.Cos(h) };
        }

        // Quaternion.FromToRotation: the shortest rotation taking «a» to «b»
        static double[] FromTo(double[] a, double[] b)
        {
            a = Norm(a); b = Norm(b);
            double d = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
            if (d < -0.999999)
            {
                var ax = Cross(new double[] { 1, 0, 0 }, a);
                if (Len(ax) < 1e-6) ax = Cross(new double[] { 0, 1, 0 }, a);
                return AngleAxis(180, ax);
            }
            var c = Cross(a, b);
            var q = new[] { c[0], c[1], c[2], 1 + d };
            double l = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            return new[] { q[0] / l, q[1] / l, q[2] / l, q[3] / l };
        }

        static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        static double Len(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        static double[] Norm(double[] v) { double l = Len(v); return l < 1e-12 ? new double[] { 0, 1, 0 } : new[] { v[0] / l, v[1] / l, v[2] / l }; }
        static double[] Scale(double[] v, double k) => new[] { v[0] * k, v[1] * k, v[2] * k };

        /* ------------------------------------------------ reading values */

        public static double D(string s, double def) =>
            double.TryParse(s?.Trim(), NumberStyles.Float, Inv, out double v) && double.IsFinite(v) ? v : def;

        static bool Bool(string s) => s != null && s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        public static double[] V(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var p = s.Split(',');
            if (p.Length < 3) return null;
            var v = new double[3];
            for (int k = 0; k < 3; k++) v[k] = D(p[k], double.NaN);
            return v.All(double.IsFinite) ? v : null;
        }
    }
}
