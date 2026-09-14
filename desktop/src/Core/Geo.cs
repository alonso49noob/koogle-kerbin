using System;
using System.Collections.Generic;
using System.Globalization;

namespace KerbinMaps.Core
{
    public readonly record struct LatLon(double Lat, double Lon);

    /* Proyección y geodesia sobre Kerbin. El mapa es equirectangular (plate carrée),
       la misma proyección de las texturas de KSP: una imagen 2:1 se pega tal cual. */
    public static class Geo
    {
        public const double D2R = Math.PI / 180, R2D = 180 / Math.PI;
        const double R = Body.Radius;
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /* Distancia sobre el gran círculo, en metros. */
        public static double Distance(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * D2R, p2 = lat2 * D2R;
            double dp = (lat2 - lat1) * D2R, dl = (lon2 - lon1) * D2R;
            double h = Math.Pow(Math.Sin(dp / 2), 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Pow(Math.Sin(dl / 2), 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        }

        /* Rumbo inicial de A a B, en grados desde el norte. */
        public static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * D2R, p2 = lat2 * D2R, dl = (lon2 - lon1) * D2R;
            double y = Math.Sin(dl) * Math.Cos(p2);
            double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            return (Math.Atan2(y, x) * R2D + 360) % 360;
        }

        /* Punto a distancia `dist` (m) y rumbo `brg` (°) desde un origen. */
        public static LatLon Destination(double lat, double lon, double brg, double dist)
        {
            double d = dist / R, t = brg * D2R, p1 = lat * D2R, l1 = lon * D2R;
            double p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(t));
            double l2 = l1 + Math.Atan2(Math.Sin(t) * Math.Sin(d) * Math.Cos(p1),
                                        Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
            return new LatLon(p2 * R2D, WrapLon(l2 * R2D));
        }

        /* Interpolación sobre el gran círculo: en equirectangular la recta entre dos
           puntos NO es el camino más corto, así que hay que trocearla. */
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

        /* Círculo de radio constante sobre la superficie (p. ej. el horizonte visible
           desde una altitud dada). */
        public static List<LatLon> Circle(double lat, double lon, double radiusM, int steps = 180)
        {
            var pts = new List<LatLon>(steps + 1);
            for (int i = 0; i <= steps; i++) pts.Add(Destination(lat, lon, i * 360.0 / steps, radiusM));
            return pts;
        }

        /* Radio sobre la superficie del casquete visible desde una altitud h. */
        public static double HorizonRadius(double h) => R * Math.Acos(R / (R + h));

        public static double WrapLon(double lon)
        {
            double x = (lon + 180) % 360;
            if (x < 0) x += 360;
            return x - 180;
        }

        /* Una polilínea que cruza ±180° se dibujaría como una raya de lado a lado. La
           partimos en tramos y calculamos la latitud del cruce por interpolación. */
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

        /* Deshace los saltos de ±360° entre puntos consecutivos: la línea queda
           continua en longitud y el mapa la pinta en cada copia del mundo. */
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

        /* ---------- formato ---------- */

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

        /* Segundos -> d/h/m/s con el día solar de Kerbin (6 h). */
        public static string FmtTime(double s)
        {
            double day = Body.SolarDay;
            double d = Math.Floor(s / day); s -= d * day;
            double h = Math.Floor(s / 3600); s -= h * 3600;
            double m = Math.Floor(s / 60); s -= m * 60;
            string P(double n) => ((long)n).ToString("00", Inv);
            return (d != 0 ? ((long)d).ToString(Inv) + "d " : "") + P(h) + ":" + P(m) + ":" + P(Math.Floor(s));
        }

        /* Número entero con separador de miles al estilo español: igual que
           toLocaleString('es'), no agrupa por debajo de 10 000. */
        public static string FmtIntEs(long n)
        {
            string s = Math.Abs(n).ToString(Inv);
            if (s.Length > 4)
                for (int i = s.Length - 3; i > 0; i -= 3) s = s.Insert(i, ".");
            return (n < 0 ? "-" : "") + s;
        }

        /* Calendario del juego: días de 6 h y años de 426 días, desde el año 1, día 1. */
        public static string FechaKerbal(double t)
        {
            double DIA = Body.SolarDay, ANIO = 426 * DIA;
            double y = Math.Floor(t / ANIO), d = Math.Floor((t - y * ANIO) / DIA);
            double sg = t - y * ANIO - d * DIA;
            string P(double n) => ((long)Math.Floor(n)).ToString("00", Inv);
            return "Año " + ((long)y + 1) + " · día " + ((long)d + 1) + " · " +
                   P(sg / 3600) + ":" + P((sg % 3600) / 60) + ":" + P(sg % 60);
        }

        /* Acepta «-0.0972, -74.5577», «-0.0972 -74.5577» y «0.09 S 74.55 W». */
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
