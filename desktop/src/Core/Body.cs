using System;

namespace KerbinMaps.Core
{
    /* El cuerpo que se está viendo. Casi todo el visor habla de «el cuerpo» sin nombrarlo:
       estas propiedades leen el seleccionado (Kerbin si no se elige otro). Todo en SI. */
    public static class Body
    {
        public static BodyDef Current = SolarSystem.Home;

        public static string Name => Current.Name;
        public static string Label => Current.Label;
        public static double Radius => Current.Radius;             // m
        public static double Mu => Current.Mu;                     // m^3/s^2  (GM)
        public static double SiderealDay => Current.SiderealDay;   // s
        public static double SolarDay => Current.SolarDay;         // s
        public static double Atmosphere => Current.Atmosphere;     // m
        public static double Soi => Current.Soi;                   // m

        // Altitud de una órbita síncrona, derivada de mu y del día sidéreo
        public static double SynchronousAlt =>
            Math.Cbrt(Mu * SiderealDay * SiderealDay / (4 * Math.PI * Math.PI)) - Radius;
    }

    /* Dónde está el Sol visto desde el cuerpo actual.

       La posición del cuerpo respecto a la estrella sale de su cadena de órbitas (una luna
       suma la de su planeta), en el mismo marco inercial que usan las órbitas de las naves:
       el Sol se ve en la dirección opuesta, y su longitud en el mapa es la inercial menos la
       rotación del cuerpo, igual que la de una nave. La latitud subsolar ya no es siempre
       cero: sale de la inclinación de la órbita. */
    public static class Sun
    {
        public readonly record struct Position(double Lat, double Lon, double AngularRadius, bool Exists);

        public static Position Subsolar(double ut, double rotation)
        {
            var b = Body.Current;
            var star = SolarSystem.Star;
            if (b.IsStar || star == null) return new Position(0, 0, 0, false);
            var p = SolarSystem.PositionAt(b, ut);
            double r = Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
            if (r <= 0) return new Position(0, 0, 0, false);
            double lat = Math.Asin(Math.Clamp(-p[2] / r, -1, 1)) * 180 / Math.PI;
            double lonIner = Math.Atan2(-p[1], -p[0]) * 180 / Math.PI;
            return new Position(lat, Geo.WrapLon(lonIner - rotation), star.Radius / r, true);
        }

        /* Sin partida no hay con qué medir la rotación: la del juego al empezar. */
        public static double DefaultRotation(double ut) => Body.Current.InitialRotation + 360 * ut / Body.SiderealDay;

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
