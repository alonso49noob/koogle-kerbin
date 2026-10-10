using System;
using System.Collections.Generic;
using System.Globalization;

namespace KerbinMaps.Core
{
    public readonly record struct LatLon(double Lat, double Lon);

    /* Projection and geodesy on Kerbin. The map is equirectangular (plate carrée), the same
       projection as KSP's textures: a 2:1 image is pasted as is. */
    public static class Geo
    {
        public const double D2R = Math.PI / 180, R2D = 180 / Math.PI;
        static double R => Body.Radius;
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /* Great-circle distance, in meters. */
        public static double Distance(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * D2R, p2 = lat2 * D2R;
            double dp = (lat2 - lat1) * D2R, dl = (lon2 - lon1) * D2R;
            double h = Math.Pow(Math.Sin(dp / 2), 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Pow(Math.Sin(dl / 2), 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        }

        /* Initial bearing from A to B, in degrees from north. */
        public static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * D2R, p2 = lat2 * D2R, dl = (lon2 - lon1) * D2R;
            double y = Math.Sin(dl) * Math.Cos(p2);
            double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            return (Math.Atan2(y, x) * R2D + 360) % 360;
        }

        /* Point at distance `dist` (m) and bearing `brg` (°) from an origin. */
        public static LatLon Destination(double lat, double lon, double brg, double dist)
        {
            double d = dist / R, t = brg * D2R, p1 = lat * D2R, l1 = lon * D2R;
            double p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(t));
            double l2 = l1 + Math.Atan2(Math.Sin(t) * Math.Sin(d) * Math.Cos(p1),
                                        Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
            return new LatLon(p2 * R2D, WrapLon(l2 * R2D));
        }

        /* Great-circle interpolation: in equirectangular the straight line between two points
           is NOT the shortest path, so it has to be split up. */
        public static List<LatLon> GreatCircle(double lat1, double lon1, double lat2, double lon2, int steps = 0)
        {
            double d = Distance(lat1, lon1, lat2, lon2) / R;
            int n = steps > 0 ? steps : Math.Max(2, (int)Math.Ceiling(d * R2D / 2));
            if (d < 1e-9) return new List<LatLon> { new(lat1, lon1), new(lat2, lon2) };
            double p1 = lat1 * D2R, l1 = lon1 * D2R, p2 = lat2 * D2R, l2 = lon2 * D2R;
            var output = new List<LatLon>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                double f = (double)i / n;
                double A = Math.Sin((1 - f) * d) / Math.Sin(d);
                double B = Math.Sin(f * d) / Math.Sin(d);
                double x = A * Math.Cos(p1) * Math.Cos(l1) + B * Math.Cos(p2) * Math.Cos(l2);
                double y = A * Math.Cos(p1) * Math.Sin(l1) + B * Math.Cos(p2) * Math.Sin(l2);
                double z = A * Math.Sin(p1) + B * Math.Sin(p2);
                output.Add(new LatLon(Math.Atan2(z, Math.Sqrt(x * x + y * y)) * R2D, Math.Atan2(y, x) * R2D));
            }
            return output;
        }

        /* Circle of constant radius on the surface (e.g. the visible horizon from a given
           altitude). */
        public static List<LatLon> Circle(double lat, double lon, double radiusM, int steps = 180)
        {
            var pts = new List<LatLon>(steps + 1);
            for (int i = 0; i <= steps; i++) pts.Add(Destination(lat, lon, i * 360.0 / steps, radiusM));
            return pts;
        }

        /* Surface radius of the cap visible from an altitude h. */
        public static double HorizonRadius(double h) => R * Math.Acos(R / (R + h));

        public static double WrapLon(double lon)
        {
            double x = (lon + 180) % 360;
            if (x < 0) x += 360;
            return x - 180;
        }

        /* A polyline that crosses ±180° would be drawn as a stripe from side to side. We split
           it into pieces and compute the crossing latitude by interpolation. */
        public static List<List<LatLon>> SplitAntimeridian(IReadOnlyList<LatLon> points)
        {
            var segs = new List<List<LatLon>>();
            var cur = new List<LatLon>();
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (i > 0)
                {
                    var prev = points[i - 1];
                    if (Math.Abs(p.Lon - prev.Lon) > 180)
                    {
                        double dir = p.Lon > prev.Lon ? -180 : 180;
                        double dLon = (p.Lon - prev.Lon) - Math.Sign(p.Lon - prev.Lon) * 360;
                        double f = dLon == 0 ? 0.5 : (dir - prev.Lon) / dLon;
                        double latX = prev.Lat + (p.Lat - prev.Lat) * f;
                        cur.Add(new LatLon(latX, dir));
                        segs.Add(cur);
                        cur = new List<LatLon> { new(latX, -dir) };
                    }
                }
                cur.Add(p);
            }
            if (cur.Count > 1) segs.Add(cur);
            return segs;
        }

        /* Undoes the ±360° jumps between consecutive points: the line stays continuous in
           longitude and the map paints it on every copy of the world. */
        public static List<LatLon> Unwrap(IReadOnlyList<LatLon> points)
        {
            var output = new List<LatLon>(points.Count);
            double off = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0)
                {
                    double d = points[i].Lon - points[i - 1].Lon;
                    if (d > 180) off -= 360; else if (d < -180) off += 360;
                }
                output.Add(new LatLon(points[i].Lat, points[i].Lon + off));
            }
            return output;
        }

        /* ---------- formatting ---------- */

        public static string F(double v, int dec) => v.ToString("F" + dec, Inv);

        public static string FmtLat(double lat) => F(Math.Abs(lat), 4) + "° " + (lat >= 0 ? "N" : "S");
        public static string FmtLon(double lon) => F(Math.Abs(lon), 4) + "° " + (lon >= 0 ? "E" : "W");

        public static string FmtDist(double m)
        {
            if (Math.Abs(m) < 1000) return F(m, 0) + " m";
            if (Math.Abs(m) < 100000) return F(m / 1000, 2) + " km";
            return F(m / 1000, 1) + " km";
        }

        public static string FmtAlt(double m) => (m >= 0 ? "+" : "") + F(m, 0) + " m";

        /* Seconds -> d/h/m/s with Kerbin's solar day (6 h). */
        public static string FmtTime(double s)
        {
            double day = SolarSystem.Home.SolarDay;      // the calendar is always the home planet's
            double d = Math.Floor(s / day); s -= d * day;
            double h = Math.Floor(s / 3600); s -= h * 3600;
            double m = Math.Floor(s / 60); s -= m * 60;
            string P(double n) => ((long)n).ToString("00", Inv);
            return (d != 0 ? ((long)d).ToString(Inv) + "d " : "") + P(h) + ":" + P(m) + ":" + P(Math.Floor(s));
        }

        /* Integer with a thousands separator in Spanish style: like toLocaleString('es'), it
           doesn't group below 10 000. */
        public static string FmtIntEs(long n)
        {
            string s = Math.Abs(n).ToString(Inv), sep = Lang.Code == "es" ? "." : ",";
            if (s.Length > 4)
                for (int i = s.Length - 3; i > 0; i -= 3) s = s.Insert(i, sep);
            return (n < 0 ? "-" : "") + s;
        }

        /* The game's calendar: 6 h days and 426-day years, from year 1, day 1. */
        public static string FechaKerbal(double t)
        {
            double DIA = SolarSystem.Home.SolarDay, ANIO = 426 * DIA;
            double y = Math.Floor(t / ANIO), d = Math.Floor((t - y * ANIO) / DIA);
            double sg = t - y * ANIO - d * DIA;
            string P(double n) => ((long)Math.Floor(n)).ToString("00", Inv);
            return Lang.F("Año {0} · día {1} · {2}", (long)y + 1, (long)d + 1,
                P(sg / 3600) + ":" + P((sg % 3600) / 60) + ":" + P(sg % 60));
        }

        /* The same date without the clock, for narrow lists. */
        public static string FechaCorta(double t)
        {
            double DIA = SolarSystem.Home.SolarDay, ANIO = 426 * DIA;
            double y = Math.Floor(t / ANIO), d = Math.Floor((t - y * ANIO) / DIA);
            return Lang.F("Año {0} · día {1}", (long)y + 1, (long)d + 1);
        }

        /* Accepts «-0.0972, -74.5577», «-0.0972 -74.5577» and «0.09 S 74.55 W». */
        public static LatLon? ParseCoords(string s)
        {
            string t = (s ?? "").Trim();
            var m = System.Text.RegularExpressions.Regex.Match(t, @"^(-?\d+(?:\.\d+)?)\s*[,;\s]\s*(-?\d+(?:\.\d+)?)$");
            if (m.Success)
            {
                double lat = double.Parse(m.Groups[1].Value, Inv), lon = double.Parse(m.Groups[2].Value, Inv);
                if (Math.Abs(lat) <= 90 && Math.Abs(lon) <= 360) return new LatLon(lat, WrapLon(lon));
            }
            m = System.Text.RegularExpressions.Regex.Match(t, @"^(\d+(?:\.\d+)?)\s*°?\s*([NSns])\s*[,;\s]\s*(\d+(?:\.\d+)?)\s*°?\s*([EWew])$");
            if (m.Success)
            {
                double lat = double.Parse(m.Groups[1].Value, Inv) * (m.Groups[2].Value.ToUpperInvariant() == "S" ? -1 : 1);
                double lon = double.Parse(m.Groups[3].Value, Inv) * (m.Groups[4].Value.ToUpperInvariant() == "W" ? -1 : 1);
                if (Math.Abs(lat) <= 90) return new LatLon(lat, WrapLon(lon));
            }
            return null;
        }
    }
}
