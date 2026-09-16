using System;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Luz y atmósfera con base física, compartidas por el globo y la vista del cielo.

       Dispersión simple de Rayleigh (el azul del cielo, el rojo de los atardeceres) y de
       Mie (la bruma y el halo alrededor del Sol), integrada a lo largo de cada rayo. La
       profundidad óptica hacia el Sol no se integra: sale de la aproximación de Schüler a
       la función de Chapman, que además da la sombra del planeta con su penumbra rojiza
       en el terminador. Todo en radios de Kerbin: 70 km de atmósfera, escalas de altura
       de 6 km (Rayleigh) y 1,2 km (Mie), coeficientes de la Tierra al nivel del mar.

       El suelo recibe el Sol filtrado por el aire y la luz azulada del cielo; el agua,
       además, el brillo especular del Sol con Fresnel. El resultado va en luz lineal y
       se comprime con un tonemapping filmic (ACES) antes de pasar a sRGB. */
    public sealed partial class GlobeView
    {
        /* Irradiancia del Sol y exposición: con ellas el suelo a pleno Sol queda en tonos
           medios y ni el cielo ni el reflejo del mar se queman. */
        public double SunIntensity = 20, Exposure = 1.0;

        ShaderProgram starProg;
        uint starVao;

        const string Header = "#version 330 core\n";

        const string AtmosphereGlsl = @"
const float PI = 3.14159265;
/* El aire del cuerpo que se está viendo, en radios del cuerpo: coeficientes de Rayleigh y
   Mie al nivel del suelo, escalas de altura y techo de la atmósfera. */
uniform vec3 BETA_R;
uniform float BETA_M, BETA_ME, HR, HM, ATM_TOP;
uniform float uSunI, uExposure;
uniform int uAtmos, uSteps;

/* Distancias de entrada y salida del rayo en la esfera. Si no la toca, las dos
   negativas: así «x > 0» significa de verdad que el rayo choca por delante. */
vec2 raySphere(vec3 o, vec3 d, float r) {
  float b = dot(o, d), c = dot(o, o) - r * r, h = b * b - c;
  if (h < 0.0) return vec2(-1.0, -1.0);
  h = sqrt(h);
  return vec2(-b - h, -b + h);
}

/* Espesor óptico hacia el infinito desde la altura h (en escalas de altura) con el
   ángulo cenital chi, sobre un planeta de radio X (también en escalas de altura), ya
   multiplicado por la densidad del punto. Por debajo del horizonte el rayo roza o
   atraviesa el planeta: ahí crece muy deprisa, y eso es la sombra. */
float chapman(float X, float h, float cosChi) {
  float c = sqrt(X + h);
  if (cosChi >= 0.0) return c / (c * cosChi + 1.0) * exp(-h);
  float x0 = sqrt(max(1.0 - cosChi * cosChi, 0.0)) * (X + h);
  if (X - x0 > 40.0) return 1e8;
  float c0 = sqrt(x0);
  return max(2.0 * c0 * exp(X - x0) - c / (1.0 - c * cosChi) * exp(-h), 0.0);
}

/* Cuánta luz del Sol llega a un punto, por canal. */
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

/* Ruido de gradiente entrelazado: desplaza las muestras de cada píxel para que el
   escalonado de pocas muestras se vea como un grano fino y no como bandas. */
float ign(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }

/* Luz dispersada hacia el observador a lo largo del tramo [t0, t1] del rayo o + d·t, y
   la transmitancia del tramo (lo que queda de lo que hay detrás). */
vec3 inscatter(vec3 o, vec3 d, float t0, float t1, vec3 s, float jitter, out vec3 trans) {
  float ds = max(t1 - t0, 0.0) / float(uSteps);
  float odR = 0.0, odM = 0.0;
  vec3 sumR = vec3(0.0), sumM = vec3(0.0);
  for (int i = 0; i < uSteps; i++) {
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

/* Luz que sale de un punto del suelo hacia el observador (v, unitario hacia él). */
vec3 shadeGround(vec3 p, vec3 n, vec3 v, vec3 s, vec3 albedo, float water) {
  /* Los mapas de color de Kerbin ya vienen «iluminados»: tomados como albedo, la tierra a
     pleno Sol sale más clara que el cielo. Se rebajan a un albedo creíble. */
  albedo *= 0.5;
  vec3 up = normalize(p);
  vec3 sun = uSunI * sunTransmittance(p, s);
  // el cielo como luz ambiente: azulada, y más débil cuanto más bajo está el Sol
  // (sin aire no hay cielo que ilumine: queda un resto neutro para no ver las sombras negras del todo)
  vec3 skyE = uSunI * (uAtmos != 0 ? vec3(0.05, 0.085, 0.16) : vec3(0.012)) * smoothstep(-0.12, 0.4, dot(up, s));
  // de noche, un resto de luz fría para adivinar el terreno
  const vec3 nightE = vec3(0.35, 0.42, 0.62);
  vec3 L = albedo / PI * (sun * max(dot(n, s), 0.0) + skyE * (0.75 + 0.25 * dot(n, up)) + nightE);
  if (water > 0.0) {
    // el agua es lisa: refleja el cielo según Fresnel y el Sol como un brillo concentrado
    float cosV = clamp(dot(up, v), 0.0, 1.0);
    float F = 0.02 + 0.98 * pow(1.0 - cosV, 5.0);
    vec3 h = normalize(s + v);
    const float SP = 900.0;
    float spec = (SP + 8.0) / (8.0 * PI) * pow(max(dot(up, h), 0.0), SP);
    float Fh = 0.02 + 0.98 * pow(1.0 - clamp(dot(h, v), 0.0, 1.0), 5.0);
    float cosS = max(dot(up, s), 0.0);
    // el agua absorbe casi todo lo que entra: su color del mapa, más oscuro que el de la tierra
    vec3 W = albedo * 0.7 / PI * (sun * cosS + skyE + nightE) * (1.0 - F) + skyE / PI * 1.4 * F + sun * spec * Fh * cosS * 0.5;
    L = mix(L, W, water);
  }
  return L;
}

vec3 toneMap(vec3 c) {
  c *= uExposure;
  c = (c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14);
  return pow(clamp(c, 0.0, 1.0), vec3(1.0 / 2.2));
}
";

        const string StarsGlsl = @"
float hash13(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }
vec3 hash33(vec3 p) { p = fract(p * vec3(0.1031, 0.1030, 0.0973)); p += dot(p, p.yxz + 33.33); return fract((p.xxy + p.yxx) * p.zyx); }

/* Estrellas fijas en el espacio: giran con Kerbin igual que los anillos de las órbitas.
   Cada celda de una rejilla sobre la esfera tiene o no una estrella; se miran también
   las vecinas, porque con un campo de visión amplio una estrella ocupa más que su celda
   y sin ellas sale cortada en rayas. */
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

        /* El aire del cuerpo actual. Los coeficientes se dan por metro al nivel del suelo y se
           pasan a radios del cuerpo; la escala de altura sale del grosor de la atmósfera (en
           Kerbin, 70 km dan 6 km). Con Kerbin salen los valores con los que se ajustó el cielo. */
        void AtmosUniforms(ShaderProgram p, int steps, bool sunOn = true)
        {
            var b = Body.Current;
            double R = Math.Max(b.Radius, 1);
            double h = Math.Max(b.Atmosphere, 1000);
            double hr = h / 11.67;
            var c = b.AirColor ?? new[] { 5.802, 13.558, 33.1 };
            double k = 1e-6 * R * b.AirDensity;
            /* En un gigante gaseoso, cientos de km de aire con los coeficientes de la Tierra lo
               dejan todo blanco: la profundidad óptica vertical se limita (Kerbin ronda 0,2 y no
               llega al tope). */
            double tau = Math.Max(c[0], Math.Max(c[1], c[2])) * k * hr / R;
            if (tau > 0.25) k *= 0.15 / tau;
            p.Float("uSunI", sunOn ? SunIntensity : 0);
            p.Float("uExposure", Exposure);
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

        /* El fondo de estrellas del globo, detrás de todo. */
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
