using System;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Cámara libre a ras de suelo: se vuela por encima del terreno como en un avión, pero
       sin nave, sin inercia y sin poder estrellarse.

       Se dibuja con el mismo trazado de rayos que la vista del cielo, al que se le añade
       el relieve: cada rayo avanza por el mapa de alturas hasta cruzar la superficie. Una
       malla no serviría aquí — a ras de suelo haría falta teselado y niveles de detalle —,
       y el trazado da el horizonte y las siluetas de las montañas exactos. */
    public sealed partial class GlobeView
    {
        public double FreeLat = -0.0972, FreeLon = -74.5577, FreeAlt = 120;   // m sobre el nivel del mar
        public double FreeAz = 90, FreeEl = -5;
        public double FreeSpeed = 120;                                        // m/s
        public bool FreeRelief = true;

        /* Texturas de detalle del suelo, las del propio juego: hierba, arena, roca y
           nieve. Se mezclan por pendiente y por el color del mapa, y solo se notan de
           cerca: a un kilómetro ya manda el mapa. */
        public Texture DetGrass, DetSand, DetRock, DetSnow;
        public double DetailTile = 22;            // metros por repetición
        public double DetailAmount = 0.85;
        public bool Detail = true;
        public int Debug;                         // 0 normal; 1 gris, 2 metros, 3 distancia

        /* Nubes: el mapa del juego (EVE / Stock Volumetric Clouds) sobre una capa a su
           altura. Sin el mod instalado no hay textura y no se pintan. */
        public Texture CloudTex;
        public bool Clouds = true;
        public bool GlobeClouds = true;           // la capa de nubes también en la vista 3D
        public double CloudAlt = 5200;            // m sobre el nivel del mar
        public double CloudAmount = 1.0;
        public double CloudOff;                   // giro en longitud del mapa de nubes

        public bool HasDetail => DetGrass != null || DetSand != null || DetRock != null || DetSnow != null;

        /* Con Parallax las cuatro ranuras son baja, media, alta y pendiente (en ese orden:
           DetGrass, DetSand, DetSnow y DetRock) y se mezclan con sus parámetros. */
        public bool DetailParallax;
        public (double a, double b) PxLowMid = (0, 1), PxMidHigh = (1e6, 1e6 + 1);
        public (double power, double contrast, double mid) PxSteep = (8, 4, 0.7);
        public Texture PxInfluence, PxDisplacement, PxOcclusion;
        public Texture[] PxBump;                  // normales de baja, media, alta y pendiente

        /* Variación de textura: que el mosaico no se vea repetido (ver detalle en el shader). */
        public bool DetailVariation = true;

        /* Altura del terreno bajo un punto, en metros sobre el nivel del mar. La pone la
           ventana con el mapa de alturas del cuerpo; sin mapa, todo a cero. */
        public Func<double, double, double> GroundAt;

        public const double FreeMinSpeed = 2, FreeMaxSpeed = 20000;

        public double FreeGround => GroundAt?.Invoke(FreeLat, FreeLon) ?? 0;

        /* Altura sobre el suelo, que es la que importa volando. */
        public double FreeAgl => FreeAlt - FreeGround;

        public void EnterFree() => SetMode(CamMode.Free);

        public void ExitFree()
        {
            if (Mode != CamMode.Free) return;
            SetMode(CamMode.Planet);
        }

        /* Coloca la cámara y la deja mirando al horizonte. */
        public void SetFree(double lat, double lon, double? alt = null, double? az = null)
        {
            FreeLat = Math.Clamp(lat, -89.9, 89.9);
            FreeLon = Geo.WrapLon(lon);
            if (az is double a) FreeAz = ((a % 360) + 360) % 360;
            double suelo = FreeGround;
            FreeAlt = alt ?? (suelo + 150);
            if (FreeAlt < suelo + 2) FreeAlt = suelo + 2;
        }

        public void FreeLook(double daz, double del)
        {
            FreeAz = ((FreeAz + daz) % 360 + 360) % 360;
            FreeEl = Math.Clamp(FreeEl + del, -89, 89);
        }

        /* Un paso de vuelo. `fwd`, `side` y `up` van de -1 a 1; `dt` en segundos.

           Adelante sigue a donde se mira (como un avión), lateral va por el horizonte y
           arriba es la vertical del sitio. Al moverse, la latitud y la longitud se
           recalculan por geodesia sobre la esfera, así que cruzar los polos o el
           antimeridiano no rompe nada. */
        public bool MoveFree(double dt, double fwd, double side, double up, double boost = 1)
        {
            if (Mode != CamMode.Free) return false;
            if (fwd == 0 && side == 0 && up == 0) return false;

            double v = FreeSpeed * boost * dt;                    // metros en este paso
            double el = FreeEl * D2R, az = FreeAz * D2R;

            // componentes en el marco local: norte, este y vertical
            double norte = fwd * Math.Cos(el) * Math.Cos(az) + side * Math.Cos(az + Math.PI / 2);
            double este = fwd * Math.Cos(el) * Math.Sin(az) + side * Math.Sin(az + Math.PI / 2);
            double vert = fwd * Math.Sin(el) + up;

            double dist = Math.Sqrt(norte * norte + este * este) * v;
            if (dist > 0)
            {
                double rumbo = Math.Atan2(este, norte) * R2D;
                var p = Geo.Destination(FreeLat, FreeLon, rumbo, dist);
                FreeLat = Math.Clamp(p.Lat, -89.9, 89.9);
                FreeLon = p.Lon;
            }
            FreeAlt += vert * v;

            // no se atraviesa el suelo: dos metros por encima, como quien camina
            double suelo = FreeGround;
            if (FreeAlt < suelo + 2) FreeAlt = suelo + 2;
            if (FreeAlt > Body.Radius * 2) FreeAlt = Body.Radius * 2;
            return true;
        }

        /* Si el suelo ha subido por debajo de la cámara (el mapa de alturas llega cuando
           ya estaba colocada, o se ha cambiado su calibración), se la sube encima. Dentro
           del terreno todos los rayos chocan al salir y la pantalla queda de un gris liso. */
        public bool ClampFree()
        {
            if (Mode != CamMode.Free) return false;
            double suelo = FreeGround;
            if (FreeAlt >= suelo + 2) return false;
            FreeAlt = suelo + 2;
            return true;
        }

        public void FreeSpeedStep(int pasos)
        {
            FreeSpeed = Math.Clamp(FreeSpeed * Math.Pow(1.3, pasos), FreeMinSpeed, FreeMaxSpeed);
        }

        /* Posición de la cámara en radios del cuerpo. */
        public double[] FreePos() => Sph(FreeLat, FreeLon, 1 + FreeAlt / Body.Radius);
    }
}
