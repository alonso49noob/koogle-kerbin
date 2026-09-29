using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KerbinMaps.Ksp
{
    /* Los edificios del KSC de serie, sacados de los datos del juego.

       No hay .mu: están dentro de KSP_x64_Data/sharedassets9.assets como prefabs de Unity
       (uno por nivel de cada instalación: RunwayLevel1..3, VABLevel1/2/4...), y el fichero
       no lleva la descripción de los tipos, así que cada clase se lee con su disposición
       de Unity 2019.4 escrita a mano: GameObject, Transform, MeshFilter, MeshRenderer,
       Material y Mesh. De cada prefab sale un AssembledVessel como el de un .mu, y lo
       pinta el mismo código que los edificios de Kerbal Konstructs.

       Qué hay y dónde va lo dice el prefab «KSC»: un hijo por instalación (Runway,
       LaunchPad, VehicleAssemblyBuilding...), con su posición dentro del centro espacial y
       un script UpgradeableFacility que apunta a los prefabs de sus niveles, en orden.
       KK llama a esos mismos prefabs «KSC_<instalación>_level_<n>»; también saca piezas
       sueltas de alguno (KSC_FuelTanks es el nodo ksp_pad_cylTank del hangar de nivel 3). */
    public sealed class StockPrefabs
    {
        const string Fichero = "sharedassets9.assets";

        static readonly Dictionary<string, StockPrefabs> porCarpeta = new(StringComparer.OrdinalIgnoreCase);

        readonly StockAssets assets;
        readonly UnitySerialized s;
        readonly object cerrojo = new();

        sealed class Go { public string Name; public bool Active; public List<(int F, long P)> Comps = new(); public long Tr; }
        sealed class Tr { public long Go; public double[] Rot = new double[4], Pos = new double[3], Scale = new double[3]; public List<long> Kids = new(); public long Father; }

        readonly Dictionary<long, Go> gos = new();
        readonly Dictionary<long, Tr> trs = new();
        readonly Dictionary<(UnitySerialized, long), MuMesh> mallas = new();

        /* Una instalación del KSC: su nombre, dónde está dentro del prefab KSC y sus niveles. */
        public sealed class Instalacion
        {
            public string Name;
            public double[] M;                    // dentro del KSC (marco de Unity)
            public long[] Niveles;                // GameObject raíz de cada nivel
        }

        public readonly List<Instalacion> Ksc = new();
        /* Modelos con nombre de KK («KSC_Runway_level_2», «KSC_FuelTanks»...) → nodo raíz. */
        public readonly Dictionary<string, long> PorNombreKK = new(StringComparer.OrdinalIgnoreCase);

        StockPrefabs(StockAssets assets, UnitySerialized s)
        {
            this.assets = assets;
            this.s = s;
            LeerJerarquia();
            LeerKsc();
        }

        public static StockPrefabs For(string gameData)
        {
            var a = StockAssets.For(gameData);
            if (a == null) return null;
            lock (porCarpeta)
            {
                if (porCarpeta.TryGetValue(a.Dir, out var p)) return p;
                try
                {
                    var s = a.Serializado(Fichero);
                    p = s == null ? null : new StockPrefabs(a, s);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[stock] prefabs: " + ex.Message);
                    p = null;
                }
                porCarpeta[a.Dir] = p;
                return p;
            }
        }

        /* ------------------------------------------------ jerarquía */

        void LeerJerarquia()
        {
            foreach (var (id, o) in s.Objetos)
            {
                if (o.ClassId == 1)
                {
                    var r = new R(s.Leer(id));
                    var g = new Go();
                    int n = r.I32();
                    for (int i = 0; i < n; i++) g.Comps.Add(r.PPtr());
                    r.U32();                              // capa
                    g.Name = r.Str();
                    r.Pos += 2;                           // etiqueta
                    g.Active = r.U8() != 0;
                    gos[id] = g;
                }
                else if (o.ClassId == 4)
                {
                    var r = new R(s.Leer(id));
                    var t = new Tr { Go = r.PPtr().P };
                    for (int i = 0; i < 4; i++) t.Rot[i] = r.F32();
                    for (int i = 0; i < 3; i++) t.Pos[i] = r.F32();
                    for (int i = 0; i < 3; i++) t.Scale[i] = r.F32();
                    int n = r.I32();
                    for (int i = 0; i < n; i++) t.Kids.Add(r.PPtr().P);
                    t.Father = r.PPtr().P;
                    trs[id] = t;
                }
            }
            foreach (var (id, t) in trs)
                if (gos.TryGetValue(t.Go, out var g)) g.Tr = id;
        }

        void LeerKsc()
        {
            long ksc = trs.Where(t => t.Value.Father == 0 && gos.TryGetValue(t.Value.Go, out var g) && g.Name == "KSC").Select(t => t.Key).FirstOrDefault();
            if (ksc == 0) return;
            var candidatos = trs[ksc].Kids.ToList();
            foreach (var k in trs[ksc].Kids)
                if (gos[trs[k].Go].Name == "Grounds") candidatos.AddRange(trs[k].Kids.Select(x => -x));   // negativo: bajo Grounds
            foreach (var c in candidatos)
            {
                bool bajoGrounds = c < 0;
                long t = Math.Abs(c);
                var go = gos[trs[t].Go];
                var niveles = new List<long>();
                foreach (var (f, p) in go.Comps)
                {
                    if (f != 0 || !s.Objetos.TryGetValue(p, out var o) || o.ClassId != 114) continue;
                    var d = s.Leer(p);
                    // m_GameObject, m_Enabled, m_Script y m_Name; luego los datos del script
                    int nl = BitConverter.ToInt32(d, 28);
                    int ini = 32 + ((nl + 3) & ~3);
                    for (int k = ini; k + 12 <= d.Length; k += 4)
                    {
                        long pid = BitConverter.ToInt64(d, k + 4);
                        if (BitConverter.ToInt32(d, k) == 0 && pid != 0 && gos.TryGetValue(pid, out var lg) && trs.TryGetValue(lg.Tr, out var lt) && lt.Father == 0)
                            niveles.Add(pid);
                    }
                }
                if (niveles.Count == 0) continue;
                var m = Local(t);
                if (bajoGrounds) m = Mat.Mul(Local(trs.First(x => x.Value.Kids.Contains(t)).Key), m);
                Ksc.Add(new Instalacion { Name = go.Name, M = m, Niveles = niveles.ToArray() });
                if (!bajoGrounds)
                    for (int i = 0; i < niveles.Count; i++) PorNombreKK["KSC_" + go.Name + "_level_" + (i + 1)] = niveles[i];
            }
            // las piezas sueltas que KK registra a partir del hangar de nivel 3
            if (PorNombreKK.TryGetValue("KSC_SpaceplaneHangar_level_3", out long sph))
            {
                foreach (var (nombreKK, nodo) in new[] { ("KSC_FuelTank", "Tank"), ("KSC_FuelTanks", "ksp_pad_cylTank"), ("KSC_WaterTower", "ksp_pad_waterTower") })
                    if (Buscar(gos[sph].Tr, nodo) is long g) PorNombreKK[nombreKK] = g;
            }
        }

        long? Buscar(long tr, string nombre)
        {
            var go = gos[trs[tr].Go];
            if (go.Name == nombre) return trs[tr].Go;
            foreach (var k in trs[tr].Kids)
                if (Buscar(k, nombre) is long r) return r;
            return null;
        }

        double[] Local(long tr)
        {
            var t = trs[tr];
            return Mat.Mul(Mat.Translate(t.Pos), Mat.Mul(Mat.Rotate(t.Rot), Mat.Scale(t.Scale[0], t.Scale[1], t.Scale[2])));
        }

        /* Un PPtr leído en `desde`: el fichero al que apunta y el objeto. fileID 0 es el propio;
           los demás, la tabla de externos (solo los ficheros del propio juego). */
        (UnitySerialized S, long Id)? Resolver(UnitySerialized desde, int fileId, long pathId)
        {
            if (pathId == 0) return null;
            if (fileId == 0) return (desde, pathId);
            if (fileId - 1 >= desde.Externos.Count) return null;
            string file = Path.GetFileName(desde.Externos[fileId - 1]);
            if (!file.EndsWith(".assets", StringComparison.OrdinalIgnoreCase) && !file.StartsWith("level", StringComparison.OrdinalIgnoreCase)) return null;
            var otro = assets.Serializado(file);
            return otro == null || !otro.Objetos.ContainsKey(pathId) ? null : (otro, pathId);
        }

        /* ------------------------------------------------ montaje */

        /* El modelo de un prefab (o de un nodo suyo), con la raíz en el origen: como en KK,
           de la raíz solo se conserva la escala. */
        public AssembledVessel Build(long rootGo)
        {
            lock (cerrojo)
            {
                var a = new AssembledVessel { PartsTotal = 1, Stock = assets };
                if (!gos.TryGetValue(rootGo, out var g) || !trs.TryGetValue(g.Tr, out var t)) return a;
                double r2 = 0;
                Walk(g.Tr, Mat.Scale(t.Scale[0], t.Scale[1], t.Scale[2]), a, ref r2, raiz: true);
                a.Radius = Math.Sqrt(r2);
                a.PartsDrawn = a.Items.Count > 0 ? 1 : 0;
                return a;
            }
        }

        void Walk(long tr, double[] m, AssembledVessel a, ref double r2, bool raiz)
        {
            var t = trs[tr];
            var go = gos[t.Go];
            if (!go.Active && !raiz) return;
            // por el nombre: colisionadores, sombras falsas y demás que no se ven
            string lower = go.Name.ToLowerInvariant();
            if (lower.Contains("collider") || lower.Contains("occlusion") || lower.Contains("_occluder") || lower.Contains("wreck")) return;

            MuMesh mesh = null;
            var mats = new List<(UnitySerialized, long)?>();
            bool visible = false;
            foreach (var (f, p) in go.Comps)
            {
                if (f != 0 || !s.Objetos.TryGetValue(p, out var o)) continue;
                if (o.ClassId == 33) mesh = Malla(p);
                else if (o.ClassId == 23) visible = Renderer(p, mats);
            }
            if (mesh != null && visible && mats.Count > 0)
            {
                for (int sm = 0; sm < mesh.Submeshes.Count; sm++)
                {
                    var mat = Material(mats[Math.Min(sm, mats.Count - 1)]);
                    if (mat == null || mat.Omitir) continue;
                    a.Items.Add(new DrawItem
                    {
                        Mesh = mesh, Submesh = sm, Part = go.Name, Node = go.Name, Shader = mat.Nombre, M = m,
                        TexturePath = mat.Tex, Color = mat.Color, TexScale = mat.Escala, TexOffset = mat.Desfase,
                        Cutout = mat.Recorte, Transparent = mat.Transparente, Ground = mat.Suelo,
                    });
                }
                var vv = mesh.Verts;
                for (int i = 0; i + 2 < vv.Length; i += 3 * Math.Max(1, vv.Length / 3000))
                {
                    var q = Mat.Apply(m, vv[i], vv[i + 1], vv[i + 2]);
                    r2 = Math.Max(r2, q[0] * q[0] + q[1] * q[1] + q[2] * q[2]);
                }
            }
            foreach (var k in t.Kids) Walk(k, Mat.Mul(m, Local(k)), a, ref r2, raiz: false);
        }

        /* MeshRenderer: si está activo y sus materiales. */
        bool Renderer(long id, List<(UnitySerialized, long)?> mats)
        {
            var r = new R(s.Leer(id));
            r.PPtr();                                     // m_GameObject
            bool enabled = r.U8() != 0;
            r.Pos += 7;                                   // sombras, sondas, vectores de movimiento, trazado de rayos
            r.Align();
            r.U32();                                      // m_RenderingLayerMask
            r.I32();                                      // m_RendererPriority
            r.Pos += 4;                                   // índices de lightmap
            r.Pos += 32;                                  // desplazamientos de lightmap
            int n = r.I32();
            if (n < 0 || n > 64) return false;
            for (int i = 0; i < n; i++)
            {
                var (f, p) = r.PPtr();
                mats.Add(Resolver(s, f, p));
            }
            return enabled;
        }

        sealed class Mat_
        {
            public string Nombre, Tex;
            public float[] Color = { 1, 1, 1, 1 }, Escala = { 1, 1 }, Desfase = { 0, 0 };
            public bool Recorte, Transparente, Omitir;
            public GroundMat Suelo;
        }
        readonly Dictionary<(UnitySerialized, long), Mat_> materiales = new();

        Mat_ Material((UnitySerialized, long)? refm)
        {
            if (refm == null) return null;
            var (ms, id) = refm.Value;
            if (materiales.TryGetValue((ms, id), out var mm)) return mm;
            mm = null;
            try
            {
                var r = new R(ms.Leer(id));
                var m = new Mat_ { Nombre = r.Str() };
                r.PPtr();                                 // m_Shader
                string keywords = r.Str();
                r.U32();                                  // m_LightmapFlags
                r.U8(); r.U8(); r.Align();                // instancing, doble cara GI
                int cola = r.I32();                       // m_CustomRenderQueue
                int tags = r.I32();
                var tagMap = new Dictionary<string, string>();
                for (int i = 0; i < tags; i++) tagMap[r.Str()] = r.Str();
                int dis = r.I32();
                for (int i = 0; i < dis; i++) r.Str();
                int nt = r.I32();
                for (int i = 0; i < nt; i++)
                {
                    string nombre = r.Str();
                    var (f, p) = r.PPtr();
                    float sx = r.F32(), sy = r.F32(), ox = r.F32(), oy = r.F32();
                    if (nombre == "_MainTex" && p != 0)
                    {
                        m.Tex = Textura(ms, f, p);
                        m.Escala = new[] { sx, sy };
                        m.Desfase = new[] { ox, oy };
                    }
                    else if (nombre == "_NearGrassTexture" && p != 0) (m.Suelo ??= new GroundMat()).Grass = Textura(ms, f, p);
                    else if (nombre == "_TarmacTexture" && p != 0)
                    {
                        m.Suelo ??= new GroundMat();
                        m.Suelo.Tarmac = Textura(ms, f, p);
                        m.Suelo.TarmacScale = new[] { sx, sy };
                        m.Suelo.TarmacOffset = new[] { ox, oy };
                    }
                    else if (nombre == "_BlendMaskTexture" && p != 0)
                    {
                        m.Suelo ??= new GroundMat();
                        m.Suelo.Mask = Textura(ms, f, p);
                        m.Suelo.MaskScale = new[] { sx, sy, ox, oy };
                    }
                }
                int nf = r.I32();
                for (int i = 0; i < nf; i++)
                {
                    string nombre = r.Str();
                    float v = r.F32();
                    if (nombre == "_Cutoff" && v > 0) m.Recorte = true;
                    if (nombre == "_NearGrassTiling") (m.Suelo ??= new GroundMat()).GrassTiling = v;
                }
                int nc = r.I32();
                for (int i = 0; i < nc; i++)
                {
                    string nombre = r.Str();
                    float cr = r.F32(), cg = r.F32(), cb = r.F32(), ca = r.F32();
                    if (nombre == "_Color") m.Color = new[] { cr, cg, cb, ca };
                    else if (nombre == "_GrassColor") (m.Suelo ??= new GroundMat()).GrassColor = new[] { cr, cg, cb, ca };
                    else if (nombre == "_TarmacColor") (m.Suelo ??= new GroundMat()).TarmacColor = new[] { cr, cg, cb, ca };
                }
                string lower = (m.Nombre ?? "").ToLowerInvariant();
                m.Transparente = cola >= 3000 || (tagMap.TryGetValue("RenderType", out var rt) && rt == "Transparent");
                if (m.Transparente) m.Recorte = false;
                if (m.Suelo?.Grass == null) m.Suelo = null;
                else { m.Recorte = false; m.Color = new float[] { 1, 1, 1, 1 }; }   // el suelo ni recorta ni se tiñe con _Color
                // halos, destellos y luces falsas: solo tienen sentido con sus shaders
                m.Omitir = lower.Contains("flare") || lower.Contains("glow") || lower.Contains("light_beam") || lower.Contains("lightbeam")
                           || lower.Contains("shadow") || lower.Contains("occlu") || keywords.Contains("PARTICLE", StringComparison.OrdinalIgnoreCase);
                mm = m;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[stock] material " + id + ": " + ex.Message); }
            materiales[(ms, id)] = mm;
            return mm;
        }

        string Textura(UnitySerialized ms, int fileId, long pathId)
        {
            string file = fileId == 0 ? assets.NombreDe(ms) : fileId - 1 < ms.Externos.Count ? Path.GetFileName(ms.Externos[fileId - 1]) : null;
            return file == null ? null : StockAssets.Prefijo + file + ":" + pathId;
        }

        /* ------------------------------------------------ mallas */

        MuMesh Malla(long filterId)
        {
            var r = new R(s.Leer(filterId));
            r.PPtr();
            var (f, p) = r.PPtr();
            // las primitivas de Unity (en «unity default resources») no se leen
            if (Resolver(s, f, p) is not var (ms, id)) return null;
            if (mallas.TryGetValue((ms, id), out var m)) return m;
            try { m = LeerMalla(ms, id); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[stock] malla " + id + ": " + ex.Message); m = null; }
            mallas[(ms, id)] = m;
            return m;
        }

        MuMesh LeerMalla(UnitySerialized ms, long id)
        {
            var r = new R(ms.Leer(id));
            r.Str();                                      // nombre
            int nSub = r.I32();
            var subs = new List<(uint First, uint Count, int Topo, uint BaseV)>();
            for (int i = 0; i < nSub; i++)
            {
                uint firstByte = r.U32(), count = r.U32();
                int topo = r.I32();
                uint baseV = r.U32();
                r.U32(); r.U32();                         // firstVertex, vertexCount
                r.Pos += 24;                              // localAABB
                subs.Add((firstByte, count, topo, baseV));
            }
            // m_Shapes
            int nv = r.I32(); r.Pos += nv * 40;
            int ns = r.I32(); for (int i = 0; i < ns; i++) { r.Pos += 8; r.U8(); r.U8(); r.Align(); }
            int nch = r.I32(); for (int i = 0; i < nch; i++) { r.Str(); r.Pos += 12; }
            int nw = r.I32(); r.Pos += nw * 4;
            int nb = r.I32(); r.Pos += nb * 64;           // m_BindPose
            int nh = r.I32(); r.Pos += nh * 4;            // m_BoneNameHashes
            r.U32();                                      // m_RootBoneNameHash
            int naabb = r.I32(); r.Pos += naabb * 24;     // m_BonesAABB
            int nvw = r.I32(); r.Pos += nvw * 4;          // m_VariableBoneCountWeights
            int compresion = r.U8();
            r.U8(); r.U8(); r.U8(); r.Align();            // legible, conservar vértices e índices
            int indexFormat = r.I32();
            int nIdx = r.I32();
            byte[] idx = r.Bytes(nIdx);
            // m_VertexData
            uint vCount = r.U32();
            int nCanales = r.I32();
            var canales = new (byte Stream, byte Offset, byte Format, byte Dim)[nCanales];
            for (int i = 0; i < nCanales; i++) canales[i] = (r.U8(), r.U8(), r.U8(), r.U8());
            int nDatos = r.I32();
            byte[] datos = r.Bytes(nDatos);
            if (compresion != 0 || vCount == 0) return null;
            if (datos.Length == 0)
            {
                // el resto de la malla va por delante; los datos en el .resS, al final
                SaltarHastaStreamData(r);
                uint off = r.U32(), size = r.U32();
                string path = r.Str();
                if (size == 0) return null;
                datos = LeerRecurso(path, off, (int)size);
                if (datos == null) return null;
            }

            // dónde empieza cada flujo y su paso, como los reparte Unity
            int nStreams = canales.Length == 0 ? 0 : canales.Max(c => c.Stream) + 1;
            var stride = new int[nStreams];
            foreach (var c in canales)
                if ((c.Dim & 0xF) > 0) stride[c.Stream] = Math.Max(stride[c.Stream], c.Offset + TamFormato(c.Format) * (c.Dim & 0xF));
            var inicio = new int[nStreams];
            int acum = 0;
            for (int k = 0; k < nStreams; k++) { inicio[k] = acum; acum += (int)vCount * stride[k]; acum = (acum + 15) & ~15; }

            float[] Canal(int ch, int dimWanted)
            {
                if (ch >= canales.Length) return null;
                var c = canales[ch];
                int dim = c.Dim & 0xF;
                if (dim == 0) return null;
                var o = new float[vCount * dimWanted];
                int ts = TamFormato(c.Format);
                for (int v = 0; v < vCount; v++)
                {
                    int b = inicio[c.Stream] + v * stride[c.Stream] + c.Offset;
                    for (int k = 0; k < Math.Min(dim, dimWanted); k++)
                        o[v * dimWanted + k] = Leer(datos, b + k * ts, c.Format);
                }
                return o;
            }

            var mesh = new MuMesh { VertCount = (int)vCount, Verts = Canal(0, 3), Normals = Canal(1, 3), Uvs = Canal(4, 2), Uvs2 = Canal(5, 2) };
            if (mesh.Verts == null) return null;
            int isz = indexFormat == 1 ? 4 : 2;
            foreach (var (first, count, topo, baseV) in subs)
            {
                if (topo != 0) { mesh.Submeshes.Add(Array.Empty<int>()); continue; }   // solo triángulos
                var tri = new int[count];
                int f0 = (int)(first / isz);
                for (int k = 0; k < count; k++)
                {
                    int pos = (f0 + k) * isz;
                    if (pos + isz > idx.Length) { tri = tri.Take(k - k % 3).ToArray(); break; }
                    tri[k] = (int)((isz == 4 ? BitConverter.ToUInt32(idx, pos) : BitConverter.ToUInt16(idx, pos)) + baseV);
                }
                mesh.Submeshes.Add(tri);
            }
            return mesh;
        }

        /* Tras m_VertexData vienen la malla comprimida, la caja, las mallas de colisión y las
           métricas; hay que pasar por encima de todo para llegar a m_StreamData. */
        static void SaltarHastaStreamData(R r)
        {
            for (int i = 0; i < 4; i++) SaltarPackedFloat(r);   // vértices, UV, normales y tangentes
            SaltarPackedInt(r);                                  // pesos
            SaltarPackedInt(r);                                  // signos de normales
            SaltarPackedInt(r);                                  // signos de tangentes
            SaltarPackedFloat(r);                                // colores
            SaltarPackedInt(r);                                  // índices de huesos
            SaltarPackedInt(r);                                  // triángulos
            r.U32();                                             // m_UVInfo
            r.Pos += 24;                                         // m_LocalAABB
            r.I32();                                             // m_MeshUsageFlags
            r.Bytes(r.I32());                                    // colisión convexa
            r.Bytes(r.I32());                                    // colisión de triángulos
            r.Pos += 8;                                          // m_MeshMetrics
        }

        static void SaltarPackedFloat(R r) { r.U32(); r.F32(); r.F32(); r.Bytes(r.I32()); r.U8(); r.Align(); }
        static void SaltarPackedInt(R r) { r.U32(); r.Bytes(r.I32()); r.U8(); r.Align(); }

        byte[] LeerRecurso(string ruta, long off, int size) => assets.LeerRecurso(ruta, off, size);

        static int TamFormato(byte f) => f switch { 0 => 4, 1 => 2, 2 or 3 or 6 or 7 => 1, 4 or 5 or 8 or 9 => 2, _ => 4 };

        static float Leer(byte[] d, int o, byte f) => f switch
        {
            0 => BitConverter.ToSingle(d, o),
            1 => (float)BitConverter.ToHalf(d, o),
            2 => d[o] / 255f,
            3 => Math.Max((sbyte)d[o] / 127f, -1f),
            4 => BitConverter.ToUInt16(d, o) / 65535f,
            5 => Math.Max(BitConverter.ToInt16(d, o) / 32767f, -1f),
            6 => d[o],
            7 => (sbyte)d[o],
            8 => BitConverter.ToUInt16(d, o),
            9 => BitConverter.ToInt16(d, o),
            10 => BitConverter.ToUInt32(d, o),
            _ => BitConverter.ToInt32(d, o),
        };

        /* Lector de los datos de un objeto, little endian y con el alineado de Unity. */
        sealed class R
        {
            readonly byte[] d;
            public int Pos;
            public R(byte[] data) { d = data; }
            public byte U8() => d[Pos++];
            public int I32() { int v = BitConverter.ToInt32(d, Pos); Pos += 4; return v; }
            public uint U32() { uint v = BitConverter.ToUInt32(d, Pos); Pos += 4; return v; }
            public float F32() { float v = BitConverter.ToSingle(d, Pos); Pos += 4; return v; }
            public (int F, long P) PPtr() { int f = I32(); long p = BitConverter.ToInt64(d, Pos); Pos += 8; return (f, p); }
            public void Align() => Pos = (Pos + 3) & ~3;
            public string Str() { int n = I32(); string v = Encoding.UTF8.GetString(d, Pos, n); Pos += n; Align(); return v; }
            public byte[] Bytes(int n) { var o = new byte[n]; Buffer.BlockCopy(d, Pos, o, 0, n); Pos += n; Align(); return o; }
        }
    }
}
