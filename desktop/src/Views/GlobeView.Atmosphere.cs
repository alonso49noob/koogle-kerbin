using System;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Physically based light and atmosphere, shared by the globe and the sky view.

       Single scattering of Rayleigh (the blue of the sky, the red of sunsets) and Mie (the haze
       and the halo around the Sun), integrated along each ray. The optical depth toward the Sun
       isn't integrated: it comes from Schüler's approximation to the Chapman function, which
       also gives the planet's shadow with its reddish penumbra at the terminator. Everything in
       Kerbin radii: 70 km of atmosphere, scale heights of 6 km (Rayleigh) and 1.2 km (Mie),
       Earth's coefficients at sea level.

       The ground gets the Sun filtered by the air and the bluish light of the sky; water also
       gets the Sun's specular glint with Fresnel. The result is in linear light and is
       compressed with filmic tonemapping (ACES) before going to sRGB. */
    public sealed partial class GlobeView
    {
        /* Sun irradiance and exposure: with them the ground in full Sun sits in the midtones
           and neither the sky nor the sea reflection blows out. */
        public double SunIntensity = 20, Exposure = 1.0;

        ShaderProgram starProg;
        uint starVao;

        const string Header = "#version 330 core\n";

        const string AtmosphereGlsl = @"
const float PI = 3.14159265;
/* The air of the body being viewed, in body radii: Rayleigh and Mie coefficients at ground
   level, scale heights and top of the atmosphere. */
uniform vec3 BETA_R;
uniform float BETA_M, BETA_ME, HR, HM, ATM_TOP;
uniform float uSunI, uExposure;
/* 0 = the planet seen from outside; 1 = at ground level (sky and flight). From outside the haze
   rules and the usual setting works; at ground level direct sunlight rules, and with that same
   setting midday ground got washed out. */
uniform float uCerca;
uniform int uAtmos, uSteps;

/* Entry and exit distances of the ray through the sphere. If it misses, both negative: that way
   «x > 0» really means the ray hits in front. */
vec2 raySphere(vec3 o, vec3 d, float r) {
  float b = dot(o, d), c = dot(o, o) - r * r, h = b * b - c;
  if (h < 0.0) return vec2(-1.0, -1.0);
  h = sqrt(h);
  return vec2(-b - h, -b + h);
}

/* Optical depth toward infinity from height h (in scale heights) with zenith angle chi, over a
   planet of radius X (also in scale heights), already multiplied by the point's density. Below
   the horizon the ray grazes or crosses the planet: there it grows very fast, and that's the
   shadow. */
float chapman(float X, float h, float cosChi) {
  float c = sqrt(X + h);
  if (cosChi >= 0.0) return c / (c * cosChi + 1.0) * exp(-h);
  float x0 = sqrt(max(1.0 - cosChi * cosChi, 0.0)) * (X + h);
  if (X - x0 > 40.0) return 1e8;
  float c0 = sqrt(x0);
  return max(2.0 * c0 * exp(X - x0) - c / (1.0 - c * cosChi) * exp(-h), 0.0);
}

/* How much sunlight reaches a point, per channel. */
vec3 sunTransmittance(vec3 p, vec3 s) {
  float len = length(p);
  float cosChi = dot(p, s) / len;
  if (uAtmos == 0) return vec3(smoothstep(-0.01, 0.01, cosChi));
  float h = max(len - 1.0, 0.0);
  float odR = HR * chapman(1.0 / HR, h / HR, cosChi);
  float odM = HM * chapman(1.0 / HM, h / HM, cosChi);
  return exp(-(BETA_R * odR + BETA_ME * odM));
}

float phaseR(float mu) { return 3.0 / (16.0 * PI) * (1.0 + mu * mu); }

float phaseM(float mu) {
  const float g = 0.8;
  float g2 = g * g;
  return 3.0 / (8.0 * PI) * ((1.0 - g2) * (1.0 + mu * mu)) / ((2.0 + g2) * pow(1.0 + g2 - 2.0 * g * mu, 1.5));
}

/* Interleaved gradient noise: offsets each pixel's samples so the banding of few samples looks
   like fine grain and not like stripes. */
float ign(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }

/* Light scattered toward the observer along the segment [t0, t1] of the ray o + d·t, and the
   segment's transmittance (what's left of whatever is behind). */
vec3 inscatterN(vec3 o, vec3 d, float t0, float t1, vec3 s, float jitter, int pasos, out vec3 trans) {
  float ds = max(t1 - t0, 0.0) / float(pasos);
  float odR = 0.0, odM = 0.0;
  vec3 sumR = vec3(0.0), sumM = vec3(0.0);
  for (int i = 0; i < pasos; i++) {
    vec3 p = o + d * (t0 + (float(i) + jitter) * ds);
    float alt = max(length(p) - 1.0, 0.0);
    float dR = exp(-alt / HR) * ds, dM = exp(-alt / HM) * ds;
    vec3 att = exp(-(BETA_R * (odR + 0.5 * dR) + BETA_ME * (odM + 0.5 * dM))) * sunTransmittance(p, s);
    sumR += dR * att;
    sumM += dM * att;
    odR += dR;
    odM += dM;
  }
  trans = exp(-(BETA_R * odR + BETA_ME * odM));
  float mu = dot(d, s);
  return uSunI * (sumR * BETA_R * phaseR(mu) + sumM * BETA_M * phaseM(mu));
}

vec3 inscatter(vec3 o, vec3 d, float t0, float t1, vec3 s, float jitter, out vec3 trans) {
  return inscatterN(o, d, t0, t1, s, jitter, uSteps, trans);
}

/* On the 2D map seen from above, without the sun glint on the water: looking straight down,
   with the Sun high, the whole sea fell inside the glint and came out white. */
uniform int uSinBrillo;

/* Light leaving a ground point toward the observer (v, unit vector toward it). */
vec3 shadeGround(vec3 p, vec3 n, vec3 v, vec3 s, vec3 albedo, float water) {
  /* Kerbin's color maps already come «lit»: taken as albedo, land in full Sun comes out
     brighter than the sky. They're toned down to a believable albedo. At ground level a bit
     more: with 0.5 midday ground went to the top of the curve and lost its color. Ambient light
     is compensated so twilight stays the same. */
  float alb = mix(0.5, 0.36, uCerca);
  float comp = 0.5 / alb;
  albedo *= alb;
  vec3 up = normalize(p);
  vec3 sun = uSunI * sunTransmittance(p, s);
  // the sky as ambient light: bluish, and weaker the lower the Sun is
  // (without air there's no sky to light things: a neutral remainder is left so shadows aren't completely black)
  vec3 skyE = uSunI * (uAtmos != 0 ? vec3(0.05, 0.085, 0.16) : vec3(0.012)) * smoothstep(-0.12, 0.4, dot(up, s));
  // at night, a bit of cold light to make out the terrain
  const vec3 nightE = vec3(0.35, 0.42, 0.62);
  vec3 L = albedo / PI * (sun * max(dot(n, s), 0.0) + (skyE * (0.75 + 0.25 * dot(n, up)) + nightE) * comp);
  if (water > 0.0) {
    // water is smooth: it reflects the sky according to Fresnel and the Sun as a concentrated glint
    float cosV = clamp(dot(up, v), 0.0, 1.0);
    float F = 0.02 + 0.98 * pow(1.0 - cosV, 5.0);
    vec3 h = normalize(s + v);
    // at ground level the Sun's glint on the water is more concentrated and less intense:
    // otherwise, at noon the whole sea comes out white
    float SP = mix(900.0, 1500.0, uCerca);
    float spec = uSinBrillo != 0 ? 0.0 : (SP + 8.0) / (8.0 * PI) * pow(max(dot(up, h), 0.0), SP);
    float Fh = 0.02 + 0.98 * pow(1.0 - clamp(dot(h, v), 0.0, 1.0), 5.0);
    float cosS = max(dot(up, s), 0.0);
    // water absorbs almost everything that goes in: its map color, darker than the land's
    vec3 W = albedo * 0.7 / PI * (sun * cosS + (skyE + nightE) * comp) * (1.0 - F) + skyE / PI * 1.4 * F + sun * spec * Fh * cosS * mix(0.5, 0.32, uCerca);
    L = mix(L, W, water);
  }
  return L;
}

float aces(float x) { return (x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14); }

/* At ground level the curve works mostly on luminance, keeping the color. Applied channel by
   channel, in full Sun the ground got washed out: a map's sand (200,180,140) came out almost
   white, with a fifth of its vividness. Part is left per channel because very bright things do
   tend to white, as in film. From outside it stays channel by channel: keeping the blue of the
   haze made the planet look more veiled. */
vec3 toneMap(vec3 c) {
  c *= uExposure;
  float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
  vec3 porLum = l > 1e-6 ? c * (aces(l) / l) : vec3(0.0);
  /* Keeping luminance can leave a channel above 1 (sand, in red): clipping it changes the hue,
     so the whole color is scaled. */
  porLum /= max(1.0, max(porLum.r, max(porLum.g, porLum.b)));
  vec3 porCanal = vec3(aces(c.r), aces(c.g), aces(c.b));
  return pow(clamp(mix(porLum, porCanal, mix(1.0, 0.4, uCerca)), 0.0, 1.0), vec3(1.0 / 2.2));
}
";

        const string StarsGlsl = @"
float hash13(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }
vec3 hash33(vec3 p) { p = fract(p * vec3(0.1031, 0.1030, 0.0973)); p += dot(p, p.yxz + 33.33); return fract((p.xxy + p.yxx) * p.zyx); }

/* Stars fixed in space: they rotate with Kerbin just like the orbit rings. Each cell of a grid
   on the sphere has a star or not; the neighbors are checked too, because with a wide field of
   view a star takes up more than its cell and without them it comes out sliced into stripes. */
vec3 starField(vec3 d, float pix, float shift) {
  float c = cos(shift), s = sin(shift);
  vec3 ds = vec3(d.x * c + d.z * s, d.y, -d.x * s + d.z * c);
  vec3 cell0 = floor(ds * 90.0);
  vec3 col = vec3(0.0);
  for (int i = -1; i <= 1; i++)
  for (int j = -1; j <= 1; j++)
  for (int k = -1; k <= 1; k++) {
    vec3 cell = cell0 + vec3(float(i), float(j), float(k));
    float h = hash13(cell);
    if (h <= 0.972) continue;
    vec3 cdir = normalize(cell + 0.2 + hash33(cell) * 0.6);
    if (dot(cdir, ds) < 0.0) continue;
    float ang = length(cross(cdir, ds));
    float mag = pow(fract(h * 71.3), 3.0);
    float rad = pix * (0.6 + 1.0 * mag);
    float glow = smoothstep(rad, rad * 0.2, ang) * (0.22 + 0.85 * mag);
    col += mix(vec3(0.75, 0.82, 1.0), vec3(1.0, 0.9, 0.75), fract(h * 13.7)) * glow;
  }
  return col;
}
";

        const string StarFS = Header + StarsGlsl + @"
uniform vec2 uView;
uniform vec3 uF, uR, uU;
uniform float uTan, uAspect, uPix, uStarShift;
out vec4 frag;
void main() {
  vec2 ndc = gl_FragCoord.xy / uView * 2.0 - 1.0;
  vec3 d = normalize(uF + uR * ndc.x * uTan * uAspect + uU * ndc.y * uTan);
  frag = vec4(min(starField(d, uPix, uStarShift), vec3(1.0)), 1.0);
}";

        /* The current body's air. Coefficients are given per meter at ground level and converted
           to body radii; the scale height comes from the atmosphere's thickness (on Kerbin, 70 km
           gives 6 km). With Kerbin, the values the sky was tuned with come out. */
        void AtmosUniforms(ShaderProgram p, int steps, bool sunOn = true)
        {
            var b = Body.Current;
            double R = Math.Max(b.Radius, 1);
            double h = Math.Max(b.Atmosphere, 1000);
            double hr = h / 11.67;
            var c = b.AirColor ?? new[] { 5.802, 13.558, 33.1 };
            double k = 1e-6 * R * b.AirDensity;
            /* On a gas giant, hundreds of km of air with Earth's coefficients turn everything
               white: the vertical optical depth is capped (Kerbin is around 0.2 and doesn't
               reach the cap). */
            double tau = Math.Max(c[0], Math.Max(c[1], c[2])) * k * hr / R;
            if (tau > 0.25) k *= 0.15 / tau;
            p.Float("uSunI", sunOn ? SunIntensity : 0);
            p.Float("uExposure", Exposure);
            p.Float("uCerca", 0);                 // from outside; the sky and flight views change it
            p.Int("uSteps", steps);
            p.Int("uAtmos", Atmosphere && b.HasAir ? 1 : 0);
            p.Vec3("BETA_R", c[0] * k, c[1] * k, c[2] * k);
            p.Float("BETA_M", 2.0 * k);
            p.Float("BETA_ME", 2.22 * k);
            p.Float("HR", hr / R);
            p.Float("HM", hr / 5 / R);
            p.Float("ATM_TOP", 1 + h / R);
            p.Vec3("uTint", b.Tint[0], b.Tint[1], b.Tint[2]);
        }

        /* The globe's star background, behind everything. */
        void DrawStars()
        {
            starProg ??= new ShaderProgram(SkyVS, StarFS);
            if (starVao == 0) starVao = GL.GenVertexArray();
            var right = Cross(fwdL, upL);
            right = Len(right) < 1e-9 ? new double[] { 1, 0, 0 } : Norm(right);
            var camUp = Cross(right, fwdL);
            double tan = Math.Tan(fovL * D2R / 2);

            GL.Disable(GL.DEPTH_TEST);
            GL.Disable(GL.BLEND);
            starProg.Use();
            starProg.Vec2("uView", W, H);
            starProg.Vec3("uF", fwdL[0], fwdL[1], fwdL[2]);
            starProg.Vec3("uR", right[0], right[1], right[2]);
            starProg.Vec3("uU", camUp[0], camUp[1], camUp[2]);
            starProg.Float("uTan", tan);
            starProg.Float("uAspect", (double)W / H);
            starProg.Float("uPix", 2 * tan / H);
            starProg.Float("uStarShift", orbitShift);
            GL.BindVertexArray(starVao);
            GL.DrawArrays(GL.TRIANGLES, 0, 3);
            GL.BindVertexArray(0);
        }

        void DisposeStars()
        {
            starProg?.Dispose();
            starProg = null;
            if (starVao != 0) GL.DeleteVertexArray(starVao);
            starVao = 0;
        }
    }
}
