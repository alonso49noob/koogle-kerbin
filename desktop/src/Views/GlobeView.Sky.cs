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
uniform sampler2D uColor, uBiome, uHeightTex;
uniform int uHasColor, uHasBiome, uGrid, uHasHeight, uRelief;
uniform float uHeightOff, uHMin, uHMax, uRadiusM, uTopR;
uniform vec2 uHeightSize, uColorSize;
uniform sampler2D uDetGrass, uDetSand, uDetRock, uDetSnow;
uniform int uHasDetail;
uniform float uDetTile, uDetAmt;
uniform vec2 uDetOrigin;
uniform int uDebug;
uniform sampler2D uCloudTex;
uniform int uHasClouds;
uniform float uCloudR, uCloudAmt, uCloudOff;
uniform float uColorOff, uBiomeOff, uBiomeAmt;
uniform vec3 uSun, uTint;
out vec4 frag;

/* El mapa de alturas se sube sin filtrar, porque los biomas se leen por color exacto.
   Aqui se interpola a mano: sin esto el terreno sale a escalones del tamano de un texel
   (en Kerbin, mesetas de 460 m). La curva quintica ademas quita las aristas entre
   texeles, que a ras de suelo se notan. */
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

/* Altura del terreno en esa direccion, en metros sobre el nivel del mar. */
float terrainH(vec3 n) {
  float lat = asin(clamp(n.y, -1.0, 1.0));
  float lon = atan(n.x, n.z);
  vec2 uv = vec2(fract(lon / (2.0 * PI) + 0.5 + uHeightOff), 0.5 - lat / PI);
  return uHMin + grisSuave(uv) * (uHMax - uHMin);
}

/* Radio de la superficie en ese punto. Bajo el nivel del mar manda el mar: el agua
   es una esfera lisa y el rayo no tiene que bajar al fondo. */
float terrainR(vec3 p) { return 1.0 + max(terrainH(normalize(p)), 0.0) / uRadiusM; }

/* Normal del terreno por diferencias en el mapa de alturas, a unas decenas de metros. */
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

/* Avanza por el mapa de alturas hasta cruzar la superficie. Los pasos crecen con la
   distancia (cerca hace falta detalle; lejos, un pixel abarca cientos de metros) y el
   cruce se afina por biseccion. Devuelve la distancia al choque, o -1 si no hay. */
float marchTerrain(vec3 o, vec3 d, out vec3 nOut, out float wasSea) {
  nOut = vec3(0.0); wasSea = 0.0;
  vec2 tTop = raySphere(o, d, uTopR);
  vec2 tSea = raySphere(o, d, 1.0);
  if (tTop.y <= 0.0) return -1.0;
  float t0 = max(tTop.x, 0.0);
  float t1 = tSea.x > 0.0 ? tSea.x : tTop.y;
  if (t1 <= t0) return -1.0;

  float prevT = t0;
  const int N = 128;
  for (int i = 1; i <= N; i++) {
    float f01 = float(i) / float(N);
    float tt = mix(t0, t1, f01 * f01);
    vec3 p = o + d * tt;
    if (length(p) < terrainR(p)) {
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
    prevT = tt;
  }
  if (tSea.x > 0.0) { wasSea = 1.0; nOut = normalize(o + d * tSea.x); return tSea.x; }
  return -1.0;
}

/* Detalle del suelo con las texturas del juego. Cerca, el mapa del cuerpo no da mas de
   si (un texel son cientos de metros), asi que se le superpone una textura que se repite
   cada pocos metros, elegida por la pendiente y por el color del sitio: hierba en lo
   verde, arena en lo claro, roca en lo empinado y nieve en lo blanco. Se desvanece con
   la distancia para que no haga muare. */
vec3 detalle(vec3 p, vec3 n, vec3 base, float dist) {
  if (uHasDetail == 0 || uDetAmt <= 0.0) return base;
  float amt = uDetAmt * (1.0 - smoothstep(1500.0, 9000.0, dist));
  if (amt <= 0.001) return base;

  vec3 rel = (p - uEye) * uRadiusM;
  vec2 uv = (vec2(dot(rel, uEast), dot(rel, uNorth)) + uDetOrigin) / uDetTile;

  vec3 up = normalize(p);
  float pend = 1.0 - clamp(dot(n, up), 0.0, 1.0);          // 0 llano, crece con la pendiente
  float roca = smoothstep(0.02, 0.12, pend);
  float verde = clamp((base.g - max(base.r, base.b)) * 6.0, 0.0, 1.0);
  float blanco = smoothstep(0.62, 0.82, min(min(base.r, base.g), base.b));
  float arena = clamp(1.0 - verde - blanco, 0.0, 1.0);

  vec3 d = texture(uDetGrass, uv).rgb * verde
         + texture(uDetSand, uv).rgb * arena
         + texture(uDetSnow, uv).rgb * blanco;
  float suma = max(verde + arena + blanco, 0.001);
  d /= suma;
  d = mix(d, texture(uDetRock, uv).rgb, roca);

  // el detalle modula, no pinta: mantiene el color del mapa y le pone grano
  float lum = dot(d, vec3(0.2126, 0.7152, 0.0722));
  return base * mix(1.0, clamp(lum / 0.42, 0.45, 1.8), amt);
}

void main() {
  vec2 ndc = gl_FragCoord.xy / uView * 2.0 - 1.0;
  /* Diagnostico: 1 pinta el gris del mapa de alturas donde el rayo cruza el nivel del
     mar, 2 pinta la altura en metros y 3 la distancia al choque con el terreno. */
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
  vec3 d = normalize(uF + uR * ndc.x * uTan * uAspect + uU * ndc.y * uTan);
  float el = asin(clamp(dot(d, uUp), -1.0, 1.0));
  float jit = ign(gl_FragCoord.xy);
  vec2 ta = raySphere(uEye, d, ATM_TOP);
  vec2 tg = raySphere(uEye, d, 1.0);

  vec3 col;
  {
    float t = tg.x;
    vec3 nRel = vec3(0.0); float esMar = 0.0;
    bool relieve = uRelief != 0 && uHasHeight != 0;
    if (relieve) t = marchTerrain(uEye, d, nRel, esMar);
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

      /* Con relieve, las derivadas de pantalla no sirven: la distancia al choque cambia
         de golpe entre pixeles vecinos (siluetas) y encima se calculan dentro de una
         rama que no todos los pixeles toman, que es justo lo que el lenguaje deja sin
         definir. El mipmap se iba al nivel mas basto y el suelo salia de un gris plano.
         Aqui el nivel se calcula del tamano que tiene el pixel sobre el suelo. */
      float lod = 0.0;
      if (relieve) {
        vec3 up0 = normalize(p);
        float cosInc = max(abs(dot(d, up0)), 0.02);
        float huella = t * uRadiusM * uPix / cosInc;            // metros que abarca el pixel
        float texel = 2.0 * PI * uRadiusM / max(uColorSize.x, 1.0);
        lod = clamp(log2(max(huella / texel, 0.0001)), 0.0, 14.0);
      }

      vec3 base = uTint;
      if (uHasColor != 0) base = relieve
        ? textureLod(uColor, vec2(fract(u + uColorOff), v), lod).rgb
        : textureGrad(uColor, vec2(fract(u + uColorOff), v), gx, gy).rgb;
      // el mar por el color, antes de mezclar los biomas
      float water = uHasColor != 0 ? smoothstep(0.03, 0.08, base.b - max(base.r, base.g)) : 0.0;
      if (uHasBiome != 0 && uBiomeAmt > 0.0)
        base = mix(base, relieve
          ? textureLod(uBiome, vec2(fract(u + uBiomeOff), v), 0.0).rgb
          : textureGrad(uBiome, vec2(fract(u + uBiomeOff), v), gx, gy).rgb, uBiomeAmt);
      // cada punto con su Sol: el suelo lejano puede estar al otro lado del terminador
      if (relieve && esMar > 0.5) water = 1.0;
      vec3 nSup = relieve ? nRel : normalize(p);
      if (water < 0.5) base = detalle(p, nSup, base, t * uRadiusM);
      vec3 L = shadeGround(p, nSup, -d, uSun, pow(base, vec3(2.2)), water);
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
  /* Nubes: una capa esferica a su altura con el mapa del juego. Se toma el corte que
     queda por delante de lo que ya se ve (suelo o cielo) y se mezcla con su cobertura.
     Con la camara por debajo vale el corte de salida; por encima, el de entrada. */
  if (uHasClouds != 0 && uCloudAmt > 0.0) {
    vec2 tn = raySphere(uEye, d, uCloudR);
    float tc = length(uEye) < uCloudR ? tn.y : tn.x;
    float tSuelo = tg.x;
    if (tc > 0.0 && (tSuelo <= 0.0 || tc < tSuelo)) {
      vec3 pc = uEye + d * tc;
      vec3 nc = normalize(pc);
      float latc = asin(clamp(nc.y, -1.0, 1.0)), lonc = atan(nc.x, nc.z);
      vec2 uvc = vec2(fract(lonc / (2.0 * PI) + 0.5 + uCloudOff), 0.5 - latc / PI);
      // el nivel de mipmap, como en el suelo, por el tamano del pixel sobre la capa
      float huella = tc * uRadiusM * uPix;
      float texel = 2.0 * PI * uRadiusM / 2048.0;
      float lodc = clamp(log2(max(huella / texel, 0.0001)), 0.0, 12.0);
      vec4 nube = textureLod(uCloudTex, uvc, lodc);
      float a = clamp(nube.a * uCloudAmt, 0.0, 1.0);
      if (a > 0.002) {
        // iluminacion sencilla: el Sol por encima de la capa, algo de cielo por debajo
        vec3 luz = uSunI * sunTransmittance(pc, uSun) * max(dot(nc, uSun), 0.0) * 0.55
                 + uSunI * vec3(0.05, 0.07, 0.12) * 0.5;
        vec3 colNube = nube.rgb * luz / PI;
        // lo que hay detras se atenua con el aire que queda por delante de la nube
        vec3 trN = vec3(1.0), insN = vec3(0.0);
        if (uAtmos != 0) insN = inscatter(uEye, d, max(ta.x, 0.0), tc, uSun, jit, trN);
        col = mix(col, colNube * trN + insN, a);
      }
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
            /* Relieve: solo con mapa de alturas, y en la camara libre siempre; de pie en
               el suelo tambien, que es lo que hace que se vean las montanas de cerca. */
            bool relieve = HeightTex != null && FreeRelief && (Mode == CamMode.Free || Mode == CamMode.Sky);
            skyProg.Int("uHasHeight", HeightTex != null ? 1 : 0);
            skyProg.Int("uRelief", relieve ? 1 : 0);
            skyProg.Float("uHeightOff", HeightOff / 360);
            skyProg.Float("uHMin", HMin);
            skyProg.Float("uHMax", HMax);
            skyProg.Float("uRadiusM", Body.Radius);
            // cascara por encima de la cima mas alta, que es donde empieza a buscarse el suelo
            skyProg.Float("uTopR", 1 + Math.Max(0, HMax) / Body.Radius);
            skyProg.Vec2("uHeightSize", HeightTex?.Width ?? 1, HeightTex?.Height ?? 1);
            skyProg.Vec2("uColorSize", ColorTex?.Width ?? 1, ColorTex?.Height ?? 1);
            BindTex(0, ColorTex); skyProg.Int("uColor", 0);
            BindTex(1, BiomeTex); skyProg.Int("uBiome", 1);
            BindTex(2, HeightTex); skyProg.Int("uHeightTex", 2);
            /* Detalle del suelo: solo volando, que es donde se ve, y con las cuatro
               texturas cargadas del juego. El origen va en metros ya reducido al tamano
               del mosaico, para no perder precision al sumarlo en el shader. */
            bool det = Detail && HasDetail && (Mode == CamMode.Free || Mode == CamMode.Sky);
            skyProg.Int("uDebug", Debug);
            bool nubes = CloudTex != null && Clouds;
            skyProg.Int("uHasClouds", nubes ? 1 : 0);
            skyProg.Float("uCloudR", 1 + CloudAlt / Body.Radius);
            skyProg.Float("uCloudAmt", nubes ? CloudAmount : 0);
            skyProg.Float("uCloudOff", CloudOff / 360);
            BindTex(7, CloudTex); skyProg.Int("uCloudTex", 7);
            skyProg.Int("uHasDetail", det ? 1 : 0);
            skyProg.Float("uDetTile", DetailTile);
            skyProg.Float("uDetAmt", det ? DetailAmount : 0);
            double lat0 = Mode == CamMode.Free ? FreeLat : ObsLat, lon0 = Mode == CamMode.Free ? FreeLon : ObsLon;
            double este0 = lon0 * D2R * Body.Radius * Math.Cos(lat0 * D2R), norte0 = lat0 * D2R * Body.Radius;
            skyProg.Vec2("uDetOrigin", este0 % DetailTile, norte0 % DetailTile);
            BindTex(3, DetGrass); skyProg.Int("uDetGrass", 3);
            BindTex(4, DetSand); skyProg.Int("uDetSand", 4);
            BindTex(5, DetRock); skyProg.Int("uDetRock", 5);
            BindTex(6, DetSnow); skyProg.Int("uDetSnow", 6);
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
