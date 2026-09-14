using System;

namespace KerbinMaps.Core
{
    /* Parámetros del cuerpo y configuración global.
       Valores de Kerbin stock (KSP 1.x). Todo en unidades SI salvo donde se indique. */
    public static class Body
    {
        public const string Name = "Kerbin";
        public const double Radius = 600000;          // m
        public const double Mu = 3.5316000e12;        // m^3/s^2  (GM)
        public const double SiderealDay = 21549.425;  // s  (5 h 59 m 9,425 s)
        public const double SolarDay = 21600;         // s  (6 h exactas)
        public const double Atmosphere = 70000;       // m
        public const double Soi = 84159286;           // m

        // Altitud de una órbita síncrona (keoestacionaria), derivada de mu y siderealDay
        public static double SynchronousAlt =>
            Math.Cbrt(Mu * SiderealDay * SiderealDay / (4 * Math.PI * Math.PI)) - Radius;
    }

    /* Dónde está el Sol. Kerbin no tiene inclinación del eje, así que el punto subsolar
       va siempre por el ecuador y basta su longitud.

       Kerbin gira alrededor de Kerbol en una órbita circular, sin inclinación, con LAN y
       argumento del periapsis a cero: su longitud inercial es su anomalía media, y el Sol
       se ve en la opuesta. Es el mismo marco inercial que usan las órbitas de las naves
       (ahí se mide el ángulo de rotación del cuerpo), así que la longitud del mapa sale
       igual que la de una nave: longitud inercial menos rotación. */
    public static class Sun
    {
        public const double Mu = 1.1723328e18;         // m^3/s^2 (Kerbol)
        public const double KerbinSma = 13599840256;   // m
        public const double KerbinMna = 3.14;          // rad, en la época 0
        public const double InitialRotation = 90;      // ° de Kerbin en UT 0
        public const double AngularRadius = 261600000.0 / KerbinSma;   // rad, ~1,1°

        public static double InertialLon(double ut)
        {
            double n = Math.Sqrt(Mu / (KerbinSma * KerbinSma * KerbinSma));
            return (KerbinMna + n * ut) * 180 / Math.PI + 180;
        }

        /* Longitud subsolar con la rotación del cuerpo en ese instante (la que usan las
           posiciones de las naves). */
        public static double SubsolarLon(double ut, double rotation) => Geo.WrapLon(InertialLon(ut) - rotation);

        /* Sin partida no hay con qué medir la rotación: la del juego al empezar. */
        public static double DefaultRotation(double ut) => InitialRotation + 360 * ut / Body.SiderealDay;

        /* Hora solar local (0 a 6 h, mediodía a las 3) en una longitud. */
        public static double LocalHours(double lon, double subsolarLon)
        {
            double f = ((lon - subsolarLon) / 360 + 0.5) % 1;
            if (f < 0) f += 1;
            return f * Body.SolarDay / 3600;
        }
    }

    public static class MapConfig
    {
        public const int MinZoom = 0;
        public const int MaxZoom = 9;
        public const double InitialZoom = 2;
        public const double InitialLat = 0, InitialLon = -74.5;
        public const int TileSize = 256;
    }

    /* Rango con el que se traduce el gris de un heightmap a metros: gris 0 -> min,
       gris 255 -> max. El terreno de Kerbin stock es procedural: sin calibrar contra
       dos altitudes reales, las cifras que salgan no significan nada. */
    public static class HeightRange
    {
        public const double Min = -1000, Max = 6764;
    }

    public static class BiomeConfig
    {
        public const double DefaultOpacity = 0.65;
        /* Al listar la leyenda se ignoran los colores que ocupen menos de esto:
           son el dentado de los bordes, no biomas de verdad. */
        public const double MinAreaPct = 0.02;
    }
}
