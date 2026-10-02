using System;
using System.Collections.Generic;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Los demás cuerpos del sistema vistos desde el globo: las lunas, los planetas y el Sol
       en su sitio de verdad para el instante de la barra de tiempo, a escala real.

       Cada uno se pinta en un cuadrado de pantalla que lo envuelve, trazando el rayo contra
       su esfera en el propio shader: así sale redondo a cualquier distancia, con la luz del
       Sol de su lado (la Mun con sus fases) y con su mapa de color si lo hay. Las cuentas se
       hacen divididas por su distancia, porque Jool o Eeloo están a decenas de miles de
       radios de Kerbin y en float la intersección se quedaría sin precisión. Los que miden
       menos de un par de píxeles salen como un punto, para que se encuentren en el cielo.
       Kerbin los tapa cuando quedan detrás. */
    public sealed partial class GlobeView
    {
        public sealed class CuerpoEnElCielo
        {
            public string Nombre;
            public double[] Pos;                  // centro, en el marco del globo y en radios del cuerpo que se ve
            public double Radio;                  // en radios del cuerpo que se ve
            public double[] Luz;                  // hacia la estrella, unitario, en el marco del globo
            public double Giro;                   // fracción de vuelta entre su mapa y el marco del globo
            public float[] Tinte;
            public Texture Mapa;                  // su mapa de color, o null
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
  // sin profundidad: delante de todo lo que haya detrás; la ocultación la decide el shader
  gl_Position = vec4(c.xy, 0.0, c.w);
}";

        const string CuerpoFS = @"#version 330 core
in vec3 vDir;
uniform vec3 uCentro;          // unitario: todo va dividido por la distancia al cuerpo
uniform float uR, uPix;        // su radio y lo que abarca un píxel, en esas unidades
uniform vec3 uPlaneta;         // el cuerpo que se ve, en las mismas unidades
uniform float uRPlaneta;
uniform vec3 uLuz, uTinte;
uniform int uHasMapa, uEstrella;
uniform float uGiro;
uniform sampler2D uMapa;
out vec4 frag;
const float PI = 3.14159265;

// la t de entrada de un rayo desde el origen en una esfera, o -1
float entrada(vec3 d, vec3 c, float r) {
  float b = dot(d, c), h = b * b - (dot(c, c) - r * r);
  if (h < 0.0) return -1.0;
  return b - sqrt(h);
}

void main() {
  vec3 d = normalize(vDir);
  float ang = acos(clamp(dot(d, uCentro), -1.0, 1.0));
  float rMin = 1.6 * uPix;
  // el planeta desde el que se mira tapa lo que quede detrás
  float tp = entrada(d, uPlaneta, uRPlaneta);
  if (tp > 0.0 && tp < 1.0 - uR) discard;

  if (uR < rMin) {
    // un punto: su brillo medio según la fase que se ve desde aquí
    float a = 1.0 - smoothstep(rMin * 0.5, rMin, ang);
    if (a <= 0.0) discard;
    float fase = uEstrella != 0 ? 1.0 : 0.25 + 0.75 * (0.5 + 0.5 * dot(uLuz, -uCentro));
    vec3 c = uEstrella != 0 ? vec3(1.0, 0.95, 0.82) : uTinte * fase * 1.4;
    frag = vec4(min(c, vec3(1.0)), a);
    return;
  }

  // el borde, suavizado en un píxel
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

        /* Pinta los cuerpos que tengan algo que pintar. `eye` en radios del cuerpo que se ve. */
        void DrawCuerpos(double[] eye)
        {
            if (!VerCuerpos || Cuerpos.Count == 0) return;
            cuerpoProg ??= new ShaderProgram(CuerpoVS, CuerpoFS);
            if (cuerpoVao == 0) cuerpoVao = GL.GenVertexArray();

            // la vista sin la traslación: las posiciones ya van relativas al ojo
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
            // de lejos a cerca, por si uno pasa por delante de otro
            var orden = new List<(double D, CuerpoEnElCielo C)>();
            foreach (var c in Cuerpos)
            {
                var rel = Add(c.Pos, eye, -1);
                double dist = Len(rel);
                if (dist <= c.Radio * 1.01) continue;                // dentro de él: no se pinta
                orden.Add((dist, c));
            }
            orden.Sort((a, b) => b.D.CompareTo(a.D));
            foreach (var (dist, c) in orden)
            {
                var rel = Add(c.Pos, eye, -1);
                var u = Scale(rel, 1 / dist);
                double r = c.Radio / dist;
                // detrás de la cámara (con margen para lo que asoma por el borde)
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

        /* Sus nombres, como los marcadores: al lado del disco o del punto, salvo tapados. */
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
