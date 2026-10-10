using System;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* The sea, shared by the globe seen from outside and by the nearby ground (sky, flight, the
       globe descent and the close-up 2D map).

       The surface is still the ocean sphere; waves only tilt the normal. They're a sum of eight
       wave trains from 2 to 60 m, with deep-water wave speed (ω² = g·k), which fade out when a
       wavelength spans few pixels. What fades isn't lost: its slope goes into the roughness of
       the sun glint (a Beckmann distribution with the Cox and Munk variance). That way, up close
       you see the waves and the scattered sparkles, and from afar the reflection widens on its
       own into the glint patch seen from orbit, with smoother and choppier areas depending on
       the wind.

       Underneath, the water shows the bottom where it's shallow: the light going down and up is
       attenuated per channel (red is lost right away), so the sand of the shore turns turquoise
       and then the color of the open sea from the map (which on Eve is purple). On the shore,
       foam that arrives in lines with the waves. */
    public sealed partial class GlobeView
    {
        /* Waves and foam; without them, the usual smooth sea with its glint. */
        public bool Olas = true;
        /* Real-clock seconds to move the waves. They don't follow simulation time: at x1000
           they'd be a boiling pot. */
        public double AguaT;

        /* Wavelength (m), rotation relative to the wind (degrees) and steepness (a·k) of each
           train: sixteen waves from 80 to 1.6 m, spread on a logarithmic scale with some
           randomness and directions fanned out around the wind, and eight ripples from 1.4 m to
           25 cm that only show within a few meters (without them, at water level the sea came
           out smooth and blurry). With few, neatly ordered trains, the sea came out striped,
           like a fabric. Fixed (same seed) so they don't change on startup. */
        const int NumOlas = 24, NumLargas = 16;
        static readonly (double L, double Giro, double Pend)[] TrenesDeOlas = CrearTrenes();
        const double RumboViento = 35;            // in the projection plane, degrees
        const double CeldaGrupos = 170;           // meters: the size of the wave groups

        static (double, double, double)[] CrearTrenes()
        {
            var r = new Random(1971);
            var t = new (double, double, double)[NumOlas];
            for (int i = 0; i < NumOlas; i++)
            {
                if (i >= NumLargas)
                {
                    // ripples: from any side, steeper
                    double lr = 1.4 * Math.Pow(0.25 / 1.4, (i - NumLargas) / (NumOlas - NumLargas - 1.0)) * (0.9 + 0.2 * r.NextDouble());
                    t[i] = (lr, (r.NextDouble() * 2 - 1) * 95, 0.07);
                    continue;
                }
                double l = 80 * Math.Pow(1.6 / 80, i / (NumLargas - 1.0)) * (0.88 + 0.24 * r.NextDouble());
                // two dice: more trains near the wind than to the side. The swell (the long ones)
                // comes almost all from one side; the short ones, from anywhere
                double abierto = i < 4 ? 22 : i < 9 ? 50 : 80;
                double giro = ((r.NextDouble() + r.NextDouble()) - 1) * abierto;
                if (i == 6) giro += 63;                                               // a bit of cross sea
                double pend = l > 12 ? 0.062 : l > 4 ? 0.056 : 0.048;
                t[i] = (l, giro, pend);
            }
            return t;
        }

        /* Close enough to see the waves or the foam move. */
        public bool MarAnimating
        {
            get
            {
                if (!Olas || !Body.Current.Ocean || HeightTex == null) return false;
                if (Mode != CamMode.Free && Mode != CamMode.Sky && !PlanetaCerca) return false;
                return EyeGround().Agl < 6000;
            }
        }

        /* The wave trains in the projection plane: the wavenumber is an integer multiple of
           2π/period so the pattern doesn't jump when uEyeMod wraps around, and the phase is
           computed here in double precision. */
        void AguaUniforms(ShaderProgram p, double periodo)
        {
            bool olas = Olas && Body.Current.Ocean;
            p.Int("uOlas", olas ? 1 : 0);
            p.Float("uAguaT", AguaT % 8000);
            double g = Math.Max(0.1, Body.Mu / (Body.Radius * Body.Radius));
            double sigma = 0;
            for (int i = 0; i < TrenesDeOlas.Length; i++)
            {
                var (l, giro, pend) = TrenesDeOlas[i];
                double rumbo = (RumboViento + giro) * D2R;
                double nx = Math.Round(Math.Cos(rumbo) * periodo / l), ny = Math.Round(Math.Sin(rumbo) * periodo / l);
                double kx = 2 * Math.PI * nx / periodo, ky = 2 * Math.PI * ny / periodo;
                double k = Math.Sqrt(kx * kx + ky * ky);
                if (k <= 0) { p.Vec4("uOla[" + i + "]", 1, 0, 0, 0); continue; }
                double fase = Math.Sqrt(g * k) * AguaT % (2 * Math.PI);
                p.Vec4("uOla[" + i + "]", kx, ky, olas ? pend / k : 0, fase);
                sigma += 0.5 * pend * pend;
            }
            /* What's never drawn (capillary waves, centimeters long): together with the waves
               it adds up to about 0.03, Cox and Munk's value for a 6 m/s wind. */
            p.Float("uOlaCapilar", olas ? 0.004 : 0.03);
            p.Float("uOlaSigmaTotal", (olas ? sigma : 0) + (olas ? 0.004 : 0.03));
            // the groups: a periodic noise with an integer number of cells per period
            double n = Math.Max(1, Math.Round(periodo / CeldaGrupos));
            p.Vec2("uOlaGrupo", n / periodo, n);
        }

        /* uSinBrillo (the map seen from above) removes reflections; uCerca is 0 from outside. */
        const string AguaGlsl = @"
uniform int uOlas;
uniform float uAguaT;
uniform vec4 uOla[24];          // wavenumber (rad/m) in the projection plane, amplitude (m) and phase
uniform float uOlaCapilar, uOlaSigmaTotal;
uniform vec2 uOlaGrupo;         // group cells per meter, and per period

float aHash(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }
float aHash2(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }

/* Value noise that repeats every per cells: with uEyeMod's period, it doesn't jump. */
float aRuidoP(vec2 p, float per) {
  vec2 i = floor(p), f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  vec2 i1 = mod(i + 1.0, per);
  i = mod(i, per);
  return mix(mix(aHash2(i), aHash2(vec2(i1.x, i.y)), f.x), mix(aHash2(vec2(i.x, i1.y)), aHash2(i1), f.x), f.y);
}

/* Smooth 3D value noise, for the wind zones (on directions: no precision problems). */
float aRuido(vec3 x) {
  vec3 i = floor(x), f = fract(x);
  f = f * f * (3.0 - 2.0 * f);
  return mix(mix(mix(aHash(i), aHash(i + vec3(1, 0, 0)), f.x), mix(aHash(i + vec3(0, 1, 0)), aHash(i + vec3(1, 1, 0)), f.x), f.y),
             mix(mix(aHash(i + vec3(0, 0, 1)), aHash(i + vec3(1, 0, 1)), f.x), mix(aHash(i + vec3(0, 1, 1)), aHash(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

/* How choppy the sea is in each zone: the wind doesn't blow the same everywhere, and from orbit
   the sun glint comes out patchy (smoother in the lee of the coast). */
float aViento(vec3 dir) {
  float n = aRuido(dir * 37.0) * 0.65 + aRuido(dir * 113.0) * 0.35;
  return mix(0.55, 1.45, smoothstep(0.2, 0.8, n));
}

/* The waves on a plane: (dh/da, dh/db, h) and, separately, the slope variance of the trains
   that can no longer be drawn at px meters per pixel. The trains come in groups: two slow
   noises raise and lower their amplitude, like in the real sea, where waves arrive in sets. */
vec3 aOlasPlano(vec2 c, float px, out float sig2) {
  vec3 r = vec3(0.0);
  sig2 = 0.0;
  vec2 q = c * uOlaGrupo.x;
  float g1 = 0.2 + 1.6 * smoothstep(0.15, 0.85, aRuidoP(q, uOlaGrupo.y));
  float g2 = 0.2 + 1.6 * smoothstep(0.15, 0.85, aRuidoP(q * 3.0 + 41.0, uOlaGrupo.y * 3.0));
  for (int i = 0; i < 24; i++) {
    vec4 o = uOla[i];
    float k = length(o.xy);
    float f = smoothstep(3.0, 10.0, 6.2831853 / (k * max(px, 1e-4)));
    float pend = o.z * k;
    sig2 += 0.5 * pend * pend * (1.0 - f * f);
    if (f <= 0.0) continue;
    float ph = dot(o.xy, c) - o.w;
    float a = o.z * f * ((i & 1) == 0 ? g1 : g2);
    r += vec3(o.xy * (a * cos(ph)), a * sin(ph));
  }
  return r;
}

/* The water normal: the waves are projected on the two world-axis planes that face the vertical
   most (like the ground textures) and blended where the plane changes. c in meters. */
vec3 aNormalOlas(vec3 c, vec3 up, float px, out float sig2, out float alto) {
  vec3 m = abs(up);
  int ma = m.x > m.y ? (m.x > m.z ? 0 : 2) : (m.y > m.z ? 1 : 2);
  int mi = m.x < m.y ? (m.x < m.z ? 0 : 2) : (m.y < m.z ? 1 : 2);
  if (mi == ma) mi = (ma + 1) % 3;
  int me = 3 - ma - mi;
  vec2 w = pow(vec2(m[ma], m[me]), vec2(8.0));
  w /= w.x + w.y;
  if (w.y < 0.02) w = vec2(1.0, 0.0);
  w /= w.x + w.y;
  vec3 grad = vec3(0.0);
  sig2 = 0.0; alto = 0.0;
  for (int k = 0; k < 2; k++) {
    float wk = k == 0 ? w.x : w.y;
    if (wk <= 0.0) continue;
    int e = k == 0 ? ma : me;
    vec2 cc = e == 0 ? c.yz : (e == 1 ? c.zx : c.xy);
    vec3 ax = e == 0 ? vec3(0, 1, 0) : (e == 1 ? vec3(0, 0, 1) : vec3(1, 0, 0));
    vec3 bx = e == 0 ? vec3(0, 0, 1) : (e == 1 ? vec3(1, 0, 0) : vec3(0, 1, 0));
    float s;
    vec3 o = aOlasPlano(cc, px, s);
    grad += wk * (o.x * ax + o.y * bx);
    alto += wk * o.z;
    sig2 += wk * s;
  }
  grad -= up * dot(grad, up);
  return normalize(up - grad);
}

float aBeckmann(float NoH, float s2) {
  float c2 = max(NoH * NoH, 1e-4);
  return exp(-(1.0 - c2) / (c2 * s2)) / (PI * s2 * c2 * c2);
}

float aSmith(float NoX, float s2) {
  float c = NoX / (sqrt(s2) * sqrt(max(1.0 - NoX * NoX, 1e-6)));
  return c >= 1.6 ? 1.0 : (3.535 * c + 2.181 * c * c) / (1.0 + 2.276 * c + 2.577 * c * c);
}

/* Water albedo (linear): the bottom seen through prof meters, over the open-sea color from the
   map. Sand next to the shore and, deeper, a darker bottom. */
vec3 aAlbedo(vec3 mar, float prof, float cosV) {
  vec3 hondo = pow(mar, vec3(2.2)) * 0.7;
  vec3 fondo = pow(mix(vec3(0.80, 0.74, 0.56), vec3(0.36, 0.40, 0.30), smoothstep(3.0, 22.0, prof)), vec3(2.2)) * 0.55;
  // light path: down vertically and up toward the eye, refracted (more vertical than the view ray)
  float camino = prof * (1.0 + 1.0 / mix(1.0, max(cosV, 0.05), 0.35));
  vec3 T = exp(-vec3(0.30, 0.065, 0.045) * camino);
  return mix(hondo, fondo, T);
}

/* Foam: on the shore, lines that arrive with the waves and break up; offshore, the odd broken
   crest when waves are visible. pxm is meters per pixel. */
float aEspuma(vec3 c, vec3 dir, float prof, float alto, float pxm) {
  if (uOlas == 0) return (1.0 - smoothstep(0.1, 1.4, prof)) * (1.0 - smoothstep(12.0, 60.0, pxm)) * 0.75;
  float lejos = 1.0 - smoothstep(10.0, 70.0, pxm);
  if (lejos <= 0.0) return 0.0;
  float ruptura = aRuido(c * 0.35) * 0.6 + aRuido(c * 1.3) * 0.4;
  // lines advancing toward the shore: the phase grows with depth and with time
  float linea = sin(prof * 2.2 + uAguaT * 1.1 + ruptura * 5.0);
  float orilla = 1.0 - smoothstep(0.3, 3.2, prof);
  float f = orilla * (0.55 * (1.0 - smoothstep(0.0, 0.9, prof)) + smoothstep(0.35, 0.95, linea) * smoothstep(0.25, 0.6, ruptura));
  // the odd broken crest offshore, only where the waves are drawn
  float cresta = smoothstep(2.0, 2.9, alto) * smoothstep(0.65, 0.9, ruptura) * (1.0 - smoothstep(0.4, 2.0, pxm)) * 0.35;
  return clamp(max(f, cresta), 0.0, 1.0) * lejos;
}

/* Light leaving the water toward the eye. n: normal with the waves; s2: glint roughness; mar:
   open-sea color (sRGB); cielo: sky light in the reflected direction (or negative to estimate it);
   espuma: how much it's covered. */
vec3 aLuz(vec3 p, vec3 n, vec3 v, vec3 s, vec3 mar, float prof, float s2, vec3 cielo, float espuma) {
  vec3 up = normalize(p);
  float alb = mix(0.5, 0.36, uCerca);
  float comp = 0.5 / alb;
  vec3 sun = uSunI * sunTransmittance(p, s);
  vec3 skyE = uSunI * (uAtmos != 0 ? vec3(0.05, 0.085, 0.16) : vec3(0.012)) * smoothstep(-0.12, 0.4, dot(up, s));
  const vec3 nightE = vec3(0.35, 0.42, 0.62);
  float NoV = clamp(dot(n, v), 1e-3, 1.0);
  float cosS = max(dot(up, s), 0.0);
  vec3 albedo = aAlbedo(mar, prof, dot(up, v)) * alb;

  // the map seen from above: the water with its color, light as on the ground and the waves' relief
  if (uSinBrillo != 0) {
    vec3 L = albedo / PI * (sun * max(dot(n, s), 0.0) + (skyE * (0.75 + 0.25 * dot(n, up)) + nightE) * comp);
    vec3 E = vec3(0.8) * alb / PI * (sun * max(dot(up, s), 0.0) + (skyE + nightE) * comp);
    return mix(L, E, espuma);
  }

  float F = 0.02 + 0.98 * pow(1.0 - NoV, 5.0);
  // what comes out from inside the water
  vec3 dentro = albedo / PI * (sun * cosS + (skyE + nightE) * comp) * (1.0 - F);
  // the reflected sky
  if (cielo.x < 0.0) {
    vec3 r = reflect(-v, n);
    float el = clamp(dot(r, up), 0.0, 1.0);
    cielo = skyE / PI * mix(1.9, 0.9, sqrt(el)) + vec3(0.002, 0.003, 0.006);
  }
  vec3 refl = cielo * F;
  // the Sun: Beckmann microfacets with the slope that doesn't show as waves
  vec3 h = normalize(s + v);
  float NoL = dot(n, s), NoH = max(dot(n, h), 0.0);
  float spec = 0.0;
  if (NoL > 0.0 && cosS > 0.0) {
    float Fh = 0.02 + 0.98 * pow(1.0 - clamp(dot(h, v), 0.0, 1.0), 5.0);
    spec = aBeckmann(NoH, s2) * Fh * aSmith(NoV, s2) * aSmith(NoL, s2) / (4.0 * NoV);
    spec = min(spec, 60.0) * mix(1.0, 0.55, uCerca);
  }
  vec3 L = dentro + refl + sun * spec;
  vec3 E = vec3(0.85) * alb / PI * (sun * max(dot(n, s), 0.0) + (skyE * (0.75 + 0.25 * dot(n, up)) + nightE) * comp);
  return mix(L, E, espuma);
}
";
    }
}
