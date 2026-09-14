using System;
using System.Collections.Generic;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;

namespace KerbinMaps.Views
{
    /* Pinta el modelo de una nave montado con las piezas de KSP.

       Todo se hace relativo a la cámara y en metros: en radios de Kerbin y con la cámara
       en su sitio, una nave de 10 m son 1,7e-5 unidades a una distancia de ~1 del origen,
       y la precisión de un float (unos 8 cm ahí) haría temblar los vértices. Tiene su
       propio plano cercano y su propio búfer de profundidad, que se limpia antes. */
    public sealed class VesselModelRenderer : IDisposable
    {
        sealed class GpuMesh
        {
            public uint Vao, Vbo;
            public uint[] Ebo;
            public int[] Count;
        }

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
  /* Iluminación de dos caras: el cambio de marco de Unity (mano izquierda) al del
     visor invierte el sentido de los triángulos, y así da igual hacia dónde miren.
     Además del «sol» fijo del globo, una luz desde la cámara: sin ella, la cara de la
     nave que queda en sombra se ve negra justo cuando uno se acerca a mirarla. */
  vec3 n = normalize(vN);
  float sun = abs(dot(n, uLight));
  float head = abs(dot(n, normalize(-vW)));
  /* En la sombra del planeta no hay Sol: queda la luz de la cámara, más tenue y fría,
     para que la nave no desaparezca del todo en la cara de noche. */
  vec3 dayAmt = vec3(uAmbient + (1.0 - uAmbient) * (0.6 * sun + 0.4 * head));
  vec3 nightAmt = vec3(0.16, 0.18, 0.24) + vec3(0.30, 0.33, 0.40) * head;
  vec3 lightAmt = uAmbient >= 1.0 ? vec3(1.0) : mix(nightAmt, dayAmt, uSunlight);
  frag = vec4(c.rgb * lightAmt, uBlend != 0 ? c.a : 1.0);
}";

        readonly ShaderProgram prog;
        readonly Dictionary<MuMesh, GpuMesh> meshes = new();
        readonly Dictionary<string, uint> textures = new(StringComparer.OrdinalIgnoreCase);

        public VesselModelRenderer()
        {
            prog = new ShaderProgram(VS, FS, ("aPos", 0), ("aNrm", 1), ("aUv", 2));
        }

        GpuMesh Upload(MuMesh m)
        {
            if (meshes.TryGetValue(m, out var g)) return g;
            int n = m.VertCount;
            var data = new float[n * 8];
            for (int i = 0; i < n; i++)
            {
                int o = i * 8;
                data[o] = m.Verts[i * 3]; data[o + 1] = m.Verts[i * 3 + 1]; data[o + 2] = m.Verts[i * 3 + 2];
                if (m.Normals != null) { data[o + 3] = m.Normals[i * 3]; data[o + 4] = m.Normals[i * 3 + 1]; data[o + 5] = m.Normals[i * 3 + 2]; }
                else data[o + 4] = 1;
                if (m.Uvs != null) { data[o + 6] = m.Uvs[i * 2]; data[o + 7] = m.Uvs[i * 2 + 1]; }
            }
            g = new GpuMesh { Vao = GL.GenVertexArray(), Vbo = GL.GenBuffer(), Ebo = new uint[m.Submeshes.Count], Count = new int[m.Submeshes.Count] };
            GL.BindVertexArray(g.Vao);
            GL.BindBuffer(GL.ARRAY_BUFFER, g.Vbo);
            GL.BufferData(GL.ARRAY_BUFFER, data, data.Length, GL.STATIC_DRAW);
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, GL.FLOAT, false, 32, 0);
            GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, GL.FLOAT, false, 32, 12);
            GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 2, GL.FLOAT, false, 32, 24);
            GL.BindVertexArray(0);
            for (int s = 0; s < m.Submeshes.Count; s++)
            {
                var idx = m.Submeshes[s];
                var u = new uint[idx.Length];
                for (int i = 0; i < idx.Length; i++) u[i] = (uint)Math.Clamp(idx[i], 0, Math.Max(0, n - 1));
                g.Ebo[s] = GL.GenBuffer();
                GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, g.Ebo[s]);
                GL.BufferData(GL.ELEMENT_ARRAY_BUFFER, u, u.Length, GL.STATIC_DRAW);
                g.Count[s] = u.Length;
            }
            GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, 0);
            meshes[m] = g;
            return g;
        }

        uint Tex(AssembledVessel a, string path)
        {
            if (path == null) return 0;
            if (textures.TryGetValue(path, out var id)) return id;
            if (a.Textures == null || !a.Textures.TryGetValue(path, out var tf) || tf == null || tf.Levels.Count == 0)
                return textures[path] = 0;
            id = GL.GenTexture();
            GL.BindTexture(GL.TEXTURE_2D, id);
            GL.PixelStore(GL.UNPACK_ALIGNMENT, 1);
            if (tf.CompressedFormat != 0)
            {
                int w = tf.Width, h = tf.Height;
                for (int l = 0; l < tf.Levels.Count; l++)
                {
                    GL.CompressedTexImage2D(GL.TEXTURE_2D, l, tf.CompressedFormat, w, h, tf.Levels[l]);
                    w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                }
                GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MAX_LEVEL, tf.Levels.Count - 1);
                GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, tf.Levels.Count > 1 ? GL.LINEAR_MIPMAP_LINEAR : GL.LINEAR);
            }
            else
            {
                GL.TexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA8, tf.Width, tf.Height, GL.RGBA, GL.UNSIGNED_BYTE, tf.Levels[0]);
                GL.GenerateMipmap(GL.TEXTURE_2D);
                GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, GL.LINEAR_MIPMAP_LINEAR);
            }
            GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, GL.LINEAR);
            GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, GL.REPEAT);
            GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, GL.REPEAT);
            if (GL.MaxAnisotropy > 0) GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MAX_ANISOTROPY, Math.Min(8f, GL.MaxAnisotropy));
            GL.BindTexture(GL.TEXTURE_2D, 0);
            a.Textures.Remove(path);              // ya está en la GPU: la copia en memoria sobra
            return textures[path] = id;
        }

        static float[] F(double[] m)
        {
            var f = new float[16];
            for (int i = 0; i < 16; i++) f[i] = (float)m[i];
            return f;
        }

        /* Cofactores del bloque 3x3: la inversa traspuesta sin dividir por el
           determinante, que da igual porque el shader normaliza y usa el valor absoluto. */
        static float[] NormalMatrix(double[] m)
        {
            double a = m[0], b = m[4], c = m[8], d = m[1], e = m[5], f = m[9], g = m[2], h = m[6], i = m[10];
            var r = new float[16];
            r[0] = (float)(e * i - f * h); r[4] = (float)(-(d * i - f * g)); r[8] = (float)(d * h - e * g);
            r[1] = (float)(-(b * i - c * h)); r[5] = (float)(a * i - c * g); r[9] = (float)(-(a * h - b * g));
            r[2] = (float)(b * f - c * e); r[6] = (float)(-(a * f - c * d)); r[10] = (float)(a * e - b * d);
            r[15] = 1;
            return r;
        }

        /* offsetM: posición del centro de masas respecto a la cámara, en metros y en el
           marco del visor. fwd/up: la orientación de la cámara. */
        public void Draw(AssembledVessel a, double[] offsetM, double[] fwd, double[] up, double fovDeg, int w, int h, double[] light, bool lit, double sunlight = 1)
        {
            double dist = Math.Sqrt(offsetM[0] * offsetM[0] + offsetM[1] * offsetM[1] + offsetM[2] * offsetM[2]);
            double near = Math.Max(0.05, (dist - a.Radius * 1.2) * 0.25);
            double far = dist + a.Radius * 3 + 50;
            var proj = new float[16];
            Mat4.Perspective(proj, fovDeg * Math.PI / 180, (double)w / h, near, far);
            var view = new float[16];
            Mat4.LookAt(view, new double[] { 0, 0, 0 }, fwd, up);

            /* De la nave (Unity, mano izquierda, relativa a la raíz) al visor: se quita el
               centro de masas, se aplica el giro guardado respecto al planeta, se cambian
               los ejes X y Z (así se pasa del marco del cuerpo en KSP al del globo) y se
               coloca respecto a la cámara. */
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
                    var gm = Upload(it.Mesh);
                    if (it.Submesh >= gm.Ebo.Length || gm.Count[it.Submesh] == 0) continue;
                    var m = Mat.Mul(baseM, it.M);
                    prog.Mat("uModel", F(m));
                    prog.Mat("uNrm", NormalMatrix(m));
                    prog.Vec4("uUvXform", it.TexScale[0], it.TexScale[1], it.TexOffset[0], it.TexOffset[1]);
                    prog.Vec4("uColor", it.Color[0], it.Color[1], it.Color[2], it.Color[3]);
                    uint tex = Tex(a, it.TexturePath);
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
            foreach (var g in meshes.Values)
            {
                foreach (var e in g.Ebo) GL.DeleteBuffer(e);
                GL.DeleteBuffer(g.Vbo);
                GL.DeleteVertexArray(g.Vao);
            }
            meshes.Clear();
            foreach (var t in textures.Values) GL.DeleteTexture(t);
            textures.Clear();
        }
    }
}
