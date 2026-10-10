using System;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Clouds that move and change shape.

       The cloud mod's map is a still photo. In the game the layer rotates around the planet
       (Kerbin: 29.9 m/s at the surface according to its clouds.cfg, one turn every 35 h) and
       changes shape with animated noise. Same here:

       - Rotation: a longitude offset that grows with cloud time.
       - Shape: the map is sampled displaced by a smooth noise on the sphere that evolves over
         time (about 80 km of displacement in 100 km cells), and its coverage rises and falls by
         area: fronts deform, grow and break up.
       - Detail: up close, the mod's detail texture (detail1) breaks up the map, which at 8192
         wide is still 5 km per texel.

       Cloud time is simulation time plus the real clock, so that while paused they keep moving
       at the game's x1 pace. */
    public sealed partial class GlobeView
    {
        public double CloudTime;                  // seconds (simulation + real clock)
        public double CloudSpeed = 29.89;         // m/s at the surface, westward
        public Texture CloudDetailTex;

        /* Moving, they're noticeable up close: at 30 m/s a cloud 2 km up crosses the sky. */
        public bool CloudsAnimating => CloudTex != null && Clouds && CloudAmount > 0
                                       && (Mode == CamMode.Free || Mode == CamMode.Sky || PlanetaCerca);

        const string NubesGlsl = @"
uniform float uNubeFase;        // cloud time for the noise, already reduced
uniform sampler2D uNubeDet;
uniform int uHasNubeDet;
uniform vec2 uNubeDetOff;       // detail offset, in meters already reduced

float nHash(vec3 p) {
  p = fract(p * 0.3183099 + 0.1);
  p *= 17.0;
  return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float nRuido(vec3 x) {
  vec3 i = floor(x), f = fract(x);
  f = f * f * (3.0 - 2.0 * f);
  return mix(mix(mix(nHash(i), nHash(i + vec3(1.0, 0.0, 0.0)), f.x),
                 mix(nHash(i + vec3(0.0, 1.0, 0.0)), nHash(i + vec3(1.0, 1.0, 0.0)), f.x), f.y),
             mix(mix(nHash(i + vec3(0.0, 0.0, 1.0)), nHash(i + vec3(1.0, 0.0, 1.0)), f.x),
                 mix(nHash(i + vec3(0.0, 1.0, 1.0)), nHash(i + vec3(1.0, 1.0, 1.0)), f.x), f.y), f.z);
}

/* The cloud map's uv (already rotated), displaced over time. n: the direction from the body's
   center. In longitude it's divided by the cosine of latitude so the displacement measures the
   same in km everywhere. */
vec2 nubeUv(vec3 n, vec2 uv) {
  vec3 q = n * 6.0;
  vec3 tt = vec3(0.0, 0.0, uNubeFase);
  vec2 w = vec2(nRuido(q + tt), nRuido(q + vec3(31.7, 11.3, 0.0) + tt)) - 0.5;
  w += 0.5 * (vec2(nRuido(q * 2.7 + vec3(5.1, 0.0, 0.0) + tt * 1.6),
                   nRuido(q * 2.7 + vec3(0.0, 9.2, 0.0) + tt * 1.6)) - 0.5);
  float c = max(sqrt(max(1.0 - n.y * n.y, 0.0)), 0.15);
  return vec2(uv.x + w.x * 0.0035 / c, clamp(uv.y + w.y * 0.005, 0.0, 1.0));
}

/* How much cloud is left in each zone: between a bit over half and a bit over the map's. */
float nubeVida(vec3 n) {
  return 0.55 + 0.6 * smoothstep(0.15, 0.8, nRuido(n * 3.5 + vec3(0.0, 0.0, uNubeFase * 0.7 + 40.0)));
}

/* The mod's detail up close: it breaks up the edges and the inside of the cloud. m: the point
   in meters on the local plane (x east, y north); cerca: 1 within a few km, 0 far away. */
float nubeDetalle(vec2 m, float cerca) {
  if (uHasNubeDet == 0 || cerca <= 0.0) return 1.0;
  vec2 p = (m + uNubeDetOff) / 3000.0;
  // the mod's detail is multiplicative and low contrast (0.65 to 1, mean 0.81)
  float d = texture(uNubeDet, p).g * 0.6 + texture(uNubeDet, p * 3.7 + 0.37).g * 0.4;
  return mix(1.0, clamp(1.0 + (d - 0.81) * 3.5, 0.3, 1.6), cerca);
}
";

        /* The time-dependent cloud uniforms, for the shader that paints them. */
        void NubeUniforms(ShaderProgram p, int unidadDetalle)
        {
            double horas = CloudTime / 3600;
            // the noise phase: one cell every ~7 h of game time; reduced for precision
            p.Float("uNubeFase", horas * 0.15 % 1000.0);
            // the detail texture unit may not exist on modest GPUs (the minimum is 16)
            bool det = CloudDetailTex != null && unidadDetalle >= 0 && unidadDetalle < GL.MaxTextureUnits;
            p.Int("uHasNubeDet", det ? 1 : 0);
            if (det) { BindTex(unidadDetalle, CloudDetailTex); p.Int("uNubeDet", unidadDetalle); }
            // the detail goes with the wind somewhat slower than the layer (the mod's detailSpeed: 6 m/s)
            double metros = CloudTime * 6.0 % 30000.0;
            p.Vec2("uNubeDetOff", -metros, 0);
        }

        /* The layer's rotation, as a fraction of a turn: westward, at CloudSpeed. */
        double NubeGiro()
        {
            double vuelta = 2 * Math.PI * Body.Radius;
            double f = CloudTime * CloudSpeed / vuelta;
            return CloudOff / 360 + (f - Math.Floor(f));
        }
    }
}
