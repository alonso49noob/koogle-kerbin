using System;

namespace KerbinMaps.Core
{
    /* The body being viewed. Almost the whole viewer talks about «the body» without naming it:
       these properties read the selected one (Kerbin unless another is chosen). Everything in
       SI. */
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

        // Altitude of a synchronous orbit, derived from mu and the sidereal day
        public static double SynchronousAlt =>
            Math.Cbrt(Mu * SiderealDay * SiderealDay / (4 * Math.PI * Math.PI)) - Radius;
    }

    /* Where the Sun is as seen from the current body.

       The body's position relative to the star comes from its chain of orbits (a moon adds its
       planet's), in the same inertial frame used by vessel orbits: the Sun is seen in the
       opposite direction, and its longitude on the map is the inertial one minus the body's
       rotation, same as a vessel's. The subsolar latitude is no longer always zero: it comes
       from the orbit's inclination. */
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

        /* Without a save there's nothing to measure the rotation with: the game's at the start. */
        public static double DefaultRotation(double ut) => Body.Current.InitialRotation + 360 * ut / Body.SiderealDay;

        /* Local solar time (0 to 6 h, noon at 3) at a longitude. */
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
        public const int MaxZoom = 9;             // the one for tiles and images
        /* The map zooms in further: up close the flight ground paints it in top-down view (see
           MapView.Fondo), and at 16 the KSC buildings can be made out. */
        public const int MaxZoomVista = 16;
        public const double InitialZoom = 2;
        public const double InitialLat = 0, InitialLon = -74.5;
        public const int TileSize = 256;
    }

    /* Range used to translate the gray of a heightmap into meters: gray 0 -> min, gray 255 ->
       max. Stock Kerbin terrain is procedural: without calibrating against two real altitudes,
       the figures that come out mean nothing. */
    public static class HeightRange
    {
        public const double Min = -1000, Max = 6764;
    }

    public static class BiomeConfig
    {
        public const double DefaultOpacity = 0.65;
        /* When listing the legend, colors covering less than this are ignored: they're the
           jagged edges, not real biomes. */
        public const double MinAreaPct = 0.02;
    }
}
