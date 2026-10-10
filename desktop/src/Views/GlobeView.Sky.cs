using System;
using System.Linq;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Sky view: the camera standing at a point on the surface, looking around.

       The planet isn't drawn as a mesh: from a few meters up the depth buffer can't tell the
       ground from an orbit hundreds of kilometers away, and the sphere's triangles would show
       on the horizon. Instead, each pixel casts a ray: if it hits the sphere it's ground (with
       the map texture and haze according to distance) and if not it's sky. The horizon comes
       out exact. */
    public sealed partial class GlobeView
    {
        public double ObsLat = -0.0972, ObsLon = -74.5577, ObsAlt = 70;
        public double SkyAz = 90, SkyEl = 25, SkyFov = 70;
        public bool SkyGrid = true, SkyForceNight;

        ShaderProgram skyProg;
        uint skyVao;

        /* Observer position: the altitude above sea level plus two meters of eye height, in
           Kerbin radii. Never below the painted ground: the stored altitude may come from
           another height map, and the KSC's leveled area and the detail relief raise the ground
           above it. */
        public double[] ObserverPos()
        {
            double suelo = Math.Max(Math.Max(ObsAlt, 0), GroundAt?.Invoke(ObsLat, ObsLon) ?? 0);
            // and if there's a building right there, on top of it
            if (TechoDeEstaticos(ObsLat, ObsLon, suelo) is double t && t > suelo) suelo = t;
            return Sph(ObsLat, ObsLon, 1 + (suelo + 2) / Body.Radius);
        }

        const string SkyVS = @"#version 330 core
const vec2 P[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
void main() { gl_Position = vec4(P[gl_VertexID], 0.0, 1.0); }";

        /* Each pixel casts a ray from the observer: if it hits the ground, the ground with its
           light and the air in between; if not, the sky that air scatters, the Sun and the
           stars it lets through. The transition to space when climbing comes purely from the
           physics. */
        const string SkyFS = Header + AtmosphereGlsl + StarsGlsl + @"
uniform vec2 uView;
uniform vec3 uEye, uF, uR, uU, uUp, uEast, uNorth;
/* Top-down view for the 2D map: each pixel looks straight down at its lat/lon, in the map's
   projection (uCentro in degrees, lon and lat; uPpd pixels per degree). */
uniform int uCenital;
uniform vec2 uCentro;
uniform float uPpd, uCenitalR;
uniform float uDistCenital;     // the distance at which flight would see a pixel this big
uniform float uPlano;           // 1: like the flat map (its color, unlit); 0: the flight ground
uniform float uTan, uAspect, uPix, uStarShift, uSunRad;
uniform sampler2D uColor, uBiome, uHeightTex;
uniform int uHasColor, uHasBiome, uGrid, uHasHeight, uRelief;
uniform float uHeightOff, uHMin, uHMax, uRadiusM, uTopR;
uniform vec2 uHeightSize, uColorSize;
uniform sampler2D uDetGrass, uDetSand, uDetRock, uDetSnow;
uniform int uHasDetail;
uniform float uDetTile, uDetAmt;
/* Eye position in meters from the body's center, reduced modulo a multiple of all the textures'
   periods: adding the distance to the point gives its world coordinates without losing
   precision, and the texture stays pinned to the ground. */
uniform vec3 uEyeMod;
uniform float uPeriodo;                 // that modulus, in texture repeats
/* Parallax mode: the four slots are low, mid, high and slope, and they're blended by the site's
   altitude and by slope with the numbers from Terrain.cfg. */
uniform int uModoParallax;
uniform vec2 uPxLowMid, uPxMidHigh;
uniform vec3 uPxSteep;                  // strength, contrast and midpoint
/* What gives the ground variety in Parallax: influence, displacement and occlusion, with one
   channel per texture (see ParallaxTerrain). */
uniform sampler2D uPxInf, uPxDisp, uPxOcc;
uniform int uPxHasInf, uPxHasDisp, uPxHasOcc;
uniform int uVariacion;                 // break up the tiling repetition
uniform sampler2D uPxBumpL, uPxBumpM, uPxBumpH, uPxBumpS;
uniform int uPxHasBump;
uniform int uDebug;
uniform sampler2D uCloudTex;
uniform int uHasClouds;
uniform float uCloudR, uCloudAmt, uCloudOff;
" + NubesGlsl + AguaGlsl + FaccionesGlsl.Codigo + @"
uniform int uFacConMar;
uniform float uColorOff, uBiomeOff, uBiomeAmt;
uniform vec3 uSun, uTint, uSeaColor;
uniform sampler2D uSeaTex;
uniform int uHasSeaTex;
/* Depth for the scatters, which are painted afterwards with depth testing: the distance at
   which the ray hits, on a logarithmic scale (see GlobeView.Scatters). */
uniform int uWriteDepth;
uniform float uDepthFar;
out vec4 frag;

float profundidad(float z) { return clamp(log2(1.0 + max(z, 0.0)) / log2(1.0 + uDepthFar), 0.0, 1.0); }

/* The height map is uploaded unfiltered, because biomes are read by exact color. Here it's
   interpolated by hand: without this the terrain comes out in steps the size of a texel (on
   Kerbin, 460 m plateaus). The quintic curve also removes the edges between texels, which show
   at ground level. */
float texel(ivec2 p) {
  p.x = int(mod(float(p.x), uHeightSize.x));
  p.y = clamp(p.y, 0, int(uHeightSize.y) - 1);
  return dot(texelFetch(uHeightTex, p, 0).rgb, vec3(0.2126, 0.7152, 0.0722));
}

float grisSuave(vec2 uv) {
  vec2 t = uv * uHeightSize - 0.5;
  vec2 f = fract(t);
  ivec2 i = ivec2(floor(t));
  f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
  float a = mix(texel(i), texel(i + ivec2(1, 0)), f.x);
  float b = mix(texel(i + ivec2(0, 1)), texel(i + ivec2(1, 1)), f.x);
  return mix(a, b, f.y);
}

/* Leveled areas (the KSC's): direction and height, and flat and blend radii. */
uniform int uFlatCount;
uniform vec4 uFlat[4];
uniform vec2 uFlatR[4];

/* Detail height tile under the camera (see TeselaAltura): a window of the map at full
   resolution. Rect: first texel and window size; Nivel: whole level size and longitude offset;
   Rango: meters of gray 0 and gray 255; and the blend width. */
uniform int uTesela;
uniform sampler2D uTeselaTex;
uniform vec4 uTeselaRect;
uniform vec3 uTeselaNivel;
uniform vec3 uTeselaRango;

/* Catmull-Rom weights (see TeselaAltura.Altura, which does the same on the CPU). */
vec4 pesosCatmullRom(float f) {
  float f2 = f * f, f3 = f2 * f;
  return vec4(-f + 2.0 * f2 - f3, 2.0 - 5.0 * f2 + 3.0 * f3, f + 4.0 * f2 - 3.0 * f3, -f2 + f3) * 0.5;
}

/* Tile height in meters and its weight (0 outside, 1 inside, blended near the edge). */
vec2 alturaTesela(float lat, float lon) {
  float u = fract(lon / (2.0 * PI) + 0.5 + uTeselaNivel.z), v = 0.5 - lat / PI;
  vec2 t = vec2(u * uTeselaNivel.x, v * uTeselaNivel.y) - 0.5 - uTeselaRect.xy;
  if (t.x < -uTeselaNivel.x * 0.5) t.x += uTeselaNivel.x;
  if (t.x > uTeselaNivel.x * 0.5) t.x -= uTeselaNivel.x;
  vec2 lim = uTeselaRect.zw - 3.0;
  if (t.x < 1.0 || t.y < 1.0 || t.x > lim.x || t.y > lim.y) return vec2(0.0);
  ivec2 i = ivec2(floor(t));
  vec2 f = t - vec2(i);
  vec4 wx = pesosCatmullRom(f.x), wy = pesosCatmullRom(f.y);
  float h = 0.0;
  for (int j = 0; j < 4; j++) {
    int y = i.y - 1 + j;
    vec4 fila = vec4(texelFetch(uTeselaTex, ivec2(i.x - 1, y), 0).r, texelFetch(uTeselaTex, ivec2(i.x, y), 0).r,
                     texelFetch(uTeselaTex, ivec2(i.x + 1, y), 0).r, texelFetch(uTeselaTex, ivec2(i.x + 2, y), 0).r);
    h += wy[j] * dot(wx, fila);
  }
  float borde = min(min(t.x - 1.0, lim.x - t.x), min(t.y - 1.0, lim.y - t.y));
  return vec2(h, smoothstep(0.0, 1.0, clamp(borde / uTeselaRango.z, 0.0, 1.0)));
}

/* Terrain height in that direction, in meters above sea level. */
float terrainH(vec3 n) {
  float lat = asin(clamp(n.y, -1.0, 1.0));
  float lon = atan(n.x, n.z);
  vec2 uv = vec2(fract(lon / (2.0 * PI) + 0.5 + uHeightOff), 0.5 - lat / PI);
  float h;
  vec2 ht = uTesela != 0 ? alturaTesela(lat, lon) : vec2(0.0);
  // inside the tile the base map isn't even read
  if (ht.y >= 1.0) h = ht.x;
  else h = mix(uHMin + grisSuave(uv) * (uHMax - uHMin), ht.x, ht.y);
  for (int i = 0; i < uFlatCount; i++) {
    float d = length(n - uFlat[i].xyz) * uRadiusM;
    if (d < uFlatR[i].y) h = mix(h, uFlat[i].w, 1.0 - smoothstep(uFlatR[i].x, uFlatR[i].y, d));
  }
  return h;
}

/* Depth for the water color. The height map is 8-bit and the sea floor drops in steps of tens of
   meters: seen through the water, each step showed up as a contour line. It's averaged over a
   texel and a half around; next to the shore the exact one rules (the foam and the sand have to
   match the coast). */
float profColor(vec3 n, float exacta) {
  float lat = asin(clamp(n.y, -1.0, 1.0)), lon = atan(n.x, n.z);
  vec2 uv = vec2(fract(lon / (2.0 * PI) + 0.5 + uHeightOff), 0.5 - lat / PI);
  vec2 e = 1.5 / uHeightSize;
  float g = (grisSuave(uv) + grisSuave(uv + vec2(e.x, 0.0)) + grisSuave(uv - vec2(e.x, 0.0))
           + grisSuave(uv + vec2(0.0, e.y)) + grisSuave(uv - vec2(0.0, e.y))) * 0.2;
  float suave = max(-(uHMin + g * (uHMax - uHMin)), 0.0);
  return mix(exacta, suave, smoothstep(2.0, 10.0, exacta));
}

/* Surface radius at that point. Below sea level the sea rules: the water is a smooth sphere and
   the ray doesn't have to go down to the bottom. The bottom is left a meter below the water and
   not right on it: otherwise, the ray's last sample falls on the sea sphere and rounding
   decides at random whether it's land or water, and at sea level stripes of both appeared. */
float terrainR(vec3 p) { return 1.0 + max(terrainH(normalize(p)), -1.0) / uRadiusM; }

/* Terrain normal by differences on the height map, at a few tens of meters. */
vec3 terrainNormal(vec3 p) {
  vec3 up = normalize(p);
  vec3 east = normalize(cross(vec3(0.0, 1.0, 0.0), up));
  vec3 north = cross(up, east);
  float e = 60.0 / uRadiusM;
  float hE = terrainH(normalize(up + east * e)), hW = terrainH(normalize(up - east * e));
  float hN = terrainH(normalize(up + north * e)), hS = terrainH(normalize(up - north * e));
  float dx = (hE - hW) / (2.0 * e * uRadiusM);
  float dy = (hN - hS) / (2.0 * e * uRadiusM);
  return normalize(up - east * dx - north * dy);
}

/* Steps through the height map until it crosses the surface. Steps grow with distance (up close
   detail is needed; far away, a pixel spans hundreds of meters) and the crossing is refined by
   bisection. Returns the distance to the hit, or -1 if there's none. */
float marchTerrain(vec3 o, vec3 d, out vec3 nOut, out float wasSea) {
  nOut = vec3(0.0); wasSea = 0.0;
  vec2 tTop = raySphere(o, d, uTopR);
  vec2 tSea = raySphere(o, d, 1.0);
  if (tTop.y <= 0.0) return -1.0;
  float t0 = max(tTop.x, 0.0);
  float t1 = tSea.x > 0.0 ? tSea.x : tTop.y;
  if (t1 <= t0) return -1.0;

  /* Steps according to the clearance above the terrain: with slopes up to about 60° nothing is
     skipped by advancing half the height left above. A minimum that grows with distance (what a
     pixel spans) and another fixed, small one. With fixed steps, thin ridges far away were
     skipped (steps in the silhouettes), especially with the detail tile. The fixed minimum used
     to be (t1 - t0) / 256, which skimming a mountain is about 300 m: a grazing ray jumped the
     summit and flat floating discs appeared. Now it's 1/1500, and to always reach the end (a
     ray hugging the ground uses up its steps much earlier) the last third of the budget spreads
     out whatever is left. */
  float prevT = t0, tt = t0;
  float pasoMin = (t1 - t0) / 1500.0;
  for (int i = 0; i < 400; i++) {
    vec3 p = o + d * tt;
    float encima = length(p) - terrainR(p);
    if (encima < 0.0) {
      float a = prevT, b = tt;
      for (int k = 0; k < 14; k++) {
        float m = 0.5 * (a + b);
        vec3 pm = o + d * m;
        if (length(pm) < terrainR(pm)) b = m; else a = m;
      }
      float th = 0.5 * (a + b);
      nOut = terrainNormal(o + d * th);
      return th;
    }
    if (tt >= t1) break;
    prevT = tt;
    float rescate = i > 260 ? (t1 - tt) / float(400 - i) : 0.0;
    tt = min(t1, tt + max(max(max(encima * 0.5, tt * 0.0015 + 1.0 / uRadiusM), pasoMin), rescate));
  }
  if (tSea.x > 0.0) { wasSea = 1.0; nOut = normalize(o + d * tSea.x); return tSea.x; }
  return -1.0;
}

/* Ground detail with the game's textures. Up close, the body's map has nothing more to give (a
   texel is hundreds of meters), so textures that repeat every few meters are laid over it. They
   fade with distance so they don't moiré. */

/* Linear step from 0 to 1 between a and b, exactly like Parallax's
   GetPercentageAltitudeBetween: with the range reversed (b < a) it gives 0 above a, which is
   how the Mun and other bodies say «always the bottom texture». */
float pct(float a, float b, float x) {
  if (b == a) return x >= a ? 1.0 : 0.0;
  return clamp((x - a) / (b - a), 0.0, 1.0);
}

float hash12(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

/* Periodic value noise: when the coordinates' modulus wraps around, the variation pattern stays
   the same. */
float ruidoP(vec2 p, float per) {
  vec2 i = floor(p), f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  vec2 i1 = mod(i + 1.0, per);
  i = mod(i, per);
  return mix(mix(hash12(i), hash12(vec2(i1.x, i.y)), f.x), mix(hash12(vec2(i.x, i1.y)), hash12(i1), f.x), f.y);
}

/* Texture variation (Inigo Quilez's technique): each zone reads the texture with a different
   offset, chosen by a smooth noise, and zones blend where the patterns of both look alike. The
   tiling stops looking repeated with only two reads. */
vec4 variada(sampler2D s, vec2 uv, vec2 gx, vec2 gy, float per) {
  if (uVariacion == 0) return textureGrad(s, uv, gx, gy);
  float k = ruidoP(uv * 0.125, per * 0.125) * 8.0;
  float ia = floor(k), f = fract(k);
  vec2 offa = sin(vec2(3.0, 7.0) * ia), offb = sin(vec2(3.0, 7.0) * (ia + 1.0));
  vec4 a = textureGrad(s, uv + offa, gx, gy), b = textureGrad(s, uv + offb, gx, gy);
  return mix(a, b, smoothstep(0.2, 0.8, f - 0.1 * dot(a.rgb - b.rgb, vec3(1.0))));
}

/* Biplanar projection, like Parallax: the texture is projected onto the two world-axis planes
   that face the normal most, so it doesn't stretch on slopes. */
ivec2 gEjes;
vec2 gPesos;

void biplanar(vec3 n) {
  vec3 m = abs(n);
  int ma = m.x > m.y ? (m.x > m.z ? 0 : 2) : (m.y > m.z ? 1 : 2);
  int mi = m.x < m.y ? (m.x < m.z ? 0 : 2) : (m.y < m.z ? 1 : 2);
  if (mi == ma) mi = (ma + 1) % 3;
  int me = 3 - ma - mi;
  gEjes = ivec2(ma, me);
  vec2 w = pow(vec2(m[ma], m[me]), vec2(8.0));
  w = w / (w.x + w.y);
  // the second only if it weighs something: one read is almost always enough
  if (w.y < 0.02) w = vec2(1.0, 0.0);
  gPesos = w / (w.x + w.y);
}

vec2 plano(vec3 c, int e) { return e == 0 ? c.yz : (e == 1 ? c.zx : c.xy); }

/* A texture in world coordinates (meters), at esc meters per repeat. */
vec4 bip(sampler2D s, vec3 c, vec3 dx, vec3 dy, float esc, float per, bool conVar) {
  vec4 acc = vec4(0.0);
  for (int k = 0; k < 2; k++) {
    float w = k == 0 ? gPesos.x : gPesos.y;
    if (w <= 0.0) continue;
    int e = k == 0 ? gEjes.x : gEjes.y;
    vec2 uv = plano(c, e) / esc;
    vec2 gx = plano(dx, e) / esc, gy = plano(dy, e) / esc;
    acc += w * (conVar ? variada(s, uv, gx, gy, per) : textureGrad(s, uv, gx, gy));
  }
  return acc;
}

/* Parallax's two scales: up close the texture repeats more often and far away less, in powers
   of two by distance, and they're blended in between. That way the tiling never looks too small
   or too large on screen. */
float gLb, gS0, gS1;

vec4 dosEscalas(sampler2D s, vec3 c, vec3 dx, vec3 dy, bool conVar) {
  vec4 a = gLb < 0.999 ? bip(s, c, dx, dy, uDetTile * gS0, uPeriodo / gS0, conVar) : vec4(0.0);
  vec4 b = gLb > 0.001 ? bip(s, c, dx, dy, uDetTile * gS1, uPeriodo / gS1, conVar) : vec4(0.0);
  return mix(a, b, gLb);
}

/* Relief from a normal map (Unity's DXT5nm: x in alpha, y in green) projected on the same
   planes: how much the normal tilts, in world axes. */
vec3 bipRelieve(sampler2D s, vec3 c, vec3 dx, vec3 dy, float esc, float per) {
  vec3 acc = vec3(0.0);
  for (int k = 0; k < 2; k++) {
    float w = k == 0 ? gPesos.x : gPesos.y;
    if (w <= 0.0) continue;
    int e = k == 0 ? gEjes.x : gEjes.y;
    vec2 uv = plano(c, e) / esc;
    vec2 gx = plano(dx, e) / esc, gy = plano(dy, e) / esc;
    vec4 t = variada(s, uv, gx, gy, per);
    vec2 xy = vec2(t.a, t.g) * 2.0 - 1.0;
    acc += w * (e == 0 ? vec3(0.0, xy.x, xy.y) : (e == 1 ? vec3(xy.y, 0.0, xy.x) : vec3(xy.x, xy.y, 0.0)));
  }
  return acc;
}

vec3 relieveDos(sampler2D s, vec3 c, vec3 dx, vec3 dy) {
  vec3 a = gLb < 0.999 ? bipRelieve(s, c, dx, dy, uDetTile * gS0, uPeriodo / gS0) : vec3(0.0);
  vec3 b = gLb > 0.001 ? bipRelieve(s, c, dx, dy, uDetTile * gS1, uPeriodo / gS1) : vec3(0.0);
  return mix(a, b, gLb);
}

/* Displacement blend (Parallax's GetDisplacementLerpFactor): in the transition between two
   textures, the one with more relief at each point wins, so grass peeks out between stones
   instead of fading into them. From afar it softens. */
float mezclaDesp(float h, float d1, float d2, float logD) {
  float suave = mix(0.15, 1.0, clamp(logD * 0.15 - 0.5, 0.0, 1.0));
  d2 = clamp(d2 + h, 0.0, 1.0);
  d1 = clamp(d1 * (1.0 - h), 0.0, 1.0);
  return clamp((d2 - d1) * h / suave, 0.0, 1.0);
}

/* Influence: how much the texture dominates over the planet color. Where it dominates little,
   the map color stays with the texture's pattern (the luminance). */
vec3 conInfluencia(vec3 t, float inf, vec3 base) {
  float lum = dot(t, vec3(0.21, 0.72, 0.07)) + 0.5;
  return mix(base * lum, t, inf);
}

/* Returns the ground color with its detail and, with Parallax's normal maps, tilts `n` with the
   textures' fine relief, which is what gives them light and shadow. */
vec3 detalle(vec3 p, inout vec3 n, vec3 base, float dist) {
  if (uHasDetail == 0 || uDetAmt <= 0.0) return base;
  float amt = uDetAmt * (1.0 - smoothstep(1500.0, 9000.0, dist));
  if (amt <= 0.001) return base;

  // the point's world coordinates, in meters and without losing precision
  vec3 c = uEyeMod + (p - uEye) * uRadiusM;
  vec3 dx = dFdx(c), dy = dFdy(c);
  vec3 up = normalize(p);
  biplanar(n);
  float logD = log2(dist * 0.2 + 0.4);
  float fl = floor(logD);
  gS1 = exp2(fl); gS0 = gS1 * 0.5;
  gLb = clamp(logD - fl, 0.0, 1.0);

  if (uModoParallax != 0) {
    // Parallax mask: low-mid (r), mid-high (g), slope (b)
    float alt = uHasHeight != 0 ? max(terrainH(up), 0.0) : 0.0;
    float r = pct(uPxLowMid.x, uPxLowMid.y, alt);
    float g = pct(uPxMidHigh.x, uPxMidHigh.y, alt);
    float nUp = abs(dot(n, up));
    float b = 1.0 - clamp((pow(nUp, uPxSteep.x) - uPxSteep.z) * uPxSteep.y + uPxSteep.z, 0.0, 1.0);
    bool bajo = alt / max(uPxMidHigh.x + uPxLowMid.y, 1.0) < 0.5;

    if (uPxHasDisp != 0) {
      vec4 d = dosEscalas(uPxDisp, c, dx, dy, false);
      float r2 = mezclaDesp(r, d.r, d.g, logD);
      float g2 = mezclaDesp(g, d.g, d.b, logD);
      float dAlt = bajo ? mix(d.r, d.g, r) : mix(d.g, d.b, g);
      b = mezclaDesp(b, dAlt, d.a, logD);
      r = r2; g = g2;
    }
    float wL = bajo ? 1.0 - r : 0.0, wM = bajo ? r : 1.0 - g, wH = bajo ? 0.0 : g;
    wL *= 1.0 - b; wM *= 1.0 - b; wH *= 1.0 - b;
    float wS = b;

    vec4 inf = uPxHasInf != 0 ? dosEscalas(uPxInf, c, dx, dy, false) : vec4(1.0);
    vec3 col = vec3(0.0);
    if (wL > 0.001) col += wL * conInfluencia(dosEscalas(uDetGrass, c, dx, dy, true).rgb, inf.r, base);
    if (wM > 0.001) col += wM * conInfluencia(dosEscalas(uDetSand, c, dx, dy, true).rgb, inf.g, base);
    if (wH > 0.001) col += wH * conInfluencia(dosEscalas(uDetSnow, c, dx, dy, true).rgb, inf.b, base);
    if (wS > 0.001) col += wS * conInfluencia(dosEscalas(uDetRock, c, dx, dy, true).rgb, inf.a, base);

    if (uPxHasOcc != 0) {
      vec4 o = dosEscalas(uPxOcc, c, dx, dy, false);
      float oAlt = bajo ? mix(o.r, o.g, r) : mix(o.g, o.b, g);
      col *= mix(1.0, mix(oAlt, o.a, b), 0.8);
    }
    if (uPxHasBump != 0) {
      vec3 dn = vec3(0.0);
      if (wL > 0.001) dn += wL * relieveDos(uPxBumpL, c, dx, dy);
      if (wM > 0.001) dn += wM * relieveDos(uPxBumpM, c, dx, dy);
      if (wH > 0.001) dn += wH * relieveDos(uPxBumpH, c, dx, dy);
      if (wS > 0.001) dn += wS * relieveDos(uPxBumpS, c, dx, dy);
      n = normalize(n + dn * amt);
    }
    return mix(base, col, amt);
  }

  float pend = 1.0 - clamp(dot(n, up), 0.0, 1.0);          // 0 flat, grows with the slope
  float roca = smoothstep(0.02, 0.12, pend);
  float verde = clamp((base.g - max(base.r, base.b)) * 6.0, 0.0, 1.0);
  float blanco = smoothstep(0.62, 0.82, min(min(base.r, base.g), base.b));
  float arena = clamp(1.0 - verde - blanco, 0.0, 1.0);

  vec3 d = dosEscalas(uDetGrass, c, dx, dy, true).rgb * verde
         + dosEscalas(uDetSand, c, dx, dy, true).rgb * arena
         + dosEscalas(uDetSnow, c, dx, dy, true).rgb * blanco;
  float suma = max(verde + arena + blanco, 0.001);
  d /= suma;
  d = mix(d, dosEscalas(uDetRock, c, dx, dy, true).rgb, roca);

  // detail modulates, it doesn't paint: it keeps the map color and adds grain
  float lum = dot(d, vec3(0.2126, 0.7152, 0.0722));
  return base * mix(1.0, clamp(lum / 0.42, 0.45, 1.8), amt);
}

void main() {
  gl_FragDepth = 1.0;
  vec2 ndc = gl_FragCoord.xy / uView * 2.0 - 1.0;
  /* Diagnostics: 1 paints the height map's gray where the ray crosses sea level, 2 paints the
     height in meters and 3 the distance to the terrain hit. */
  if (uDebug != 0) {
    vec3 dd = normalize(uF + uR * ndc.x * uTan * uAspect + uU * ndc.y * uTan);
    vec2 tgd = raySphere(uEye, dd, 1.0);
    if (tgd.x <= 0.0) { frag = vec4(0.0, 0.0, 0.25, 1.0); return; }
    vec3 pd = uEye + dd * tgd.x;
    vec3 nd = normalize(pd);
    float latd = asin(clamp(nd.y, -1.0, 1.0)), lond = atan(nd.x, nd.z);
    vec2 uvd = vec2(fract(lond / (2.0 * PI) + 0.5 + uHeightOff), 0.5 - latd / PI);
    if (uDebug == 1) { frag = vec4(vec3(grisSuave(uvd)), 1.0); return; }
    if (uDebug == 2) { frag = vec4(vec3(terrainH(nd) / 8000.0), 1.0); return; }
    vec3 nn; float mar;
    float th = marchTerrain(uEye, dd, nn, mar);
    frag = vec4(th < 0.0 ? vec3(1.0, 0.0, 0.0) : vec3(th * uRadiusM / 30000.0), 1.0);
    return;
  }
  vec3 eye = uEye;
  vec3 d;
  if (uCenital != 0) {
    vec2 ll = uCentro + (gl_FragCoord.xy - 0.5 * uView) / uPpd;
    if (abs(ll.y) > 90.0) { frag = vec4(0.027, 0.043, 0.067, 1.0); return; }
    float la = radians(ll.y), lo = radians(ll.x);
    vec3 n = vec3(cos(la) * sin(lo), sin(la), cos(la) * cos(lo));
    eye = n * uCenitalR;
    d = -n;
  }
  else d = normalize(uF + uR * ndc.x * uTan * uAspect + uU * ndc.y * uTan);
  float el = asin(clamp(dot(d, uUp), -1.0, 1.0));
  float jit = ign(gl_FragCoord.xy);
  vec2 ta = raySphere(eye, d, ATM_TOP);
  vec2 tg = raySphere(eye, d, 1.0);

  vec3 col;
  vec4 facA = vec4(0.0);           // the political map over the ground, already lit
  float tapaNube = 0.0;            // how much the clouds cover it
  float tSuelo = -1.0;             // where the ray hits the real ground (with relief), or nothing
  vec3 plano = vec3(0.0);          // the map color as is, to blend with the flat map
  {
    float t = tg.x;
    vec3 nRel = vec3(0.0); float esMar = 0.0;
    bool relieve = uRelief != 0 && uHasHeight != 0;
    if (relieve) t = marchTerrain(eye, d, nRel, esMar);
    if (t > 0.0) {
      tSuelo = t;
      vec3 p = eye + d * t;
      if (uWriteDepth != 0) gl_FragDepth = profundidad(t * uRadiusM * dot(d, uF));
      float lat = asin(clamp(p.y, -1.0, 1.0));
      float lon = atan(p.x, p.z);
      float u = lon / (2.0 * PI) + 0.5, v = 0.5 - lat / PI;
      /* At the ±180° seam u jumps from 1 to 0 and its derivatives blow up; the one from u
         shifted half a turn is taken, which is continuous there. */
      vec2 uv = vec2(u, v);
      vec2 gx = dFdx(uv), gy = dFdy(uv);
      vec2 uv2 = vec2(fract(u + 0.5), v);
      vec2 gx2 = dFdx(uv2), gy2 = dFdy(uv2);
      if (abs(gx2.x) + abs(gy2.x) < abs(gx.x) + abs(gy.x)) { gx = gx2; gy = gy2; }

      /* With relief, screen derivatives are useless: the hit distance changes abruptly between
         neighboring pixels (silhouettes) and on top of that they're computed inside a branch
         not all pixels take, which is exactly what the language leaves undefined. The mipmap
         went to the coarsest level and the ground came out flat gray. Here the level is
         computed from the size the pixel has on the ground. */
      float lod = 0.0;
      if (relieve) {
        vec3 up0 = normalize(p);
        float cosInc = max(abs(dot(d, up0)), 0.02);
        float huella = t * uRadiusM * uPix / cosInc;            // meters the pixel spans
        float texel = 2.0 * PI * uRadiusM / max(uColorSize.x, 1.0);
        lod = clamp(log2(max(huella / texel, 0.0001)), 0.0, 14.0);
      }

      vec3 base = uTint;
      if (uHasColor != 0) base = relieve
        ? textureLod(uColor, vec2(fract(u + uColorOff), v), lod).rgb
        : textureGrad(uColor, vec2(fract(u + uColorOff), v), gx, gy).rgb;
      plano = base;
      // the sea by color, before mixing in the biomes
      float water = uHasColor != 0 ? smoothstep(0.03, 0.08, base.b - max(base.r, base.g)) : 0.0;
      if (uHasBiome != 0 && uBiomeAmt > 0.0)
        base = mix(base, relieve
          ? textureLod(uBiome, vec2(fract(u + uBiomeOff), v), 0.0).rgb
          : textureGrad(uBiome, vec2(fract(u + uBiomeOff), v), gx, gy).rgb, uBiomeAmt);
      // each point with its own Sun: distant ground may be on the other side of the terminator
      float aguaAmt = water, profA = 300.0;
      vec3 marA = base;
      if (relieve) {
        /* The coast. The color map is kilometers per texel: next to the sea the land inherits
           its blue and, taken for water, came out with the sea's glint and the shore was lost.
           With relief the geometry rules: sea is what the ray finds on the sea sphere. In KSP
           there's no water above sea level (the water is always that sphere), so on land the
           map's blue is always land: sand next to the water (wet at the shore) and, higher up,
           the color of the nearby land. The sea is painted below (see GlobeView.Agua) with its
           depth: the bottom through the water where it's shallow, waves, sun glint and foam on
           the shore. */
        float hSuelo = terrainH(normalize(p));
        aguaAmt = esMar;
        if (esMar > 0.5) {
          profA = max(-hSuelo, 0.0);
          // next to the coast the map has land and sand: the color of its open sea (see MapaDelMar)
          marA = uHasSeaTex != 0 ? textureLod(uSeaTex, vec2(fract(u), v), 0.0).rgb : mix(uSeaColor, base, water);
        } else {
          /* In KSP there are no lakes above the sea: the water is the ocean sphere. The map's
             blue over land is a coastal texel (4.6 km) that falls inside: sand next to the
             water and grass higher up, with the ground textures on top. Taken for a lake, it
             left straight-edged lagoons behind the beach. */
          // the color of the nearby land: the neighboring texels that aren't blue
          vec3 tierra = vec3(0.4, 0.48, 0.26), acc = vec3(0.0);
          float pesoT = 0.0;
          vec2 txl = 1.6 / max(uColorSize, vec2(1.0));
          for (int k = 0; k < 8; k++) {
            float ang = float(k) * 0.7853982;
            vec2 o = vec2(cos(ang), sin(ang)) * txl;
            vec3 cv = textureLod(uColor, vec2(fract(u + uColorOff + o.x), clamp(v + o.y, 0.0, 1.0)), 0.0).rgb;
            float wv = 1.0 - smoothstep(0.03, 0.08, cv.b - max(cv.r, cv.g));
            acc += cv * wv; pesoT += wv;
          }
          if (pesoT > 0.2) tierra = acc / pesoT;
          tierra = mix(tierra, vec3(0.8, 0.74, 0.56), 1.0 - smoothstep(10.0, 40.0, hSuelo));
          // with a blue detection more sensitive than the water's: the bilinear edge between a
          // blue texel and a green one already tints blue before counting as water
          base = mix(base, tierra, smoothstep(-0.03, 0.05, base.b - max(base.r, base.g)));
          base *= mix(0.7, 1.0, smoothstep(0.15, 1.2, hSuelo));  // wet sand on the shore
          water = 0.0;
        }
      }
      else if (water > 0.0 && uHasHeight != 0) profA = max(-terrainH(normalize(p)), 0.0);
      vec3 nSup = relieve ? nRel : normalize(p);
      vec3 L = vec3(0.0);
      // on the map, the eye's distance (fixed, very high) killed the detail at any zoom:
      // we use the one a pixel of the same size would have in flight
      if (aguaAmt < 0.999) {
        base = detalle(p, nSup, base, uCenital != 0 ? uDistCenital : t * uRadiusM);
        L = shadeGround(p, nSup, -d, uSun, pow(base, vec3(2.2)), 0.0);
      }
      if (aguaAmt > 0.001) {
        vec3 up0 = normalize(p);
        // meters per pixel over the water; at grazing angles the pixel stretches into the distance
        float cosInc = max(abs(dot(d, up0)), 0.02);
        float pxm = t * uRadiusM * uPix;
        float pxOla = pxm / mix(sqrt(cosInc), cosInc, 0.6);
        vec3 c = uEyeMod + (p - uEye) * uRadiusM;
        float sig2 = 0.0, alto = 0.0;
        vec3 nA = uOlas != 0 ? aNormalOlas(c, up0, pxOla, sig2, alto) : up0;
        float s2 = (uOlaCapilar + sig2) * aViento(up0);
        // the sky it reflects, with the same air as the real sky (few steps)
        vec3 cielo = vec3(-1.0);
        if (uAtmos != 0 && uCenital == 0) {
          vec3 r = reflect(d, nA);
          r = normalize(r + up0 * max(0.0, 0.03 - dot(r, up0)));
          vec3 trR;
          cielo = inscatterN(p, r, 0.0, max(raySphere(p, r, ATM_TOP).y, 0.0), uSun, 0.5, 8, trR);
        }
        float esp = relieve ? aEspuma(c, up0, profA, alto, pxm / mix(1.0, cosInc, 0.5)) : 0.0;
        L = mix(L, aLuz(p, nA, -d, uSun, marA, uHasHeight != 0 ? profColor(up0, profA) : profA, s2, cielo, esp), aguaAmt);
      }
      // the territories, clipped by the relief's coast (see FaccionesGlsl)
      if (uFacOn != 0 && uCenital == 0) {
        vec3 upF = normalize(p);
        float cosF = max(abs(dot(d, upF)), 0.05);
        float celdas = t * uRadiusM * uPix / sqrt(cosF) / (PI * uRadiusM / uFacSize.y);
        facA = faccionEn(vec2(fract(u), v), celdas) * (uFacConMar != 0 ? 1.0 - aguaAmt : 1.0);
        facA.rgb *= mix(0.45, 1.0, smoothstep(-0.12, 0.2, dot(upF, uSun)));
      }
      vec3 tr = vec3(1.0), ins = vec3(0.0);
      if (uAtmos != 0) ins = inscatter(eye, d, max(ta.x, 0.0), t, uSun, jit, tr);
      col = L * tr + ins;
    } else {
      vec3 tr = vec3(1.0), ins = vec3(0.0);
      if (uAtmos != 0 && ta.y > 0.0) ins = inscatter(eye, d, max(ta.x, 0.0), ta.y, uSun, jit, tr);
      col = ins;
      // the Sun, 1.1° radius seen from Kerbin, with the color the air leaves it
      float ang = acos(clamp(dot(d, uSun), -1.0, 1.0));
      col += uSunI * 10.0 * (1.0 - smoothstep(uSunRad, uSunRad + uPix * 1.5, ang)) * tr;
      // the stars: the sky's brightness hides them, as in real life
      float skyLum = dot(ins, vec3(0.2126, 0.7152, 0.0722)) * uExposure;
      col += starField(d, uPix, uStarShift) * tr * exp(-skyLum * 30.0) * smoothstep(-0.02, 0.08, el);
    }
  }
  /* Clouds: a spherical layer at its height with the game's map. The intersection in front of
     what's already visible (ground or sky) is taken and blended with its coverage. With the
     camera below, the exit intersection counts; above, the entry one. */
  if (uHasClouds != 0 && uCloudAmt > 0.0) {
    vec2 tn = raySphere(eye, d, uCloudR);
    float tc = length(eye) < uCloudR ? tn.y : tn.x;
    /* With the relief's ground, not the sea sphere: looking at a slope almost horizontally the
       ray doesn't reach the sea, and the cloud got painted over the hill. */
    if (tc > 0.0 && (tSuelo <= 0.0 || tc < tSuelo)) {
      vec3 pc = eye + d * tc;
      vec3 nc = normalize(pc);
      float latc = asin(clamp(nc.y, -1.0, 1.0)), lonc = atan(nc.x, nc.z);
      vec2 uvc = nubeUv(nc, vec2(lonc / (2.0 * PI) + 0.5 + uCloudOff, 0.5 - latc / PI));
      uvc.x = fract(uvc.x);
      // the mipmap level, as on the ground, from the pixel size on the layer
      float huella = tc * uRadiusM * uPix;
      float texel = 2.0 * PI * uRadiusM / 2048.0;
      float lodc = clamp(log2(max(huella / texel, 0.0001)), 0.0, 12.0);
      vec4 nube = textureLod(uCloudTex, uvc, lodc);
      float a = clamp(nube.a * uCloudAmt * nubeVida(nc), 0.0, 1.0);
      // up close, the mod's detail, which moves with the layer
      float lonN = lonc + uCloudOff * 2.0 * PI;
      a = clamp(a * nubeDetalle(vec2(lonN * cos(latc), latc) * uRadiusM, 1.0 - smoothstep(4000.0, 30000.0, tc * uRadiusM)), 0.0, 1.0);
      if (a > 0.002) {
        // simple lighting: the Sun above the layer, some sky below it
        vec3 luz = uSunI * sunTransmittance(pc, uSun) * max(dot(nc, uSun), 0.0) * 0.55
                 + uSunI * vec3(0.05, 0.07, 0.12) * 0.5;
        vec3 colNube = nube.rgb * luz / PI;
        // whatever is behind is attenuated by the air in front of the cloud
        vec3 trN = vec3(1.0), insN = vec3(0.0);
        if (uAtmos != 0) insN = inscatter(eye, d, max(ta.x, 0.0), tc, uSun, jit, trN);
        col = mix(col, colNube * trN + insN, a);
        tapaNube = a;
        // a thick cloud hides whatever is behind it, trees included
        if (uWriteDepth != 0 && a > 0.5) gl_FragDepth = min(gl_FragDepth, profundidad(tc * uRadiusM * dot(d, uF)));
      }
    }
  }

  col = toneMap(col);
  if (facA.a > 0.0) col = col * (1.0 - facA.a * (1.0 - tapaNube)) + facA.rgb * (1.0 - tapaNube);

  /* Altitude and azimuth grid: circles every 15° and meridians every 30°. The azimuth jumps at
     ±180°; its derivative is taken from the version that doesn't jump. */
  if (uGrid != 0) {
    float elDeg = el * 57.29578;
    float az = atan(dot(d, uEast), dot(d, uNorth)) * 57.29578;
    float aa = fwidth(elDeg);
    float lineEl = 1.0 - smoothstep(0.0, aa * 1.5, abs(fract(elDeg / 15.0 + 0.5) - 0.5) * 15.0);
    float fa = min(fwidth(az), fwidth(mod(az + 360.0, 360.0)));
    float lineAz = 1.0 - smoothstep(0.0, fa * 1.5, abs(fract(az / 30.0 + 0.5) - 0.5) * 30.0);
    lineAz *= step(0.0, elDeg) * (1.0 - smoothstep(72.0, 84.0, elDeg));
    float g = max(lineEl * step(0.5, elDeg), lineAz);
    col = mix(col, vec3(0.75, 0.85, 1.0), g * 0.16);
    float horizon = 1.0 - smoothstep(0.0, aa * 2.0, abs(elDeg));
    col = mix(col, vec3(0.31, 0.64, 1.0), horizon * 0.6);
  }

  /* On the 2D map, when zooming in, it starts from the flat map's look and the light and detail
     come in gradually: the change from one to the other is barely noticeable. */
  if (uCenital != 0 && uPlano > 0.0 && tSuelo > 0.0) col = mix(col, plano, uPlano);

  frag = vec4(col, 1.0);
}";

        void InitSky()
        {
            skyProg = new ShaderProgram(SkyVS, SkyFS);
            skyVao = GL.GenVertexArray();
        }

        void DisposeSky()
        {
            skyProg?.Dispose();
            GL.DeleteVertexArray(skyVao);
            DisposeStars();
        }

        /* Heading (0 = north, 90 = east) and elevation above the horizon under a pixel. */
        public (double az, double el) SkyDirAt(double px, double py)
        {
            var d = RayDir(px, py);
            LocalBasis(eyeL, out var up, out var east, out var north);
            double el = Math.Asin(Math.Clamp(Dot(d, up), -1, 1)) * R2D;
            double az = Math.Atan2(Dot(d, east), Dot(d, north)) * R2D;
            return ((az + 360) % 360, el);
        }

        /* Heading and elevation of the Sun for the observer (it's so far away that the
           observer's height doesn't matter). */
        public (double az, double el) SunAltAz()
        {
            LocalBasis(ObserverPos(), out var up, out var east, out var north);
            var s = SunDir;
            double az = Math.Atan2(Dot(s, east), Dot(s, north)) * R2D;
            return ((az + 360) % 360, Math.Asin(Math.Clamp(Dot(s, up), -1, 1)) * R2D);
        }

        public void SkyNudge(double daz, double del)
        {
            SkyAz = ((SkyAz + daz) % 360 + 360) % 360;
            SkyEl = Math.Clamp(SkyEl + del, -89, 89);
        }

        /* The nearby ground for the 2D map, seen from above in its projection: the same ground
           as flight (detail relief, game textures, coasts) and the buildings in plan view. The
           map then paints its grid, tracks and markers on top. */
        bool cenital, cenSolReal;
        double cenPlano;
        double cenLat, cenLon, cenPpd;
        double CenitalAltM => Math.Max(HMax, 0) + 5000;       // the eye, above everything
        public double CercaCenital = 1;                        // exposure: 0 the globe's, 1 the landscape one

        public void RenderCenital(Batch2D batch, TextCache tc, double lat, double lon, double ppd, bool solReal, double plano = 0)
        {
            if (skyProg == null) return;
            cenital = true; cenLat = lat; cenLon = lon; cenPpd = ppd; cenSolReal = solReal; cenPlano = Math.Clamp(plano, 0, 1);
            try
            {
                var n = Sph(lat, lon, 1);
                LocalBasis(n, out _, out _, out var north);
                RenderSky(batch, tc, new Cam { Eye = Scale(n, 1 + CenitalAltM / Body.Radius), Target = n, Up = north, Fov = 30 });
            }
            finally { cenital = false; }
        }

        void RenderSky(Batch2D batch, TextCache tc, Cam cam)
        {
            GL.Viewport(0, 0, W, H);
            GL.ClearColor(0, 0, 0, 1);
            GL.Clear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT);
            GL.Disable(GL.DEPTH_TEST);
            GL.Disable(GL.CULL_FACE);

            var eye = cam.Eye;
            SetupCamera(cam, 2e-6, Len(eye) + SceneR + 2);
            var right = Cross(fwdL, upL);
            right = Len(right) < 1e-9 ? new double[] { 1, 0, 0 } : Norm(right);
            var camUp = Cross(right, fwdL);
            LocalBasis(eye, out var up, out var east, out var north);
            double tan = Math.Tan(cam.Fov * D2R / 2);

            skyProg.Use();
            skyProg.Vec2("uView", W, H);
            skyProg.Vec3("uEye", eye[0], eye[1], eye[2]);
            skyProg.Float("uC", Dot(eye, eye) - 1);
            skyProg.Vec3("uF", fwdL[0], fwdL[1], fwdL[2]);
            skyProg.Vec3("uR", right[0], right[1], right[2]);
            skyProg.Vec3("uU", camUp[0], camUp[1], camUp[2]);
            skyProg.Vec3("uUp", up[0], up[1], up[2]);
            skyProg.Vec3("uEast", east[0], east[1], east[2]);
            skyProg.Vec3("uNorth", north[0], north[1], north[2]);
            skyProg.Float("uTan", tan);
            skyProg.Float("uAspect", (double)W / H);
            skyProg.Float("uPix", 2 * tan / H);
            skyProg.Int("uCenital", cenital ? 1 : 0);
            if (cenital)
            {
                skyProg.Vec2("uCentro", cenLon, cenLat);
                skyProg.Float("uPpd", cenPpd);
                skyProg.Float("uCenitalR", 1 + CenitalAltM / Body.Radius);
                // what a pixel spans on the ground is t·R·uPix, with t the eye height
                double mpp = Body.Radius * D2R / cenPpd;
                skyProg.Float("uPix", mpp / CenitalAltM);
                // in flight (70° field, 1080 pixels tall) a pixel spans 0.0013 radians
                skyProg.Float("uDistCenital", mpp / 0.0013);
                skyProg.Float("uPlano", cenPlano);
            }
            skyProg.Float("uAlt", Len(eye) - 1);
            skyProg.Float("uStarShift", orbitShift);
            skyProg.Int("uGrid", SkyGrid && Mode == CamMode.Sky && !cenital ? 1 : 0);
            // without day and night, the Sun stays overhead the observer; forcing night, off
            var sun = (cenital ? cenSolReal : Light) ? SunDir : up;
            // without day and night, the map is lit like a hillshade: from the
            // northwest at 45°, which is where you expect the light when reading a map
            if (cenital && !cenSolReal) sun = Norm(Add(Add(Scale(up, 1), north, 0.7071), east, -0.7071));
            skyProg.Vec3("uSun", sun[0], sun[1], sun[2]);
            skyProg.Int("uSinBrillo", cenital ? 1 : 0);
            skyProg.Float("uSunRad", Math.Max(SunAngularRadius, 0.0015));
            AtmosUniforms(skyProg, 24, sunOn: !SkyForceNight);
            if (cenital) skyProg.Int("uAtmos", 0);           // a map: without the haze of 10 km of air
            // at ground level, the landscape exposure setting (see shadeGround); coming down
            // from the globe it blends with the outside one so it doesn't jump
            skyProg.Float("uCerca", cenital ? CercaCenital : Mode == CamMode.Planet ? 1 - SuaveEntre((Len(eye) - 1) * Body.Radius, 3000, 40000) : 1);
            skyProg.Float("uColorOff", ColorOff / 360);
            skyProg.Float("uBiomeOff", BiomeOff / 360);
            skyProg.Float("uBiomeAmt", BiomeTex != null ? BiomeAmt : 0);
            skyProg.Int("uHasColor", ColorTex != null ? 1 : 0);
            skyProg.Vec3("uSeaColor", SeaColor[0], SeaColor[1], SeaColor[2]);
            // not every GPU has unit 16 (the minimum is 16: from 0 to 15)
            bool marTex = SeaTex != null && GL.MaxTextureUnits > 16;
            skyProg.Int("uHasSeaTex", marTex ? 1 : 0);
            if (marTex) { BindTex(16, SeaTex); skyProg.Int("uSeaTex", 16); }
            skyProg.Int("uHasBiome", BiomeTex != null ? 1 : 0);
            /* Relief: only with a height map, and always in the free camera; standing on the
               ground too, which is what makes the mountains show up close. And coming down in
               the 3D view, which below RadioCerca is painted with this very same thing: it has
               to look the same as flight. */
            bool cerca = cenital || Mode == CamMode.Free || Mode == CamMode.Sky || Mode == CamMode.Planet;
            bool relieve = HeightTex != null && FreeRelief && cerca;
            skyProg.Int("uHasHeight", HeightTex != null ? 1 : 0);
            skyProg.Int("uRelief", relieve ? 1 : 0);
            skyProg.Float("uHeightOff", HeightOff / 360);
            skyProg.Float("uHMin", HMin);
            var tesela = Tesela;
            bool conTesela = tesela != null && TeselaTex != null;
            skyProg.Int("uTesela", conTesela ? 1 : 0);
            if (conTesela)
            {
                BindTex(15, TeselaTex); skyProg.Int("uTeselaTex", 15);
                skyProg.Vec4("uTeselaRect", tesela.X0, tesela.Y0, tesela.Tw, tesela.Th);
                skyProg.Vec3("uTeselaNivel", tesela.W, tesela.H, tesela.Offset / 360);
                skyProg.Vec3("uTeselaRango", tesela.Min, tesela.Max, tesela.Margen);
            }
            int nFlat = Math.Min(4, Aplanados?.Count ?? 0);
            skyProg.Int("uFlatCount", nFlat);
            for (int i = 0; i < nFlat; i++)
            {
                var a = Aplanados[i];
                skyProg.Vec4("uFlat[" + i + "]", a.N[0], a.N[1], a.N[2], a.H);
                skyProg.Vec2("uFlatR[" + i + "]", a.R0, a.R1);
            }
            skyProg.Float("uHMax", HMax);
            skyProg.Float("uRadiusM", Body.Radius);
            // shell above the highest peak, which is where the search for the ground starts
            skyProg.Float("uTopR", 1 + Math.Max(0, HMax) / Body.Radius);
            skyProg.Vec2("uHeightSize", HeightTex?.Width ?? 1, HeightTex?.Height ?? 1);
            skyProg.Vec2("uColorSize", ColorTex?.Width ?? 1, ColorTex?.Height ?? 1);
            BindTex(0, ColorTex); skyProg.Int("uColor", 0);
            BindTex(1, BiomeTex); skyProg.Int("uBiome", 1);
            BindTex(2, HeightTex); skyProg.Int("uHeightTex", 2);
            /* Ground detail: only near the ground, which is where it shows, and with the game's
               four textures loaded. The origin is in meters already reduced to the tile size,
               so precision isn't lost when it's added in the shader. */
            bool det = Detail && HasDetail && cerca;
            skyProg.Int("uDebug", Debug);
            bool nubes = CloudTex != null && Clouds && !cenital;
            skyProg.Int("uHasClouds", nubes ? 1 : 0);
            skyProg.Float("uCloudR", 1 + CloudAlt / Body.Radius);
            skyProg.Float("uCloudAmt", nubes ? CloudAmount : 0);
            skyProg.Float("uCloudOff", NubeGiro());
            NubeUniforms(skyProg, 17);
            BindTex(7, CloudTex); skyProg.Int("uCloudTex", 7);
            skyProg.Int("uHasDetail", det ? 1 : 0);
            skyProg.Float("uDetTile", DetailTile);
            skyProg.Int("uModoParallax", DetailParallax ? 1 : 0);
            skyProg.Vec2("uPxLowMid", PxLowMid.a, PxLowMid.b);
            skyProg.Vec2("uPxMidHigh", PxMidHigh.a, PxMidHigh.b);
            skyProg.Vec3("uPxSteep", PxSteep.power, PxSteep.contrast, PxSteep.mid);
            skyProg.Float("uDetAmt", det ? DetailAmount : 0);
            double lat0 = Mode == CamMode.Free ? FreeLat : ObsLat, lon0 = Mode == CamMode.Free ? FreeLon : ObsLon;
            if (Mode == CamMode.Planet) { var g = EyeGround(); lat0 = g.Lat; lon0 = g.Lon; }
            if (cenital) { lat0 = cenLat; lon0 = cenLon; }
            /* The modulus is a multiple of all the textures' periods (4096 repeats: the largest
               scale used is 1024) and of their variation noise. */
            double periodo = DetailTile * 4096;
            double Mod(double v) => ((v % periodo) + periodo) % periodo;
            skyProg.Vec3("uEyeMod", Mod(eye[0] * Body.Radius), Mod(eye[1] * Body.Radius), Mod(eye[2] * Body.Radius));
            skyProg.Float("uPeriodo", 4096);
            AguaUniforms(skyProg, periodo);
            // the political map, only when coming down in the planet view (unit 18, if there is one)
            FacUniforms(skyProg, 18, Mode == CamMode.Planet && !cenital && GL.MaxTextureUnits > 18);
            skyProg.Int("uVariacion", DetailVariation ? 1 : 0);
            skyProg.Int("uPxHasInf", DetailParallax && PxInfluence != null ? 1 : 0);
            skyProg.Int("uPxHasDisp", DetailParallax && PxDisplacement != null ? 1 : 0);
            skyProg.Int("uPxHasOcc", DetailParallax && PxOcclusion != null ? 1 : 0);
            BindTex(8, PxInfluence); skyProg.Int("uPxInf", 8);
            BindTex(9, PxDisplacement); skyProg.Int("uPxDisp", 9);
            BindTex(10, PxOcclusion); skyProg.Int("uPxOcc", 10);
            bool bump = DetailParallax && PxBump != null && PxBump.All(t => t != null);
            skyProg.Int("uPxHasBump", bump ? 1 : 0);
            BindTex(11, bump ? PxBump[0] : null); skyProg.Int("uPxBumpL", 11);
            BindTex(12, bump ? PxBump[1] : null); skyProg.Int("uPxBumpM", 12);
            BindTex(13, bump ? PxBump[2] : null); skyProg.Int("uPxBumpH", 13);
            BindTex(14, bump ? PxBump[3] : null); skyProg.Int("uPxBumpS", 14);
            BindTex(3, DetGrass); skyProg.Int("uDetGrass", 3);
            BindTex(4, DetSand); skyProg.Int("uDetSand", 4);
            BindTex(5, DetRock); skyProg.Int("uDetRock", 5);
            BindTex(6, DetSnow); skyProg.Int("uDetSnow", 6);
            /* With scatters, the sky leaves in the depth buffer where the ground is. It's only
               written with the test enabled, so it's enabled without discarding anything. */
            bool scatters = ScattersActive && !cenital;
            bool edificios = cenital ? HasBuildings || HasGroundVessels : StaticsActive;
            bool profundidad = scatters || edificios;
            skyProg.Int("uWriteDepth", profundidad ? 1 : 0);
            skyProg.Float("uDepthFar", cenital ? CenitalAltM + 1000 : ScatterFar);
            if (profundidad)
            {
                GL.Enable(GL.DEPTH_TEST);
                GL.DepthFunc(GL.ALWAYS);
                GL.DepthMask(true);
            }
            GL.BindVertexArray(skyVao);
            GL.DrawArrays(GL.TRIANGLES, 0, 3);
            GL.BindVertexArray(0);
            if (scatters)
            {
                try { DrawScatters(eye, right, camUp, tan, lat0, lon0); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[scatters] " + ex.Message); }
            }
            else ScatterVisibleReset();
            if (edificios)
            {
                try
                {
                    if (cenital)
                    {
                        // orthographic in the map's projection: on it a degree of longitude measures
                        // the same as one of latitude, so east is stretched by 1/cos(lat)
                        double pxN = cenPpd / (Body.Radius * D2R);
                        double pxE = pxN / Math.Max(Math.Cos(cenLat * D2R), 0.01);
                        double radio = Math.Sqrt(W * W + H * H) / 2 / pxN;
                        DrawStatics(eye, right, camUp, tan, new[] { pxE / (W / 2.0), pxN / (H / 2.0) }, radio, CenitalAltM + 1000);
                    }
                    else DrawStatics(eye, right, camUp, tan);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[edificios] " + ex.Message); }
            }
            else StaticsVisible = 0;
            if (profundidad)
            {
                // what comes after (orbits, labels) doesn't count on this depth
                GL.DepthFunc(GL.LESS);
                GL.Clear(GL.DEPTH_BUFFER_BIT);
                GL.Disable(GL.DEPTH_TEST);
                GL.Disable(GL.CULL_FACE);
            }
            if (cenital) return;                            // the map puts the rest on top

            // orbits and tracks, hidden by the planet with the shader's ray cut
            DrawOrbits(eye, occlude: true);
            DrawTrack(eye, occlude: true, ground: false);

            batch.Begin(W, H);
            if (Mode == CamMode.Planet) DrawFacEtiquetas(batch, tc, eye, cam.Fov);
            PlacePins(batch, tc, eye, vesselsOnly: Mode != CamMode.Planet);
            DrawTrazos(batch, eye);
            if (SkyGrid && Mode == CamMode.Sky) DrawCompass(batch, tc, eye, up, east, north);
            batch.End();
        }

        static readonly string[] Rumbos = { "N", "NE", "E", "SE", "S", "SO", "O", "NO" };

        void DrawCompass(Batch2D b, TextCache tc, double[] eye, double[] up, double[] east, double[] north)
        {
            for (int i = 0; i < 8; i++)
            {
                double az = i * 45 * D2R;
                var dir = Add(Scale(north, Math.Cos(az)), east, Math.Sin(az));
                var p = Add(Add(eye, dir, 0.05), up, 0.0004);
                if (!ToScreen(p, out double sx, out double sy)) continue;
                if (sx < -20 || sx > W + 20 || sy < -20 || sy > H + 20) continue;
                bool main = i % 2 == 0;
                var t = tc.Get(Rumbos[i], new TextStyle("Segoe UI", (main ? 13 : 11) * S, main, unchecked((int)(i == 0 ? 0xFF4EA3FF : 0xFFDBE6F2)), true));
                if (t == null) continue;
                b.Text(t, sx - t.TextW / 2.0, sy - t.TextH - 4 * S);
            }
            // elevations along the meridian of the heading being looked at
            double azLook = SkyAz * D2R;
            var h = Add(Scale(north, Math.Cos(azLook)), east, Math.Sin(azLook));
            foreach (int e in new[] { 15, 30, 45, 60, 75 })
            {
                double er = e * D2R;
                var dir = Add(Scale(h, Math.Cos(er)), up, Math.Sin(er));
                if (!ToScreen(Add(eye, dir, 0.05), out double sx, out double sy)) continue;
                if (sx < 0 || sx > W || sy < 0 || sy > H) continue;
                var t = tc.Get(e + "°", new TextStyle(UI.Theme.MonoFamily, 10 * S, false, unchecked((int)0xB3C8DEF5), true));
                b.Text(t, sx + 4 * S, sy - t.TextH / 2.0);
            }
        }
    }
}
