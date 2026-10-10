using System.Linq;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* The political map drawn over the ground, the same on the 2D map, the globe and the nearby
       ground (see MapaPolitico).

       The grid is read unfiltered and each pixel looks at the four cells around it: with the
       bilinear interpolation weights, the faction that weighs most wins, and the border is where
       the two heaviest tie. Since that blend is linear within each cell, its gradient gives the
       distance to the border in cells, and with how big a cell is on screen, in pixels: the line
       comes out the same thickness at any zoom and without cell steps. Each side of the line
       takes its faction's color, a bit darker, and the fill intensifies next to it, like in
       classic political maps.

       The caller clips at the coast: it multiplies what this returns by the amount of land. */
    public static class FaccionesGlsl
    {
        public const string Codigo = @"
uniform sampler2D uFacTex;      // faction number per cell (R8), unfiltered
uniform vec4 uFacCol[64];       // each faction's color and whether it's shown (alpha)
uniform vec2 uFacSize;
uniform float uFacRelleno;      // fill opacity
uniform float uFacBorde;        // border thickness, in pixels
uniform int uFacOn;

int facId(ivec2 p) {
  ivec2 sz = ivec2(uFacSize);
  p.x = (p.x % sz.x + sz.x) % sz.x;
  p.y = clamp(p.y, 0, sz.y - 1);
  return int(texelFetch(uFacTex, p, 0).r * 255.0 + 0.5);
}

/* The territory at uv (u wraps around), with px cells per screen pixel. Returns the color
   premultiplied by its opacity. */
vec4 faccionEn(vec2 uv, float px) {
  vec2 t = vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0)) * uFacSize - 0.5;
  ivec2 i = ivec2(floor(t));
  vec2 f = t - vec2(i);
  int o[4];
  o[0] = facId(i); o[1] = facId(i + ivec2(1, 0)); o[2] = facId(i + ivec2(0, 1)); o[3] = facId(i + ivec2(1, 1));
  if (o[0] + o[1] + o[2] + o[3] == 0) return vec4(0.0);
  float w[4];
  vec2 g[4];
  w[0] = (1.0 - f.x) * (1.0 - f.y); g[0] = vec2(f.y - 1.0, f.x - 1.0);
  w[1] = f.x * (1.0 - f.y);         g[1] = vec2(1.0 - f.y, -f.x);
  w[2] = (1.0 - f.x) * f.y;         g[2] = vec2(-f.y, 1.0 - f.x);
  w[3] = f.x * f.y;                 g[3] = vec2(f.y, f.x);
  // the two (counting «nobody») that weigh most here
  int a = -1, b = -1;
  float wa = -1.0, wb = -1.0;
  vec2 ga = vec2(0.0), gb = vec2(0.0);
  for (int k = 0; k < 4; k++) {
    int id = o[k];
    if (id == a || id == b) continue;
    float W = 0.0;
    vec2 G = vec2(0.0);
    for (int j = 0; j < 4; j++) if (o[j] == id) { W += w[j]; G += g[j]; }
    if (W > wa) { b = a; wb = wa; gb = ga; a = id; wa = W; ga = G; }
    else if (W > wb) { b = id; wb = W; gb = G; }
  }
  // distance to the border between the two, in pixels
  float dist = 1e4;
  if (b >= 0) dist = (wa - wb) / max(length(ga - gb), 1e-3) / max(px, 1e-6);
  vec4 ca = a > 0 ? uFacCol[a] : vec4(0.0);
  vec4 cb = b > 0 ? uFacCol[b] : vec4(0.0);
  float rel = min(ca.a * uFacRelleno * (1.0 + 0.9 * (1.0 - smoothstep(0.0, 16.0, dist))), 0.9);
  vec4 fill = vec4(ca.rgb * rel, rel);
  // the line: each half in its side's color; outside a territory, the neighbor's
  vec4 cr = a > 0 ? ca : cb;
  float media = uFacBorde * 0.5;
  float raya = (1.0 - smoothstep(media - 0.7, media + 0.7, dist)) * cr.a * 0.95;
  vec3 colRaya = cr.rgb * 0.5;
  return vec4(colRaya * raya, raya) + fill * (1.0 - raya);
}
";

        static readonly string[] NombresCol = System.Linq.Enumerable.Range(0, 64).Select(i => "uFacCol[" + i + "]").ToArray();

        /* The faction colors for the shader's vector (64 × rgba). */
        public static float[] Colores(MapaPolitico m)
        {
            var c = new float[64 * 4];
            if (m == null) return c;
            foreach (var f in m.Facciones)
            {
                if (f.Id <= 0 || f.Id >= 64) continue;
                var col = ColorF.Hex(f.Color);
                c[f.Id * 4] = col.R; c[f.Id * 4 + 1] = col.G; c[f.Id * 4 + 2] = col.B;
                c[f.Id * 4 + 3] = f.Visible ? 1 : 0;
            }
            return c;
        }

        public static void Uniformes(ShaderProgram p, Texture rejilla, float[] colores, double relleno, double borde, int unidad)
        {
            bool on = rejilla != null && colores != null;
            p.Int("uFacOn", on ? 1 : 0);
            if (!on) return;
            GL.ActiveTexture(GL.TEXTURE0 + (uint)unidad);
            GL.BindTexture(GL.TEXTURE_2D, rejilla.Id);
            p.Int("uFacTex", unidad);
            p.Vec2("uFacSize", rejilla.Width, rejilla.Height);
            p.Float("uFacRelleno", relleno);
            p.Float("uFacBorde", borde);
            for (int i = 1; i < 64; i++)
                p.Vec4(NombresCol[i], colores[i * 4], colores[i * 4 + 1], colores[i * 4 + 2], colores[i * 4 + 3]);
        }
    }
}
