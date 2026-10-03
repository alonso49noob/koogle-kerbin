using System;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* El mar, compartido por el globo visto desde fuera y por el suelo de cerca (cielo, vuelo,
       la bajada del globo y el mapa 2D de cerca).

       La superficie sigue siendo la esfera del océano; las olas solo inclinan la normal. Son
       una suma de ocho trenes de onda de 2 a 60 m, con la velocidad de las olas de aguas
       profundas (ω² = g·k), que se van apagando cuando una longitud de onda ocupa pocos
       píxeles. Lo que se apaga no se pierde: su pendiente pasa a la rugosidad del brillo del
       Sol (una distribución de Beckmann con la varianza de Cox y Munk). Así, de cerca se ven
       las olas y los destellos sueltos, y de lejos el reflejo se ensancha solo hasta la mancha
       de brillo que se ve desde órbita, con zonas más lisas y más rizadas según el viento.

       Debajo, el agua deja ver el fondo donde cubre poco: la luz que baja y sube se atenúa por
       canal (el rojo se pierde enseguida), así que la arena de la orilla pasa a turquesa y luego
       al color del mar abierto del mapa (que en Eve es morado). En la orilla, espuma que llega
       en líneas con las olas. */
    public sealed partial class GlobeView
    {
        /* Olas y espuma; sin ellas, el mar liso de siempre con su brillo. */
        public bool Olas = true;
        /* Segundos de reloj real para mover las olas. No van con el tiempo de la simulación:
           a x1000 serían un hervidero. */
        public double AguaT;

        /* Longitud de onda (m), giro respecto al viento (grados) y pendiente (a·k) de cada tren:
           dieciséis olas de 80 a 1,6 m, repartidas en escala logarítmica con algo de azar y las
           direcciones abiertas alrededor del viento, y ocho rizos de 1,4 m a 25 cm que solo se
           ven a pocos metros (sin ellos, a ras de agua el mar salía liso y borroso). Con pocos
           trenes y bien ordenados, el mar salía a rayas, como un tejido. Fijos (misma semilla)
           para que no cambien al abrir. */
        const int NumOlas = 24, NumLargas = 16;
        static readonly (double L, double Giro, double Pend)[] TrenesDeOlas = CrearTrenes();
        const double RumboViento = 35;            // en el plano de proyección, grados
        const double CeldaGrupos = 170;           // metros: el tamaño de los grupos de olas

        static (double, double, double)[] CrearTrenes()
        {
            var r = new Random(1971);
            var t = new (double, double, double)[NumOlas];
            for (int i = 0; i < NumOlas; i++)
            {
                if (i >= NumLargas)
                {
                    // rizos: de cualquier lado, con más pendiente
                    double lr = 1.4 * Math.Pow(0.25 / 1.4, (i - NumLargas) / (NumOlas - NumLargas - 1.0)) * (0.9 + 0.2 * r.NextDouble());
                    t[i] = (lr, (r.NextDouble() * 2 - 1) * 95, 0.07);
                    continue;
                }
                double l = 80 * Math.Pow(1.6 / 80, i / (NumLargas - 1.0)) * (0.88 + 0.24 * r.NextDouble());
                // dos dados: más trenes cerca del viento que de lado. El mar de fondo (lo largo)
                // viene casi todo de un lado; lo corto, de cualquiera
                double abierto = i < 4 ? 22 : i < 9 ? 50 : 80;
                double giro = ((r.NextDouble() + r.NextDouble()) - 1) * abierto;
                if (i == 6) giro += 63;                                               // algo de mar cruzado
                double pend = l > 12 ? 0.062 : l > 4 ? 0.056 : 0.048;
                t[i] = (l, giro, pend);
            }
            return t;
        }

        /* Lo bastante cerca para que se vean moverse las olas o la espuma. */
        public bool MarAnimating
        {
            get
            {
                if (!Olas || !Body.Current.Ocean || HeightTex == null) return false;
                if (Mode != CamMode.Free && Mode != CamMode.Sky && !PlanetaCerca) return false;
                return EyeGround().Agl < 6000;
            }
        }

        /* Los trenes de olas en el plano de proyección: el número de onda es un múltiplo entero
           de 2π/periodo para que el dibujo no salte cuando uEyeMod da la vuelta, y la fase se
           calcula aquí en doble precisión. */
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
            /* Lo que no se dibuja nunca (las ondas capilares, de centímetros): con lo de las
               olas suma unos 0,03, lo de Cox y Munk con 6 m/s de viento. */
            p.Float("uOlaCapilar", olas ? 0.004 : 0.03);
            p.Float("uOlaSigmaTotal", (olas ? sigma : 0) + (olas ? 0.004 : 0.03));
            // los grupos: un ruido periódico con un número entero de celdas por periodo
            double n = Math.Max(1, Math.Round(periodo / CeldaGrupos));
            p.Vec2("uOlaGrupo", n / periodo, n);
        }

        /* uSinBrillo (el mapa visto desde arriba) quita reflejos; uCerca es 0 desde fuera. */
        const string AguaGlsl = @"
uniform int uOlas;
uniform float uAguaT;
uniform vec4 uOla[24];          // número de onda (rad/m) en el plano de proyección, amplitud (m) y fase
uniform float uOlaCapilar, uOlaSigmaTotal;
uniform vec2 uOlaGrupo;         // celdas de los grupos por metro, y por periodo

float aHash(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }
float aHash2(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }

/* Ruido de valor que se repite cada per celdas: con el periodo de uEyeMod, no salta. */
float aRuidoP(vec2 p, float per) {
  vec2 i = floor(p), f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  vec2 i1 = mod(i + 1.0, per);
  i = mod(i, per);
  return mix(mix(aHash2(i), aHash2(vec2(i1.x, i.y)), f.x), mix(aHash2(vec2(i.x, i1.y)), aHash2(i1), f.x), f.y);
}

/* Ruido de valor en 3D, suave, para las zonas de viento (en direcciones: sin problemas de precisión). */
float aRuido(vec3 x) {
  vec3 i = floor(x), f = fract(x);
  f = f * f * (3.0 - 2.0 * f);
  return mix(mix(mix(aHash(i), aHash(i + vec3(1, 0, 0)), f.x), mix(aHash(i + vec3(0, 1, 0)), aHash(i + vec3(1, 1, 0)), f.x), f.y),
             mix(mix(aHash(i + vec3(0, 0, 1)), aHash(i + vec3(1, 0, 1)), f.x), mix(aHash(i + vec3(0, 1, 1)), aHash(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

/* Lo rizado del mar en cada zona: el viento no sopla igual en todas partes, y desde órbita el
   brillo del Sol sale a manchas (más liso al abrigo de la costa). */
float aViento(vec3 dir) {
  float n = aRuido(dir * 37.0) * 0.65 + aRuido(dir * 113.0) * 0.35;
  return mix(0.55, 1.45, smoothstep(0.2, 0.8, n));
}

/* Las olas en un plano: (dh/da, dh/db, h) y, aparte, la varianza de pendiente de los trenes que
   ya no se pueden dibujar a px metros por píxel. Los trenes van en grupos: dos ruidos lentos
   suben y bajan su amplitud, como en el mar de verdad, donde las olas llegan en series. */
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

/* La normal del agua: las olas se proyectan en los dos planos de ejes del mundo más de cara a la
   vertical (como las texturas del suelo) y se funden donde cambia el plano. c en metros. */
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

/* Albedo del agua (lineal): el fondo visto a través de prof metros, sobre el color del mar abierto
   del mapa. Arena junto a la orilla y, más hondo, fondo más oscuro. */
vec3 aAlbedo(vec3 mar, float prof, float cosV) {
  vec3 hondo = pow(mar, vec3(2.2)) * 0.7;
  vec3 fondo = pow(mix(vec3(0.80, 0.74, 0.56), vec3(0.36, 0.40, 0.30), smoothstep(3.0, 22.0, prof)), vec3(2.2)) * 0.55;
  // camino de la luz: baja vertical y sube hacia el ojo, refractada (más vertical que la mirada)
  float camino = prof * (1.0 + 1.0 / mix(1.0, max(cosV, 0.05), 0.35));
  vec3 T = exp(-vec3(0.30, 0.065, 0.045) * camino);
  return mix(hondo, fondo, T);
}

/* Espuma: en la orilla, líneas que llegan con las olas y se deshacen; mar adentro, alguna cresta
   rota cuando se ven las olas. pxm son metros por píxel. */
float aEspuma(vec3 c, vec3 dir, float prof, float alto, float pxm) {
  if (uOlas == 0) return (1.0 - smoothstep(0.1, 1.4, prof)) * (1.0 - smoothstep(12.0, 60.0, pxm)) * 0.75;
  float lejos = 1.0 - smoothstep(10.0, 70.0, pxm);
  if (lejos <= 0.0) return 0.0;
  float ruptura = aRuido(c * 0.35) * 0.6 + aRuido(c * 1.3) * 0.4;
  // líneas que avanzan hacia la orilla: la fase crece con la profundidad y con el tiempo
  float linea = sin(prof * 2.2 + uAguaT * 1.1 + ruptura * 5.0);
  float orilla = 1.0 - smoothstep(0.3, 3.2, prof);
  float f = orilla * (0.55 * (1.0 - smoothstep(0.0, 0.9, prof)) + smoothstep(0.35, 0.95, linea) * smoothstep(0.25, 0.6, ruptura));
  // alguna cresta rota mar adentro, solo donde se dibujan las olas
  float cresta = smoothstep(2.0, 2.9, alto) * smoothstep(0.65, 0.9, ruptura) * (1.0 - smoothstep(0.4, 2.0, pxm)) * 0.35;
  return clamp(max(f, cresta), 0.0, 1.0) * lejos;
}

/* Luz que sale del agua hacia el ojo. n: normal con las olas; s2: rugosidad del brillo; mar: color
   del mar abierto (sRGB); cielo: luz del cielo en la dirección reflejada (o negativa para
   estimarla); espuma: cuánto la tapa. */
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

  // el mapa visto desde arriba: el agua con su color, la luz como en el suelo y el relieve de las olas
  if (uSinBrillo != 0) {
    vec3 L = albedo / PI * (sun * max(dot(n, s), 0.0) + (skyE * (0.75 + 0.25 * dot(n, up)) + nightE) * comp);
    vec3 E = vec3(0.8) * alb / PI * (sun * max(dot(up, s), 0.0) + (skyE + nightE) * comp);
    return mix(L, E, espuma);
  }

  float F = 0.02 + 0.98 * pow(1.0 - NoV, 5.0);
  // lo que sale de dentro del agua
  vec3 dentro = albedo / PI * (sun * cosS + (skyE + nightE) * comp) * (1.0 - F);
  // el cielo reflejado
  if (cielo.x < 0.0) {
    vec3 r = reflect(-v, n);
    float el = clamp(dot(r, up), 0.0, 1.0);
    cielo = skyE / PI * mix(1.9, 0.9, sqrt(el)) + vec3(0.002, 0.003, 0.006);
  }
  vec3 refl = cielo * F;
  // el Sol: microfacetas de Beckmann con la pendiente que no se ve como ola
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
