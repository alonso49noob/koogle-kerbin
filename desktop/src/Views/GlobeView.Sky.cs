using System;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Vista del cielo: la cámara de pie en un punto de la superficie, mirando alrededor.

       No se dibuja el planeta como malla: desde unos metros de altura el búfer de
       profundidad no distingue el suelo de una órbita a cientos de kilómetros, y los
       triángulos de la esfera se verían en el horizonte. En su lugar, cada píxel lanza
       un rayo: si corta la esfera es suelo (con la textura del mapa y bruma según la
       distancia) y si no es cielo. El horizonte sale exacto. */
    public sealed partial class GlobeView
    {
        public double ObsLat = -0.0972, ObsLon = -74.5577, ObsAlt = 70;
        public double SkyAz = 90, SkyEl = 25, SkyFov = 70;
        public bool SkyGrid = true, SkyForceNight;

        ShaderProgram skyProg;
        uint skyVao;

        /* Posición del observador: la altitud sobre el nivel del mar más dos metros de
           ojos, en radios de Kerbin. */
        public double[] ObserverPos() => Sph(ObsLat, ObsLon, 1 + (Math.Max(ObsAlt, 0) + 2) / Body.Radius);

        const string SkyVS = @"#version 330 core
const vec2 P[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
void main() { gl_Position = vec4(P[gl_VertexID], 0.0, 1.0); }";

        const string SkyFS = @"#version 330 core
uniform vec2 uView;
uniform vec3 uEye, uF, uR, uU, uUp, uEast, uNorth;
uniform float uC, uTan, uAspect, uPix, uAlt, uStarShift, uSunRad;
uniform sampler2D uColor, uBiome;
uniform int uHasColor, uHasBiome, uGrid, uNight, uSunOn;
uniform float uColorOff, uBiomeOff, uBiomeAmt;
uniform vec3 uSun;
out vec4 frag;
const float PI = 3.14159265;

float hash13(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }
vec3 hash33(vec3 p) { p = fract(p * vec3(0.1031, 0.1030, 0.0973)); p += dot(p, p.yxz + 33.33); return fract((p.xxy + p.yxx) * p.zyx); }

void main() {
  vec2 ndc = gl_FragCoord.xy / uView * 2.0 - 1.0;
  vec3 d = normalize(uF + uR * ndc.x * uTan * uAspect + uU * ndc.y * uTan);
  float el = asin(clamp(dot(d, uUp), -1.0, 1.0));

  /* Con el Sol: de día cuando está alto, de noche cuando baja más de unos 9° bajo el
     horizonte, y un crepúsculo entre medias en el que el horizonte de su lado se
     enciende. Forzando la noche, o sin día y noche, el cielo no depende de él. */
  bool sunOn = uSunOn != 0 && uNight == 0;
  float sunUp = dot(uSun, uUp);
  float dayK = uNight != 0 ? 0.0 : (sunOn ? smoothstep(-0.16, 0.09, sunUp) : 1.0);
  vec3 zen = mix(vec3(0.006, 0.010, 0.022), vec3(0.16, 0.36, 0.74), dayK);
  vec3 hor = mix(vec3(0.045, 0.075, 0.13), vec3(0.62, 0.75, 0.90), dayK);
  if (sunOn) {
    vec3 sh = uSun - uUp * sunUp;
    vec3 dh = d - uUp * dot(d, uUp);
    float toward = (length(sh) > 1e-4 && length(dh) > 1e-4) ? max(dot(normalize(sh), normalize(dh)), 0.0) : 0.0;
    float dusk = exp(-pow((sunUp + 0.03) / 0.09, 2.0));
    hor = mix(hor, vec3(0.98, 0.50, 0.22), dusk * (0.2 + 0.65 * toward * toward));
  }
  vec3 space = vec3(0.004, 0.007, 0.014);
  // al subir, el aire se acaba y el cielo pasa a ser espacio
  float air = clamp(1.0 - uAlt / 0.12, 0.0, 1.0);

  vec3 col;
  bool ground = false;
  float b = dot(uEye, d);
  float disc = b * b - uC;
  if (disc > 0.0) {
    float t = -b - sqrt(disc);
    if (t > 0.0) {
      ground = true;
      vec3 p = uEye + d * t;
      float lat = asin(clamp(p.y, -1.0, 1.0));
      float lon = atan(p.x, p.z);
      float u = lon / (2.0 * PI) + 0.5, v = 0.5 - lat / PI;
      /* En la costura de ±180° la u salta de 1 a 0 y sus derivadas se disparan; se
         toma la de la u desplazada media vuelta, que allí es continua. */
      vec2 uv = vec2(u, v);
      vec2 gx = dFdx(uv), gy = dFdy(uv);
      vec2 uv2 = vec2(fract(u + 0.5), v);
      vec2 gx2 = dFdx(uv2), gy2 = dFdy(uv2);
      if (abs(gx2.x) + abs(gy2.x) < abs(gx.x) + abs(gy.x)) { gx = gx2; gy = gy2; }
      vec3 base = vec3(0.10, 0.13, 0.18);
      if (uHasColor != 0) base = textureGrad(uColor, vec2(fract(u + uColorOff), v), gx, gy).rgb;
      if (uHasBiome != 0 && uBiomeAmt > 0.0)
        base = mix(base, textureGrad(uBiome, vec2(fract(u + uBiomeOff), v), gx, gy).rgb, uBiomeAmt);
      if (uNight != 0) base *= 0.32;
      else if (sunOn) {
        // el suelo lejano puede estar al otro lado del terminador: cada punto con su Sol
        float m = dot(p, uSun);
        base *= mix(0.08, 0.30 + 0.68 * max(m, 0.0), smoothstep(-0.08, 0.08, m));
      }
      else base *= 0.95;
      float haze = 1.0 - exp(-(t * 600.0) / 45.0);      // bruma: la distancia en km
      col = mix(base, hor, haze * air);
    }
  }

  if (!ground) {
    vec3 sky = mix(hor, zen, smoothstep(-0.02, 0.55, el));
    sky += hor * exp(-abs(el) * 14.0) * 0.25;
    col = mix(space, sky, air);

    /* Estrellas fijas en el espacio: giran con Kerbin igual que los anillos de las
       órbitas. De día el aire las tapa. */
    float starAmt = 1.0 - dayK * air;
    if (starAmt > 0.0) {
      float c = cos(uStarShift), s = sin(uStarShift);
      vec3 ds = vec3(d.x * c + d.z * s, d.y, -d.x * s + d.z * c);
      /* Cada celda de una rejilla sobre la esfera tiene o no una estrella. Se miran
         también las vecinas: con un campo de visión amplio una estrella ocupa más que
         su celda y, sin ellas, sale cortada en rayas. */
      vec3 q = ds * 90.0;
      vec3 cell0 = floor(q);
      float starHor = smoothstep(-0.02, 0.08, el);
      for (int i = -1; i <= 1; i++)
      for (int j = -1; j <= 1; j++)
      for (int k = -1; k <= 1; k++) {
        vec3 cell = cell0 + vec3(float(i), float(j), float(k));
        float h = hash13(cell);
        if (h <= 0.972) continue;
        vec3 cdir = normalize(cell + 0.2 + hash33(cell) * 0.6);
        float ang = length(cross(cdir, ds));
        if (dot(cdir, ds) < 0.0) continue;
        float mag = pow(fract(h * 71.3), 3.0);
        float rad = uPix * (0.6 + 1.0 * mag);
        float glow = smoothstep(rad, rad * 0.2, ang) * (0.22 + 0.85 * mag);
        vec3 tint = mix(vec3(0.75, 0.82, 1.0), vec3(1.0, 0.9, 0.75), fract(h * 13.7));
        col += tint * glow * starAmt * starHor;
      }
    }

    /* El Sol: su disco (1,1° de radio visto desde Kerbin) y un halo, más amplio con
       aire. Rojizo cuando está bajo. */
    if (sunOn) {
      float ang = acos(clamp(dot(d, uSun), -1.0, 1.0));
      float disk = 1.0 - smoothstep(uSunRad, uSunRad + uPix * 1.5, ang);
      float warm = air * (1.0 - smoothstep(-0.02, 0.20, sunUp));
      vec3 sunC = mix(vec3(1.0, 0.97, 0.90), vec3(1.0, 0.55, 0.25), warm);
      float halo = exp(-ang / 0.06) * 0.45 * air + exp(-ang / 0.008) * 0.7;
      col += sunC * halo * smoothstep(-0.12, 0.0, sunUp + (1.0 - air));
      col = mix(col, sunC * 1.15, disk);
    }
  }

  /* Rejilla de altura y acimut: círculos cada 15° y meridianos cada 30°. El acimut
     salta en ±180°; su derivada se toma de la versión que no salta. */
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
        }

        /* Rumbo (0 = norte, 90 = este) y altura sobre el horizonte bajo un píxel. */
        public (double az, double el) SkyDirAt(double px, double py)
        {
            var d = RayDir(px, py);
            LocalBasis(eyeL, out var up, out var east, out var north);
            double el = Math.Asin(Math.Clamp(Dot(d, up), -1, 1)) * R2D;
            double az = Math.Atan2(Dot(d, east), Dot(d, north)) * R2D;
            return ((az + 360) % 360, el);
        }

        /* Rumbo y altura del Sol para el observador (está tan lejos que da igual la
           altura a la que se ponga). */
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
            skyProg.Float("uAlt", Len(eye) - 1);
            skyProg.Float("uStarShift", orbitShift);
            skyProg.Int("uGrid", SkyGrid && Mode == CamMode.Sky ? 1 : 0);
            skyProg.Int("uNight", SkyForceNight ? 1 : 0);
            skyProg.Int("uSunOn", Light ? 1 : 0);
            var sun = SunDir;
            skyProg.Vec3("uSun", sun[0], sun[1], sun[2]);
            skyProg.Float("uSunRad", Sun.AngularRadius);
            skyProg.Float("uColorOff", ColorOff / 360);
            skyProg.Float("uBiomeOff", BiomeOff / 360);
            skyProg.Float("uBiomeAmt", BiomeTex != null ? BiomeAmt : 0);
            skyProg.Int("uHasColor", ColorTex != null ? 1 : 0);
            skyProg.Int("uHasBiome", BiomeTex != null ? 1 : 0);
            BindTex(0, ColorTex); skyProg.Int("uColor", 0);
            BindTex(1, BiomeTex); skyProg.Int("uBiome", 1);
            GL.BindVertexArray(skyVao);
            GL.DrawArrays(GL.TRIANGLES, 0, 3);
            GL.BindVertexArray(0);

            // órbitas y trazas, tapadas por el planeta con el corte de rayo del shader
            DrawOrbits(eye, occlude: true);
            DrawTrack(eye, occlude: true, ground: false);

            batch.Begin(W, H);
            PlacePins(batch, tc, eye, vesselsOnly: true);
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
            // alturas sobre el meridiano del rumbo al que se mira
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
