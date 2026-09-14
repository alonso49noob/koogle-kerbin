using System;
using System.Collections.Generic;

namespace KerbinMaps.Core
{
    public readonly record struct TrackPoint(double Lat, double Lon, double Alt, double T);

    public sealed class OrbitResult
    {
        public List<TrackPoint> Points;
        public double A, E, T, VPe, VAp, Drift, Pe, Ap, MaxLat, FootprintPe, FootprintAp;
        public bool Synchronous, Suborbital, InAtmosphere;
    }

    /* Traza terrestre de una órbita alrededor de Kerbin.

       KSP usa cónicas parcheadas: dentro de la SOI de Kerbin la órbita es una elipse
       kepleriana exacta, sin achatamiento ni J2. Basta propagar Kepler y restar la
       rotación del planeta para pasar del marco inercial al fijo al cuerpo. */
    public static class ManualOrbit
    {
        /* Ecuación de Kepler M = E - e·sen E, por Newton-Raphson. */
        public static double EccentricAnomaly(double M, double e, double tol = 1e-12)
        {
            M = ((M % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
            double E = e < 0.8 ? M : Math.PI;
            for (int i = 0; i < 60; i++)
            {
                double d = (E - e * Math.Sin(E) - M) / (1 - e * Math.Cos(E));
                E -= d;
                if (Math.Abs(d) < tol) break;
            }
            return E;
        }

        /* pe/ap en metros sobre el nivel del mar; ángulos en grados. */
        public static OrbitResult Compute(double pe, double ap, double incDeg, double lanDeg, double argpDeg, int orbitsIn)
        {
            if (ap < pe) (pe, ap) = (ap, pe);

            double rp = Body.Radius + pe, ra = Body.Radius + ap;
            double a = (rp + ra) / 2;
            double e = (ra - rp) / (ra + rp);
            double n = Math.Sqrt(Body.Mu / (a * a * a));     // movimiento medio, rad/s
            double T = 2 * Math.PI / n;                       // periodo orbital, s
            double wb = 2 * Math.PI / Body.SiderealDay;       // rotación de Kerbin, rad/s

            double inc = incDeg * Geo.D2R, lan = lanDeg * Geo.D2R, argp = argpDeg * Geo.D2R;
            int orbits = Math.Max(1, Math.Min(60, orbitsIn == 0 ? 1 : orbitsIn));

            /* Más muestras en órbitas excéntricas: cerca del periapsis la traza avanza
               muy rápido y con pocos puntos se ve angulosa. */
            int perOrbit = (int)Math.Round(180 * (1 + 2 * e), MidpointRounding.AwayFromZero);
            int steps = orbits * perOrbit;
            var pts = new List<TrackPoint>(steps + 1);

            for (int i = 0; i <= steps; i++)
            {
                double t = ((double)i / steps) * orbits * T;
                double E = EccentricAnomaly(n * t, e);
                double nu = 2 * Math.Atan2(Math.Sqrt(1 + e) * Math.Sin(E / 2), Math.Sqrt(1 - e) * Math.Cos(E / 2));
                double r = a * (1 - e * Math.Cos(E));
                double u = argp + nu;                          // argumento de latitud

                double X = r * (Math.Cos(lan) * Math.Cos(u) - Math.Sin(lan) * Math.Sin(u) * Math.Cos(inc));
                double Y = r * (Math.Sin(lan) * Math.Cos(u) + Math.Cos(lan) * Math.Sin(u) * Math.Cos(inc));
                double Z = r * (Math.Sin(u) * Math.Sin(inc));

                double lat = Math.Asin(Math.Max(-1, Math.Min(1, Z / r))) * Geo.R2D;
                double lon = Geo.WrapLon((Math.Atan2(Y, X) - wb * t) * Geo.R2D);
                pts.Add(new TrackPoint(lat, lon, r - Body.Radius, t));
            }

            return new OrbitResult
            {
                Points = pts,
                A = a, E = e, T = T,
                VPe = Math.Sqrt(Body.Mu * (2 / rp - 1 / a)),
                VAp = Math.Sqrt(Body.Mu * (2 / ra - 1 / a)),
                Drift = -(T / Body.SiderealDay) * 360,        // desplazamiento de la traza por vuelta
                Pe = pe, Ap = ap,
                MaxLat = Math.Min(90, Math.Abs(incDeg) <= 90 ? Math.Abs(incDeg) : 180 - Math.Abs(incDeg)),
                FootprintPe = Geo.HorizonRadius(pe),
                FootprintAp = Geo.HorizonRadius(ap),
                Synchronous = Math.Abs(T - Body.SiderealDay) / Body.SiderealDay < 0.005,
                Suborbital = pe < 0,
                InAtmosphere = pe < Body.Atmosphere
            };
        }
    }
}
