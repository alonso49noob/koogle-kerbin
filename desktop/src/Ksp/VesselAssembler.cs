using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KerbinMaps.Core;

namespace KerbinMaps.Ksp
{
    /* A piece of mesh with its material, placed in the vessel's frame (meters, Unity's
       left-handed frame, with the root part at the origin). */
    public sealed class DrawItem
    {
        public MuMesh Mesh;
        public int Submesh;
        public string Part, Node, Shader;
        public double[] M;                        // 4x4 column-major
        public string TexturePath;
        public float[] Color, TexScale, TexOffset;
        public bool Cutout, Transparent;
        public GroundMat Ground;                  // KSC ground: grass and asphalt blended by a mask
    }

    /* The game's «Diffuse Ground KSC» shader: grass tiled by position, tinted, and asphalt by
       UV, blended with a mask that follows the mesh's UVs. */
    public sealed class GroundMat
    {
        public string Grass, Tarmac, Mask;
        public float GrassTiling = 0.3f;
        public float[] GrassColor = { 0.58f, 0.61f, 0.39f, 1 }, TarmacColor = { 1, 1, 1, 1 }, TarmacScale = { 1, 1 }, TarmacOffset = { 0, 0 };
        public float[] MaskScale = { 1, 1, 0, 0 };        // mask scale and offset
    }

    public sealed class AssembledVessel
    {
        public readonly List<DrawItem> Items = new();
        public double Radius;                    // radius of the enclosing sphere, from the center of mass
        public double[] CoM = { 0, 0, 0 };
        public double[] Rot = { 0, 0, 0, 1 };
        public int PartsTotal, PartsDrawn;
        public readonly List<string> Missing = new();
        // animations and pivot rotations applied according to the saved state
        public int AnimsApplied, PivotsApplied;
        public readonly List<string> AnimsMissing = new(), PivotsMissing = new();
        /* Textures already read from disk, so the GPU upload doesn't have to touch it. */
        public Dictionary<string, TextureFile> Textures;
        public StockAssets Stock;                 // for the textures that are in the game's data

        public void LoadTextures()
        {
            var t = new Dictionary<string, TextureFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Items.SelectMany(it => new[] { it.TexturePath, it.Ground?.Grass, it.Ground?.Tarmac, it.Ground?.Mask }))
            {
                if (path == null || t.ContainsKey(path)) continue;
                try
                {
                    t[path] = path.StartsWith(StockAssets.Prefijo, StringComparison.Ordinal)
                        ? Stock?.Load(path)
                        : TextureFile.Load(path);
                }
                catch { t[path] = null; }       // e.g. the Crunch masks, which aren't read yet
            }
            Textures = t;
        }
    }

    /* Assembles a vessel part by part, just as KSP builds it when loading: each part at its
       position and rotation relative to the root; inside it, the «model» object scaled by
       rescaleFactor; inside that, each MODEL with its own position, rotation and scale; and
       inside, the .mu hierarchy. Variants turn off the objects that don't belong. */
    public sealed class VesselAssembler
    {
        readonly PartCatalog catalog;
        readonly Dictionary<string, MuFile> muCache = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> texCache = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Errors = new();

        readonly StockAssets stock;

        public VesselAssembler(PartCatalog catalog, StockAssets stock = null) { this.catalog = catalog; this.stock = stock; }

        MuFile Mu(string url)
        {
            lock (muCache)
            {
                if (muCache.TryGetValue(url, out var mu)) return mu;
                string path = Path.Combine(catalog.GameData, url.Replace('/', Path.DirectorySeparatorChar) + ".mu");
                try
                {
                    if (!File.Exists(path))
                    {
                        // «mesh = model.mu» with another name: the first .mu in the folder
                        string dir = Path.GetDirectoryName(path);
                        path = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.mu").FirstOrDefault() : null;
                    }
                    mu = path != null ? MuFile.Load(path) : null;
                    if (mu == null) Errors[url] = "no existe el modelo";
                }
                catch (Exception ex)
                {
                    Errors[url] = ex.Message;
                    mu = null;
                }
                muCache[url] = mu;
                return mu;
            }
        }

        public AssembledVessel Build(Vessel v, Action<int, int> progress = null)
        {
            var result = new AssembledVessel { CoM = v.CoM, Rot = v.Rot, PartsTotal = v.Parts.Count };
            double r2 = 0;
            int done = 0;
            foreach (var part in v.Parts)
            {
                progress?.Invoke(done++, v.Parts.Count);
                var def = catalog.Find(part.Name);
                if (def == null) { result.Missing.Add(part.Name); continue; }

                // which objects this part's variants turn off or on
                var off = new HashSet<string>(StringComparer.Ordinal);
                var on = new HashSet<string>(StringComparer.Ordinal);
                string variant = part.Variant ?? def.BaseVariant;
                if (variant != null && def.Variants.TryGetValue(variant, out var objs))
                    foreach (var kv in objs) (kv.Value ? on : off).Add(kv.Key);
                foreach (var b9 in def.B9)
                {
                    string current = part.B9.FirstOrDefault(x => x.ModuleId == b9.ModuleId).Subtype
                                     ?? (def.B9.Count == 1 && part.B9.Count == 1 ? part.B9[0].Subtype : null)
                                     ?? b9.Subtypes.FirstOrDefault().Name;
                    var keep = b9.Subtypes.FirstOrDefault(s => s.Name == current).Transforms ?? new List<string>();
                    foreach (var s in b9.Subtypes)
                        foreach (var t in s.Transforms)
                            if (!keep.Contains(t)) off.Add(t);
                    foreach (var t in keep) { off.Remove(t); on.Add(t); }
                }

                // what the saved state hides regardless of the variants
                var hide = new HashSet<string>(StringComparer.Ordinal);
                var (anims, pivots) = SavedPose(def, part, hide);

                var partM = Mat.Mul(Mat.Translate(part.Pos), Mat.Rotate(part.Rot));
                var modelM = Mat.Mul(partM, Mat.Scale(def.RescaleFactor, def.RescaleFactor, def.RescaleFactor));
                bool any = false;
                var animsFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pivotsFound = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mr in def.Models)
                {
                    var mu = Mu(mr.Url);
                    if (mu == null) continue;
                    string muDir = Path.GetDirectoryName(Path.Combine(catalog.GameData, mr.Url.Replace('/', Path.DirectorySeparatorChar)));
                    var ov = Overrides(mu, anims, pivots, animsFound, pivotsFound);
                    double[] rootM = mr.HasTransform
                        ? Mat.Mul(modelM, Mat.Mul(Mat.Translate(mr.Pos), Mat.Mul(Mat.Rotate(Mat.Euler(mr.Euler)), Mat.Scale(mr.Scale[0], mr.Scale[1], mr.Scale[2]))))
                        : Mat.Mul(modelM, Trs(mu.Root, ov));
                    // the .mu root takes the MODEL node's transform; its children, their own
                    var world = WorldMatrices(mu.Root, rootM, ov);
                    Walk(mu, mu.Root, world, mr, muDir, off, on, hide, result, ref r2, ref any, isRoot: true);
                }
                foreach (var (clip, _) in anims)
                    if (animsFound.Contains(clip)) result.AnimsApplied++; else result.AnimsMissing.Add(part.Name + ": " + clip);
                foreach (var (node, _) in pivots)
                    if (pivotsFound.Contains(node)) result.PivotsApplied++; else result.PivotsMissing.Add(part.Name + ": " + node);
                if (any) result.PartsDrawn++;
            }
            result.Radius = Math.Sqrt(r2);
            return result;
        }

        /* A standalone model, like Kerbal Konstructs buildings: the .mu hierarchy with the root
           at the origin. KK places and rotates the root on its own and only keeps its scale, so
           that's all that's taken from it. */
        public static AssembledVessel BuildModel(string muPath, StockAssets stock = null)
        {
            var mu = MuFile.Load(muPath);
            var result = new AssembledVessel { PartsTotal = 1, Stock = stock };
            if (mu?.Root == null) return result;
            var asm = new VesselAssembler(null, stock);
            var mr = new ModelRef { Url = Path.GetFileNameWithoutExtension(muPath) };
            var s = mu.Root.Scale;
            var world = WorldMatrices(mu.Root, Mat.Scale(s[0], s[1], s[2]), new Dictionary<MuNode, float[]>());
            var nada = new HashSet<string>();
            double r2 = 0;
            bool any = false;
            asm.Walk(mu, mu.Root, world, mr, Path.GetDirectoryName(muPath), nada, nada, nada, result, ref r2, ref any, isRoot: true);
            result.Radius = Math.Sqrt(r2);
            result.PartsDrawn = any ? 1 : 0;
            return result;
        }

        static double ParseD(string s, double def) =>
            double.TryParse(s?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;

        static double[] ParseQuat(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var p = s.Trim().TrimStart('(').TrimEnd(')').Split(',');
            if (p.Length != 4) return null;
            var q = new double[4];
            for (int i = 0; i < 4; i++) q[i] = ParseD(p[i], double.NaN);
            double n = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            return double.IsFinite(n) && n > 0.5 ? q : null;
        }

        /* The part's saved pose: which animations are at which point (0 = retracted, 1 =
           deployed) and which pivots are rotated, as they were when saved. The save's modules
           are matched with the configuration's by name and order of appearance. */
        static (List<(string Clip, double T)> anims, List<(string Node, double[] Q)> pivots) SavedPose(PartDef def, PartSnapshot part, HashSet<string> hide)
        {
            var anims = new List<(string, double)>();
            var pivots = new List<(string, double[])>();
            var seen = new Dictionary<string, int>();
            foreach (var (name, cfg) in def.AnimModules)
            {
                int nth = seen.GetValueOrDefault(name);
                seen[name] = nth + 1;
                var saved = part.Modules.Where(m => m.Name == name).Skip(nth).Select(m => m.Fields).FirstOrDefault();
                if (saved == null) continue;
                static bool True(string s) => string.Equals(s, "True", StringComparison.OrdinalIgnoreCase);
                switch (name)
                {
                    case "ModuleJettison":
                    {
                        /* An engine shroud: it disappears when jettisoned, and also isn't shown
                           if there's nothing attached below. */
                        var names = (cfg.GetValueOrDefault("jettisonName") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        string active = saved.GetValueOrDefault("activejettisonName");
                        string bottom = cfg.GetValueOrDefault("bottomNodeName") ?? "bottom";
                        bool gone = True(saved.GetValueOrDefault("isJettisoned")) || True(saved.GetValueOrDefault("shroudHideOverride")) || !part.Attached.Contains(bottom);
                        if (gone)
                            foreach (var n in names)
                                if (string.IsNullOrEmpty(active) || n == active) hide.Add(n);
                        break;
                    }
                    case "ModuleStructuralNode":
                        // interstage structures: they only exist if the game has generated them
                        if (cfg.TryGetValue("rootObject", out var ro) && ro.Length > 0 &&
                            (!True(saved.GetValueOrDefault("spawnState")) || saved.GetValueOrDefault("visibilityState") == "False"))
                            hide.Add(ro);
                        break;
                    case "ModuleDynamicNodes":
                    {
                        // engine plates: the mesh of the chosen node set and no other
                        var meshes = (cfg.GetValueOrDefault("__meshes") ?? "").Split(',');
                        int idx = (int)ParseD(saved.GetValueOrDefault("NodeSetIdx"), 0);
                        for (int i = 0; i < meshes.Length; i++)
                            if (i != idx && meshes[i].Length > 0) hide.Add(meshes[i]);
                        break;
                    }
                    case "ModuleProceduralFairing":
                        break;
                    case "ModuleParachute":
                    case "RealChuteFAR":
                    {
                        /* The model comes with the canopy open. Packed, only the cap shows; on
                           opening the cap flies off and the canopy takes its animation pose;
                           when cut, neither remains. */
                        string st = (saved.GetValueOrDefault("deploymentState") ?? saved.GetValueOrDefault("depState") ?? "STOWED").ToUpperInvariant();
                        string canopy = cfg.GetValueOrDefault("canopyName") ?? "canopy";
                        string cap = cfg.GetValueOrDefault("capName") ?? "cap";
                        string semi = cfg.GetValueOrDefault("semiDeployedAnimation");
                        string full = cfg.GetValueOrDefault("fullyDeployedAnimation");
                        switch (st)
                        {
                            case "SEMIDEPLOYED":
                            case "PREDEPLOYED":
                            case "LOWDEPLOYED":
                                hide.Add(cap);
                                if (!string.IsNullOrEmpty(semi)) anims.Add((semi, 1));
                                break;
                            case "DEPLOYED":
                                hide.Add(cap);
                                if (!string.IsNullOrEmpty(full)) anims.Add((full, 1));
                                break;
                            case "CUT":
                                hide.Add(cap);
                                hide.Add(canopy);
                                break;
                            default:                 // STOWED, ACTIVE: waiting to open
                                hide.Add(canopy);
                                break;
                        }
                        break;
                    }
                    case "ModuleAnimateGeneric":
                        if (cfg.TryGetValue("animationName", out var ag) && ag.Length > 0)
                            anims.Add((ag, Math.Clamp(ParseD(saved.GetValueOrDefault("animTime"), 0), 0, 1)));
                        break;
                    case "ModuleWheelDeployment":
                        if (cfg.TryGetValue("animationStateName", out var aw) && aw.Length > 0)
                            anims.Add((aw, Math.Clamp(ParseD(saved.GetValueOrDefault("position"), 0), 0, 1)));
                        break;
                    case "ModuleAnimationGroup":
                        if (cfg.TryGetValue("deployAnimationName", out var ad) && ad.Length > 0)
                            anims.Add((ad, saved.GetValueOrDefault("isDeployed")?.Equals("True", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0));
                        break;
                    default:
                    {
                        // panels, antennas, radiators, reflectors and the rest of ModuleDeployablePart
                        string state = saved.GetValueOrDefault("deployState") ?? "RETRACTED";
                        double t = state switch
                        {
                            "EXTENDED" => 1,
                            "BROKEN" => 1,
                            "RETRACTED" => 0,
                            _ => Math.Clamp(ParseD(saved.GetValueOrDefault("storedAnimationTime"), 0), 0, 1)
                        };
                        if (cfg.TryGetValue("animationName", out var an) && an.Length > 0) anims.Add((an, t));
                        if (state == "EXTENDED")
                        {
                            string pivot = cfg.GetValueOrDefault("pivotName") ?? (name == "ModuleDeployableSolarPanel" ? "sunPivot" : null);
                            var q = ParseQuat(saved.GetValueOrDefault("currentRotation"));
                            if (pivot != null && q != null) pivots.Add((pivot, q));
                        }
                        // a broken panel has lost the part that breaks off
                        if (state == "BROKEN" && cfg.TryGetValue("breakName", out var br) && br.Length > 0) hide.Add(br);
                        break;
                    }
                }
            }
            return (anims, pivots);
        }

        public static MuNode Resolve(MuNode owner, string path)
        {
            if (string.IsNullOrEmpty(path)) return owner;
            var node = owner;
            foreach (var name in path.Split('/'))
            {
                node = node.Children.FirstOrDefault(c => c.Name == name);
                if (node == null) return null;
            }
            return node;
        }

        static MuNode FindNode(MuNode n, string name)
        {
            if (n.Name == name) return n;
            foreach (var c in n.Children)
            {
                var r = FindNode(c, name);
                if (r != null) return r;
            }
            return null;
        }

        static IEnumerable<MuNode> AllNodes(MuNode n)
        {
            yield return n;
            foreach (var c in n.Children)
                foreach (var d in AllNodes(c)) yield return d;
        }

        /* Local position, rotation and scale that change relative to the model at rest: first
           the animations evaluated at their point, then the pivots with their saved rotation. */
        static Dictionary<MuNode, float[]> Overrides(MuFile mu, List<(string Clip, double T)> anims, List<(string Node, double[] Q)> pivots,
                                                     HashSet<string> animsFound, HashSet<string> pivotsFound)
        {
            var ov = new Dictionary<MuNode, float[]>();
            if (anims.Count == 0 && pivots.Count == 0) return ov;

            float[] Get(MuNode n)
            {
                if (!ov.TryGetValue(n, out var o))
                    ov[n] = o = new[] { n.Pos[0], n.Pos[1], n.Pos[2], n.Rot[0], n.Rot[1], n.Rot[2], n.Rot[3], n.Scale[0], n.Scale[1], n.Scale[2] };
                return o;
            }

            foreach (var owner in AllNodes(mu.Root))
            {
                if (owner.Clips == null) continue;
                foreach (var (clipName, t) in anims)
                {
                    var clip = owner.Clips.FirstOrDefault(c => string.Equals(c.Name, clipName, StringComparison.OrdinalIgnoreCase));
                    if (clip == null) continue;
                    animsFound.Add(clipName);
                    float time = (float)(t * clip.Length);
                    var euler = new Dictionary<MuNode, double[]>();
                    foreach (var curve in clip.Curves)
                    {
                        var target = Resolve(owner, curve.Path);
                        if (target == null) continue;
                        float v = curve.Eval(time);
                        int idx = curve.Property switch
                        {
                            "m_LocalPosition.x" => 0, "m_LocalPosition.y" => 1, "m_LocalPosition.z" => 2,
                            "m_LocalRotation.x" => 3, "m_LocalRotation.y" => 4, "m_LocalRotation.z" => 5, "m_LocalRotation.w" => 6,
                            "m_LocalScale.x" => 7, "m_LocalScale.y" => 8, "m_LocalScale.z" => 9,
                            "localEulerAnglesRaw.x" or "localEulerAngles.x" or "localEulerAnglesBaked.x" => 10,
                            "localEulerAnglesRaw.y" or "localEulerAngles.y" or "localEulerAnglesBaked.y" => 11,
                            "localEulerAnglesRaw.z" or "localEulerAngles.z" or "localEulerAnglesBaked.z" => 12,
                            _ => -1
                        };
                        if (idx < 0) continue;
                        if (idx >= 10)
                        {
                            if (!euler.TryGetValue(target, out var e)) euler[target] = e = new double[3];
                            e[idx - 10] = v;
                        }
                        else Get(target)[idx] = v;
                    }
                    foreach (var (target, e) in euler)
                    {
                        var q = Mat.Euler(e);
                        var o = Get(target);
                        o[3] = (float)q[0]; o[4] = (float)q[1]; o[5] = (float)q[2]; o[6] = (float)q[3];
                    }
                }
            }

            foreach (var (name, q) in pivots)
            {
                var node = FindNode(mu.Root, name);
                if (node == null) continue;
                pivotsFound.Add(name);
                var o = Get(node);
                o[3] = (float)q[0]; o[4] = (float)q[1]; o[5] = (float)q[2]; o[6] = (float)q[3];
            }
            return ov;
        }

        static double[] Trs(MuNode n, Dictionary<MuNode, float[]> ov)
        {
            if (!ov.TryGetValue(n, out var o)) return Mat.Trs(n);
            // between two keys each component is interpolated separately: it has to be renormalized
            double qn = Math.Sqrt((double)o[3] * o[3] + (double)o[4] * o[4] + (double)o[5] * o[5] + (double)o[6] * o[6]);
            var q = qn > 1e-9 ? new[] { o[3] / qn, o[4] / qn, o[5] / qn, o[6] / qn } : new double[] { 0, 0, 0, 1 };
            return Mat.Mul(Mat.Translate(new double[] { o[0], o[1], o[2] }),
                   Mat.Mul(Mat.Rotate(q), Mat.Scale(o[7], o[8], o[9])));
        }

        /* Matrix of each model object in the vessel's frame. They're all computed before
           drawing: a skinned mesh needs its bones', which can hang from another branch or from
           a hidden one. */
        static Dictionary<MuNode, double[]> WorldMatrices(MuNode root, double[] rootM, Dictionary<MuNode, float[]> ov)
        {
            var w = new Dictionary<MuNode, double[]>();
            void Go(MuNode n, double[] m)
            {
                w[n] = m;
                foreach (var c in n.Children) Go(c, Mat.Mul(m, Trs(c, ov)));
            }
            Go(root, rootM);
            return w;
        }

        /* Skinned mesh (panels that deploy by folding, masts): each vertex is carried by its bones
           as Unity does, Σ weight · (bone now × bind pose) · vertex, and comes out already in the
           vessel's frame. The .mu bind poses are row-major. */
        static MuMesh Skin(MuFile mu, MuNode node, Dictionary<MuNode, double[]> world)
        {
            var src = node.Mesh;
            int nb = node.Bones?.Length ?? 0, nv = src.VertCount;
            if (nb == 0 || src.BoneWeightsRaw == null || src.BindPoses == null || src.BindPoses.Length < nb * 16 || src.BoneWeightsRaw.Length < nv * 32) return null;

            var byName = new Dictionary<string, MuNode>(StringComparer.Ordinal);
            foreach (var n in AllNodes(mu.Root)) byName.TryAdd(n.Name, n);
            var skin = new double[nb][];
            for (int b = 0; b < nb; b++)
            {
                if (!byName.TryGetValue(node.Bones[b], out var bn) || !world.TryGetValue(bn, out var bw)) return null;
                var bind = new double[16];
                for (int j = 0; j < 16; j++) bind[(j % 4) * 4 + j / 4] = src.BindPoses[b * 16 + j];
                skin[b] = Mat.Mul(bw, bind);
            }

            var own = world[node];
            var raw = src.BoneWeightsRaw;
            var outV = new float[nv * 3];
            var outN = src.Normals != null ? new float[nv * 3] : null;
            for (int i = 0; i < nv; i++)
            {
                double x = src.Verts[i * 3], y = src.Verts[i * 3 + 1], z = src.Verts[i * 3 + 2];
                double nx0 = outN != null ? src.Normals[i * 3] : 0, ny0 = outN != null ? src.Normals[i * 3 + 1] : 0, nz0 = outN != null ? src.Normals[i * 3 + 2] : 0;
                double px = 0, py = 0, pz = 0, nx = 0, ny = 0, nz = 0, ws = 0;
                for (int k = 0; k < 4; k++)
                {
                    float w = BitConverter.ToSingle(raw, i * 32 + k * 8 + 4);
                    int idx = BitConverter.ToInt32(raw, i * 32 + k * 8);
                    if (w <= 0 || (uint)idx >= (uint)nb) continue;
                    var s = skin[idx];
                    px += w * (s[0] * x + s[4] * y + s[8] * z + s[12]);
                    py += w * (s[1] * x + s[5] * y + s[9] * z + s[13]);
                    pz += w * (s[2] * x + s[6] * y + s[10] * z + s[14]);
                    nx += w * (s[0] * nx0 + s[4] * ny0 + s[8] * nz0);
                    ny += w * (s[1] * nx0 + s[5] * ny0 + s[9] * nz0);
                    nz += w * (s[2] * nx0 + s[6] * ny0 + s[10] * nz0);
                    ws += w;
                }
                if (ws <= 1e-6)
                {
                    // no weights: it stays attached to the object
                    var p = Mat.Apply(own, x, y, z);
                    px = p[0]; py = p[1]; pz = p[2];
                    nx = own[0] * nx0 + own[4] * ny0 + own[8] * nz0;
                    ny = own[1] * nx0 + own[5] * ny0 + own[9] * nz0;
                    nz = own[2] * nx0 + own[6] * ny0 + own[10] * nz0;
                }
                else if (Math.Abs(ws - 1) > 1e-3) { px /= ws; py /= ws; pz /= ws; }
                outV[i * 3] = (float)px; outV[i * 3 + 1] = (float)py; outV[i * 3 + 2] = (float)pz;
                if (outN != null)
                {
                    double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (l > 1e-12) { nx /= l; ny /= l; nz /= l; }
                    outN[i * 3] = (float)nx; outN[i * 3 + 1] = (float)ny; outN[i * 3 + 2] = (float)nz;
                }
            }
            var mesh = new MuMesh { VertCount = nv, Verts = outV, Normals = outN, Uvs = src.Uvs, Uvs2 = src.Uvs2 };
            mesh.Submeshes.AddRange(src.Submeshes);
            return mesh;
        }

        void Walk(MuFile mu, MuNode node, Dictionary<MuNode, double[]> world, ModelRef mr, string muDir, HashSet<string> off, HashSet<string> on, HashSet<string> hide,
                  AssembledVessel result, ref double r2, ref bool any, bool isRoot)
        {
            // whatever is tagged «Icon_Only» is the silhouette for the editor icon (the sample fairing, for example)
            if (node.Tag == "Icon_Only") return;
            if (!isRoot && (hide.Contains(node.Name) || (off.Contains(node.Name) && !on.Contains(node.Name)))) return;

            var m = world[node];
            var mesh = node.Mesh;
            if (mesh?.Verts != null && node.Bones != null && Skin(mu, node, world) is MuMesh skinned)
            {
                mesh = skinned;
                m = Mat.Identity();
            }

            if (mesh?.Verts != null && node.Materials != null)
            {
                for (int s = 0; s < mesh.Submeshes.Count; s++)
                {
                    int mi = node.Materials.Length == 0 ? -1 : node.Materials[Math.Min(s, node.Materials.Length - 1)];
                    var mat = mi >= 0 && mi < mu.Materials.Count ? mu.Materials[mi] : null;
                    if (mat != null && (mat.Shader ?? "").Contains("Particle")) continue;   // effects, not hull
                    if (mat != null && mat.Shader != null && (mat.Shader == "legacy14" || mat.Shader == "legacy15")) continue;
                    result.Items.Add(new DrawItem
                    {
                        Mesh = mesh,
                        Submesh = s,
                        Part = mr.Url,
                        Node = node.Name,
                        Shader = mat?.Shader,
                        M = m,
                        TexturePath = mat != null && mat.MainTex >= 0 && mat.MainTex < mu.Textures.Count ? ResolveTexture(mu.Textures[mat.MainTex].Name, mr, muDir) : null,
                        Color = mat?.Color ?? new float[] { 1, 1, 1, 1 },
                        TexScale = mat?.TexScale ?? new float[] { 1, 1 },
                        TexOffset = mat?.TexOffset ?? new float[] { 0, 0 },
                        Cutout = mat?.Cutout ?? false,
                        Transparent = mat?.Transparent ?? false
                    });
                    any = true;
                }
                var vv = mesh.Verts;
                for (int i = 0; i < vv.Length; i += 3 * Math.Max(1, vv.Length / 3000))
                {
                    var p = Mat.Apply(m, vv[i], vv[i + 1], vv[i + 2]);
                    r2 = Math.Max(r2, p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
                }
            }
            foreach (var c in node.Children)
                Walk(mu, c, world, mr, muDir, off, on, hide, result, ref r2, ref any, isRoot: false);
        }

        string ResolveTexture(string name, ModelRef mr, string muDir)
        {
            string bare = Path.GetFileNameWithoutExtension(name);
            string key = muDir + "|" + bare + "|" + mr.Url;
            lock (texCache)
            {
                if (texCache.TryGetValue(key, out var cached)) return cached;
                string found = null;
                if (mr.TextureSwap.TryGetValue(bare, out var swap))
                    found = TextureFile.Find(Path.Combine(catalog.GameData, swap.Replace('/', Path.DirectorySeparatorChar)));
                found ??= TextureFile.Find(Path.Combine(muDir, bare));
                // the stock KSC ones that Kerbal Konstructs buildings reuse
                found ??= stock?.Find(name);
                texCache[key] = found;
                return found;
            }
        }
    }

    /* 4x4 double-precision matrices, column-major, with Unity's convention. */
    public static class Mat
    {
        public static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public static double[] Mul(double[] a, double[] b)
        {
            var r = new double[16];
            for (int c = 0; c < 4; c++)
                for (int row = 0; row < 4; row++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += a[k * 4 + row] * b[c * 4 + k];
                    r[c * 4 + row] = s;
                }
            return r;
        }

        public static double[] Translate(double[] p) =>
            new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, p[0], p[1], p[2], 1 };

        public static double[] Scale(double x, double y, double z) =>
            new double[] { x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1 };

        public static double[] Rotate(double[] q)
        {
            double x = q[0], y = q[1], z = q[2], w = q[3];
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n < 1e-12) return Identity();
            x /= n; y /= n; z /= n; w /= n;
            return new double[]
            {
                1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w), 0,
                2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w), 0,
                2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y), 0,
                0, 0, 0, 1
            };
        }

        /* Unity's Quaternion.Euler: first Z, then X, then Y. */
        public static double[] Euler(double[] deg)
        {
            double D = Math.PI / 360;
            double cx = Math.Cos(deg[0] * D), sx = Math.Sin(deg[0] * D);
            double cy = Math.Cos(deg[1] * D), sy = Math.Sin(deg[1] * D);
            double cz = Math.Cos(deg[2] * D), sz = Math.Sin(deg[2] * D);
            var qx = new[] { sx, 0, 0, cx };
            var qy = new[] { 0, sy, 0, cy };
            var qz = new[] { 0, 0, sz, cz };
            return QMul(qy, QMul(qx, qz));
        }

        public static double[] QMul(double[] a, double[] b) => new[]
        {
            a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
            a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
            a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]
        };

        public static double[] Trs(MuNode n) =>
            Mul(Translate(new double[] { n.Pos[0], n.Pos[1], n.Pos[2] }),
                Mul(Rotate(new double[] { n.Rot[0], n.Rot[1], n.Rot[2], n.Rot[3] }), Scale(n.Scale[0], n.Scale[1], n.Scale[2])));

        /* General inverse by cofactors (column-major, like everything here). */
        public static double[] Inverse(double[] m)
        {
            var inv = new double[16];
            inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
            inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
            inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
            inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
            inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
            inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
            inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
            inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
            inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
            inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
            inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
            inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
            inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
            inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
            inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
            inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
            double det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
            if (Math.Abs(det) < 1e-18) return null;
            for (int i = 0; i < 16; i++) inv[i] /= det;
            return inv;
        }

        public static double[] Apply(double[] m, double x, double y, double z) => new[]
        {
            m[0] * x + m[4] * y + m[8] * z + m[12],
            m[1] * x + m[5] * y + m[9] * z + m[13],
            m[2] * x + m[6] * y + m[10] * z + m[14]
        };
    }
}
