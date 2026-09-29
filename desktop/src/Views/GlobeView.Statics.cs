using System;
using System.Collections.Generic;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;

namespace KerbinMaps.Views
{
    /* Edificios de Kerbal Konstructs en la vista de vuelo y en la del cielo.

       Se pintan como los scatters: relativos al ojo y en metros (a 600 km del centro un
       float ya no distingue centímetros), con la misma proyección y la misma profundidad
       logarítmica que escribe el suelo trazado por rayos, así que una colina tapa un hangar
       y un hangar tapa los árboles de detrás. La luz es la del suelo (Sol filtrado por el
       aire, cielo y bruma), no la de estudio de la nave enfocada.

       Cada instancia lleva su matriz en el marco del cuerpo de KSP (ver Konstructs.cs); al
       del visor se pasa cambiando X por Z. */
    public sealed partial class GlobeView
    {
        public KkDatabase Statics;                // null: sin Kerbal Konstructs
        public bool StaticsOn = true;
        /* El modelo montado de un edificio, o null mientras se carga (quien lo da avisa para
           pintar otra vez cuando esté). */
        public Func<KkModel, AssembledVessel> StaticModel;
        public KkInstance StaticSelected;         // la que se está editando, resaltada
        public int StaticsVisible { get; private set; }

        ShaderProgram staticProg;
        ModelGpu staticGpu;

        bool StaticsActive => StaticsOn && Statics != null && StaticModel != null
                              && (Mode == CamMode.Free || Mode == CamMode.Sky || PlanetaCerca);

        const string StaticVS = Header + AtmosphereGlsl + @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNrm;
layout(location = 2) in vec2 aUv;
layout(location = 3) in vec2 aUv2;
uniform mat4 uModel, uNrm;
uniform vec4 uUvXform;
uniform vec2 uOrto;                     // en el mapa 2D: ortográfica, en NDC por metro
uniform vec3 uF, uR, uU, uEyeR, uSun;
uniform float uTan, uAspect, uNear, uFar, uRadiusM;
out vec2 vUv, vUv2;
out vec3 vN, vRel, vIns, vTr, vLocal;
out float vZ;
void main() {
  vec3 w = (uModel * vec4(aPos, 1.0)).xyz;
  vUv2 = aUv2;
  vLocal = aPos;
  vRel = w;
  vN = mat3(uNrm) * aNrm;
  vUv = aUv * uUvXform.xy + uUvXform.zw;
  float vx = dot(w, uR), vy = dot(w, uU), vz = dot(w, uF);
  vZ = vz;
  gl_Position = uOrto.x > 0.0
    ? vec4(vx * uOrto.x, vy * uOrto.y, 0.0, 1.0)
    : vec4(vx / (uTan * uAspect), vy / uTan, (vz * (uFar + uNear) - 2.0 * uFar * uNear) / (uFar - uNear), vz);
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

        const string StaticFS = Header + AtmosphereGlsl + @"
uniform vec3 uEyeR, uSun;
uniform float uRadiusM, uFar;
uniform sampler2D uTex;
uniform int uHasTex, uCutout, uBlend;
uniform vec4 uColor;
uniform vec3 uTint;                     // resaltado de la instancia elegida
/* Suelo del KSC («Diffuse Ground KSC»): hierba repetida por la posición en la malla y
   teñida, asfalto por las UV, y una máscara con las UV que dice dónde va cada uno. */
uniform int uGround, uHasGrass, uHasTarmac, uHasMask;
uniform sampler2D uGrass, uTarmac, uMask;
uniform float uGrassTiling;
uniform vec3 uGrassColor, uTarmacColor;
uniform vec2 uTarmacScale;
uniform vec4 uMaskXform;
in vec2 vUv, vUv2;
in vec3 vN, vRel, vIns, vTr, vLocal;
in float vZ;
out vec4 frag;
void main() {
  vec4 c = (uHasTex != 0 ? texture(uTex, vUv) : vec4(0.7, 0.7, 0.7, 1.0)) * uColor;
  if (uGround != 0) {
    vec3 g = (uHasGrass != 0 ? texture(uGrass, vLocal.xz * uGrassTiling).rgb : vec3(0.5)) * uGrassColor;
    vec3 t = (uHasTarmac != 0 ? texture(uTarmac, vUv * uTarmacScale).rgb : vec3(0.55)) * uTarmacColor;
    // la máscara va por el segundo canal de UV, que cubre la explanada entera de 0 a 1
    float m = uHasMask != 0 ? texture(uMask, vUv2 * uMaskXform.xy + uMaskXform.zw).r : 0.0;
    c = vec4(mix(g, t, m), 1.0);
  }
  if (uCutout != 0 && c.a < 0.5) discard;
  /* Las caras de KSP son de una sola cara y el cambio de marco (mano izquierda a
     derecha) invierte su sentido: la normal se vuelve hacia quien mira. */
  vec3 n = normalize(vN);
  vec3 v = normalize(-vRel);
  if (dot(n, v) < 0.0) n = -n;
  vec3 pR = uEyeR + vRel / uRadiusM;
  vec3 alb = pow(max(c.rgb, vec3(0.0)), vec3(2.2));
  vec3 L = shadeGround(pR, n, v, uSun, alb, 0.0);
  vec3 col = L * vTr + vIns;
  col = toneMap(col);
  col = mix(col, uTint, 0.35 * step(0.001, dot(uTint, uTint)));
  frag = vec4(col, uBlend != 0 ? c.a : 1.0);
  gl_FragDepth = clamp(log2(1.0 + max(vZ, 0.0)) / log2(1.0 + uFar), 0.0, 1.0);
}";

        // del marco del cuerpo de KSP al del visor: X y Z cambian de sitio
        static readonly double[] SwapXZ = { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1 };

        readonly List<(KkInstance I, AssembledVessel A, double[] M)> staticsFrame = new();

        /* Pinta los edificios encima del suelo ya trazado. `eye` en radios del cuerpo. */
        void DrawStatics(double[] eye, double[] right, double[] camUp, double tan, double[] orto = null, double radioVista = 0, double far = 0)
        {
            staticProg ??= new ShaderProgram(StaticVS, StaticFS, ("aPos", 0), ("aNrm", 1), ("aUv", 2), ("aUv2", 3));
            staticGpu ??= new ModelGpu();
            double R = Body.Radius;
            // el ojo en el marco de KSP, en metros
            double kx = eye[2] * R, ky = eye[1] * R, kz = eye[0] * R;
            double fx = fwdL[0], fy = fwdL[1], fz = fwdL[2];

            staticsFrame.Clear();
            foreach (var i in Statics.Instances)
            {
                if (!i.Placed || i.Body != Body.Name || i.ModelRef == null || !i.ModelRef.HasMesh) continue;
                var m = i.M;
                // posición respecto al ojo, ya en el marco del visor
                double dx = m[14] - kz, dy = m[13] - ky, dz = m[12] - kx;
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (orto != null)
                {
                    // en planta: lo que cae en la vista, por la distancia en horizontal
                    double along = dx * fx + dy * fy + dz * fz;
                    dist = Math.Sqrt(Math.Max(0, dist * dist - along * along));
                    if (dist > radioVista + 3000) continue;
                }
                else if (dist > Math.Min(i.Visibility, ScatterFar) + 500) continue;
                var a = StaticModel(i.ModelRef);
                if (a == null || a.Items.Count == 0) continue;
                double rad = a.Radius * i.Scale * (i.GroupRef?.Scale ?? 1) + 1;
                if (orto != null) { if (dist - rad > radioVista) continue; }
                else
                {
                    if (dist - rad > ScatterFar) continue;
                    if (dx * fx + dy * fy + dz * fz < -rad) continue;   // detrás de la cámara
                }
                var rel = Mat.Mul(SwapXZ, Mat.Mul(Mat.Translate(new[] { -kx, -ky, -kz }), m));
                staticsFrame.Add((i, a, rel));
            }
            StaticsVisible = staticsFrame.Count;
            picF = new[] { fx, fy, fz }; picR = (double[])right.Clone(); picU = (double[])camUp.Clone(); picTan = tan;
            if (staticsFrame.Count == 0) return;

            GL.Enable(GL.DEPTH_TEST);
            GL.DepthFunc(GL.LEQUAL);
            GL.DepthMask(true);
            GL.Disable(GL.CULL_FACE);
            GL.Disable(GL.BLEND);
            var p = staticProg;
            p.Use();
            p.Vec3("uF", fx, fy, fz);
            p.Vec3("uR", right[0], right[1], right[2]);
            p.Vec3("uU", camUp[0], camUp[1], camUp[2]);
            p.Vec3("uEyeR", eye[0], eye[1], eye[2]);
            p.Float("uTan", tan);
            p.Float("uAspect", (double)W / H);
            p.Float("uNear", ScatterNear);
            p.Float("uFar", far > 0 ? far : ScatterFar);
            p.Float("uRadiusM", R);
            var sun = (cenital ? cenSolReal : Light) ? SunDir : Norm(eye);
            if (cenital && !cenSolReal)
            {
                LocalBasis(eye, out var up0, out var east0, out var north0);
                sun = Norm(Add(Add(up0, north0, 0.7071), east0, -0.7071));
            }
            p.Vec3("uSun", sun[0], sun[1], sun[2]);
            AtmosUniforms(p, 4, sunOn: !SkyForceNight);
            if (orto != null) p.Int("uAtmos", 0);
            p.Vec2("uOrto", orto?[0] ?? 0, orto?[1] ?? 0);
            p.Float("uCerca", 1);
            p.Int("uTex", 0);
            p.Int("uGrass", 1); p.Int("uTarmac", 2); p.Int("uMask", 3);
            GL.ActiveTexture(GL.TEXTURE0);

            for (int pass = 0; pass < 2; pass++)
            {
                bool blend = pass == 1;
                if (blend) { GL.Enable(GL.BLEND); GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA); GL.DepthMask(false); }
                p.Int("uBlend", blend ? 1 : 0);
                foreach (var (inst, a, rel) in staticsFrame)
                {
                    if (inst == StaticSelected) p.Vec3("uTint", 1.0, 0.71, 0.33); else p.Vec3("uTint", 0, 0, 0);
                    foreach (var it in a.Items)
                    {
                        if (it.Transparent != blend) continue;
                        var gm = staticGpu.Upload(it.Mesh);
                        if (it.Submesh >= gm.Ebo.Length || gm.Count[it.Submesh] == 0) continue;
                        var mm = Mat.Mul(rel, it.M);
                        p.Mat("uModel", VesselModelRenderer.F(mm));
                        p.Mat("uNrm", VesselModelRenderer.NormalMatrix(mm));
                        p.Vec4("uUvXform", it.TexScale[0], it.TexScale[1], it.TexOffset[0], it.TexOffset[1]);
                        p.Vec4("uColor", it.Color[0], it.Color[1], it.Color[2], it.Color[3]);
                        var gr = it.Ground;
                        p.Int("uGround", gr != null ? 1 : 0);
                        if (gr != null)
                        {
                            uint tg = staticGpu.Tex(a, gr.Grass), tt = staticGpu.Tex(a, gr.Tarmac), tm = staticGpu.Tex(a, gr.Mask);
                            GL.ActiveTexture(GL.TEXTURE0 + 1); GL.BindTexture(GL.TEXTURE_2D, tg);
                            GL.ActiveTexture(GL.TEXTURE0 + 2); GL.BindTexture(GL.TEXTURE_2D, tt);
                            GL.ActiveTexture(GL.TEXTURE0 + 3); GL.BindTexture(GL.TEXTURE_2D, tm);
                            GL.ActiveTexture(GL.TEXTURE0);
                            p.Int("uHasGrass", tg != 0 ? 1 : 0); p.Int("uHasTarmac", tt != 0 ? 1 : 0); p.Int("uHasMask", tm != 0 ? 1 : 0);
                            p.Float("uGrassTiling", gr.GrassTiling);
                            p.Vec3("uGrassColor", gr.GrassColor[0], gr.GrassColor[1], gr.GrassColor[2]);
                            p.Vec3("uTarmacColor", gr.TarmacColor[0], gr.TarmacColor[1], gr.TarmacColor[2]);
                            p.Vec2("uTarmacScale", gr.TarmacScale[0], gr.TarmacScale[1]);
                            p.Vec4("uMaskXform", gr.MaskScale[0], gr.MaskScale[1], gr.MaskScale[2], gr.MaskScale[3]);
                        }
                        uint tex = staticGpu.Tex(a, it.TexturePath);
                        GL.BindTexture(GL.TEXTURE_2D, tex);
                        p.Int("uHasTex", tex != 0 ? 1 : 0);
                        p.Int("uCutout", it.Cutout ? 1 : 0);
                        GL.BindVertexArray(gm.Vao);
                        GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, gm.Ebo[it.Submesh]);
                        GL.DrawElements(GL.TRIANGLES, gm.Count[it.Submesh], GL.UNSIGNED_INT, 0);
                    }
                }
                if (blend) { GL.Disable(GL.BLEND); GL.DepthMask(true); }
            }
            GL.BindVertexArray(0);
            GL.BindTexture(GL.TEXTURE_2D, 0);
        }

        // la cámara del último fotograma con edificios, para elegir uno con el ratón
        double[] picF, picR, picU;
        double picTan;

        /* El edificio bajo el ratón en la vista de vuelo o del cielo, o null. Primero las
           esferas que los envuelven y luego, de los candidatos, los triángulos de verdad:
           un hangar grande envuelve con su esfera a medio grupo. */
        public KkInstance PickStatic(int px, int py)
        {
            if (picF == null || staticsFrame.Count == 0 || W <= 1 || H <= 1) return null;
            double sx = (2.0 * px / W - 1) * picTan * W / H, sy = (1 - 2.0 * py / H) * picTan;
            var d = new double[3];
            for (int k = 0; k < 3; k++) d[k] = picF[k] + picR[k] * sx + picU[k] * sy;
            double dl = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            for (int k = 0; k < 3; k++) d[k] /= dl;

            KkInstance mejor = null;
            double tMejor = double.MaxValue;
            foreach (var (inst, a, rel) in staticsFrame)
            {
                double cx = rel[12], cy = rel[13], cz = rel[14];
                double rad = a.Radius * inst.Scale * (inst.GroupRef?.Scale ?? 1) + 1;
                double tc = cx * d[0] + cy * d[1] + cz * d[2];
                double d2 = cx * cx + cy * cy + cz * cz - tc * tc;
                if (d2 > rad * rad || tc + rad < 0 || tc - rad > tMejor) continue;
                foreach (var it in a.Items)
                {
                    if (it.Submesh >= it.Mesh.Submeshes.Count) continue;
                    var inv = Mat.Inverse(Mat.Mul(rel, it.M));
                    if (inv == null) continue;
                    // el rayo en el espacio de la malla: el parámetro t es el mismo que fuera
                    var o = Mat.Apply(inv, 0, 0, 0);
                    var dd = new[]
                    {
                        inv[0] * d[0] + inv[4] * d[1] + inv[8] * d[2],
                        inv[1] * d[0] + inv[5] * d[1] + inv[9] * d[2],
                        inv[2] * d[0] + inv[6] * d[1] + inv[10] * d[2],
                    };
                    double t = RayMesh(o, dd, it.Mesh.Verts, it.Mesh.Submeshes[it.Submesh], tMejor);
                    if (t < tMejor) { tMejor = t; mejor = inst; }
                }
            }
            return mejor;
        }

        /* Lo más alto de los edificios en la vertical de un punto, en metros sobre el nivel
           del mar, o null si no hay ninguno. Es donde se pone de pie el observador del cielo
           cuando cae encima de uno: la plataforma de lanzamiento, que es donde está por
           defecto, o un tejado. */
        public double? TechoDeEstaticos(double lat, double lon, double sobre)
        {
            if (Statics == null || StaticModel == null || !StaticsOn) return null;
            var clave = (lat, lon, sobre, Statics, Statics.Instances.Count, Body.Name);
            if (Equals(techoClave, clave)) return techo;
            double R = Body.Radius;
            var n = Sph(lat, lon, 1);
            // en el marco de KSP (x y z cambiados respecto al del visor), desde 500 m más arriba
            double top = R + sobre + 500;
            var o = new[] { n[2] * top, n[1] * top, n[0] * top };
            var d = new[] { -n[2], -n[1], -n[0] };
            const double largo = 1000;
            double tMejor = largo;
            bool completo = true;
            foreach (var i in Statics.Instances)
            {
                if (!i.Placed || i.Body != Body.Name || i.ModelRef == null || !i.ModelRef.HasMesh) continue;
                var m = i.M;
                double cx = m[12] - o[0], cy = m[13] - o[1], cz = m[14] - o[2];
                if (cx * cx + cy * cy + cz * cz > 4e6) continue;          // a más de 2 km: ni se mira
                var a = StaticModel(i.ModelRef);
                if (a == null) { completo = false; continue; }
                double rad = a.Radius * i.Scale * (i.GroupRef?.Scale ?? 1) + 1;
                double tc = cx * d[0] + cy * d[1] + cz * d[2];
                if (cx * cx + cy * cy + cz * cz - tc * tc > rad * rad || tc + rad < 0 || tc - rad > tMejor) continue;
                foreach (var it in a.Items)
                {
                    if (it.Submesh >= it.Mesh.Submeshes.Count) continue;
                    var inv = Mat.Inverse(Mat.Mul(m, it.M));
                    if (inv == null) continue;
                    var om = Mat.Apply(inv, o[0], o[1], o[2]);
                    var dd = new[]
                    {
                        inv[0] * d[0] + inv[4] * d[1] + inv[8] * d[2],
                        inv[1] * d[0] + inv[5] * d[1] + inv[9] * d[2],
                        inv[2] * d[0] + inv[6] * d[1] + inv[10] * d[2],
                    };
                    tMejor = Math.Min(tMejor, RayMesh(om, dd, it.Mesh.Verts, it.Mesh.Submeshes[it.Submesh], tMejor));
                }
            }
            double? r = tMejor < largo ? top - R - tMejor : null;
            // si algún modelo aún no estaba montado, se vuelve a mirar la próxima vez
            if (completo) { techoClave = clave; techo = r; }
            return r;
        }

        object techoClave;
        double? techo;

        /* Moller-Trumbore contra una lista de triángulos: el t más cercano por debajo de `max`. */
        static double RayMesh(double[] o, double[] d, float[] v, int[] idx, double max)
        {
            double best = max;
            for (int k = 0; k + 2 < idx.Length; k += 3)
            {
                int a = idx[k] * 3, b = idx[k + 1] * 3, c = idx[k + 2] * 3;
                if (a < 0 || b < 0 || c < 0 || a + 2 >= v.Length || b + 2 >= v.Length || c + 2 >= v.Length) continue;
                double e1x = v[b] - v[a], e1y = v[b + 1] - v[a + 1], e1z = v[b + 2] - v[a + 2];
                double e2x = v[c] - v[a], e2y = v[c + 1] - v[a + 1], e2z = v[c + 2] - v[a + 2];
                double px = d[1] * e2z - d[2] * e2y, py = d[2] * e2x - d[0] * e2z, pz = d[0] * e2y - d[1] * e2x;
                double det = e1x * px + e1y * py + e1z * pz;
                if (Math.Abs(det) < 1e-12) continue;
                double f = 1 / det;
                double sx = o[0] - v[a], sy = o[1] - v[a + 1], sz = o[2] - v[a + 2];
                double u = f * (sx * px + sy * py + sz * pz);
                if (u < 0 || u > 1) continue;
                double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                double w = f * (d[0] * qx + d[1] * qy + d[2] * qz);
                if (w < 0 || u + w > 1) continue;
                double t = f * (e2x * qx + e2y * qy + e2z * qz);
                if (t > 0.01 && t < best) best = t;
            }
            return best;
        }

        /* Al cambiar de instalación o apagar los edificios: fuera lo subido a la GPU. */
        public void DisposeStatics()
        {
            staticGpu?.Dispose();
            staticGpu = null;
            StaticsVisible = 0;
        }
    }
}
