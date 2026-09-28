using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KerbinMaps.Ksp;

namespace KerbinMaps.Views
{
    /* Dónde va cada hierba, arbusto, árbol o roca de los scatters de Parallax.

       Parallax los reparte por triángulo del terreno de KSP en la GPU (TerrainScatters.compute
       en su código): por cada triángulo, `populationMultiplier` candidatos al azar, y cada uno
       sale o no según una probabilidad que baja con la pendiente y cerca de los límites de
       altitud, y según un ruido fractal sobre la esfera por encima de un umbral. El mismo
       ruido decide el tamaño. Aquí se hace igual, con estas diferencias:

       - Aquí no hay triángulos: se reparte por celdas de latitud y longitud, con tantos
         candidatos como triángulos de ~2150 m² cabrían (los del nivel más fino del terreno de
         Kerbin: celdas de 66 m partidas en dos).
       - El terreno de KSP se hace más basto lejos de la cámara, y Parallax pone el mismo
         número de candidatos por triángulo, así que lejos hay menos. Eso se imita con una
         densidad que baja con la distancia.
       - Todo es determinista (cada candidato sale de un hash de su celda y su número), así
         que al volver a un sitio están los mismos árboles.

       Las celdas se generan en segundo plano, de la más cercana a la más lejana, y se tiran
       las que se quedan fuera de alcance. */
    public sealed class ScatterField
    {
        public struct Inst
        {
            public double X, Y, Z;                     // en metros, desde el centro del cuerpo
            public float Ux, Uy, Uz;                   // eje vertical del modelo
            public float Yaw, S;                       // giro en grados y tamaño (0 = mínimo, 1 = máximo)
            public float R, G, B;                      // color del terreno (si el scatter lo usa)
            public float Rank;                         // para aclarar de forma estable
        }

        public sealed class Cell
        {
            public Inst[] Items;
            public double Density;                     // fracción de candidatos con la que se generó
            public double Cx, Cy, Cz;                  // centro, en metros
        }

        public sealed class Layer
        {
            public ScatterDef Def;
            public Layer Parent;                       // los compartidos usan las celdas del padre
            public double CellM;
            public readonly ConcurrentDictionary<(int, int), Cell> Cells = new();
        }

        /* Área de un triángulo del nivel más fino del terreno (ver arriba). */
        const double AreaTriangulo = 2158;
        /* Hasta esta distancia, densidad completa; luego baja como 1/d. */
        const double DistanciaPlena = 1500;

        public readonly double R;
        public readonly List<Layer> Layers = new();
        public Func<double, double, double> Altura;              // lat, lon → metros (fondo marino incluido)
        public Func<double, double, string> Bioma;               // nombre del bioma, o null
        public Func<double, double, (float, float, float)> Color;
        public Action Changed;                                   // hay celdas nuevas: pintar otra vez

        volatile bool vivo = true;
        double objLat, objLon;
        int trabajando;                                          // 1 mientras hay un hilo generando

        public ScatterField(double radius, IEnumerable<ScatterDef> defs)
        {
            R = radius;
            var propias = new Dictionary<string, Layer>();
            foreach (var d in defs.Where(d => !d.Shared))
            {
                // lo que va bajo el agua no se ve: el mar aquí es opaco
                if (d.MaxAltitude <= 0 && d.PlacementAltitude == null) continue;
                var l = new Layer { Def = d, CellM = Math.Clamp(d.Range / 12, 32, 1024) };
                Layers.Add(l);
                propias[d.Name] = l;
            }
            foreach (var d in defs.Where(d => d.Shared))
                if (propias.TryGetValue(d.Parent, out var p)) Layers.Add(new Layer { Def = d, Parent = p, CellM = p.CellM });
        }

        public void Stop() => vivo = false;

        /* La cámara está aquí: si faltan celdas, se ponen a generar en otro hilo. */
        public void Request(double lat, double lon)
        {
            objLat = lat; objLon = lon;
            if (Interlocked.CompareExchange(ref trabajando, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try { while (vivo && Generar()) { } }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[scatters] " + ex); }
                finally { Volatile.Write(ref trabajando, 0); }
            });
        }

        /* Una tanda: las celdas que faltan alrededor de la posición pedida, de la más cercana
           a la más lejana, durante unas decenas de milisegundos. Devuelve si queda trabajo. */
        bool Generar()
        {
            double lat = objLat, lon = objLon;
            var eye = Pos(lat, lon, Altura(lat, lon));
            var faltan = new List<(Layer l, int j, int i, double d, double f)>();
            foreach (var l in Layers)
            {
                if (l.Parent != null) continue;
                double alcance = l.Def.Range + l.CellM;
                var vistas = new HashSet<(int, int)>();
                foreach (var (j, i) in CeldasCerca(l, lat, lon, alcance))
                {
                    var c = Centro(l, j, i);
                    double d = Dist(eye, c) ;
                    if (d > alcance) continue;
                    vistas.Add((j, i));
                    double f = DensidadCelda(d - l.CellM * 0.75);
                    if (!l.Cells.TryGetValue((j, i), out var ya) || ya.Density < f - 1e-9) faltan.Add((l, j, i, d, f));
                }
                // se tiran las que ya no hacen falta
                foreach (var k in l.Cells.Keys)
                    if (!vistas.Contains(k)) l.Cells.TryRemove(k, out _);
            }
            if (faltan.Count == 0) return false;

            var reloj = System.Diagnostics.Stopwatch.StartNew();
            foreach (var (l, j, i, _, f) in faltan.OrderBy(x => x.d / Math.Max(1, x.l.Def.Range)))
            {
                if (!vivo) return false;
                l.Cells[(j, i)] = GenerarCelda(l, j, i, f);
                if (reloj.ElapsedMilliseconds > 40) break;
            }
            Changed?.Invoke();
            return true;
        }

        /* Densidad con la que se generan las celdas a esa distancia, en potencias de dos para
           no rehacerlas a cada paso de la cámara. */
        static double DensidadCelda(double d)
        {
            double f = DensidadEn(d);
            return Math.Max(1.0 / 64, Math.Pow(2, Math.Ceiling(Math.Log2(f))));
        }

        /* Fracción de candidatos que se ven a esa distancia. */
        public static double DensidadEn(double d) => d <= DistanciaPlena ? 1 : DistanciaPlena / d;

        /* ------------------------------------------------------------ celdas */

        /* Filas de latitud de alto CellM; cada fila partida en tantas celdas de longitud como
           quepan, así todas miden lo mismo aunque se acerquen al polo. */
        double DLat(Layer l) => l.CellM / R;

        int Columnas(Layer l, int j)
        {
            double latc = (j + 0.5) * DLat(l);
            return Math.Max(1, (int)Math.Floor(2 * Math.PI * R * Math.Cos(Math.Clamp(latc, -Math.PI / 2, Math.PI / 2)) / l.CellM));
        }

        IEnumerable<(int j, int i)> CeldasCerca(Layer l, double lat, double lon, double alcance)
        {
            double dl = DLat(l), la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
            double ang = alcance / R;
            int j0 = (int)Math.Floor((la - ang) / dl), j1 = (int)Math.Floor((la + ang) / dl);
            for (int j = j0; j <= j1; j++)
            {
                double latc = (j + 0.5) * dl;
                if (Math.Abs(latc) > Math.PI / 2) continue;
                int n = Columnas(l, j);
                double cos = Math.Max(Math.Cos(Math.Min(Math.PI / 2, Math.Abs(latc) + dl)), 1e-6);
                double dlon = Math.Min(Math.PI, ang / cos);
                int i0 = (int)Math.Floor((lo - dlon + Math.PI) / (2 * Math.PI) * n);
                int i1 = (int)Math.Floor((lo + dlon + Math.PI) / (2 * Math.PI) * n);
                if (i1 - i0 + 1 >= n) { i0 = 0; i1 = n - 1; }
                for (int i = i0; i <= i1; i++) yield return (j, ((i % n) + n) % n);
            }
        }

        double[] Centro(Layer l, int j, int i)
        {
            double dl = DLat(l);
            int n = Columnas(l, j);
            double lat = (j + 0.5) * dl, lon = -Math.PI + (i + 0.5) * 2 * Math.PI / n;
            double lat2 = lat * 180 / Math.PI, lon2 = lon * 180 / Math.PI;
            return Pos(lat2, lon2, Altura(lat2, lon2));
        }

        Cell GenerarCelda(Layer l, int j, int i, double f)
        {
            var d = l.Def;
            double dl = DLat(l);
            int n = Columnas(l, j);
            double lat0 = j * dl, dlon = 2 * Math.PI / n, lon0 = -Math.PI + i * dlon;
            double area = R * R * dl * dlon * Math.Cos(Math.Clamp((j + 0.5) * dl, -Math.PI / 2, Math.PI / 2));
            double esperados = d.Population * area / AreaTriangulo;
            ulong semilla = Hash((ulong)BitConverter.DoubleToInt64Bits(d.Seed) ^ Fnv(d.Name), (ulong)(uint)j, (ulong)(uint)i);
            // el número de candidatos se redondea al azar, para que celdas pequeñas no se queden a cero
            int total = (int)Math.Floor(esperados + U(Hash(semilla, 0xABCD)));

            var res = new List<Inst>();
            for (int k = 0; k < total; k++)
            {
                ulong h = Hash(semilla, (ulong)k);
                double rank = U(h);
                if (rank >= f) continue;                                  // aún no hace falta a esta distancia
                double r4 = U(Hash(h, 4));
                if (r4 >= d.SpawnChance) continue;                        // la probabilidad nunca pasará de esto

                double lat = (lat0 + U(Hash(h, 1)) * dl) * 180 / Math.PI;
                double lon = (lon0 + U(Hash(h, 2)) * dlon) * 180 / Math.PI;
                double cl = Math.Cos(lat * Math.PI / 180);
                var dir = new[] { cl * Math.Sin(lon * Math.PI / 180), Math.Sin(lat * Math.PI / 180), cl * Math.Cos(lon * Math.PI / 180) };

                double ruido = Ruido(d, dir);
                if (ruido <= d.CutoffScale) continue;

                double alt = Altura(lat, lon);
                double escalarAlt = EscalarAltitud(d, alt);
                if (escalarAlt <= 0) continue;
                if (d.PlacementAltitude == null && alt < 0) continue;     // bajo el agua

                var nrm = Normal(lat, lon, dir);
                double cosUp = Math.Abs(nrm[0] * dir[0] + nrm[1] * dir[1] + nrm[2] * dir[2]);
                double pend = Math.Clamp((Math.Pow(cosUp, d.SteepPower) - d.SteepMidpoint) * d.SteepContrast + d.SteepMidpoint, 0, 1);
                if (pend < 0.025) pend = 0;
                if (r4 >= d.SpawnChance * escalarAlt * pend) continue;

                if (d.BiomeBlacklist.Count > 0 && Bioma != null)
                {
                    string b = Bioma(lat, lon);
                    if (b != null && d.BiomeBlacklist.Contains(b)) continue;
                }

                var p = Pos(lat, lon, d.PlacementAltitude ?? alt);
                double escalaRuido = Math.Clamp((ruido - d.CutoffScale) / (1 - d.CutoffScale), 0, 1);
                double s = escalaRuido + (U(Hash(h, 3)) - escalaRuido) * d.ScaleRandomness;
                var up = d.AlignToNormal ? nrm : dir;
                float cr = 1, cg = 1, cb = 1;
                if (d.ColoredByTerrain && Color != null) (cr, cg, cb) = Color(lat, lon);
                res.Add(new Inst
                {
                    X = p[0], Y = p[1], Z = p[2],
                    Ux = (float)up[0], Uy = (float)up[1], Uz = (float)up[2],
                    Yaw = (float)(U(Hash(h, 5)) * 180), S = (float)s,
                    R = cr, G = cg, B = cb,
                    Rank = (float)rank,
                });
            }
            var c = Centro(l, j, i);
            return new Cell { Items = res.ToArray(), Density = f, Cx = c[0], Cy = c[1], Cz = c[2] };
        }

        /* Como GetAltitudeScalar de Parallax: 1 dentro del rango de altitudes y bajando a 0
           en una franja de altitudeFadeRange centrada en cada límite. */
        static double EscalarAltitud(ScatterDef d, double alt)
        {
            double f = Math.Max(d.AltitudeFadeRange, 1e-6);
            if (d.MinAltitude == double.MinValue && d.MaxAltitude == double.MaxValue) return 1;
            double min = d.MinAltitude, max = d.MaxAltitude;
            double bajo = Math.Clamp((alt - (min - f / 2)) / f, 0, 1);
            double alto = Math.Clamp((max + f / 2 - alt) / f, 0, 1);
            return alt < (min + max) / 2 ? bajo : alto;
        }

        /* Normal del terreno por diferencias, a 40 m (los triángulos del terreno de KSP en su
           nivel más fino rondan los 66). */
        double[] Normal(double lat, double lon, double[] up)
        {
            const double e = 40;
            double dLat = e / R * 180 / Math.PI;
            double dLon = dLat / Math.Max(Math.Cos(lat * Math.PI / 180), 1e-6);
            double hN = Altura(lat + dLat, lon), hS = Altura(lat - dLat, lon);
            double hE = Altura(lat, lon + dLon), hW = Altura(lat, lon - dLon);
            double dx = (Math.Max(hE, 0) - Math.Max(hW, 0)) / (2 * e), dy = (Math.Max(hN, 0) - Math.Max(hS, 0)) / (2 * e);
            // este y norte locales
            var east = Norm(new[] { up[2], 0, -up[0] });
            if (double.IsNaN(east[0])) east = new double[] { 1, 0, 0 };
            var north = new[] { up[1] * east[2] - up[2] * east[1], up[2] * east[0] - up[0] * east[2], up[0] * east[1] - up[1] * east[0] };
            return Norm(new[]
            {
                up[0] - east[0] * dx - north[0] * dy,
                up[1] - east[1] * dx - north[1] * dy,
                up[2] - east[2] * dx - north[2] * dy,
            });
        }

        /* ------------------------------------------------------------ ruido */

        /* El ruido de Parallax: fBm sobre la dirección desde el centro, con el primer octavo
           ya a frecuencia × lacunaridad, un desfase por semilla y otro por octavo. Invertido es
           1 - |n|, que deja franjas estrechas (los bosques en bandas); normal va de 0 a 1. */
        static double Ruido(ScatterDef d, double[] dir)
        {
            double x = dir[0] - d.NoiseSeed * 3, y = dir[1] - d.NoiseSeed * 3, z = dir[2] - d.NoiseSeed * 3;
            double v = 0, fr = d.NoiseFrequency, amp = 1;
            bool celular = d.NoiseType != null && d.NoiseType.Contains("ellular", StringComparison.OrdinalIgnoreCase);
            for (int o = 0; o < Math.Max(1, d.NoiseOctaves); o++)
            {
                x += 2; y += 2; z += 2;
                fr *= d.NoiseLacunarity;
                amp *= 0.5;
                v += (celular ? Celular(x * fr, y * fr, z * fr) * 2 : Simplex(x * fr, y * fr, z * fr) * 1.5) * amp;
            }
            return d.NoiseInverted ? 1 - Math.Abs(v) : v * 0.5 + 0.5;
        }

        static readonly int[] Perm = CrearPerm();

        static int[] CrearPerm()
        {
            var p = new int[512];
            var r = new Random(1337);
            var b = Enumerable.Range(0, 256).OrderBy(_ => r.Next()).ToArray();
            for (int i = 0; i < 512; i++) p[i] = b[i & 255];
            return p;
        }

        static readonly int[,] Grad3 =
        {
            { 1, 1, 0 }, { -1, 1, 0 }, { 1, -1, 0 }, { -1, -1, 0 }, { 1, 0, 1 }, { -1, 0, 1 },
            { 1, 0, -1 }, { -1, 0, -1 }, { 0, 1, 1 }, { 0, -1, 1 }, { 0, 1, -1 }, { 0, -1, -1 },
        };

        /* Ruido simplex 3D (Gustavson), de -1 a 1. */
        static double Simplex(double xin, double yin, double zin)
        {
            const double F3 = 1.0 / 3, G3 = 1.0 / 6;
            double s = (xin + yin + zin) * F3;
            int i = (int)Math.Floor(xin + s), j = (int)Math.Floor(yin + s), k = (int)Math.Floor(zin + s);
            double t = (i + j + k) * G3;
            double x0 = xin - (i - t), y0 = yin - (j - t), z0 = zin - (k - t);
            int i1, j1, k1, i2, j2, k2;
            if (x0 >= y0)
            {
                if (y0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
                else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
                else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
            }
            else
            {
                if (y0 < z0) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
                else if (x0 < z0) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
                else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            }
            double x1 = x0 - i1 + G3, y1 = y0 - j1 + G3, z1 = z0 - k1 + G3;
            double x2 = x0 - i2 + 2 * G3, y2 = y0 - j2 + 2 * G3, z2 = z0 - k2 + 2 * G3;
            double x3 = x0 - 1 + 3 * G3, y3 = y0 - 1 + 3 * G3, z3 = z0 - 1 + 3 * G3;
            int ii = i & 255, jj = j & 255, kk = k & 255;
            double n = 0;
            n += Esquina(x0, y0, z0, Perm[ii + Perm[jj + Perm[kk]]] % 12);
            n += Esquina(x1, y1, z1, Perm[ii + i1 + Perm[jj + j1 + Perm[kk + k1]]] % 12);
            n += Esquina(x2, y2, z2, Perm[ii + i2 + Perm[jj + j2 + Perm[kk + k2]]] % 12);
            n += Esquina(x3, y3, z3, Perm[ii + 1 + Perm[jj + 1 + Perm[kk + 1]]] % 12);
            return 32 * n;
        }

        static double Esquina(double x, double y, double z, int g)
        {
            double t = 0.6 - x * x - y * y - z * z;
            if (t < 0) return 0;
            t *= t;
            return t * t * (Grad3[g, 0] * x + Grad3[g, 1] * y + Grad3[g, 2] * z);
        }

        /* Ruido celular (distancia al punto más cercano de una rejilla con un punto por
           celda), de 0 a ~1. */
        static double Celular(double x, double y, double z)
        {
            int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
            double best = 9;
            for (int a = -1; a <= 1; a++)
            for (int b = -1; b <= 1; b++)
            for (int c = -1; c <= 1; c++)
            {
                ulong h = Hash((ulong)(uint)(xi + a), (ulong)(uint)(yi + b), (ulong)(uint)(zi + c));
                double px = xi + a + U(Hash(h, 1)) - x, py = yi + b + U(Hash(h, 2)) - y, pz = zi + c + U(Hash(h, 3)) - z;
                best = Math.Min(best, px * px + py * py + pz * pz);
            }
            return Math.Min(1, Math.Sqrt(best));
        }

        /* ------------------------------------------------------------ utilidades */

        static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        // el hash de string de .NET cambia en cada ejecución: los árboles se moverían de sitio
        static ulong Fnv(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s ?? "") { h ^= c; h *= 1099511628211UL; }
            return h;
        }

        static ulong Hash(ulong a, ulong b) => Mix(a * 0x9E3779B97F4A7C15UL + Mix(b + 0x632BE59BD9B4E019UL));
        static ulong Hash(ulong a, ulong b, ulong c) => Hash(Hash(a, b), c);
        static double U(ulong h) => (h >> 11) * (1.0 / (1UL << 53));

        public double[] Pos(double lat, double lon, double alt)
        {
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180, r = R + alt;
            return new[] { r * Math.Cos(la) * Math.Sin(lo), r * Math.Sin(la), r * Math.Cos(la) * Math.Cos(lo) };
        }

        static double Dist(double[] a, double[] b)
        {
            double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
            return Math.Sqrt(x * x + y * y + z * z);
        }

        static double[] Norm(double[] v)
        {
            double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return new[] { v[0] / l, v[1] / l, v[2] / l };
        }
    }
}
