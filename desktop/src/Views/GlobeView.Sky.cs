using System;
using System.Linq;
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
           ojos, en radios de Kerbin. Nunca por debajo del suelo que se pinta: la altitud
           guardada puede ser de otro mapa de alturas, y la explanada del KSC y el relieve
           de detalle suben el suelo por encima de ella. */
        public double[] ObserverPos()
        {
            double suelo = Math.Max(Math.Max(ObsAlt, 0), GroundAt?.Invoke(ObsLat, ObsLon) ?? 0);
            // y si hay un edificio justo ahí, encima de él
            if (TechoDeEstaticos(ObsLat, ObsLon, suelo) is double t && t > suelo) suelo = t;
            return Sph(ObsLat, ObsLon, 1 + (suelo + 2) / Body.Radius);
        }

        const string SkyVS = @"#version 330 core
const vec2 P[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
void main() { gl_Position = vec4(P[gl_VertexID], 0.0, 1.0); }";

        /* Cada píxel lanza un rayo desde el observador: si toca el suelo, el suelo con su luz
           y el aire que hay por medio; si no, el cielo que ese aire dispersa, el Sol y las
           estrellas que deja ver. El paso a espacio al subir sale solo de la física. */
        const string SkyFS = Header + AtmosphereGlsl + StarsGlsl + @"
uniform vec2 uView;
uniform vec3 uEye, uF, uR, uU, uUp, uEast, uNorth;
/* Vista cenital para el mapa 2D: cada píxel mira en vertical a su lat/lon, en la
   proyección del mapa (uCentro en grados, lon y lat; uPpd píxeles por grado). */
uniform int uCenital;
uniform vec2 uCentro;
uniform float uPpd, uCenitalR;
uniform float uTan, uAspect, uPix, uStarShift, uSunRad;
uniform sampler2D uColor, uBiome, uHeightTex;
uniform int uHasColor, uHasBiome, uGrid, uHasHeight, uRelief;
uniform float uHeightOff, uHMin, uHMax, uRadiusM, uTopR;
uniform vec2 uHeightSize, uColorSize;
uniform sampler2D uDetGrass, uDetSand, uDetRock, uDetSnow;
uniform int uHasDetail;
uniform float uDetTile, uDetAmt;
/* Posición del ojo en metros desde el centro del cuerpo, reducida módulo un múltiplo de
   todos los periodos de las texturas: sumándole la distancia al punto se tienen sus
   coordenadas en el mundo sin perder precisión, y la textura queda clavada al suelo. */
uniform vec3 uEyeMod;
uniform float uPeriodo;                 // ese módulo, en repeticiones de la textura
/* Modo Parallax: las cuatro ranuras son baja, media, alta y pendiente, y se mezclan por
   la altitud del sitio y por la pendiente con los números de Terrain.cfg. */
uniform int uModoParallax;
uniform vec2 uPxLowMid, uPxMidHigh;
uniform vec3 uPxSteep;                  // potencia, contraste y punto medio
/* Lo que da variedad al suelo en Parallax: influencia, desplazamiento y oclusión, con un
   canal por textura (ver ParallaxTerrain). */
uniform sampler2D uPxInf, uPxDisp, uPxOcc;
uniform int uPxHasInf, uPxHasDisp, uPxHasOcc;
uniform int uVariacion;                 // romper la repetición del mosaico
uniform sampler2D uPxBumpL, uPxBumpM, uPxBumpH, uPxBumpS;
uniform int uPxHasBump;
uniform int uDebug;
uniform sampler2D uCloudTex;
uniform int uHasClouds;
uniform float uCloudR, uCloudAmt, uCloudOff;
uniform float uColorOff, uBiomeOff, uBiomeAmt;
uniform vec3 uSun, uTint, uSeaColor;
uniform sampler2D uSeaTex;
uniform int uHasSeaTex;
/* Profundidad para los scatters, que se pintan después con prueba de profundidad: la
   distancia a la que choca el rayo, en escala logarítmica (ver GlobeView.Scatters). */
uniform int uWriteDepth;
uniform float uDepthFar;
out vec4 frag;

float profundidad(float z) { return clamp(log2(1.0 + max(z, 0.0)) / log2(1.0 + uDepthFar), 0.0, 1.0); }

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

/* Zonas allanadas (la explanada del KSC): dirección y altura, y radios plano y de fundido. */
uniform int uFlatCount;
uniform vec4 uFlat[4];
uniform vec2 uFlatR[4];

/* Tesela de alturas de detalle bajo la cámara (ver TeselaAltura): una ventana del mapa a
   resolución completa. Rect: primer texel y tamaño de la ventana; Nivel: tamaño del nivel
   entero y desfase de longitud; Rango: metros del gris 0 y del 255; y el ancho del fundido. */
uniform int uTesela;
uniform sampler2D uTeselaTex;
uniform vec4 uTeselaRect;
uniform vec3 uTeselaNivel;
uniform vec3 uTeselaRango;

/* Pesos de Catmull-Rom (ver TeselaAltura.Altura, que hace lo mismo en la CPU). */
vec4 pesosCatmullRom(float f) {
  float f2 = f * f, f3 = f2 * f;
  return vec4(-f + 2.0 * f2 - f3, 2.0 - 5.0 * f2 + 3.0 * f3, f + 4.0 * f2 - 3.0 * f3, -f2 + f3) * 0.5;
}

/* Altura de la tesela en metros y su peso (0 fuera, 1 dentro, fundido cerca del borde). */
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

/* Altura del terreno en esa direccion, en metros sobre el nivel del mar. */
float terrainH(vec3 n) {
  float lat = asin(clamp(n.y, -1.0, 1.0));
  float lon = atan(n.x, n.z);
  vec2 uv = vec2(fract(lon / (2.0 * PI) + 0.5 + uHeightOff), 0.5 - lat / PI);
  float h;
  vec2 ht = uTesela != 0 ? alturaTesela(lat, lon) : vec2(0.0);
  // dentro de la tesela el mapa base ni se lee
  if (ht.y >= 1.0) h = ht.x;
  else h = mix(uHMin + grisSuave(uv) * (uHMax - uHMin), ht.x, ht.y);
  for (int i = 0; i < uFlatCount; i++) {
    float d = length(n - uFlat[i].xyz) * uRadiusM;
    if (d < uFlatR[i].y) h = mix(h, uFlat[i].w, 1.0 - smoothstep(uFlatR[i].x, uFlatR[i].y, d));
  }
  return h;
}

/* Radio de la superficie en ese punto. Bajo el nivel del mar manda el mar: el agua
   es una esfera lisa y el rayo no tiene que bajar al fondo. El fondo se deja un metro
   por debajo del agua y no justo en ella: si no, la ultima muestra del rayo cae sobre la
   esfera del mar y el redondeo decide al azar si es tierra o agua, y a ras del mar
   salian franjas de las dos. */
float terrainR(vec3 p) { return 1.0 + max(terrainH(normalize(p)), -1.0) / uRadiusM; }

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

  /* Pasos según la holgura sobre el terreno: con pendientes de hasta unos 60° no se
     salta nada avanzando la mitad de la altura que queda por encima. Un mínimo que
     crece con la distancia (lo que abarca un píxel) y otro que asegura llegar al final
     en 256 pasos. Con pasos fijos, las crestas finas de lejos se saltaban (escalones en
     las siluetas), sobre todo con la tesela de detalle. */
  float prevT = t0, tt = t0;
  float pasoMin = (t1 - t0) / 256.0;
  for (int i = 0; i < 256; i++) {
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
    tt = min(t1, tt + max(max(encima * 0.5, tt * 0.0015 + 1.0 / uRadiusM), pasoMin));
  }
  if (tSea.x > 0.0) { wasSea = 1.0; nOut = normalize(o + d * tSea.x); return tSea.x; }
  return -1.0;
}

/* Detalle del suelo con las texturas del juego. Cerca, el mapa del cuerpo no da mas de
   si (un texel son cientos de metros), asi que se le superponen texturas que se repiten
   cada pocos metros. Se desvanecen con la distancia para que no hagan muare. */

/* Paso de 0 a 1 entre a y b, lineal, exactamente como GetPercentageAltitudeBetween de
   Parallax: con el rango al reves (b < a) da 0 por encima de a, que es como la Mun y
   otros cuerpos dicen «siempre la textura de abajo». */
float pct(float a, float b, float x) {
  if (b == a) return x >= a ? 1.0 : 0.0;
  return clamp((x - a) / (b - a), 0.0, 1.0);
}

float hash12(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

/* Ruido de valor periodico: al dar la vuelta el modulo de las coordenadas, el dibujo de
   la variacion sigue igual. */
float ruidoP(vec2 p, float per) {
  vec2 i = floor(p), f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  vec2 i1 = mod(i + 1.0, per);
  i = mod(i, per);
  return mix(mix(hash12(i), hash12(vec2(i1.x, i.y)), f.x), mix(hash12(vec2(i.x, i1.y)), hash12(i1), f.x), f.y);
}

/* Variacion de textura (la tecnica de Inigo Quilez): cada zona lee la textura con un
   desplazamiento distinto, elegido por un ruido suave, y las zonas se funden donde el
   dibujo de las dos se parece. El mosaico deja de verse repetido con solo dos lecturas. */
vec4 variada(sampler2D s, vec2 uv, vec2 gx, vec2 gy, float per) {
  if (uVariacion == 0) return textureGrad(s, uv, gx, gy);
  float k = ruidoP(uv * 0.125, per * 0.125) * 8.0;
  float ia = floor(k), f = fract(k);
  vec2 offa = sin(vec2(3.0, 7.0) * ia), offb = sin(vec2(3.0, 7.0) * (ia + 1.0));
  vec4 a = textureGrad(s, uv + offa, gx, gy), b = textureGrad(s, uv + offb, gx, gy);
  return mix(a, b, smoothstep(0.2, 0.8, f - 0.1 * dot(a.rgb - b.rgb, vec3(1.0))));
}

/* Proyeccion biplanar, como Parallax: la textura se proyecta sobre los dos planos de los
   ejes del mundo que mas miran hacia la normal, asi no se estira en las laderas. */
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
  // la segunda solo si pesa algo: casi siempre basta una lectura
  if (w.y < 0.02) w = vec2(1.0, 0.0);
  gPesos = w / (w.x + w.y);
}

vec2 plano(vec3 c, int e) { return e == 0 ? c.yz : (e == 1 ? c.zx : c.xy); }

/* Una textura en coordenadas del mundo (metros), a esc metros por repeticion. */
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

/* Las dos escalas de Parallax: cerca la textura se repite mas a menudo y lejos menos, en
   potencias de dos segun la distancia, y entre una y otra se funden. Asi el mosaico nunca
   se ve demasiado pequeno ni demasiado grande en pantalla. */
float gLb, gS0, gS1;

vec4 dosEscalas(sampler2D s, vec3 c, vec3 dx, vec3 dy, bool conVar) {
  vec4 a = gLb < 0.999 ? bip(s, c, dx, dy, uDetTile * gS0, uPeriodo / gS0, conVar) : vec4(0.0);
  vec4 b = gLb > 0.001 ? bip(s, c, dx, dy, uDetTile * gS1, uPeriodo / gS1, conVar) : vec4(0.0);
  return mix(a, b, gLb);
}

/* Relieve de un mapa de normales (DXT5nm de Unity: x en alfa, y en verde) proyectado en
   los mismos planos: cuanto se inclina la normal, en ejes del mundo. */
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

/* Mezcla por desplazamiento (GetDisplacementLerpFactor de Parallax): en la transicion
   entre dos texturas gana la que tiene mas relieve en cada punto, asi la hierba asoma
   entre las piedras en vez de fundirse con ellas. De lejos se suaviza. */
float mezclaDesp(float h, float d1, float d2, float logD) {
  float suave = mix(0.15, 1.0, clamp(logD * 0.15 - 0.5, 0.0, 1.0));
  d2 = clamp(d2 + h, 0.0, 1.0);
  d1 = clamp(d1 * (1.0 - h), 0.0, 1.0);
  return clamp((d2 - d1) * h / suave, 0.0, 1.0);
}

/* Influencia: cuanto manda la textura frente al color del planeta. Donde manda poco, se
   queda el color del mapa con el dibujo (la luminosidad) de la textura. */
vec3 conInfluencia(vec3 t, float inf, vec3 base) {
  float lum = dot(t, vec3(0.21, 0.72, 0.07)) + 0.5;
  return mix(base * lum, t, inf);
}

/* Devuelve el color del suelo con su detalle y, con los mapas de normales de Parallax,
   inclina `n` con el relieve fino de las texturas, que es lo que les da luz y sombra. */
vec3 detalle(vec3 p, inout vec3 n, vec3 base, float dist) {
  if (uHasDetail == 0 || uDetAmt <= 0.0) return base;
  float amt = uDetAmt * (1.0 - smoothstep(1500.0, 9000.0, dist));
  if (amt <= 0.001) return base;

  // coordenadas del punto en el mundo, en metros y sin perder precision
  vec3 c = uEyeMod + (p - uEye) * uRadiusM;
  vec3 dx = dFdx(c), dy = dFdy(c);
  vec3 up = normalize(p);
  biplanar(n);
  float logD = log2(dist * 0.2 + 0.4);
  float fl = floor(logD);
  gS1 = exp2(fl); gS0 = gS1 * 0.5;
  gLb = clamp(logD - fl, 0.0, 1.0);

  if (uModoParallax != 0) {
    // mascara de Parallax: baja-media (r), media-alta (g), pendiente (b)
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

  float pend = 1.0 - clamp(dot(n, up), 0.0, 1.0);          // 0 llano, crece con la pendiente
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

  // el detalle modula, no pinta: mantiene el color del mapa y le pone grano
  float lum = dot(d, vec3(0.2126, 0.7152, 0.0722));
  return base * mix(1.0, clamp(lum / 0.42, 0.45, 1.8), amt);
}

void main() {
  gl_FragDepth = 1.0;
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
  {
    float t = tg.x;
    vec3 nRel = vec3(0.0); float esMar = 0.0;
    bool relieve = uRelief != 0 && uHasHeight != 0;
    if (relieve) t = marchTerrain(eye, d, nRel, esMar);
    if (t > 0.0) {
      vec3 p = eye + d * t;
      if (uWriteDepth != 0) gl_FragDepth = profundidad(t * uRadiusM * dot(d, uF));
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
      if (relieve) {
        /* La costa. El mapa de color es de 1 km por texel: junto al mar la tierra hereda
           su azul y, tomada por agua, salía con el brillo del mar y la orilla se perdía.
           Con relieve manda la geometría: es mar lo que el rayo encuentra en la esfera del
           mar. En KSP no hay agua por encima del nivel del mar (el agua es siempre esa
           esfera), así que en tierra el azul del mapa es siempre tierra: junto a la orilla
           una franja de arena (mojada junto al agua) y monte arriba el propio color del
           mapa, que el mipmap ya funde con la tierra de al lado. En el mar, el color según
           la profundidad (claro en lo somero, oscuro en lo hondo, a partir del propio color
           del mapa, que en Eve es morado) y una línea de espuma en la orilla, que se apaga
           cuando un píxel abarca demasiado suelo para verla sin parpadeo. */
        float hSuelo = terrainH(normalize(p));
        float huellaM = t * uRadiusM * uPix;
        if (esMar > 0.5) {
          float prof = max(-hSuelo, 0.0);
          // junto a la costa el mapa trae tierra y arena: el color de su mar abierto (ver MapaDelMar)
          vec3 mar = uHasSeaTex != 0 ? textureLod(uSeaTex, vec2(fract(u), v), 0.0).rgb : mix(uSeaColor, base, water);
          vec3 somero = mar * 1.25 + vec3(0.03, 0.06, 0.055);
          vec3 hondo = mar * 0.72;
          // atenuación exponencial, como la de la luz en el agua: el fondo baja a escalones
          // de decenas de metros y con una rampa corta cada escalón salía como un borde
          base = mix(somero, hondo, 1.0 - exp(-prof / 70.0));
          float espuma = (1.0 - smoothstep(0.1, 1.4, prof)) * (1.0 - smoothstep(12.0, 60.0, huellaM));
          base = mix(base, vec3(0.9, 0.93, 0.93), espuma * 0.75);
          water = 1.0 - espuma * 0.85;                           // la espuma no es un espejo
        } else {
          float playa = water * (1.0 - smoothstep(20.0, 40.0, hSuelo));
          base = mix(base, vec3(0.8, 0.74, 0.56), playa);
          base *= mix(0.7, 1.0, smoothstep(0.15, 1.2, hSuelo));  // arena mojada en la orilla
          water = 0.0;         // sin agua por encima del nivel del mar: nunca es un lago
        }
      }
      vec3 nSup = relieve ? nRel : normalize(p);
      if (water < 0.5) base = detalle(p, nSup, base, t * uRadiusM);
      // en el mapa, el agua con su color y la misma luz que el suelo: desde arriba solo
      // reflejaría el cielo, casi negro sin aire
      vec3 L = shadeGround(p, nSup, -d, uSun, pow(base, vec3(2.2)), uCenital != 0 ? 0.0 : water);
      vec3 tr = vec3(1.0), ins = vec3(0.0);
      if (uAtmos != 0) ins = inscatter(eye, d, max(ta.x, 0.0), t, uSun, jit, tr);
      col = L * tr + ins;
    } else {
      vec3 tr = vec3(1.0), ins = vec3(0.0);
      if (uAtmos != 0 && ta.y > 0.0) ins = inscatter(eye, d, max(ta.x, 0.0), ta.y, uSun, jit, tr);
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
    vec2 tn = raySphere(eye, d, uCloudR);
    float tc = length(eye) < uCloudR ? tn.y : tn.x;
    float tSuelo = tg.x;
    if (tc > 0.0 && (tSuelo <= 0.0 || tc < tSuelo)) {
      vec3 pc = eye + d * tc;
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
        if (uAtmos != 0) insN = inscatter(eye, d, max(ta.x, 0.0), tc, uSun, jit, trN);
        col = mix(col, colNube * trN + insN, a);
        // una nube espesa tapa lo que haya detrás, también los árboles
        if (uWriteDepth != 0 && a > 0.5) gl_FragDepth = min(gl_FragDepth, profundidad(tc * uRadiusM * dot(d, uF)));
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

        /* El suelo de cerca para el mapa 2D, visto desde arriba en su proyección: el mismo
           suelo que el vuelo (relieve de detalle, texturas del juego, costas) y los edificios
           en planta. El mapa pinta luego encima su retícula, trazas y marcadores. */
        bool cenital, cenSolReal;
        double cenLat, cenLon, cenPpd;
        double CenitalAltM => Math.Max(HMax, 0) + 5000;       // el ojo, por encima de todo
        public double CercaCenital = 1;                        // exposición: 0 la del globo, 1 la de paisaje

        public void RenderCenital(Batch2D batch, TextCache tc, double lat, double lon, double ppd, bool solReal)
        {
            if (skyProg == null) return;
            cenital = true; cenLat = lat; cenLon = lon; cenPpd = ppd; cenSolReal = solReal;
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
                // lo que abarca un píxel en el suelo es t·R·uPix, con t la altura del ojo
                skyProg.Float("uPix", Body.Radius * D2R / cenPpd / CenitalAltM);
            }
            skyProg.Float("uAlt", Len(eye) - 1);
            skyProg.Float("uStarShift", orbitShift);
            skyProg.Int("uGrid", SkyGrid && Mode == CamMode.Sky && !cenital ? 1 : 0);
            // sin día y noche, el Sol se queda en lo alto del observador; forzando la noche, apagado
            var sun = (cenital ? cenSolReal : Light) ? SunDir : up;
            // sin día y noche, el mapa se ilumina como un sombreado de relieve: desde el
            // noroeste a 45°, que es de donde se espera la luz al leer un mapa
            if (cenital && !cenSolReal) sun = Norm(Add(Add(Scale(up, 1), north, 0.7071), east, -0.7071));
            skyProg.Vec3("uSun", sun[0], sun[1], sun[2]);
            skyProg.Int("uSinBrillo", cenital ? 1 : 0);
            skyProg.Float("uSunRad", Math.Max(SunAngularRadius, 0.0015));
            AtmosUniforms(skyProg, 24, sunOn: !SkyForceNight);
            if (cenital) skyProg.Int("uAtmos", 0);           // un mapa: sin la bruma de 10 km de aire
            // a ras de suelo, el ajuste de exposición para paisaje (ver shadeGround); bajando
            // desde el globo se funde con el de fuera para que no dé un salto
            skyProg.Float("uCerca", cenital ? CercaCenital : Mode == CamMode.Planet ? 1 - SuaveEntre((Len(eye) - 1) * Body.Radius, 3000, 40000) : 1);
            skyProg.Float("uColorOff", ColorOff / 360);
            skyProg.Float("uBiomeOff", BiomeOff / 360);
            skyProg.Float("uBiomeAmt", BiomeTex != null ? BiomeAmt : 0);
            skyProg.Int("uHasColor", ColorTex != null ? 1 : 0);
            skyProg.Vec3("uSeaColor", SeaColor[0], SeaColor[1], SeaColor[2]);
            // la unidad 16 no la tienen todas las GPU (el mínimo son 16: de la 0 a la 15)
            bool marTex = SeaTex != null && GL.MaxTextureUnits > 16;
            skyProg.Int("uHasSeaTex", marTex ? 1 : 0);
            if (marTex) { BindTex(16, SeaTex); skyProg.Int("uSeaTex", 16); }
            skyProg.Int("uHasBiome", BiomeTex != null ? 1 : 0);
            /* Relieve: solo con mapa de alturas, y en la camara libre siempre; de pie en
               el suelo tambien, que es lo que hace que se vean las montanas de cerca. Y
               bajando en la vista 3D, que por debajo de RadioCerca se pinta con esto mismo:
               tiene que verse igual que el vuelo. */
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
            // cascara por encima de la cima mas alta, que es donde empieza a buscarse el suelo
            skyProg.Float("uTopR", 1 + Math.Max(0, HMax) / Body.Radius);
            skyProg.Vec2("uHeightSize", HeightTex?.Width ?? 1, HeightTex?.Height ?? 1);
            skyProg.Vec2("uColorSize", ColorTex?.Width ?? 1, ColorTex?.Height ?? 1);
            BindTex(0, ColorTex); skyProg.Int("uColor", 0);
            BindTex(1, BiomeTex); skyProg.Int("uBiome", 1);
            BindTex(2, HeightTex); skyProg.Int("uHeightTex", 2);
            /* Detalle del suelo: solo cerca del suelo, que es donde se ve, y con las cuatro
               texturas cargadas del juego. El origen va en metros ya reducido al tamano
               del mosaico, para no perder precision al sumarlo en el shader. */
            bool det = Detail && HasDetail && cerca;
            skyProg.Int("uDebug", Debug);
            bool nubes = CloudTex != null && Clouds && !cenital;
            skyProg.Int("uHasClouds", nubes ? 1 : 0);
            skyProg.Float("uCloudR", 1 + CloudAlt / Body.Radius);
            skyProg.Float("uCloudAmt", nubes ? CloudAmount : 0);
            skyProg.Float("uCloudOff", CloudOff / 360);
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
            /* El módulo es un múltiplo de todos los periodos de las texturas (4096 repeticiones:
               la escala más grande que se usa es de 1024) y de su ruido de variación. */
            double periodo = DetailTile * 4096;
            double Mod(double v) => ((v % periodo) + periodo) % periodo;
            skyProg.Vec3("uEyeMod", Mod(eye[0] * Body.Radius), Mod(eye[1] * Body.Radius), Mod(eye[2] * Body.Radius));
            skyProg.Float("uPeriodo", 4096);
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
            /* Con scatters, el cielo deja en el búfer de profundidad dónde está el suelo. Solo
               se escribe con la prueba activada, así que se activa sin descartar nada. */
            bool scatters = ScattersActive && !cenital;
            bool edificios = cenital ? StaticsOn && (HasBuildings || HasGroundVessels) : StaticsActive;
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
                        // ortográfica en la proyección del mapa: en él un grado de longitud mide
                        // lo mismo que uno de latitud, así que el este va estirado 1/cos(lat)
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
                // lo que viene después (órbitas, rótulos) no cuenta con esta profundidad
                GL.DepthFunc(GL.LESS);
                GL.Clear(GL.DEPTH_BUFFER_BIT);
                GL.Disable(GL.DEPTH_TEST);
                GL.Disable(GL.CULL_FACE);
            }
            if (cenital) return;                            // lo demás lo pone el mapa encima

            // órbitas y trazas, tapadas por el planeta con el corte de rayo del shader
            DrawOrbits(eye, occlude: true);
            DrawTrack(eye, occlude: true, ground: false);

            batch.Begin(W, H);
            PlacePins(batch, tc, eye, vesselsOnly: Mode != CamMode.Planet);
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
