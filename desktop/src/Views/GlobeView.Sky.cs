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

        /* Cada píxel lanza un rayo desde el observador: si toca el suelo, el suelo con su luz
           y el aire que hay por medio; si no, el cielo que ese aire dispersa, el Sol y las
           estrellas que deja ver. El paso a espacio al subir sale solo de la física. */
        const string SkyFS = Header + AtmosphereGlsl + StarsGlsl + @"
uniform vec2 uView;
uniform vec3 uEye, uF, uR, uU, uUp, uEast, uNorth;
uniform float uTan, uAspect, uPix, uStarShift, uSunRad;
uniform sampler2D uColor, uBiome;
uniform int uHasColor, uHasBiome, uGrid;
uniform float uColorOff, uBiomeOff, uBiomeAmt;
uniform vec3 uSun, uTint;
out vec4 frag;

void main() {
  vec2 ndc = gl_FragCoord.xy / uView * 2.0 - 1.0;
  vec3 d = normalize(uF + uR * ndc.x * uTan * uAspect + uU * ndc.y * uTan);
  float el = asin(clamp(dot(d, uUp), -1.0, 1.0));
  float jit = ign(gl_FragCoord.xy);
  vec2 ta = raySphere(uEye, d, ATM_TOP);
  vec2 tg = raySphere(uEye, d, 1.0);

  vec3 col;
  {
    float t = tg.x;
    if (t > 0.0) {
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
      vec3 base = uTint;
      if (uHasColor != 0) base = textureGrad(uColor, vec2(fract(u + uColorOff), v), gx, gy).rgb;
      // el mar por el color, antes de mezclar los biomas
      float water = uHasColor != 0 ? smoothstep(0.03, 0.08, base.b - max(base.r, base.g)) : 0.0;
      if (uHasBiome != 0 && uBiomeAmt > 0.0)
        base = mix(base, textureGrad(uBiome, vec2(fract(u + uBiomeOff), v), gx, gy).rgb, uBiomeAmt);
      // cada punto con su Sol: el suelo lejano puede estar al otro lado del terminador
      vec3 L = shadeGround(p, normalize(p), -d, uSun, pow(base, vec3(2.2)), water);
      vec3 tr = vec3(1.0), ins = vec3(0.0);
      if (uAtmos != 0) ins = inscatter(uEye, d, max(ta.x, 0.0), t, uSun, jit, tr);
      col = L * tr + ins;
    } else {
      vec3 tr = vec3(1.0), ins = vec3(0.0);
      if (uAtmos != 0 && ta.y > 0.0) ins = inscatter(uEye, d, max(ta.x, 0.0), ta.y, uSun, jit, tr);
      col = ins;
      // el Sol, 1,1° de radio visto desde Kerbin, con el color que le deja el aire
      float ang = acos(clamp(dot(d, uSun), -1.0, 1.0));
      col += uSunI * 10.0 * (1.0 - smoothstep(uSunRad, uSunRad + uPix * 1.5, ang)) * tr;
      // las estrellas: el brillo del cielo las tapa, como de verdad
      float skyLum = dot(ins, vec3(0.2126, 0.7152, 0.0722)) * uExposure;
      col += starField(d, uPix, uStarShift) * tr * exp(-skyLum * 30.0) * smoothstep(-0.02, 0.08, el);
    }
  }
  col = toneMap(col);

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
            DisposeStars();
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
            // sin día y noche, el Sol se queda en lo alto del observador; forzando la noche, apagado
            var sun = Light ? SunDir : up;
            skyProg.Vec3("uSun", sun[0], sun[1], sun[2]);
            skyProg.Float("uSunRad", Math.Max(SunAngularRadius, 0.0015));
            AtmosUniforms(skyProg, 24, sunOn: !SkyForceNight);
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
