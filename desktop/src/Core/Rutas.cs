using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KerbinMaps.Core
{
    public enum Medio { Aire, Mar, Tierra }

    /* A computed route: the path and what it costs to travel it. */
    public sealed class Ruta
    {
        public Medio Medio;
        public bool Ok;
        public string Motivo;                     // why there's no route, if there isn't one
        public List<LatLon> Puntos = new();
        public double Distancia, Tiempo;          // m and s
        public double Subida, Bajada;             // accumulated climb, m
        public double CotaMax = double.MinValue;  // highest point under the path, m
        public double PendienteMax;               // °
        public double Salida, Llegada;            // from each end to the nearest water (or land), m
        public LatLon? PuertoSalida, PuertoLlegada;
    }

    /* A «Waze» for Kerbin: the fastest path between two points by plane, by ship and by ground
       vehicle.

       Air goes in a straight line along the great circle. Sea and land search with A* over a
       grid of 4096×2048 cells (a bit under a kilometer on Kerbin) with the height of each one:
       the ship only steps on cells below sea level and the vehicle only on those above, never
       climbing or descending slopes steeper than the maximum grade and going slower the steeper
       they are. Then the path is straightened: two points of the path are joined in a straight
       line if you can go along it without taking longer. If the origin or the destination isn't
       in the requested medium (a ship leaving from inland), the route starts at the nearest
       water or land, and says how far away it is.

       The grid is built in the background with whatever maps there are; without a height map,
       land comes from the color map and there are no slopes. */
    public sealed class MallaRutas
    {
        public const int Ancho = 4096, Alto = 2048;
        const int MaxAcercar = 256;               // cells searched for the nearest water or land
        const double MaxAcercarM = 100000;        // farther than this, the water (or land) doesn't count as «near»

        readonly float[] alt;                     // height of each cell above the sea, m
        readonly bool[] tierra;
        readonly int[] zona;                      // piece of land or sea each cell belongs to
        readonly double radio, dy;
        readonly double[] dx, cosLat, sinLat, cosLon, sinLon;
        public readonly bool HayMar, HayAlturas;

        // for each search: they're reused, so searches go one at a time
        readonly float[] coste = new float[Ancho * Alto];
        readonly byte[] desde = new byte[Ancho * Alto];
        readonly bool[] cerrada = new bool[Ancho * Alto];
        readonly object cerrojo = new();

        static readonly int[] Ddx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] Ddy = { 0, 0, 1, -1, 1, -1, 1, -1 };

        public static double LatDe(int y) => 90 - (y + 0.5) * 180.0 / Alto;
        public static double LonDe(int x) => -180 + (x + 0.5) * 360.0 / Ancho;
        static int Fila(double lat) => Math.Clamp((int)Math.Floor((90 - lat) / 180 * Alto), 0, Alto - 1);
        static int Columna(double lon)
        {
            int x = (int)Math.Floor((Geo.WrapLon(lon) + 180) / 360 * Ancho);
            return ((x % Ancho) + Ancho) % Ancho;
        }

        /* `altura` gives the height above the sea (null if there's no height map); `esTierra`,
           without heights, says what is land (null: everything). `hayMar` is false on bodies
           without an ocean. */
        public MallaRutas(Func<double, double, double> altura, Func<double, double, bool> esTierra, bool hayMar, double radio)
        {
            this.radio = radio;
            HayAlturas = altura != null;
            alt = new float[Ancho * Alto];
            tierra = new bool[Ancho * Alto];
            Parallel.For(0, Alto, y =>
            {
                double lat = LatDe(y);
                for (int x = 0; x < Ancho; x++)
                {
                    double lon = LonDe(x);
                    int i = y * Ancho + x;
                    double h = altura?.Invoke(lat, lon) ?? 0;
                    alt[i] = (float)h;
                    tierra[i] = !hayMar || (altura != null ? h > 0 : esTierra == null || esTierra(lat, lon));
                }
            });
            int nMar = 0;
            foreach (bool t in tierra) if (!t) nMar++;
            HayMar = nMar > 0;

            dy = radio * Math.PI / Alto;
            dx = new double[Alto]; cosLat = new double[Alto]; sinLat = new double[Alto];
            for (int y = 0; y < Alto; y++)
            {
                double p = LatDe(y) * Geo.D2R;
                cosLat[y] = Math.Cos(p); sinLat[y] = Math.Sin(p);
                dx[y] = radio * cosLat[y] * 2 * Math.PI / Ancho;
            }
            cosLon = new double[Ancho]; sinLon = new double[Ancho];
            for (int x = 0; x < Ancho; x++)
            {
                double l = LonDe(x) * Geo.D2R;
                cosLon[x] = Math.Cos(l); sinLon[x] = Math.Sin(l);
            }
            zona = Zonas();
        }

        /* Connected pieces of land and sea, to know right away whether two points can be joined
           without having to cross a whole ocean searching. */
        int[] Zonas()
        {
            var z = new int[Ancho * Alto];
            var pila = new Stack<int>();
            int n = 0;
            for (int s = 0; s < z.Length; s++)
            {
                if (z[s] != 0) continue;
                z[s] = ++n;
                bool t = tierra[s];
                pila.Push(s);
                while (pila.Count > 0)
                {
                    int i = pila.Pop();
                    int x = i % Ancho, y = i / Ancho;
                    for (int k = 0; k < 8; k++)
                    {
                        int yy = y + Ddy[k];
                        if (yy < 0 || yy >= Alto) continue;
                        int j = yy * Ancho + (x + Ddx[k] + Ancho) % Ancho;
                        if (z[j] != 0 || tierra[j] != t) continue;
                        z[j] = n;
                        pila.Push(j);
                    }
                }
            }
            return z;
        }

        public bool EsTierra(double lat, double lon) => tierra[Fila(lat) * Ancho + Columna(lon)];

        /* Height interpolated between cell centers. */
        public double Altura(double lat, double lon)
        {
            double fx = (Geo.WrapLon(lon) + 180) / 360 * Ancho - 0.5;
            double fy = Math.Clamp((90 - lat) / 180 * Alto - 0.5, 0, Alto - 1);
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            int y1 = Math.Min(y0 + 1, Alto - 1);
            int xa = ((x0 % Ancho) + Ancho) % Ancho, xb = (xa + 1) % Ancho;
            double a = alt[y0 * Ancho + xa] * (1 - tx) + alt[y0 * Ancho + xb] * tx;
            double b = alt[y1 * Ancho + xa] * (1 - tx) + alt[y1 * Ancho + xb] * tx;
            return a * (1 - ty) + b * ty;
        }

        /* ------------------------------------------------------------ air */

        public Ruta Aire(LatLon a, LatLon b, double v)
        {
            var r = new Ruta { Medio = Medio.Aire, Ok = true };
            r.Puntos = Densificar(new List<LatLon> { a, b });
            r.Distancia = Geo.Distance(a.Lat, a.Lon, b.Lat, b.Lon);
            r.Tiempo = r.Distancia / v;
            double paso = dy / 2;
            int n = Math.Max(1, (int)Math.Ceiling(r.Distancia / paso));
            for (int i = 0; i <= n; i++)
            {
                var p = Interpolar(a, b, (double)i / n);
                r.CotaMax = Math.Max(r.CotaMax, HayAlturas ? Altura(p.Lat, p.Lon) : 0);
            }
            return r;
        }

        /* ------------------------------------------------------------ sea and land */

        /* The speed factor on a slope: full on the flat and 30% at the maximum grade. */
        static double Factor(double pendiente, double max) => 1 - 0.7 * (pendiente / max) * (pendiente / max);

        public Ruta Buscar(Medio medio, LatLon a, LatLon b, double v, double pendMax, CancellationToken ct)
        {
            var r = new Ruta { Medio = medio };
            bool enTierra = medio == Medio.Tierra;
            if (!enTierra && !HayMar) { r.Motivo = "Este cuerpo no tiene mar."; return r; }

            lock (cerrojo)
            {
                int ia = Fila(a.Lat) * Ancho + Columna(a.Lon), ib = Fila(b.Lat) * Ancho + Columna(b.Lon);
                // both ends in the requested medium and in the same piece of land or sea
                int sa = Cercana(ia, enTierra, 0), sb = Cercana(ib, enTierra, 0);
                if (sa < 0 || sb < 0)
                {
                    r.Motivo = enTierra ? "No hay tierra a menos de 100 km del origen o del destino." : "No hay mar a menos de 100 km del origen o del destino.";
                    return r;
                }
                /* On land we don't cross the sea to the opposite coast: if the land nearest each end
                   isn't the same island or continent, there's no rover route. */
                if (zona[sa] != zona[sb] && enTierra)
                {
                    r.Motivo = "Origen y destino están en tierras separadas por el mar.";
                    return r;
                }
                if (zona[sa] != zona[sb])
                {
                    int sb2 = Cercana(ib, enTierra, zona[sa]), sa2 = Cercana(ia, enTierra, zona[sb]);
                    double d1 = sb2 < 0 ? double.MaxValue : Dist(ia, sa) + Dist(ib, sb2);
                    double d2 = sa2 < 0 ? double.MaxValue : Dist(ia, sa2) + Dist(ib, sb);
                    if (d1 == double.MaxValue && d2 == double.MaxValue)
                    {
                        r.Motivo = enTierra ? "Origen y destino están en tierras separadas por el mar."
                                            : "Origen y destino dan a mares que no se tocan.";
                        return r;
                    }
                    if (d1 <= d2) sb = sb2; else sa = sa2;
                }

                var celdas = AEstrella(sa, sb, enTierra, v, pendMax, ct);
                if (celdas == null)
                {
                    r.Motivo = ct.IsCancellationRequested ? "Cancelada."
                             : enTierra ? "Las cuestas cortan el paso: no hay camino con esa pendiente máxima."
                             : "No se encontró camino por mar.";
                    return r;
                }

                // the exact ends if they fall in their own cell; if not, the nearest water or land
                var pts = new List<LatLon>(celdas.Count);
                foreach (int i in celdas) pts.Add(Centro(i));
                if (sa == ia) pts[0] = a; else { r.PuertoSalida = pts[0]; r.Salida = Geo.Distance(a.Lat, a.Lon, pts[0].Lat, pts[0].Lon); }
                if (sb == ib) pts[^1] = b; else { r.PuertoLlegada = pts[^1]; r.Llegada = Geo.Distance(b.Lat, b.Lon, pts[^1].Lat, pts[^1].Lon); }

                var recto = Enderezar(pts, enTierra, v, pendMax);
                r.Puntos = Densificar(recto);
                Medir(r, recto, enTierra, v, pendMax);
                r.Ok = true;
                return r;
            }
        }

        LatLon Centro(int i) => new(LatDe(i / Ancho), LonDe(i % Ancho));

        double Dist(int i, int j)
        {
            var p = Centro(i); var q = Centro(j);
            return Geo.Distance(p.Lat, p.Lon, q.Lat, q.Lon);
        }

        /* The cell of the requested medium (and of the requested zone, if not 0) nearest to `i`. */
        int Cercana(int i, bool enTierra, int enZona)
        {
            bool Vale(int j) => tierra[j] == enTierra && (enZona == 0 || zona[j] == enZona);
            if (Vale(i)) return i;
            int x0 = i % Ancho, y0 = i / Ancho;
            int mejor = -1, hasta = MaxAcercar;
            double dMejor = double.MaxValue;
            for (int r = 1; r <= hasta && r * dy <= MaxAcercarM * 1.5; r++)
            {
                for (int dyy = -r; dyy <= r; dyy++)
                {
                    int y = y0 + dyy;
                    if (y < 0 || y >= Alto) continue;
                    int paso = Math.Abs(dyy) == r ? 1 : 2 * r;     // the ring: full rows above and below, only the sides for the rest
                    for (int dxx = -r; dxx <= r; dxx += paso)
                    {
                        int j = y * Ancho + (x0 + dxx + Ancho * 4) % Ancho;
                        if (!Vale(j)) continue;
                        double d = Dist(i, j);
                        if (d < dMejor) { dMejor = d; mejor = j; }
                    }
                }
                // cells narrow toward the poles: we look a bit beyond the first hit
                if (mejor >= 0 && hasta == MaxAcercar) hasta = Math.Min(MaxAcercar, r + r / 2 + 2);
            }
            return dMejor <= MaxAcercarM ? mejor : -1;
        }

        List<int> AEstrella(int s, int g, bool enTierra, double v, double pendMax, CancellationToken ct)
        {
            Array.Fill(coste, float.PositiveInfinity);
            Array.Fill(desde, (byte)255);
            Array.Clear(cerrada);
            int gx = g % Ancho, gy = g / Ancho;
            double gs = sinLat[gy], gc = cosLat[gy], gcl = cosLon[gx], gsl = sinLon[gx];
            double tanMax = Math.Tan(pendMax * Geo.D2R);

            // what's left, in seconds: the great circle at full speed (never overestimates)
            double H(int i)
            {
                int x = i % Ancho, y = i / Ancho;
                double c = sinLat[y] * gs + cosLat[y] * gc * (cosLon[x] * gcl + sinLon[x] * gsl);
                return radio * Math.Acos(Math.Clamp(c, -1, 1)) / v * 0.999;
            }

            var cola = new PriorityQueue<int, double>();
            coste[s] = 0;
            cola.Enqueue(s, H(s));
            int vueltas = 0;
            while (cola.TryDequeue(out int i, out double f))
            {
                if (i == g) break;
                /* Each cell is opened only once. Near the poles the grid gets squashed and the
                   estimate stops being exact; reopening cells there would give slightly better
                   paths at the cost of minutes of search. */
                if (cerrada[i]) continue;
                cerrada[i] = true;
                double ci = coste[i];
                if ((++vueltas & 0xFFFF) == 0 && ct.IsCancellationRequested) return null;
                int x = i % Ancho, y = i / Ancho;
                for (int k = 0; k < 8; k++)
                {
                    int yy = y + Ddy[k];
                    if (yy < 0 || yy >= Alto) continue;
                    int j = yy * Ancho + (x + Ddx[k] + Ancho) % Ancho;
                    if (tierra[j] != enTierra || cerrada[j]) continue;
                    double d = Ddy[k] == 0 ? dx[y]
                             : Ddx[k] == 0 ? dy
                             : Math.Sqrt(Math.Pow((dx[y] + dx[yy]) / 2, 2) + dy * dy);
                    double t;
                    if (enTierra)
                    {
                        double dh = Math.Abs(alt[j] - alt[i]);
                        if (dh > d * tanMax) continue;
                        t = d / (v * Factor(Math.Atan(dh / d) * Geo.R2D, pendMax));
                    }
                    else t = d / v;
                    double cj = ci + t;
                    if (cj >= coste[j]) continue;
                    coste[j] = (float)cj;
                    desde[j] = (byte)k;
                    cola.Enqueue(j, cj + H(j));
                }
            }
            if (float.IsPositiveInfinity(coste[g])) return null;

            var camino = new List<int>();
            for (int i = g; ; )
            {
                camino.Add(i);
                if (i == s) break;
                int k = desde[i];
                int x = i % Ancho, y = i / Ancho;
                i = (y - Ddy[k]) * Ancho + (x - Ddx[k] + Ancho) % Ancho;
            }
            camino.Reverse();
            return camino;
        }

        /* A straight leg from p to q: whether you can go along it and how long it takes. */
        (bool Ok, double T, double D, double Sube, double Baja, double PendMax, double HMax) Tramo(LatLon p, LatLon q, bool enTierra, double v, double pendMax)
        {
            double d = Geo.Distance(p.Lat, p.Lon, q.Lat, q.Lon);
            int n = Math.Max(1, (int)Math.Ceiling(d / (dy / 2)));
            double t = 0, sube = 0, baja = 0, pmax = 0, hmax = double.MinValue;
            double hPrev = Altura(p.Lat, p.Lon);
            for (int i = 1; i <= n; i++)
            {
                var m = Interpolar(p, q, (double)i / n);
                if (EsTierra(m.Lat, m.Lon) != enTierra) return (false, 0, 0, 0, 0, 0, 0);
                double paso = d / n;
                if (enTierra)
                {
                    double h = Altura(m.Lat, m.Lon), dh = h - hPrev;
                    double pend = paso > 0 ? Math.Atan(Math.Abs(dh) / paso) * Geo.R2D : 0;
                    if (pend > pendMax) return (false, 0, 0, 0, 0, 0, 0);
                    t += paso / (v * Factor(pend, pendMax));
                    if (dh > 0) sube += dh; else baja -= dh;
                    pmax = Math.Max(pmax, pend);
                    hmax = Math.Max(hmax, h);
                    hPrev = h;
                }
                else t += paso / v;
            }
            return (true, t, d, sube, baja, pmax, hmax);
        }

        /* Joins in a straight line the points of the path that can be joined without taking
           longer, looking for the farthest one for each (in doubling jumps and then by halves). */
        List<LatLon> Enderezar(List<LatLon> pts, bool enTierra, double v, double pendMax)
        {
            if (pts.Count < 3) return pts;
            var acum = new double[pts.Count];
            for (int k = 1; k < pts.Count; k++)
            {
                var tr = Tramo(pts[k - 1], pts[k], enTierra, v, pendMax);
                acum[k] = acum[k - 1] + (tr.Ok ? tr.T : Geo.Distance(pts[k - 1].Lat, pts[k - 1].Lon, pts[k].Lat, pts[k].Lon) / v);
            }
            bool Atajo(int i, int j)
            {
                var tr = Tramo(pts[i], pts[j], enTierra, v, pendMax);
                return tr.Ok && tr.T <= acum[j] - acum[i] + 1e-6;
            }

            var o = new List<LatLon> { pts[0] };
            int a = 0, ult = pts.Count - 1;
            const int MaxSalto = 600;
            while (a < ult)
            {
                int bien = a + 1, paso = 2;
                int tope = Math.Min(ult, a + MaxSalto);
                while (a + paso <= tope && Atajo(a, a + paso)) { bien = a + paso; paso *= 2; }
                int mal = Math.Min(tope + 1, a + paso);
                while (mal - bien > 1)
                {
                    int m = (bien + mal) / 2;
                    if (Atajo(a, m)) bien = m; else mal = m;
                }
                o.Add(pts[bien]);
                a = bien;
            }
            return o;
        }

        void Medir(Ruta r, List<LatLon> pts, bool enTierra, double v, double pendMax)
        {
            for (int k = 1; k < pts.Count; k++)
            {
                var tr = Tramo(pts[k - 1], pts[k], enTierra, v, pendMax);
                double d = Geo.Distance(pts[k - 1].Lat, pts[k - 1].Lon, pts[k].Lat, pts[k].Lon);
                r.Distancia += d;
                // a cell-to-cell leg can graze the coast when sampled: it counts at flat speed
                r.Tiempo += tr.Ok ? tr.T : d / v;
                r.Subida += tr.Sube; r.Bajada += tr.Baja;
                r.PendienteMax = Math.Max(r.PendienteMax, tr.PendMax);
                if (enTierra) r.CotaMax = Math.Max(r.CotaMax, tr.HMax);
            }
        }

        /* ------------------------------------------------------------ geometry */

        /* The point at fraction f of the great circle from p to q. */
        static LatLon Interpolar(LatLon p, LatLon q, double f)
        {
            double p1 = p.Lat * Geo.D2R, l1 = p.Lon * Geo.D2R, p2 = q.Lat * Geo.D2R, l2 = q.Lon * Geo.D2R;
            double ax = Math.Cos(p1) * Math.Cos(l1), ay = Math.Cos(p1) * Math.Sin(l1), az = Math.Sin(p1);
            double bx = Math.Cos(p2) * Math.Cos(l2), by = Math.Cos(p2) * Math.Sin(l2), bz = Math.Sin(p2);
            double w = Math.Acos(Math.Clamp(ax * bx + ay * by + az * bz, -1, 1));
            double A, B;
            if (w < 1e-12) { A = 1 - f; B = f; }
            else { A = Math.Sin((1 - f) * w) / Math.Sin(w); B = Math.Sin(f * w) / Math.Sin(w); }
            double x = A * ax + B * bx, y = A * ay + B * by, z = A * az + B * bz;
            return new LatLon(Math.Atan2(z, Math.Sqrt(x * x + y * y)) * Geo.R2D, Math.Atan2(y, x) * Geo.R2D);
        }

        /* Points every 0.2° at most, so the line follows the great circle on the flat map and
           doesn't cut through the globe. */
        static List<LatLon> Densificar(List<LatLon> pts)
        {
            var o = new List<LatLon> { pts[0] };
            for (int k = 1; k < pts.Count; k++)
            {
                double ang = Geo.Distance(pts[k - 1].Lat, pts[k - 1].Lon, pts[k].Lat, pts[k].Lon) / Body.Radius * Geo.R2D;
                int n = Math.Max(1, (int)Math.Ceiling(ang / 0.2));
                for (int i = 1; i <= n; i++) o.Add(i == n ? pts[k] : Interpolar(pts[k - 1], pts[k], (double)i / n));
            }
            return o;
        }
    }
}
