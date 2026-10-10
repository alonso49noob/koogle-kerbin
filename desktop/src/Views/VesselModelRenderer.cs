using System;
using System.Collections.Generic;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;

namespace KerbinMaps.Views
{
    /* Paints a vessel's model assembled from KSP parts.

       Everything is done relative to the camera and in meters: in Kerbin radii and with the
       camera in its place, a 10 m vessel is 1.7e-5 units at a distance of ~1 from the origin,
       and float precision (about 8 cm there) would make the vertices jitter. It has its own
       near plane and its own depth buffer, which is cleared first. */
    public sealed class VesselModelRenderer : IDisposable
    {
        const string VS = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNrm;
layout(location = 2) in vec2 aUv;
uniform mat4 uProj, uView, uModel, uNrm;
uniform vec4 uUvXform;
out vec3 vN;
out vec3 vW;
out vec2 vUv;
void main() {
  vec4 w = uModel * vec4(aPos, 1.0);
  vW = w.xyz;
  gl_Position = uProj * uView * w;
  vN = mat3(uNrm) * aNrm;
  vUv = aUv * uUvXform.xy + uUvXform.zw;
}";

        const string FS = @"#version 330 core
in vec3 vN;
in vec3 vW;
in vec2 vUv;
uniform sampler2D uTex;
uniform int uHasTex, uCutout, uBlend;
uniform vec4 uColor;
uniform vec3 uLight;
uniform float uAmbient, uSunlight;
out vec4 frag;
void main() {
  vec4 c = (uHasTex != 0 ? texture(uTex, vUv) : vec4(0.75, 0.75, 0.75, 1.0)) * uColor;
  if (uCutout != 0 && c.a < 0.5) discard;
  /* Two-sided lighting: the change from Unity's frame (left-handed) to the viewer's reverses
     the triangles' winding, and this way it doesn't matter which way they face. Besides the
     globe's fixed «sun», a light from the camera: without it, the side of the vessel in shadow
     looks black right when you get close to look at it. */
  vec3 n = normalize(vN);
  float sun = abs(dot(n, uLight));
  float head = abs(dot(n, normalize(-vW)));
  /* In the planet's shadow there's no Sun: the camera light remains, dimmer and colder, so the
     vessel doesn't disappear completely on the night side. */
  vec3 dayAmt = vec3(uAmbient + (1.0 - uAmbient) * (0.6 * sun + 0.4 * head));
  vec3 nightAmt = vec3(0.16, 0.18, 0.24) + vec3(0.30, 0.33, 0.40) * head;
  vec3 lightAmt = uAmbient >= 1.0 ? vec3(1.0) : mix(nightAmt, dayAmt, uSunlight);
  frag = vec4(c.rgb * lightAmt, uBlend != 0 ? c.a : 1.0);
}";

        readonly ShaderProgram prog;
        readonly ModelGpu gpu = new();

        public VesselModelRenderer()
        {
            prog = new ShaderProgram(VS, FS, ("aPos", 0), ("aNrm", 1), ("aUv", 2));
        }

        internal static float[] F(double[] m)
        {
            var f = new float[16];
            for (int i = 0; i < 16; i++) f[i] = (float)m[i];
            return f;
        }

        /* Cofactors of the 3x3 block: the inverse transpose without dividing by the
           determinant, which doesn't matter because the shader normalizes and uses the absolute
           value. */
        internal static float[] NormalMatrix(double[] m)
        {
            double a = m[0], b = m[4], c = m[8], d = m[1], e = m[5], f = m[9], g = m[2], h = m[6], i = m[10];
            var r = new float[16];
            r[0] = (float)(e * i - f * h); r[4] = (float)(-(d * i - f * g)); r[8] = (float)(d * h - e * g);
            r[1] = (float)(-(b * i - c * h)); r[5] = (float)(a * i - c * g); r[9] = (float)(-(a * h - b * g));
            r[2] = (float)(b * f - c * e); r[6] = (float)(-(a * f - c * d)); r[10] = (float)(a * e - b * d);
            r[15] = 1;
            return r;
        }

        /* offsetM: position of the center of mass relative to the camera, in meters and in the
           viewer's frame. fwd/up: the camera's orientation. */
        public void Draw(AssembledVessel a, double[] offsetM, double[] fwd, double[] up, double fovDeg, int w, int h, double[] light, bool lit, double sunlight = 1)
        {
            double dist = Math.Sqrt(offsetM[0] * offsetM[0] + offsetM[1] * offsetM[1] + offsetM[2] * offsetM[2]);
            double near = Math.Max(0.05, (dist - a.Radius * 1.2) * 0.25);
            double far = dist + a.Radius * 3 + 50;
            var proj = new float[16];
            Mat4.Perspective(proj, fovDeg * Math.PI / 180, (double)w / h, near, far);
            var view = new float[16];
            Mat4.LookAt(view, new double[] { 0, 0, 0 }, fwd, up);

            /* From the vessel (Unity, left-handed, relative to the root) to the viewer: the
               center of mass is removed, the saved rotation relative to the planet is applied,
               the X and Z axes are swapped (that's how you go from KSP's body frame to the
               globe's) and it's placed relative to the camera. */
            var swap = new double[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1 };
            var baseM = Mat.Mul(Mat.Translate(offsetM), Mat.Mul(swap, Mat.Mul(Mat.Rotate(a.Rot), Mat.Translate(new[] { -a.CoM[0], -a.CoM[1], -a.CoM[2] }))));

            GL.Clear(GL.DEPTH_BUFFER_BIT);
            GL.Enable(GL.DEPTH_TEST);
            GL.Disable(GL.CULL_FACE);
            GL.DepthMask(true);
            prog.Use();
            prog.Mat("uProj", proj);
            prog.Mat("uView", view);
            prog.Vec3("uLight", light[0], light[1], light[2]);
            prog.Float("uAmbient", lit ? 0.45 : 1.0);
            prog.Float("uSunlight", sunlight);
            prog.Int("uTex", 0);
            GL.ActiveTexture(GL.TEXTURE0);

            for (int pass = 0; pass < 2; pass++)
            {
                bool blend = pass == 1;
                if (blend) { GL.Enable(GL.BLEND); GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA); GL.DepthMask(false); }
                foreach (var it in a.Items)
                {
                    if (it.Transparent != blend) continue;
                    var gm = gpu.Upload(it.Mesh);
                    if (it.Submesh >= gm.Ebo.Length || gm.Count[it.Submesh] == 0) continue;
                    var m = Mat.Mul(baseM, it.M);
                    prog.Mat("uModel", F(m));
                    prog.Mat("uNrm", NormalMatrix(m));
                    prog.Vec4("uUvXform", it.TexScale[0], it.TexScale[1], it.TexOffset[0], it.TexOffset[1]);
                    prog.Vec4("uColor", it.Color[0], it.Color[1], it.Color[2], it.Color[3]);
                    uint tex = gpu.Tex(a, it.TexturePath);
                    GL.BindTexture(GL.TEXTURE_2D, tex);
                    prog.Int("uHasTex", tex != 0 ? 1 : 0);
                    prog.Int("uCutout", it.Cutout ? 1 : 0);
                    prog.Int("uBlend", blend ? 1 : 0);
                    GL.BindVertexArray(gm.Vao);
                    GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, gm.Ebo[it.Submesh]);
                    GL.DrawElements(GL.TRIANGLES, gm.Count[it.Submesh], GL.UNSIGNED_INT, 0);
                }
                if (blend) { GL.Disable(GL.BLEND); GL.DepthMask(true); }
            }
            GL.BindVertexArray(0);
            GL.BindTexture(GL.TEXTURE_2D, 0);
            GL.Disable(GL.DEPTH_TEST);
        }

        public void Dispose()
        {
            prog.Dispose();
            gpu.Dispose();
        }
    }
}
