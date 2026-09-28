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
                              && (Mode == CamMode.Free || Mode == CamMode.Sky);

        const string StaticVS = Header + AtmosphereGlsl + @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNrm;
layout(location = 2) in vec2 aUv;
uniform mat4 uModel, uNrm;
uniform vec4 uUvXform;
uniform vec3 uF, uR, uU, uEyeR, uSun;
uniform float uTan, uAspect, uNear, uFar, uRadiusM;
out vec2 vUv;
out vec3 vN, vRel, vIns, vTr;
out float vZ;
void main() {
  vec3 w = (uModel * vec4(aPos, 1.0)).xyz;
  vRel = w;
  vN = mat3(uNrm) * aNrm;
  vUv = aUv * uUvXform.xy + uUvXform.zw;
  float vx = dot(w, uR), vy = dot(w, uU), vz = dot(w, uF);
  vZ = vz;
  gl_Position = vec4(vx / (uTan * uAspect), vy / uTan, (vz * (uFar + uNear) - 2.0 * uFar * uNear) / (uFar - uNear), vz);
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
in vec2 vUv;
in vec3 vN, vRel, vIns, vTr;
in float vZ;
out vec4 frag;
void main() {
  vec4 c = (uHasTex != 0 ? texture(uTex, vUv) : vec4(0.7, 0.7, 0.7, 1.0)) * uColor;
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
        void DrawStatics(double[] eye, double[] right, double[] camUp, double tan)
        {
            staticProg ??= new ShaderProgram(StaticVS, StaticFS, ("aPos", 0), ("aNrm", 1), ("aUv", 2));
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
                if (dist > Math.Min(i.Visibility, ScatterFar) + 500) continue;
                var a = StaticModel(i.ModelRef);
                if (a == null || a.Items.Count == 0) continue;
                double rad = a.Radius * i.Scale * (i.GroupRef?.Scale ?? 1) + 1;
                if (dist - rad > ScatterFar) continue;
                if (dx * fx + dy * fy + dz * fz < -rad) continue;       // detrás de la cámara
                var rel = Mat.Mul(SwapXZ, Mat.Mul(Mat.Translate(new[] { -kx, -ky, -kz }), m));
                staticsFrame.Add((i, a, rel));
            }
            StaticsVisible = staticsFrame.Count;
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
            p.Float("uFar", ScatterFar);
            p.Float("uRadiusM", R);
            var sun = Light ? SunDir : Norm(eye);
            p.Vec3("uSun", sun[0], sun[1], sun[2]);
            AtmosUniforms(p, 4, sunOn: !SkyForceNight);
            p.Float("uCerca", 1);
            p.Int("uTex", 0);
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

        /* Al cambiar de instalación o apagar los edificios: fuera lo subido a la GPU. */
        public void DisposeStatics()
        {
            staticGpu?.Dispose();
            staticGpu = null;
            StaticsVisible = 0;
        }
    }
}
