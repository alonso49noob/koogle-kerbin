using System;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Nubes que se mueven y cambian de forma.

       El mapa del mod de nubes es una foto fija. En el juego la capa gira alrededor del
       planeta (Kerbin: 29,9 m/s en superficie según su clouds.cfg, una vuelta cada 35 h) y
       cambia de forma con ruido animado. Aquí igual:

       - Giro: un desfase en longitud que crece con el tiempo de las nubes.
       - Forma: el mapa se muestrea desplazado por un ruido suave sobre la esfera que
         evoluciona con el tiempo (unos 80 km de desplazamiento en celdas de 100 km), y su
         cobertura sube y baja por zonas: los frentes se deforman, crecen y se deshacen.
       - Detalle: de cerca, la textura de detalle del mod (detail1) rompe el mapa, que a
         8192 de ancho sigue siendo de 5 km por texel.

       El tiempo de las nubes es el de la simulación más el reloj real, para que en pausa
       sigan moviéndose al ritmo del juego a x1. */
    public sealed partial class GlobeView
    {
        public double CloudTime;                  // segundos (simulación + reloj real)
        public double CloudSpeed = 29.89;         // m/s en superficie, hacia el oeste
        public Texture CloudDetailTex;

        /* Moviéndose se notan de cerca: a 30 m/s una nube a 2 km de altura cruza el cielo. */
        public bool CloudsAnimating => CloudTex != null && Clouds && CloudAmount > 0
                                       && (Mode == CamMode.Free || Mode == CamMode.Sky || PlanetaCerca);

        const string NubesGlsl = @"
uniform float uNubeFase;        // tiempo de las nubes para el ruido, ya reducido
uniform sampler2D uNubeDet;
uniform int uHasNubeDet;
uniform vec2 uNubeDetOff;       // desplazamiento del detalle, en metros ya reducidos

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

/* El uv del mapa de nubes (ya girado), desplazado por el tiempo. n: la dirección desde el
   centro del cuerpo. En longitud se divide por el coseno de la latitud para que el
   desplazamiento mida lo mismo en km en todas partes. */
vec2 nubeUv(vec3 n, vec2 uv) {
  vec3 q = n * 6.0;
  vec3 tt = vec3(0.0, 0.0, uNubeFase);
  vec2 w = vec2(nRuido(q + tt), nRuido(q + vec3(31.7, 11.3, 0.0) + tt)) - 0.5;
  w += 0.5 * (vec2(nRuido(q * 2.7 + vec3(5.1, 0.0, 0.0) + tt * 1.6),
                   nRuido(q * 2.7 + vec3(0.0, 9.2, 0.0) + tt * 1.6)) - 0.5);
  float c = max(sqrt(max(1.0 - n.y * n.y, 0.0)), 0.15);
  return vec2(uv.x + w.x * 0.0035 / c, clamp(uv.y + w.y * 0.005, 0.0, 1.0));
}

/* Cuánta nube queda en cada zona: entre algo más de la mitad y algo más de la del mapa. */
float nubeVida(vec3 n) {
  return 0.55 + 0.6 * smoothstep(0.15, 0.8, nRuido(n * 3.5 + vec3(0.0, 0.0, uNubeFase * 0.7 + 40.0)));
}

/* El detalle del mod de cerca: rompe los bordes y el interior de la nube. m: el punto en
   metros sobre el plano local (x este, y norte); cerca: 1 a pocos km, 0 lejos. */
float nubeDetalle(vec2 m, float cerca) {
  if (uHasNubeDet == 0 || cerca <= 0.0) return 1.0;
  vec2 p = (m + uNubeDetOff) / 3000.0;
  // el detalle del mod es multiplicativo y de poco contraste (0,65 a 1, media 0,81)
  float d = texture(uNubeDet, p).g * 0.6 + texture(uNubeDet, p * 3.7 + 0.37).g * 0.4;
  return mix(1.0, clamp(1.0 + (d - 0.81) * 3.5, 0.3, 1.6), cerca);
}
";

        /* Los uniformes de las nubes que dependen del tiempo, para el shader que las pinte. */
        void NubeUniforms(ShaderProgram p, int unidadDetalle)
        {
            double horas = CloudTime / 3600;
            // la fase del ruido: una celda cada ~7 h de juego; reducida para la precisión
            p.Float("uNubeFase", horas * 0.15 % 1000.0);
            // la unidad del detalle puede no existir en GPU modestas (el mínimo son 16)
            bool det = CloudDetailTex != null && unidadDetalle >= 0 && unidadDetalle < GL.MaxTextureUnits;
            p.Int("uHasNubeDet", det ? 1 : 0);
            if (det) { BindTex(unidadDetalle, CloudDetailTex); p.Int("uNubeDet", unidadDetalle); }
            // el detalle va con el viento algo más despacio que la capa (detailSpeed del mod: 6 m/s)
            double metros = CloudTime * 6.0 % 30000.0;
            p.Vec2("uNubeDetOff", -metros, 0);
        }

        /* El giro de la capa, como fracción de vuelta: hacia el oeste, a CloudSpeed. */
        double NubeGiro()
        {
            double vuelta = 2 * Math.PI * Body.Radius;
            double f = CloudTime * CloudSpeed / vuelta;
            return CloudOff / 360 + (f - Math.Floor(f));
        }
    }
}
