using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;

namespace KerbinMaps.Views
{
    /* Mallas y texturas de los scatters de un cuerpo, ya en la GPU. */
    public sealed class ScatterGpu : IDisposable
    {
        public sealed class Mesh
        {
            public uint Vbo, Ebo;
            public int Count;
            public float Radius, Top;
        }

        public readonly Dictionary<string, Mesh> Meshes = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Texture> Textures = new(StringComparer.OrdinalIgnoreCase);
        public List<ScatterDef> Defs;

        /* Sube lo cargado. Se llama con el contexto de OpenGL activo. */
        public static ScatterGpu Upload(ScatterAssets a)
        {
            var g = new ScatterGpu { Defs = a.Defs };
            foreach (var (k, m) in a.Meshes)
            {
                if (m == null) continue;
                int n = m.Pos.Length / 3;
                var v = new float[n * 8];
                for (int i = 0; i < n; i++)
                {
                    v[i * 8] = m.Pos[i * 3]; v[i * 8 + 1] = m.Pos[i * 3 + 1]; v[i * 8 + 2] = m.Pos[i * 3 + 2];
                    v[i * 8 + 3] = m.Nrm[i * 3]; v[i * 8 + 4] = m.Nrm[i * 3 + 1]; v[i * 8 + 5] = m.Nrm[i * 3 + 2];
                    v[i * 8 + 6] = m.Uv[i * 2]; v[i * 8 + 7] = m.Uv[i * 2 + 1];
                }
                var mesh = new Mesh { Vbo = GL.GenBuffer(), Ebo = GL.GenBuffer(), Count = m.Idx.Length, Radius = m.Radius, Top = m.Top };
                GL.BindBuffer(GL.ARRAY_BUFFER, mesh.Vbo);
                GL.BufferData(GL.ARRAY_BUFFER, v, v.Length, GL.STATIC_DRAW);
                GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, mesh.Ebo);
                GL.BufferData(GL.ELEMENT_ARRAY_BUFFER, m.Idx, m.Idx.Length, GL.STATIC_DRAW);
                g.Meshes[k] = mesh;
            }
            GL.BindBuffer(GL.ARRAY_BUFFER, 0);
            GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, 0);
            foreach (var (k, t) in a.Textures)
            {
                if (t == null || t.Levels.Count == 0) continue;
                try
                {
                    g.Textures[k] = t.CompressedFormat != 0
                        ? Texture.FromCompressed(t.CompressedFormat, t.Width, t.Height, t.Levels)
                        : Texture.FromRgba(t.Levels[0], t.Width, t.Height, TexFilter.Mipmap, true, true);   // el viento se lee fuera de [0, 1]
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[scatters] textura " + k + ": " + ex.Message); }
            }
            return g;
        }

        public void Dispose()
        {
            foreach (var m in Meshes.Values) { GL.DeleteBuffer(m.Vbo); GL.DeleteBuffer(m.Ebo); }
            Meshes.Clear();
            foreach (var t in Textures.Values) t.Dispose();
            Textures.Clear();
        }
    }

    /* Scatters de Parallax en la vista de vuelo y en la del cielo: hierba, flores,
       arbustos, árboles, cactus y rocas, con los modelos y las texturas del mod.

       El suelo se traza por rayos y no tiene malla ni búfer de profundidad propio, así que
       el shader del cielo escribe en él la distancia a la que choca cada rayo y los modelos
       se pintan encima con prueba de profundidad: una colina tapa los árboles de detrás. La
       profundidad va en escala logarítmica en los dos sitios; con la lineal habitual, a diez
       kilómetros el búfer de 24 bits no distingue 30 m, y aquí distingue milímetros.

       Cada fotograma se eligen, de lo que ha generado ScatterField, los objetos que caen en
       el campo de visión y a qué distancia están, para pintar cada nivel de detalle con su
       modelo: la hierba de cerca con todas sus briznas, la de lejos como un cartel. Los
       límites de objetos por nivel son los del propio mod. */
    public sealed partial class GlobeView
    {
        public ScatterField ScatterField;
        public ScatterGpu ScatterGpu;
        public bool Scatters = true;
        public double ScatterDensity = 1;
        public int ScatterVisible { get; private set; }
        public int MsaaSamples;                   // muestras del antialias: el alfa se vuelve cobertura
        public bool Wind = true;

        /* Con viento hay que pintar sin parar mientras se vea algo que se mueva. */
        public bool WindAnimating => Wind && ScattersActive && ScatterWindy > 0;
        int ScatterWindy;                         // de los visibles, los que mueve el viento

        static readonly System.Diagnostics.Stopwatch relojViento = System.Diagnostics.Stopwatch.StartNew();

        /* Hasta dónde llega la escala de profundidad (el scatter más lejano de Kerbin, los
           icebergs, llega a 20 km). */
        const double ScatterFar = 25000;
        const double ScatterNear = 0.05;

        ShaderProgram scatterProg;

        sealed class Lote
        {
            public uint Vao, Inst;
            public float[] Data = new float[12 * 256];
            public int N;
            public string MeshKey;
        }

        readonly Dictionary<(ScatterDef, int), Lote> lotes = new();

        bool ScattersActive => Scatters && ScatterField != null && ScatterGpu != null && HeightTex != null && FreeRelief
                               && (Mode == CamMode.Free || Mode == CamMode.Sky || PlanetaCerca);

        const string ScatterVS = Header + AtmosphereGlsl + @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNrm;
layout(location = 2) in vec2 aUv;
layout(location = 3) in vec4 aI0;       // posición respecto al ojo (m) y giro (grados)
layout(location = 4) in vec4 aI1;       // eje vertical y tamaño (0..1)
layout(location = 5) in vec4 aI2;       // color del terreno
uniform vec3 uF, uR, uU, uEyeR, uSun;
uniform float uTan, uAspect, uNear, uFar, uRadiusM;
uniform vec3 uMinScale, uMaxScale;
uniform int uBillboard;                 // 0 no; 1 cartel; 2 cartel con las normales de la malla
/* Viento, como lo hace Parallax (Wind en ParallaxScatterUtils.cginc): un mapa que se
   desplaza con el tiempo, leído en los tres planos del mundo segun la vertical, empuja
   cada vertice en horizontal (y un poco en vertical) segun su altura en el modelo. */
uniform int uWind;
uniform sampler2D uWindMap;
uniform vec3 uWindEye;                  // posicion del ojo por la escala del viento, sin la parte entera
uniform float uWindScale, uWindSpeed, uWindIntensity, uWindHS, uWindHF, uTime;
out vec2 vUv;
out vec3 vN, vRel, vCol, vIns, vTr, vLocal, vNLocal;
out float vZ;

void main() {
  vec3 up = normalize(aI1.xyz);
  vec3 sc = mix(uMinScale, uMaxScale, aI1.w);
  vec3 ref = abs(up.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
  vec3 t1 = normalize(cross(ref, up));
  vec3 t2 = cross(up, t1);
  float a = radians(aI0.w);
  vec3 X = cos(a) * t1 + sin(a) * t2;
  // el modelo viene de Unity, con ejes a izquierdas: así no sale en espejo
  vec3 Z = cross(up, X);
  vec3 lp = aPos * sc;
  vec3 ln = aNrm / max(sc, vec3(1e-4));
  if (uBillboard != 0) {
    // de cara a la cámara pero de pie, como hace Parallax con los carteles
    vec3 f = -(uF - up * dot(uF, up));
    f = length(f) > 1e-5 ? normalize(f) : t2;
    X = normalize(cross(up, f));
    Z = f;
    if (uBillboard == 1) ln = vec3(0.0, 0.0, 1.0);
  }
  vec3 w = aI0.xyz + X * lp.x + up * lp.y + Z * lp.z;
  if (uWind != 0 && aPos.y > uWindHS) {
    vec3 tw = abs(up);
    tw /= tw.x + tw.y + tw.z;
    vec3 c = uWindEye + w * uWindScale;
    float off = fract(uTime / 20.0 * uWindSpeed);
    vec3 m = textureLod(uWindMap, c.yz + off, 0.0).rgb * tw.x
           + textureLod(uWindMap, c.zx + off, 0.0).rgb * tw.y
           + textureLod(uWindMap, c.xy + off, 0.0).rgb * tw.z;
    vec3 dir = -normalize(vec3(1.0) - up * dot(vec3(1.0), up) + vec3(1e-5));
    float h = pow(aPos.y, uWindHF) * uWindIntensity;
    w += (dir * m + up * m * 0.4) * h;
  }
  vN = normalize(X * ln.x + up * ln.y + Z * ln.z);
  vRel = w;
  vUv = aUv;
  vCol = aI2.rgb;
  vLocal = lp;
  vNLocal = aNrm;
  float vx = dot(w, uR), vy = dot(w, uU), vz = dot(w, uF);
  vZ = vz;
  gl_Position = vec4(vx / (uTan * uAspect), vy / uTan, (vz * (uFar + uNear) - 2.0 * uFar * uNear) / (uFar - uNear), vz);

  // el aire entre el ojo y el objeto, como en el suelo (por vértice: a esta escala basta)
  vTr = vec3(1.0); vIns = vec3(0.0);
  float dist = length(w);
  if (uAtmos != 0 && dist > 1.0) {
    vec3 d = w / dist;
    vec2 ta = raySphere(uEyeR, d, ATM_TOP);
    vec3 tr;
    vIns = inscatter(uEyeR, d, max(ta.x, 0.0), dist / uRadiusM, uSun, 0.5, tr);
    vTr = tr;
  }
}";

        const string ScatterFS = Header + AtmosphereGlsl + @"
uniform vec3 uEyeR, uSun;
uniform float uRadiusM, uFar;
uniform sampler2D uTex;
uniform vec3 uColor;
uniform float uCutoff;                  // < 0: sin recorte por alfa
uniform int uTwoSided, uBiplanar, uA2C;
uniform float uTiling;
uniform vec4 uSub;                      // translucidez: color e intensidad
uniform float uSubPow;
in vec2 vUv;
in vec3 vN, vRel, vCol, vIns, vTr, vLocal, vNLocal;
in float vZ;
out vec4 frag;

void main() {
  vec4 tex;
  if (uBiplanar != 0) {
    // las rocas y los icebergs no traen UV útiles: la textura se proyecta por los tres ejes
    vec3 b = abs(normalize(vNLocal));
    b = pow(b, vec3(4.0));
    b /= max(b.x + b.y + b.z, 1e-4);
    vec3 q = vLocal * uTiling;
    tex = texture(uTex, q.yz) * b.x + texture(uTex, q.xz) * b.y + texture(uTex, q.xy) * b.z;
  } else tex = texture(uTex, vUv);
  float alpha = 1.0;
  if (uCutoff >= 0.0) {
    if (uA2C != 0) {
      // borde nítido y sin dientes: el alfa se convierte en cobertura de las muestras
      alpha = clamp((tex.a - uCutoff) / max(fwidth(tex.a), 1e-4) + 0.5, 0.0, 1.0);
      if (alpha <= 0.0) discard;
    } else if (tex.a < uCutoff) discard;
  }
  vec3 alb = tex.rgb * uColor * vCol;
  vec3 n = normalize(vN);
  if (uTwoSided != 0 && !gl_FrontFacing) n = -n;
  vec3 v = normalize(-vRel);
  vec3 pR = uEyeR + vRel / uRadiusM;
  vec3 albLin = pow(max(alb, vec3(0.0)), vec3(2.2));
  vec3 L = shadeGround(pR, n, v, uSun, albLin, 0.0);
  vec3 sol = uSunI * sunTransmittance(pR, uSun);
  if (uTwoSided != 0) {
    // hojas y briznas finas: la luz que les da por detras las atraviesa en parte, y la
    // cara en sombra no se queda negra
    L += albLin * 0.36 / PI * sol * max(-dot(n, uSun), 0.0) * 0.6;
  }
  if (uSub.w > 0.0) {
    // la luz que atraviesa las hojas cuando se mira hacia el Sol
    float t = pow(clamp(dot(-v, uSun), 0.0, 1.0), uSubPow);
    L += albLin * 0.36 * uSub.rgb * uSub.w * t * uSunI * sunTransmittance(pR, uSun) / PI;
  }
  vec3 col = L * vTr + vIns;
  frag = vec4(toneMap(col), alpha);
  gl_FragDepth = clamp(log2(1.0 + max(vZ, 0.0)) / log2(1.0 + uFar), 0.0, 1.0);
}";

        /* Pinta los scatters encima del suelo ya trazado. `eye` en radios del cuerpo. */
        void DrawScatters(double[] eye, double[] right, double[] camUp, double tan, double lat, double lon)
        {
            var field = ScatterField;
            var gpu = ScatterGpu;
            field.Request(lat, lon, Math.Max(0, EyeGround().Agl));
            scatterProg ??= new ShaderProgram(ScatterVS, ScatterFS);

            double R = Body.Radius;
            double ex = eye[0] * R, ey = eye[1] * R, ez = eye[2] * R;
            double aspect = (double)W / H;
            double tx = tan * aspect * 1.05, ty = tan * 1.05;
            double fx = fwdL[0], fy = fwdL[1], fz = fwdL[2];
            double rx = right[0], ry = right[1], rz = right[2];
            double ux = camUp[0], uy = camUp[1], uz = camUp[2];
            double densidad = Math.Clamp(ScatterDensity, 0, 1);

            // qué capa usa qué malla en cada nivel, y con qué tamaño máximo (para recortar)
            var capas = field.Layers.Where(l => gpu.Meshes.Count > 0).ToArray();
            var llenos = new (Lote[] lotes, int[] n)[capas.Length];
            for (int c = 0; c < capas.Length; c++)
            {
                var d = capas[c].Def;
                var arr = new Lote[d.Levels.Count];
                for (int k = 0; k < arr.Length; k++)
                {
                    var lv = d.Levels[k];
                    if (lv.Model == null || !gpu.Meshes.ContainsKey(lv.Model) || lv.Material.Unsupported) continue;
                    if (!lotes.TryGetValue((d, k), out var lote)) lotes[(d, k)] = lote = new Lote { MeshKey = lv.Model };
                    lote.N = 0;
                    arr[k] = lote;
                }
                llenos[c] = (arr, new int[arr.Length]);
            }

            Parallel.For(0, capas.Length, c =>
            {
                var l = capas[c];
                var d = l.Def;
                var fuente = (l.Parent ?? l).Cells;
                var arr = llenos[c].lotes;
                if (arr.All(x => x == null)) return;
                // los compartidos (las copas) van con el tamaño de su padre (el tronco)
                var esc = (l.Parent ?? l).Def;
                float smax = Math.Max(esc.MaxScale[0], Math.Max(esc.MaxScale[1], esc.MaxScale[2]));
                var radio = new double[arr.Length];
                for (int k = 0; k < arr.Length; k++)
                    if (arr[k] != null)
                    {
                        var m = gpu.Meshes[arr[k].MeshKey];
                        radio[k] = Math.Sqrt(m.Radius * m.Radius + m.Top * m.Top) * smax;
                    }
                double range = d.Range;
                foreach (var cell in fuente.Values)
                {
                    double cdx = cell.Cx - ex, cdy = cell.Cy - ey, cdz = cell.Cz - ez;
                    if (cdx * cdx + cdy * cdy + cdz * cdz > (range + l.CellM * 1.5) * (range + l.CellM * 1.5)) continue;
                    foreach (ref readonly var it in cell.Items.AsSpan())
                    {
                        double x = it.X - ex, y = it.Y - ey, z = it.Z - ez;
                        double dist = Math.Sqrt(x * x + y * y + z * z);
                        if (dist > range) continue;
                        // lejos hay menos (el terreno de KSP es más basto), y cerca del límite se aclaran
                        if (it.Rank >= ScatterField.DensidadEn(dist) * densidad) continue;
                        double nd = dist / range;
                        if (nd > 0.8 && Frac(it.Rank * 7.31) < (nd - 0.8) / 0.2) continue;
                        int k = arr.Length - 1;
                        while (k > 0 && dist < d.Levels[k].From) k--;
                        var lote = arr[k];
                        if (lote == null) continue;
                        // fuera del campo de visión (con el tamaño del objeto de margen)
                        double r = radio[k];
                        double vz = x * fx + y * fy + z * fz;
                        if (vz < -r) continue;
                        double vx = x * rx + y * ry + z * rz, vy = x * ux + y * uy + z * uz;
                        if (Math.Abs(vx) > (vz + r) * tx + r || Math.Abs(vy) > (vz + r) * ty + r) continue;

                        if (lote.Data.Length < (lote.N + 1) * 12) Array.Resize(ref lote.Data, lote.Data.Length * 2);
                        int o = lote.N * 12;
                        var a = lote.Data;
                        a[o] = (float)x; a[o + 1] = (float)y; a[o + 2] = (float)z; a[o + 3] = it.Yaw;
                        a[o + 4] = it.Ux; a[o + 5] = it.Uy; a[o + 6] = it.Uz; a[o + 7] = it.S;
                        a[o + 8] = it.R; a[o + 9] = it.G; a[o + 10] = it.B; a[o + 11] = it.Rank;
                        lote.N++;
                    }
                }
                // el límite de objetos por nivel del propio mod: se aclara al azar, de forma estable
                for (int k = 0; k < arr.Length; k++)
                {
                    var lote = arr[k];
                    int max = d.MaxObjects[Math.Min(k, 2)];
                    if (lote == null || lote.N <= max) continue;
                    double keep = (double)max / lote.N;
                    int w = 0;
                    for (int i = 0; i < lote.N; i++)
                    {
                        if (Frac(lote.Data[i * 12 + 11] * 13.37) >= keep) continue;
                        if (w != i) Array.Copy(lote.Data, i * 12, lote.Data, w * 12, 12);
                        w++;
                    }
                    lote.N = w;
                }
            });

            GL.Enable(GL.DEPTH_TEST);
            GL.DepthFunc(GL.LEQUAL);
            GL.DepthMask(true);
            GL.Disable(GL.CULL_FACE);
            GL.Disable(GL.BLEND);
            var p = scatterProg;
            p.Use();
            p.Vec3("uF", fx, fy, fz);
            p.Vec3("uR", rx, ry, rz);
            p.Vec3("uU", ux, uy, uz);
            p.Vec3("uEyeR", eye[0], eye[1], eye[2]);
            p.Float("uTan", tan);
            p.Float("uAspect", aspect);
            p.Float("uNear", ScatterNear);
            p.Float("uFar", ScatterFar);
            p.Float("uRadiusM", R);
            var sun = Light ? SunDir : Norm(eye);
            p.Vec3("uSun", sun[0], sun[1], sun[2]);
            AtmosUniforms(p, 4, sunOn: !SkyForceNight);
            p.Float("uCerca", 1);
            p.Int("uTex", 0);
            p.Int("uWindMap", 1);
            p.Float("uTime", relojViento.Elapsed.TotalSeconds);
            bool a2c = MsaaSamples > 1;
            p.Int("uA2C", a2c ? 1 : 0);

            int visibles = 0, conViento = 0;
            for (int c = 0; c < capas.Length; c++)
            {
                var d = capas[c].Def;
                var esc = (capas[c].Parent ?? capas[c]).Def;
                var arr = llenos[c].lotes;
                for (int k = 0; k < arr.Length; k++)
                {
                    var lote = arr[k];
                    if (lote == null || lote.N == 0) continue;
                    var mesh = gpu.Meshes[lote.MeshKey];
                    var mat = d.Levels[k].Material;
                    if (lote.Vao == 0) CrearVao(lote, mesh);
                    GL.BindBuffer(GL.ARRAY_BUFFER, lote.Inst);
                    GL.BufferData(GL.ARRAY_BUFFER, lote.Data, lote.N * 12, GL.STREAM_DRAW);
                    GL.BindBuffer(GL.ARRAY_BUFFER, 0);

                    gpu.Textures.TryGetValue(mat.MainTex ?? "", out var tex);
                    BindTex(0, tex ?? BlancoScatter());
                    p.Vec3("uMinScale", esc.MinScale[0], esc.MinScale[1], esc.MinScale[2]);
                    p.Vec3("uMaxScale", esc.MaxScale[0], esc.MaxScale[1], esc.MaxScale[2]);
                    p.Vec3("uColor", mat.Color[0], mat.Color[1], mat.Color[2]);
                    p.Float("uCutoff", mat.AlphaCutoff ? mat.Cutoff : -1);
                    p.Int("uTwoSided", mat.TwoSided ? 1 : 0);
                    p.Int("uBillboard", mat.BillboardMeshNormals ? 2 : mat.Billboard ? 1 : 0);
                    p.Int("uBiplanar", mat.Biplanar ? 1 : 0);
                    p.Float("uTiling", mat.Tiling);
                    p.Vec4("uSub", mat.SubsurfaceColor[0], mat.SubsurfaceColor[1], mat.SubsurfaceColor[2], mat.Subsurface ? mat.SubsurfaceIntensity : 0);
                    p.Float("uSubPow", Math.Max(mat.SubsurfacePower, 0.5));
                    gpu.Textures.TryGetValue(mat.WindMap ?? "", out var mapaViento);
                    bool viento = Wind && mat.Wind && mapaViento != null;
                    p.Int("uWind", viento ? 1 : 0);
                    if (viento)
                    {
                        conViento += lote.N;
                        BindTex(1, mapaViento);
                        double kv = mat.WindScale;
                        p.Vec3("uWindEye", Frac(ex * kv), Frac(ey * kv), Frac(ez * kv));
                        p.Float("uWindScale", kv);
                        p.Float("uWindSpeed", mat.WindSpeed);
                        p.Float("uWindIntensity", mat.WindIntensity);
                        p.Float("uWindHS", mat.WindHeightStart);
                        p.Float("uWindHF", mat.WindHeightFactor);
                    }
                    bool cobertura = a2c && mat.AlphaCutoff;
                    if (cobertura) GL.Enable(GL.SAMPLE_ALPHA_TO_COVERAGE);
                    GL.BindVertexArray(lote.Vao);
                    GL.DrawElementsInstanced(GL.TRIANGLES, mesh.Count, GL.UNSIGNED_INT, 0, lote.N);
                    GL.BindVertexArray(0);
                    if (cobertura) GL.Disable(GL.SAMPLE_ALPHA_TO_COVERAGE);
                    visibles += lote.N;
                }
            }
            ScatterVisible = visibles;
            ScatterWindy = conViento;
        }

        static double Frac(double x) => x - Math.Floor(x);

        void ScatterVisibleReset() => ScatterVisible = ScatterWindy = 0;

        static void CrearVao(Lote lote, ScatterGpu.Mesh mesh)
        {
            lote.Vao = GL.GenVertexArray();
            lote.Inst = GL.GenBuffer();
            GL.BindVertexArray(lote.Vao);
            GL.BindBuffer(GL.ARRAY_BUFFER, mesh.Vbo);
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, GL.FLOAT, false, 32, 0);
            GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, GL.FLOAT, false, 32, 12);
            GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 2, GL.FLOAT, false, 32, 24);
            GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, mesh.Ebo);
            GL.BindBuffer(GL.ARRAY_BUFFER, lote.Inst);
            for (uint i = 0; i < 3; i++)
            {
                GL.EnableVertexAttribArray(3 + i);
                GL.VertexAttribPointer(3 + i, 4, GL.FLOAT, false, 48, (int)i * 16);
                GL.VertexAttribDivisor(3 + i, 1);
            }
            GL.BindVertexArray(0);
            GL.BindBuffer(GL.ARRAY_BUFFER, 0);
        }

        Texture blancoScatter;
        Texture BlancoScatter() => blancoScatter ??= Texture.FromRgba(new byte[] { 255, 255, 255, 255 }, 1, 1, TexFilter.Linear, true);

        /* Al cambiar de cuerpo o apagar los scatters: fuera los búferes de cada lote. */
        public void DisposeScatterBatches()
        {
            foreach (var l in lotes.Values)
            {
                GL.DeleteVertexArray(l.Vao);
                GL.DeleteBuffer(l.Inst);
            }
            lotes.Clear();
        }

        void DisposeScatters()
        {
            DisposeScatterBatches();
            scatterProg?.Dispose();
            scatterProg = null;
            blancoScatter?.Dispose();
            blancoScatter = null;
        }
    }
}
