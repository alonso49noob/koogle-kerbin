using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KerbinMaps.Ksp
{
    /* Material de un scatter de Parallax: la textura, el color por el que se multiplica y
       las palabras clave del shader que cambian cómo se pinta (recorte por alfa, dos caras,
       siempre de cara a la cámara...). */
    public sealed class ScatterMaterial
    {
        public string Shader = "Custom/ParallaxInstancedSolid";
        public string MainTex;
        public float[] Color = { 1, 1, 1 };
        public float Cutoff = 0.5f;
        public float Tiling = 1;                      // solo el biplanar: repeticiones por metro
        public float[] SubsurfaceColor = { 0, 0, 0 };
        public float SubsurfaceIntensity, SubsurfacePower = 1;
        public int CullMode = 2;
        public HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase);

        public bool AlphaCutoff => Keywords.Contains("ALPHA_CUTOFF");
        public bool TwoSided => CullMode == 0 || Keywords.Contains("TWO_SIDED");
        public bool Billboard => Keywords.Contains("BILLBOARD") || Keywords.Contains("BILLBOARD_USE_MESH_NORMALS");
        public bool BillboardMeshNormals => Keywords.Contains("BILLBOARD_USE_MESH_NORMALS");
        public bool Subsurface => Keywords.Contains("SUBSURFACE_SCATTERING") || Keywords.Contains("SUBSURFACE_USE_THICKNESS_TEXTURE");
        public bool Biplanar => Shader.Contains("Biplanar", StringComparison.OrdinalIgnoreCase);
        // las burbujas de Eve refractan lo que tienen detrás: eso no se reproduce
        public bool Unsupported => Shader.Contains("Bubble", StringComparison.OrdinalIgnoreCase);

        public ScatterMaterial Clone()
        {
            var m = (ScatterMaterial)MemberwiseClone();
            m.Color = (float[])Color.Clone();
            m.SubsurfaceColor = (float[])SubsurfaceColor.Clone();
            m.Keywords = new HashSet<string>(Keywords, StringComparer.OrdinalIgnoreCase);
            return m;
        }

        /* Aplica lo que diga el nodo; lo que no diga se queda. Así sirve igual para un
           material completo que para un MaterialOverride. */
        public void Apply(ConfigNode n)
        {
            if (n == null) return;
            if (n.Get("shader") is string sh) Shader = sh;
            if (n.Get("_MainTex") is string t) MainTex = t;
            if (Vec(n.Get("_Color")) is float[] c) Color = c;
            if (Vec(n.Get("_SubsurfaceColor")) is float[] sc) SubsurfaceColor = sc;
            Cutoff = F(n, "_Cutoff", Cutoff);
            Tiling = F(n, "_Tiling", Tiling);
            SubsurfaceIntensity = F(n, "_SubsurfaceIntensity", SubsurfaceIntensity);
            SubsurfacePower = F(n, "_SubsurfacePower", SubsurfacePower);
            CullMode = (int)F(n, "_CullMode", CullMode);
            var kw = n.Children("Keywords").FirstOrDefault();
            if (kw != null) Keywords = new HashSet<string>(kw.GetAll("name").Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        }

        static float F(ConfigNode n, string k, float def) =>
            float.TryParse(n.Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;

        internal static float[] Vec(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var p = s.Split(',');
            if (p.Length < 3) return null;
            var v = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
            return v;
        }
    }

    /* Un nivel de detalle: qué modelo se pinta desde qué distancia, y con qué material. */
    public sealed class ScatterLevel
    {
        public string Model;
        public double From;                           // metros desde los que se usa
        public ScatterMaterial Material;
    }

    /* Un scatter de Parallax para un cuerpo: la hierba, un tipo de árbol, unas rocas. Cómo
       se reparte lo dice su nodo Distribution; los números se usan como los usa el mod
       (ver ScatterField). Un SharedScatter no tiene reparto propio: va en los mismos
       sitios que su padre (las copas de los árboles, sobre sus troncos). */
    public sealed class ScatterDef
    {
        public string Name, Parent;
        public readonly List<ScatterLevel> Levels = new();

        public double Seed, SpawnChance = 1, Range = 100, Population = 1;
        public float[] MinScale = { 1, 1, 1 }, MaxScale = { 1, 1, 1 };
        public double ScaleRandomness, CutoffScale, SteepPower = 1, SteepContrast = 1, SteepMidpoint = 0.5;
        public double MaxNormalDeviance = 1, MinAltitude = double.MinValue, MaxAltitude = double.MaxValue, AltitudeFadeRange;
        public bool AlignToNormal, ColoredByTerrain;
        public double? PlacementAltitude;
        public HashSet<string> BiomeBlacklist = new(StringComparer.OrdinalIgnoreCase);

        public string NoiseType = "simplexPerlin";
        public bool NoiseInverted;
        public double NoiseFrequency = 1, NoiseLacunarity = 2, NoiseSeed;
        public int NoiseOctaves = 1;

        public readonly int[] MaxObjects = { int.MaxValue, int.MaxValue, int.MaxValue };

        public bool Shared => Parent != null;
    }

    /* Malla de un modelo de scatter, lista para subir: posiciones, normales y UV en el marco
       de Unity (y hacia arriba), con los índices de todas sus submallas juntos. */
    public sealed class ScatterMesh
    {
        public float[] Pos, Nrm, Uv;
        public int[] Idx;
        public float Radius, Top;                     // radio en planta y altura, sin escalar
    }

    /* Todo lo que hace falta para pintar los scatters de un cuerpo. */
    public sealed class ScatterAssets
    {
        public List<ScatterDef> Defs = new();
        public readonly Dictionary<string, ScatterMesh> Meshes = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, TextureFile> Textures = new(StringComparer.OrdinalIgnoreCase);
    }

    public static class ParallaxScatters
    {
        public const string Carpeta = "Parallax_StockScatterTextures";

        /* La carpeta del mod, en el KSP del jugador o en lo que haya bajado el instalador. */
        public static string FindDir(string gameData) =>
            ParallaxPlanets.GameDatas(gameData)
                .Select(gd => Path.Combine(gd, Carpeta))
                .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Configs")) && Directory.GetFiles(d, "*.unity3d").Length > 0);

        /* Lee las definiciones del cuerpo y carga sus modelos y texturas. Devuelve null si
           el mod no está o ese cuerpo no tiene scatters. */
        public static ScatterAssets Load(string gameData, string body, int maxAncho = 1024)
        {
            string dir = FindDir(gameData);
            if (dir == null) return null;
            var defs = Defs(dir, body);
            if (defs.Count == 0) return null;
            string raiz = Path.GetDirectoryName(dir);         // el GameData donde está el mod
            var a = new ScatterAssets { Defs = defs };

            foreach (var lv in defs.SelectMany(d => d.Levels))
            {
                if (lv.Model == null || a.Meshes.ContainsKey(lv.Model)) continue;
                string ruta = Path.Combine(raiz, lv.Model.Replace('/', Path.DirectorySeparatorChar) + ".mu");
                try { a.Meshes[lv.Model] = File.Exists(ruta) ? Malla(MuFile.Load(ruta)) : null; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[scatters] " + lv.Model + ": " + ex.Message);
                    a.Meshes[lv.Model] = null;
                }
            }

            string paquete = Directory.GetFiles(dir, "*.unity3d").FirstOrDefault();
            using var ub = new UnityBundle(paquete);
            var cab = ub.Nodos.First(n => !n.Path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                                       && !n.Path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));
            var sf = new UnitySerialized(ub, cab.Offset, cab.Size);
            (long, long)? Recurso(string p)
            {
                string nombre = p.Substring(p.LastIndexOf('/') + 1);
                var n = ub.Nodos.FirstOrDefault(x => x.Path.Equals(nombre, StringComparison.OrdinalIgnoreCase));
                return n.Path == null ? null : (n.Offset, n.Size);
            }
            foreach (var lv in defs.SelectMany(d => d.Levels))
            {
                string t = lv.Material?.MainTex;
                if (t == null || a.Textures.ContainsKey(t)) continue;
                var id = sf.Buscar(t);
                try { a.Textures[t] = id == null ? null : ParallaxTerrain.Convertir(sf.LeerTextura(id.Value, Recurso), maxAncho); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[scatters] " + t + ": " + ex.Message);
                    a.Textures[t] = null;
                }
            }
            return a;
        }

        /* Las definiciones de un cuerpo, de todos los .cfg del mod. */
        public static List<ScatterDef> Defs(string dir, string body)
        {
            var res = new List<ScatterDef>();
            foreach (var f in Directory.GetFiles(Path.Combine(dir, "Configs"), "*.cfg", SearchOption.AllDirectories))
            {
                ConfigNode raiz;
                try { raiz = ConfigNode.ParseFile(f); }
                catch { continue; }
                foreach (var ps in Todos(raiz, "ParallaxScatters"))
                    foreach (var b in ps.Children("Body"))
                    {
                        if (!string.Equals(b.Get("name"), body, StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var n in b.Nodes)
                        {
                            string tipo = n.Name.Trim();
                            if (tipo != "Scatter" && tipo != "SharedScatter") continue;
                            try { res.Add(Leer(n, tipo == "SharedScatter")); }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[scatters] " + n.Get("name") + ": " + ex.Message); }
                        }
                    }
            }
            // los compartidos heredan del padre el reparto y las distancias de los niveles
            foreach (var d in res.Where(d => d.Shared).ToList())
            {
                var p = res.FirstOrDefault(x => !x.Shared && x.Name == d.Parent);
                if (p == null) { res.Remove(d); continue; }
                d.Range = p.Range;
                for (int i = 0; i < d.Levels.Count && i < p.Levels.Count; i++) d.Levels[i].From = p.Levels[i].From;
            }
            return res;
        }

        // el primer nodo de algunos ficheros lleva delante la marca de orden de bytes
        static IEnumerable<ConfigNode> Todos(ConfigNode n, string nombre)
        {
            if (n.Name != null && n.Name.Trim('﻿', ' ') == nombre) yield return n;
            foreach (var c in n.Nodes)
                foreach (var x in Todos(c, nombre)) yield return x;
        }

        static ScatterDef Leer(ConfigNode n, bool shared)
        {
            var d = new ScatterDef { Name = n.Get("name"), Parent = shared ? n.Get("parentName") : null };
            var mat = new ScatterMaterial();
            mat.Apply(n.Children("Material").FirstOrDefault());
            d.Levels.Add(new ScatterLevel { Model = n.Get("model"), From = 0, Material = mat });

            var dist = n.Children("Distribution").FirstOrDefault();
            if (dist != null)
            {
                d.Seed = D(dist, "seed", 0);
                d.SpawnChance = D(dist, "spawnChance", 1);
                d.Range = D(dist, "range", 100);
                d.Population = D(dist, "populationMultiplier", 1);
                d.MinScale = ScatterMaterial.Vec(dist.Get("minScale")) ?? d.MinScale;
                d.MaxScale = ScatterMaterial.Vec(dist.Get("maxScale")) ?? d.MaxScale;
                d.ScaleRandomness = D(dist, "scaleRandomness", 0);
                d.CutoffScale = D(dist, "cutoffScale", 0);
                d.SteepPower = D(dist, "steepPower", 1);
                d.SteepContrast = D(dist, "steepContrast", 1);
                d.SteepMidpoint = D(dist, "steepMidpoint", 0.5);
                d.MaxNormalDeviance = D(dist, "maxNormalDeviance", 1);
                d.MinAltitude = D(dist, "minAltitude", double.MinValue);
                d.MaxAltitude = D(dist, "maxAltitude", double.MaxValue);
                d.AltitudeFadeRange = D(dist, "altitudeFadeRange", 0);
                d.AlignToNormal = B(dist, "alignToTerrainNormal");
                d.ColoredByTerrain = B(dist, "coloredByTerrain");
                if (dist.Get("placementAltitude") != null) d.PlacementAltitude = D(dist, "placementAltitude", 0);
                foreach (var bl in dist.Children("BiomeBlacklist"))
                    foreach (var name in bl.GetAll("name")) d.BiomeBlacklist.Add(name.Trim());

                foreach (var lods in dist.Children("LODs"))
                    foreach (var lod in lods.Children("LOD"))
                    {
                        // un Material completo sustituye al base; un MaterialOverride lo retoca
                        var m = mat.Clone();
                        var full = lod.Children("Material").FirstOrDefault();
                        if (full != null) { m = new ScatterMaterial(); m.Apply(full); }
                        m.Apply(lod.Children("MaterialOverride").FirstOrDefault());
                        d.Levels.Add(new ScatterLevel { Model = lod.Get("model"), From = D(lod, "range", 0), Material = m });
                    }
            }

            var noise = n.Children("DistributionNoise").FirstOrDefault();
            if (noise != null)
            {
                d.NoiseType = noise.Get("noiseType") ?? d.NoiseType;
                d.NoiseInverted = B(noise, "inverted");
                d.NoiseFrequency = D(noise, "frequency", 1);
                d.NoiseOctaves = (int)D(noise, "octaves", 1);
                d.NoiseLacunarity = D(noise, "lacunarity", 2);
                d.NoiseSeed = D(noise, "seed", 0);
            }

            var opt = n.Children("Optimizations").FirstOrDefault();
            if (opt != null)
                for (int i = 0; i < 3; i++)
                {
                    double v = D(opt, "maxRenderableObjectsLOD" + i, 0);
                    if (v > 0) d.MaxObjects[i] = (int)v;
                }
            return d;
        }

        static double D(ConfigNode n, string k, double def) =>
            double.TryParse(n.Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;

        static bool B(ConfigNode n, string k) => string.Equals(n.Get(k)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        /* Todas las mallas del modelo en una. La raíz se deja en el origen (Parallax coloca
           el modelo por su malla, no por dónde quedó el objeto al exportarlo); los hijos, con
           su posición, giro y escala respecto a ella. */
        static ScatterMesh Malla(MuFile mu)
        {
            var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var idx = new List<int>();
            void Recorrer(MuNode n, List<MuNode> cadena)
            {
                if (n.Mesh != null && n.Mesh.Verts != null)
                {
                    var m = n.Mesh;
                    int base0 = pos.Count / 3;
                    for (int i = 0; i < m.VertCount; i++)
                    {
                        var p = new[] { m.Verts[i * 3], m.Verts[i * 3 + 1], m.Verts[i * 3 + 2] };
                        var q = m.Normals != null ? new[] { m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2] } : new float[] { 0, 1, 0 };
                        for (int k = cadena.Count - 1; k >= 0; k--)
                        {
                            var c = cadena[k];
                            p = Rotar(c.Rot, new[] { p[0] * c.Scale[0], p[1] * c.Scale[1], p[2] * c.Scale[2] });
                            p[0] += c.Pos[0]; p[1] += c.Pos[1]; p[2] += c.Pos[2];
                            q = Rotar(c.Rot, q);
                        }
                        pos.AddRange(p); nrm.AddRange(q);
                        uv.Add(m.Uvs != null ? m.Uvs[i * 2] : 0); uv.Add(m.Uvs != null ? m.Uvs[i * 2 + 1] : 0);
                    }
                    foreach (var sm in m.Submeshes) foreach (int k in sm) idx.Add(base0 + k);
                }
                foreach (var c in n.Children)
                {
                    cadena.Add(c);
                    Recorrer(c, cadena);
                    cadena.RemoveAt(cadena.Count - 1);
                }
            }
            Recorrer(mu.Root, new List<MuNode>());
            if (idx.Count == 0) return null;

            var s = new ScatterMesh { Pos = pos.ToArray(), Nrm = nrm.ToArray(), Uv = uv.ToArray(), Idx = idx.ToArray() };
            for (int i = 0; i < s.Pos.Length; i += 3)
            {
                s.Radius = Math.Max(s.Radius, MathF.Sqrt(s.Pos[i] * s.Pos[i] + s.Pos[i + 2] * s.Pos[i + 2]));
                s.Top = Math.Max(s.Top, Math.Abs(s.Pos[i + 1]));
            }
            return s;
        }

        static float[] Rotar(float[] q, float[] v)
        {
            float x = q[0], y = q[1], z = q[2], w = q[3];
            // v + 2w(q×v) + 2 q×(q×v)
            float cx = y * v[2] - z * v[1], cy = z * v[0] - x * v[2], cz = x * v[1] - y * v[0];
            float ccx = y * cz - z * cy, ccy = z * cx - x * cz, ccz = x * cy - y * cx;
            return new[] { v[0] + 2 * (w * cx + ccx), v[1] + 2 * (w * cy + ccy), v[2] + 2 * (w * cz + ccz) };
        }
    }
}
