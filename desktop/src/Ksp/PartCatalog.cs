using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KerbinMaps.Ksp
{
    public sealed class ModelRef
    {
        public string Url;                       // relativa a GameData, sin «.mu»
        public double[] Pos = { 0, 0, 0 };
        public double[] Euler = { 0, 0, 0 };
        public double[] Scale = { 1, 1, 1 };
        public bool HasTransform;
        public readonly Dictionary<string, string> TextureSwap = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class B9Module
    {
        public string ModuleId;
        public readonly List<(string Name, List<string> Transforms)> Subtypes = new();
    }

    public sealed class PartDef
    {
        public string Name, UrlDir;
        public double RescaleFactor = 1.25;      // el valor por defecto de KSP
        public readonly List<ModelRef> Models = new();
        public readonly Dictionary<string, Dictionary<string, bool>> Variants = new(StringComparer.OrdinalIgnoreCase);
        public string BaseVariant;
        public readonly List<B9Module> B9 = new();
        /* Módulos que animan el modelo, con su configuración (animationName, pivotName...),
           en el mismo orden que en la partida. */
        public readonly List<(string Name, Dictionary<string, string> Values)> AnimModules = new();
    }

    /* Definición de cada pieza: qué modelos la forman y cómo cambian con sus variantes.
       Se lee de la caché de ModuleManager, que ya trae aplicados los parches de todos los
       mods (ReStock cambia los modelos de las piezas de serie, por ejemplo). Sin
       ModuleManager se leen los .cfg de GameData tal cual. */
    public sealed class PartCatalog
    {
        public readonly string GameData;
        readonly Dictionary<string, PartDef> parts = new(StringComparer.Ordinal);
        public bool FromCache { get; private set; }
        public int Count => parts.Count;

        PartCatalog(string gameData) { GameData = gameData; }

        /* En la partida los nombres llevan puntos donde los .cfg tienen guiones bajos. */
        public PartDef Find(string partName) =>
            partName != null && parts.TryGetValue(partName.Replace('.', '_'), out var d) ? d : null;

        public static PartCatalog Load(string gameData)
        {
            var cat = new PartCatalog(gameData);
            string cache = Path.Combine(gameData, "ModuleManager.ConfigCache");
            if (File.Exists(cache))
            {
                cat.FromCache = true;
                var root = ConfigNode.ParseFile(cache);
                foreach (var url in root.Children("UrlConfig"))
                {
                    string parent = url.Get("parentUrl") ?? "";
                    foreach (var p in url.Children("PART")) cat.Add(p, parent);
                }
            }
            else
            {
                foreach (var file in Directory.EnumerateFiles(gameData, "*.cfg", SearchOption.AllDirectories))
                {
                    ConfigNode root;
                    try { root = ConfigNode.ParseFile(file); } catch { continue; }
                    string rel = Path.GetRelativePath(gameData, file).Replace('\\', '/');
                    foreach (var p in root.Children("PART")) cat.Add(p, rel);
                }
            }
            return cat;
        }

        static double[] Vec(string s, int n, double def)
        {
            var r = Enumerable.Repeat(def, n).ToArray();
            if (string.IsNullOrWhiteSpace(s)) return r;
            var parts = s.Split(',');
            for (int i = 0; i < n && i < parts.Length; i++)
                if (double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) r[i] = v;
            return r;
        }

        void Add(ConfigNode p, string parentUrl)
        {
            string name = p.Get("name");
            if (string.IsNullOrEmpty(name)) return;
            string dir = parentUrl.Contains('/') ? parentUrl.Substring(0, parentUrl.LastIndexOf('/')) : "";
            var def = new PartDef { Name = name, UrlDir = dir };
            if (double.TryParse(p.Get("rescaleFactor"), NumberStyles.Float, CultureInfo.InvariantCulture, out double rf)) def.RescaleFactor = rf;

            foreach (var m in p.Children("MODEL"))
            {
                string model = m.Get("model");
                if (string.IsNullOrEmpty(model)) continue;
                var mr = new ModelRef
                {
                    Url = model.Trim(),
                    Pos = Vec(m.Get("position"), 3, 0),
                    Euler = Vec(m.Get("rotation"), 3, 0),
                    Scale = Vec(m.Get("scale"), 3, 1),
                    HasTransform = true
                };
                foreach (var t in m.GetAll("texture"))
                {
                    int comma = t.IndexOf(',');
                    if (comma > 0) mr.TextureSwap[t.Substring(0, comma).Trim()] = t.Substring(comma + 1).Trim();
                }
                def.Models.Add(mr);
            }
            if (def.Models.Count == 0)
            {
                /* Piezas antiguas con «mesh = model.mu»: KSP carga el modelo de la propia
                   carpeta de la pieza. */
                string mesh = p.Get("mesh");
                string file = string.IsNullOrEmpty(mesh) ? "model" : Path.GetFileNameWithoutExtension(mesh.Trim());
                def.Models.Add(new ModelRef { Url = (dir.Length > 0 ? dir + "/" : "") + file });
            }

            foreach (var mod in p.Children("MODULE"))
            {
                string mname = mod.Get("name");
                if (mname != null && Core.SaveFile.ModulosAnimados.Contains(mname))
                {
                    var vals = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var kv in mod.Values) if (!vals.ContainsKey(kv.Key)) vals[kv.Key] = kv.Value.Trim();
                    // las mallas de cada juego de nodos de una placa de motores, en orden
                    if (mname == "ModuleDynamicNodes")
                        vals["__meshes"] = string.Join(",", mod.Children("NODE_SET").Select(ns => (ns.Get("MeshTransform") ?? "").Trim()));
                    def.AnimModules.Add((mname, vals));
                }
                if (mname == "ModulePartVariants")
                {
                    def.BaseVariant = mod.Get("baseVariant");
                    foreach (var variant in mod.Children("VARIANT"))
                    {
                        string vn = variant.Get("name");
                        if (vn == null) continue;
                        var objs = new Dictionary<string, bool>(StringComparer.Ordinal);
                        foreach (var go in variant.Children("GAMEOBJECTS"))
                            foreach (var kv in go.Values)
                                objs[kv.Key] = kv.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                        def.Variants[vn] = objs;
                        def.BaseVariant ??= vn;
                    }
                }
                else if (mname == "ModuleB9PartSwitch")
                {
                    var b9 = new B9Module { ModuleId = mod.Get("moduleID") };
                    foreach (var st in mod.Children("SUBTYPE"))
                        b9.Subtypes.Add((st.Get("name") ?? "", st.GetAll("transform").Select(x => x.Trim()).ToList()));
                    if (b9.Subtypes.Any(s => s.Transforms.Count > 0)) def.B9.Add(b9);
                }
            }
            parts[name] = def;
        }
    }
}
