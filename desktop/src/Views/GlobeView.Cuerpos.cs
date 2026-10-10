using System;
using System.Collections.Generic;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* The other bodies of the system seen from the globe: moons, planets and the Sun in their
       real place for the time on the time bar, at real scale.

       Each one is painted in a screen square that encloses it, tracing the ray against its
       sphere in the shader itself: that way it comes out round at any distance, with the Sun's
       light on its side (the Mun with its phases) and with its color map if there is one. The
       math is done divided by its distance, because Jool or Eeloo are tens of thousands of
       Kerbin radii away and in float the intersection would run out of precision. Those smaller
       than a couple of pixels show as a dot, so they can be found in the sky. Kerbin hides them
       when they're behind it. */
    public sealed partial class GlobeView
    {
        public sealed class CuerpoEnElCielo
        {
            public string Nombre;
            public double[] Pos;                  // center, in the globe's frame and in radii of the viewed body
            public double Radio;                  // in radii of the viewed body
            public double[] Luz;                  // toward the star, unit vector, in the globe's frame
            public double Giro;                   // fraction of a turn between its map and the globe's frame
            public float[] Tinte;
            public Texture Mapa;                  // its color map, or null
            public bool Estrella;
        }

        public readonly List<CuerpoEnElCielo> Cuerpos = new();
        public bool VerCuerpos = true;

        ShaderProgram cuerpoProg;
        uint cuerpoVao;

        const string CuerpoVS = @"#version 330 core
uniform mat4 uProj, uRot;
uniform vec3 uCentro, uDer, uArr;
uniform float uMedio;
out vec3 vDir;
const vec2 Q[4] = vec2[4](vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(-1.0, 1.0), vec2(1.0, 1.0));
void main() {
  vec2 q = Q[gl_VertexID];
  vec3 w = uCentro + (uDer * q.x + uArr * q.y) * uMedio;
  vDir = w;
  vec4 c = uProj * uRot * vec4(w, 1.0);
  // no depth: in front of whatever is behind; the shader decides occlusion
  gl_Position = vec4(c.xy, 0.0, c.w);
}";

        const string CuerpoFS = @"#version 330 core
in vec3 vDir;
uniform vec3 uCentro;          // unit: everything is divided by the distance to the body
uniform float uR, uPix;        // its radius and what a pixel spans, in those units
uniform vec3 uPlaneta;         // the viewed body, in the same units
uniform float uRPlaneta;
uniform vec3 uLuz, uTinte;
uniform int uHasMapa, uEstrella;
uniform float uGiro;
uniform sampler2D uMapa;
out vec4 frag;
const float PI = 3.14159265;

// the entry t of a ray from the origin into a sphere, or -1
float entrada(vec3 d, vec3 c, float r) {
  float b = dot(d, c), h = b * b - (dot(c, c) - r * r);
  if (h < 0.0) return -1.0;
  return b - sqrt(h);
}

void main() {
  vec3 d = normalize(vDir);
  float ang = acos(clamp(dot(d, uCentro), -1.0, 1.0));
  float rMin = 1.6 * uPix;
  // the planet you're looking from hides whatever is behind it
  float tp = entrada(d, uPlaneta, uRPlaneta);
  if (tp > 0.0 && tp < 1.0 - uR) discard;

  if (uR < rMin) {
    // a dot: its average brightness according to the phase seen from here
    float a = 1.0 - smoothstep(rMin * 0.5, rMin, ang);
    if (a <= 0.0) discard;
    float fase = uEstrella != 0 ? 1.0 : 0.25 + 0.75 * (0.5 + 0.5 * dot(uLuz, -uCentro));
    vec3 c = uEstrella != 0 ? vec3(1.0, 0.95, 0.82) : uTinte * fase * 1.4;
    frag = vec4(min(c, vec3(1.0)), a);
    return;
  }

  // the edge, smoothed over one pixel
  float borde = 1.0 - smoothstep(uR - uPix, uR + uPix * 0.5, ang);
  if (borde <= 0.0) discard;
  if (uEstrella != 0) { frag = vec4(1.0, 0.95, 0.82, borde); return; }
  float t = entrada(d, uCentro, uR);
  vec3 n = t > 0.0 ? normalize(d * t - uCentro) : normalize(d - uCentro);
  vec3 alb = uTinte;
  if (uHasMapa != 0) {
    float lat = asin(clamp(n.y, -1.0, 1.0)), lon = atan(n.x, n.z);
    alb = texture(uMapa, vec2(fract(lon / (2.0 * PI) + 0.5 + uGiro), 0.5 - lat / PI)).rgb;
  }
  vec3 lin = pow(alb, vec3(2.2)) * (0.015 + 1.1 * max(dot(n, uLuz), 0.0));
  frag = vec4(pow(lin, vec3(1.0 / 2.2)), borde);
}";

        /* Paints the bodies that have something to paint. `eye` in radii of the viewed body. */
        void DrawCuerpos(double[] eye)
        {
            if (!VerCuerpos || Cuerpos.Count == 0) return;
            cuerpoProg ??= new ShaderProgram(CuerpoVS, CuerpoFS);
            if (cuerpoVao == 0) cuerpoVao = GL.GenVertexArray();

            // the view without translation: positions are already relative to the eye
            var rot = (float[])view.Clone();
            rot[12] = rot[13] = rot[14] = 0;
            double tan = Math.Tan(fovL * D2R / 2);
            double pix = 2 * tan / Math.Max(H, 1);
            var der = Norm(Cross(fwdL, upL));
            var arr = Cross(der, fwdL);

            GL.Enable(GL.BLEND);
            GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
            GL.Disable(GL.DEPTH_TEST);
            var p = cuerpoProg;
            p.Use();
            p.Mat("uProj", proj);
            p.Mat("uRot", rot);
            p.Int("uMapa", 0);
            GL.BindVertexArray(cuerpoVao);
            // far to near, in case one passes in front of another
            var orden = new List<(double D, CuerpoEnElCielo C)>();
            foreach (var c in Cuerpos)
            {
                var rel = Add(c.Pos, eye, -1);
                double dist = Len(rel);
                if (dist <= c.Radio * 1.01) continue;                // inside it: not painted
                orden.Add((dist, c));
            }
            orden.Sort((a, b) => b.D.CompareTo(a.D));
            foreach (var (dist, c) in orden)
            {
                var rel = Add(c.Pos, eye, -1);
                var u = Scale(rel, 1 / dist);
                double r = c.Radio / dist;
                // behind the camera (with a margin for whatever peeks in at the edge)
                if (Dot(u, fwdL) < -r) continue;
                double medio = Math.Max(r * 1.5, pix * 2.5);
                p.Vec3("uCentro", u[0], u[1], u[2]);
                p.Vec3("uDer", der[0], der[1], der[2]);
                p.Vec3("uArr", arr[0], arr[1], arr[2]);
                p.Float("uMedio", medio);
                p.Float("uR", r);
                p.Float("uPix", pix);
                var pl = Scale(eye, -1 / dist);
                p.Vec3("uPlaneta", pl[0], pl[1], pl[2]);
                p.Float("uRPlaneta", 1 / dist);
                p.Vec3("uLuz", c.Luz[0], c.Luz[1], c.Luz[2]);
                p.Vec3("uTinte", c.Tinte[0], c.Tinte[1], c.Tinte[2]);
                p.Int("uEstrella", c.Estrella ? 1 : 0);
                p.Int("uHasMapa", c.Mapa != null ? 1 : 0);
                p.Float("uGiro", c.Giro);
                BindTex(0, c.Mapa);
                GL.DrawArrays(GL.TRIANGLE_STRIP, 0, 4);
            }
            GL.BindVertexArray(0);
            GL.Disable(GL.BLEND);
        }

        /* Their names, like markers: next to the disc or the dot, unless hidden. */
        void EtiquetasCuerpos(Batch2D b, TextCache tc, double[] eye)
        {
            if (!VerCuerpos || Cuerpos.Count == 0) return;
            var estilo = new TextStyle("Segoe UI", 11 * S, false, unchecked((int)0xFFDBE6F2), true);
            double tan = Math.Tan(fovL * D2R / 2);
            foreach (var c in Cuerpos)
            {
                var rel = Add(c.Pos, eye, -1);
                double dist = Len(rel);
                if (dist <= c.Radio * 1.01 || TapadoPorPlaneta(c.Pos, eye)) continue;
                if (!ToScreen(c.Pos, out double sx, out double sy)) continue;
                if (sx < -40 || sy < -40 || sx > W + 40 || sy > H + 40) continue;
                double rPx = c.Radio / dist / (2 * tan / H);
                var t = tc.Get(c.Nombre, estilo);
                if (t == null) continue;
                b.Text(t, sx + Math.Max(rPx, 3) + 5 * S, sy - t.TextH / 2.0);
            }
        }

        void DisposeCuerpos()
        {
            cuerpoProg?.Dispose();
            if (cuerpoVao != 0) GL.DeleteVertexArray(cuerpoVao);
            cuerpoVao = 0;
        }
    }
}
