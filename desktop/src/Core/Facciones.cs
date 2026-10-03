using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace KerbinMaps.Core
{
    /* Un país o facción: su nombre, su color y el número con el que se apunta en el mapa. */
    public sealed class Faccion
    {
        public int Id;
        public string Nombre;
        public string Color;                      // #rrggbb
        public bool Visible = true;
        public string Notas;
    }

    /* Dónde poner el nombre de una facción: en lo más hondo de uno de sus territorios. */
    public readonly record struct EtiquetaFaccion(int Id, double Lat, double Lon, double RadioM);

    /* Mapa político de un cuerpo: de quién es cada trozo de superficie.

       Es una rejilla equirectangular de 4096×2048 celdas (en Kerbin, 920 m por celda en el
       ecuador) con el número de la facción en cada una, 0 para nadie. Se pinta con pincel,
       relleno o polígono, y solo se ve sobre tierra: al dibujar, la costa la recorta el mapa
       que se esté viendo (el de alturas de cerca, más fino que la rejilla). Lo que el pincel o
       el polígono dejan sobre el mar se guarda pero no se ve; así, si se cambia de mapas y la
       costa se mueve un poco, el territorio sigue llegando hasta el agua. Una máscara dice
       cuánta tierra tiene cada celda: con ella el relleno sabe qué es una isla, y la superficie
       y los nombres cuentan solo tierra. En un cuerpo sin mar todo es superficie.

       Cada pincelada se puede deshacer: se apunta qué había en cada celda que cambia. */
    public sealed class MapaPolitico
    {
        public const int Ancho = 4096, Alto = 2048;
        public const int MaxFacciones = 63;       // los colores van en un vector de 64 en el shader

        public readonly byte[] Celdas = new byte[Ancho * Alto];
        public readonly List<Faccion> Facciones = new();
        public string Cuerpo;

        byte[] tierra;                            // fracción de tierra de cada celda, 0 a 255
        public bool HayMascara => tierra != null;

        /* Filas que han cambiado desde la última vez que se subió la rejilla a la GPU. */
        public int SucioY0 = int.MaxValue, SucioY1 = -1;
        public int Version;                       // sube con cada cambio, para guardar y medir

        public static double LatDe(int y) => 90 - (y + 0.5) * 180.0 / Alto;
        public static double LonDe(int x) => -180 + (x + 0.5) * 360.0 / Ancho;
        public static int Fila(double lat) => Math.Clamp((int)Math.Floor((90 - lat) / 180 * Alto), 0, Alto - 1);

        public static int Columna(double lon)
        {
            int x = (int)Math.Floor((Geo.WrapLon(lon) + 180) / 360 * Ancho);
            return ((x % Ancho) + Ancho) % Ancho;
        }

        public int IdEn(double lat, double lon) => Celdas[Fila(lat) * Ancho + Columna(lon)];
        public Faccion Buscar(int id) => id <= 0 ? null : Facciones.FirstOrDefault(f => f.Id == id);

        /* ------------------------------------------------------------ tierra */

        public void PonerTierra(byte[] t) => tierra = t;

        /* Cuánta tierra hay en cada celda: cinco muestras (el centro cuenta doble). */
        public static byte[] ConstruirTierra(Func<double, double, bool> esTierra)
        {
            var t = new byte[Ancho * Alto];
            double dLat = 180.0 / Alto * 0.3, dLon = 360.0 / Ancho * 0.3;
            Parallel.For(0, Alto, y =>
            {
                double lat = LatDe(y);
                for (int x = 0; x < Ancho; x++)
                {
                    double lon = LonDe(x);
                    int n = (esTierra(lat, lon) ? 2 : 0)
                          + (esTierra(lat + dLat, lon + dLon) ? 1 : 0) + (esTierra(lat - dLat, lon + dLon) ? 1 : 0)
                          + (esTierra(lat + dLat, lon - dLon) ? 1 : 0) + (esTierra(lat - dLat, lon - dLon) ? 1 : 0);
                    t[y * Ancho + x] = (byte)(n * 255 / 6);
                }
            });
            return t;
        }

        /* Al rellenar, la tierra se lleva también el mar de hasta tres celdas de su costa (unos
           2,8 km en Kerbin): el mapa de color y el de alturas no siempre ponen la costa en el mismo
           sitio, y así el territorio llega hasta la que se vea. */
        const int Orla = 3;

        public bool EsTierra(double lat, double lon) => tierra == null || tierra[Fila(lat) * Ancho + Columna(lon)] > 0;

        /* ------------------------------------------------------------ cambios */

        sealed class Cambio { public int[] Idx; public byte[] Antes, Despues; }

        readonly List<int> tIdx = new();
        readonly List<byte> tAntes = new();
        readonly ulong[] tocado = new ulong[Ancho * Alto / 64];
        readonly List<Cambio> deshacer = new(), rehacer = new();
        const int MaxDeshacer = 40;

        public bool PuedeDeshacer => deshacer.Count > 0;
        public bool PuedeRehacer => rehacer.Count > 0;

        void Poner(int i, byte v)
        {
            byte antes = Celdas[i];
            if (antes == v) return;
            ulong bit = 1UL << (i & 63);
            if ((tocado[i >> 6] & bit) == 0)
            {
                tocado[i >> 6] |= bit;
                tIdx.Add(i);
                tAntes.Add(antes);
            }
            Celdas[i] = v;
            Ensuciar(i / Ancho);
        }

        void Ensuciar(int y)
        {
            if (y < SucioY0) SucioY0 = y;
            if (y > SucioY1) SucioY1 = y;
        }

        public void LimpiarSucio() { SucioY0 = int.MaxValue; SucioY1 = -1; }

        /* Una pincelada (o un relleno, o un polígono) es una sola cosa para deshacer. */
        public void EmpezarTrazo() => TerminarTrazo();

        /* Devuelve true si la pincelada cambió algo. */
        public bool TerminarTrazo()
        {
            if (tIdx.Count == 0) return false;
            var c = new Cambio { Idx = tIdx.ToArray(), Antes = tAntes.ToArray() };
            c.Despues = new byte[c.Idx.Length];
            for (int k = 0; k < c.Idx.Length; k++)
            {
                c.Despues[k] = Celdas[c.Idx[k]];
                tocado[c.Idx[k] >> 6] = 0;
            }
            tIdx.Clear();
            tAntes.Clear();
            deshacer.Add(c);
            // hasta 40 pasos, y sin pasar de unos 8 millones de celdas apuntadas (unos 50 MB)
            long total = deshacer.Sum(d => (long)d.Idx.Length);
            while (deshacer.Count > 1 && (deshacer.Count > MaxDeshacer || total > Ancho * Alto))
            {
                total -= deshacer[0].Idx.Length;
                deshacer.RemoveAt(0);
            }
            rehacer.Clear();
            Version++;
            return true;
        }

        public bool Deshacer() => Aplicar(deshacer, rehacer, atras: true);
        public bool Rehacer() => Aplicar(rehacer, deshacer, atras: false);

        bool Aplicar(List<Cambio> de, List<Cambio> a, bool atras)
        {
            TerminarTrazo();
            if (de.Count == 0) return false;
            var c = de[^1];
            de.RemoveAt(de.Count - 1);
            var valores = atras ? c.Antes : c.Despues;
            for (int k = 0; k < c.Idx.Length; k++)
            {
                Celdas[c.Idx[k]] = valores[k];
                Ensuciar(c.Idx[k] / Ancho);
            }
            a.Add(c);
            Version++;
            return true;
        }

        public void OlvidarHistoria() { TerminarTrazo(); deshacer.Clear(); rehacer.Clear(); }

        /* ¿Se puede poner `id` en la celda? Respetando, no se pisa lo que ya es de otra facción;
           borrando (id 0), solo se borra lo de `propia`. */
        bool Admite(int i, byte id, bool respetar, byte propia)
        {
            byte c = Celdas[i];
            if (id == 0) return c != 0 && (!respetar || c == propia);
            return !respetar || c == 0 || c == id;
        }

        /* ------------------------------------------------------------ herramientas */

        /* Un círculo de radioM metros alrededor del punto, medido sobre la esfera. */
        public void Pincel(double lat, double lon, double radioM, byte id, bool respetar, byte propia)
        {
            double R = Body.Radius;
            double celdaM = Math.PI * R / Alto;
            int ry = (int)Math.Ceiling(radioM / celdaM) + 1;
            int yc = Fila(lat), xc = Columna(lon);
            double la0 = lat * Geo.D2R, lo0 = lon * Geo.D2R, sla0 = Math.Sin(la0), cla0 = Math.Cos(la0);
            double cosR = Math.Cos(Math.Min(Math.PI, radioM / R));
            for (int y = Math.Max(0, yc - ry); y <= Math.Min(Alto - 1, yc + ry); y++)
            {
                double la = LatDe(y) * Geo.D2R, cl = Math.Cos(la), sl = Math.Sin(la);
                int rx = cl < 1e-3 ? Ancho / 2 : Math.Min(Ancho / 2, (int)Math.Ceiling(ry / cl) + 1);
                for (int dx = -rx; dx <= rx; dx++)
                {
                    int x = ((xc + dx) % Ancho + Ancho) % Ancho;
                    double cosd = sla0 * sl + cla0 * cl * Math.Cos(LonDe(x) * Geo.D2R - lo0);
                    if (cosd < cosR) continue;
                    int i = y * Ancho + x;
                    if (Admite(i, id, respetar, propia)) Poner(i, id);
                }
            }
        }

        /* El pincel arrastrado de a a b, por el gran círculo, sin dejar huecos. */
        public void Trazo(LatLon a, LatLon b, double radioM, byte id, bool respetar, byte propia)
        {
            double d = Geo.Distance(a.Lat, a.Lon, b.Lat, b.Lon);
            int n = Math.Max(1, (int)Math.Ceiling(d / Math.Max(radioM * 0.35, 1)));
            var pts = Geo.GreatCircle(a.Lat, a.Lon, b.Lat, b.Lon, n);
            for (int k = 1; k < pts.Count; k++) Pincel(pts[k].Lat, pts[k].Lon, radioM, id, respetar, propia);
        }

        /* Rellena la tierra unida al punto que tenga el mismo dueno que él: una isla entera, o el
           hueco que queda dentro de una frontera. Devuelve cuántas celdas ha cambiado (0 si el
           punto es mar o ya era de esa facción). */
        public int Rellenar(double lat, double lon, byte id, bool respetar, byte propia)
        {
            int i0 = Fila(lat) * Ancho + Columna(lon);
            if (tierra != null && tierra[i0] == 0) return 0;
            byte objetivo = Celdas[i0];
            if (objetivo == id) return 0;
            if (id != 0 && respetar && objetivo != 0) return 0;
            if (id == 0 && respetar && objetivo != propia) return 0;
            int antes = tIdx.Count;
            var visto = new bool[Ancho * Alto];
            var cola = new Queue<int>();
            visto[i0] = true;
            cola.Enqueue(i0);
            var hechas = new List<int>();
            while (cola.Count > 0)
            {
                int i = cola.Dequeue();
                Poner(i, id);
                hechas.Add(i);
                int x = i % Ancho, y = i / Ancho;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x, ny = y;
                    if (k == 0) nx = (x + 1) % Ancho; else if (k == 1) nx = (x + Ancho - 1) % Ancho;
                    else if (k == 2) ny = y + 1; else ny = y - 1;
                    if (ny < 0 || ny >= Alto) continue;
                    int j = ny * Ancho + nx;
                    if (visto[j] || Celdas[j] != objetivo) continue;
                    if (tierra != null && tierra[j] == 0) continue;
                    visto[j] = true;
                    cola.Enqueue(j);
                }
            }
            // la orla de mar de la costa (celdas sin tierra junto a la tierra) va con lo rellenado
            if (tierra != null)
                foreach (int i in hechas)
                {
                    int x = i % Ancho, y = i / Ancho;
                    // solo desde la costa: lo de dentro no tiene mar al lado
                    if (tierra[i] == 255) continue;
                    for (int dy = -Orla; dy <= Orla; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= Alto) continue;
                        for (int dx = -Orla; dx <= Orla; dx++)
                        {
                            int j = yy * Ancho + (x + dx + Ancho) % Ancho;
                            if (tierra[j] == 0 && Celdas[j] == objetivo) Poner(j, id);
                        }
                    }
                }
            return tIdx.Count - antes;
        }

        /* Rellena el interior de un polígono de vértices en lat/lon (lados rectos en el mapa).
           Las longitudes se desenvuelven a partir del primer vértice. */
        public int Poligono(IReadOnlyList<LatLon> vertices, byte id, bool respetar, byte propia)
        {
            if (vertices == null || vertices.Count < 3) return 0;
            var v = Geo.Unwrap(vertices);
            int antes = tIdx.Count;
            double latMin = v.Min(p => p.Lat), latMax = v.Max(p => p.Lat);
            int y0 = Fila(latMax), y1 = Fila(latMin);
            var cortes = new List<double>();
            for (int y = y0; y <= y1; y++)
            {
                double lat = LatDe(y);
                cortes.Clear();
                for (int k = 0; k < v.Count; k++)
                {
                    var a = v[k];
                    var b = v[(k + 1) % v.Count];
                    if ((a.Lat <= lat && b.Lat > lat) || (b.Lat <= lat && a.Lat > lat))
                        cortes.Add(a.Lon + (lat - a.Lat) / (b.Lat - a.Lat) * (b.Lon - a.Lon));
                }
                cortes.Sort();
                for (int k = 0; k + 1 < cortes.Count; k += 2)
                {
                    int xa = (int)Math.Ceiling((cortes[k] + 180) / 360 * Ancho - 0.5);
                    int xb = (int)Math.Floor((cortes[k + 1] + 180) / 360 * Ancho - 0.5);
                    if (xb - xa >= Ancho) xb = xa + Ancho - 1;
                    for (int xx = xa; xx <= xb; xx++)
                    {
                        int i = y * Ancho + ((xx % Ancho) + Ancho) % Ancho;
                        if (Admite(i, id, respetar, propia)) Poner(i, id);
                    }
                }
            }
            return tIdx.Count - antes;
        }

        /* ------------------------------------------------------------ facciones */

        static readonly string[] Paleta =
        {
            "#e6194b", "#3cb44b", "#ffe119", "#4363d8", "#f58231", "#911eb4", "#46f0f0", "#f032e6",
            "#bcf60c", "#fabebe", "#008080", "#e6beff", "#9a6324", "#fffac8", "#800000", "#aaffc3",
            "#808000", "#ffd8b1", "#000075", "#808080"
        };

        static bool ColorValido(string c) =>
            c != null && c.Length == 7 && c[0] == '#' &&
            int.TryParse(c.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out _);

        public Faccion Nueva(string nombre)
        {
            int id = 1;
            while (id <= MaxFacciones && Facciones.Any(f => f.Id == id)) id++;
            if (id > MaxFacciones) return null;
            string color = Paleta.FirstOrDefault(c => Facciones.All(f => !string.Equals(f.Color, c, StringComparison.OrdinalIgnoreCase)))
                           ?? Paleta[(id - 1) % Paleta.Length];
            var nueva = new Faccion { Id = id, Nombre = nombre, Color = color };
            Facciones.Add(nueva);
            Version++;
            return nueva;
        }

        /* Quita la facción y su territorio. No se puede deshacer: la historia se olvida. */
        public void Borrar(Faccion f)
        {
            if (f == null) return;
            OlvidarHistoria();
            for (int i = 0; i < Celdas.Length; i++)
                if (Celdas[i] == f.Id) { Celdas[i] = 0; Ensuciar(i / Ancho); }
            Facciones.Remove(f);
            Version++;
        }

        /* ------------------------------------------------------------ medidas */

        /* Superficie de tierra de cada facción, en km² (las celdas de costa cuentan por la
           tierra que tienen). Va con una copia de las celdas: puede ir en segundo plano. */
        public static Dictionary<int, double> AreasDe(byte[] celdas, byte[] tierra, double radio)
        {
            var suma = new double[256];
            var porFila = new double[Alto];
            for (int y = 0; y < Alto; y++)
                porFila[y] = (2 * Math.PI * radio / Ancho) * (Math.PI * radio / Alto) * Math.Cos(LatDe(y) * Geo.D2R) / 1e6;
            object cerrojo = new();
            Parallel.For(0, Alto, () => new double[256], (y, _, local) =>
            {
                int fila = y * Ancho;
                for (int x = 0; x < Ancho; x++)
                {
                    byte c = celdas[fila + x];
                    if (c == 0) continue;
                    local[c] += porFila[y] * (tierra == null ? 1 : tierra[fila + x] / 255.0);
                }
                return local;
            }, local => { lock (cerrojo) for (int k = 0; k < 256; k++) suma[k] += local[k]; });
            var r = new Dictionary<int, double>();
            for (int k = 1; k < 256; k++) if (suma[k] > 0) r[k] = suma[k];
            return r;
        }

        public Dictionary<int, double> Areas() => AreasDe(Celdas, tierra, Body.Radius);

        /* Toda la tierra del cuerpo, en km² (con la máscara; sin ella, la esfera entera). */
        public static double AreaTierra(byte[] tierra, double radio)
        {
            if (tierra == null) return 4 * Math.PI * radio * radio / 1e6;
            double s = 0;
            for (int y = 0; y < Alto; y++)
            {
                long n = 0;
                for (int x = 0; x < Ancho; x++) n += tierra[y * Ancho + x];
                s += n / 255.0 * (2 * Math.PI * radio / Ancho) * (Math.PI * radio / Alto) * Math.Cos(LatDe(y) * Geo.D2R);
            }
            return s / 1e6;
        }

        /* Dónde rotular cada facción: el punto más alejado de la frontera (y de la costa) de cada
           uno de sus territorios grandes. Se calcula a un cuarto de la resolución, con una copia
           de las celdas, así que puede ir en segundo plano. */
        public static List<EtiquetaFaccion> Etiquetas(byte[] celdas, byte[] tierra, double radio)
        {
            const int F = 4, W = Ancho / F, H = Alto / F;
            var dueno = new byte[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * F + F / 2) * Ancho + x * F + F / 2;
                    if (tierra != null && tierra[i] < 128) continue;
                    dueno[y * W + x] = celdas[i];
                }

            // territorios: trozos unidos de la misma facción
            var comp = new int[W * H];
            var areas = new List<double>();
            var duenos = new List<byte>();
            var cola = new Queue<int>();
            for (int i0 = 0; i0 < W * H; i0++)
            {
                if (dueno[i0] == 0 || comp[i0] != 0) continue;
                int c = areas.Count + 1;
                byte d = dueno[i0];
                double area = 0;
                comp[i0] = c;
                cola.Enqueue(i0);
                while (cola.Count > 0)
                {
                    int i = cola.Dequeue();
                    int x = i % W, y = i / W;
                    area += Math.Cos((90 - (y + 0.5) * 180.0 / H) * Geo.D2R);
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x, ny = y;
                        if (k == 0) nx = (x + 1) % W; else if (k == 1) nx = (x + W - 1) % W;
                        else if (k == 2) ny = y + 1; else ny = y - 1;
                        if (ny < 0 || ny >= H) continue;
                        int j = ny * W + nx;
                        if (comp[j] != 0 || dueno[j] != d) continue;
                        comp[j] = c;
                        cola.Enqueue(j);
                    }
                }
                areas.Add(area);
                duenos.Add(d);
            }
            if (areas.Count == 0) return new List<EtiquetaFaccion>();

            /* Distancia al borde de su territorio (chaflán en dos pasadas): en horizontal, una
               celda mide cos(lat) de lo que mide en vertical. */
            var dist = new float[W * H];
            for (int i = 0; i < W * H; i++) dist[i] = comp[i] == 0 ? 0 : 1e9f;
            float Cx(int y) => (float)Math.Max(0.02, Math.Cos((90 - (y + 0.5) * 180.0 / H) * Geo.D2R));
            for (int y = 0; y < H; y++)
            {
                float cx = Cx(y), cd = MathF.Sqrt(cx * cx + 1);
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    if (comp[i] == 0) continue;
                    float m = dist[i];
                    void Mira(int j, float w) { float v = comp[j] == comp[i] ? dist[j] + w : w * 0.5f; if (v < m) m = v; }
                    Mira(y * W + (x + W - 1) % W, cx);
                    if (y > 0) { Mira((y - 1) * W + x, 1); Mira((y - 1) * W + (x + W - 1) % W, cd); Mira((y - 1) * W + (x + 1) % W, cd); }
                    else m = Math.Min(m, 0.5f);
                    dist[i] = m;
                }
            }
            for (int y = H - 1; y >= 0; y--)
            {
                float cx = Cx(y), cd = MathF.Sqrt(cx * cx + 1);
                for (int x = W - 1; x >= 0; x--)
                {
                    int i = y * W + x;
                    if (comp[i] == 0) continue;
                    float m = dist[i];
                    void Mira(int j, float w) { float v = comp[j] == comp[i] ? dist[j] + w : w * 0.5f; if (v < m) m = v; }
                    Mira(y * W + (x + 1) % W, cx);
                    if (y < H - 1) { Mira((y + 1) * W + x, 1); Mira((y + 1) * W + (x + 1) % W, cd); Mira((y + 1) * W + (x + W - 1) % W, cd); }
                    else m = Math.Min(m, 0.5f);
                    dist[i] = m;
                }
            }

            // el punto más hondo de cada territorio
            var mejor = new int[areas.Count + 1];
            for (int i = 0; i < W * H; i++)
            {
                int c = comp[i];
                if (c == 0) continue;
                if (mejor[c] == 0 || dist[i] > dist[mejor[c] - 1]) mejor[c] = i + 1;
            }

            // por facción, el territorio más grande y los que sean al menos la cuarta parte (hasta 4)
            var r = new List<EtiquetaFaccion>();
            double celdaM = Math.PI * radio / H;
            foreach (var grupo in Enumerable.Range(1, areas.Count).GroupBy(c => duenos[c - 1]))
            {
                double mayor = grupo.Max(c => areas[c - 1]);
                foreach (int c in grupo.OrderByDescending(c => areas[c - 1]).Take(4))
                {
                    if (areas[c - 1] < mayor * 0.25 && areas[c - 1] != mayor) continue;
                    int i = mejor[c] - 1;
                    int x = i % W, y = i / W;
                    r.Add(new EtiquetaFaccion(grupo.Key, 90 - (y + 0.5) * 180.0 / H, -180 + (x + 0.5) * 360.0 / W, dist[i] * celdaM));
                }
            }
            return r;
        }

        public byte[] Tierra => tierra;

        /* ------------------------------------------------------------ disco */

        sealed class Guardado
        {
            public int Version = 1;
            public string Cuerpo;
            public int Ancho, Alto;
            public List<Faccion> Facciones;
            public string Celdas;                 // deflate + base64
        }

        public string ToJson()
        {
            TerminarTrazo();
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(Celdas, 0, Celdas.Length);
            var g = new Guardado { Cuerpo = Cuerpo, Ancho = Ancho, Alto = Alto, Facciones = Facciones, Celdas = Convert.ToBase64String(ms.ToArray()) };
            return JsonSerializer.Serialize(g, Store.Json);
        }

        /* Lee un mapa guardado o exportado. Si su rejilla es de otro tamaño, se reescala. */
        public static MapaPolitico FromJson(string json)
        {
            var g = JsonSerializer.Deserialize<Guardado>(json, Store.Json);
            if (g == null) throw new InvalidDataException("vacío");
            var m = new MapaPolitico { Cuerpo = g.Cuerpo };
            foreach (var f in g.Facciones ?? new List<Faccion>())
                if (f != null && f.Id >= 1 && f.Id <= MaxFacciones && m.Facciones.All(o => o.Id != f.Id))
                {
                    // un fichero editado a mano puede traer cualquier cosa
                    if (!ColorValido(f.Color)) f.Color = Paleta[(f.Id - 1) % Paleta.Length];
                    if (string.IsNullOrWhiteSpace(f.Nombre)) f.Nombre = "#" + f.Id;
                    m.Facciones.Add(f);
                }
            if (!string.IsNullOrEmpty(g.Celdas) && g.Ancho > 0 && g.Alto > 0)
            {
                using var z = new ZLibStream(new MemoryStream(Convert.FromBase64String(g.Celdas)), CompressionMode.Decompress);
                var crudo = new byte[(long)g.Ancho * g.Alto];
                int leido = 0;
                while (leido < crudo.Length)
                {
                    int n = z.Read(crudo, leido, crudo.Length - leido);
                    if (n <= 0) break;
                    leido += n;
                }
                var validos = new bool[256];
                foreach (var f in m.Facciones) validos[f.Id] = true;
                for (int y = 0; y < Alto; y++)
                {
                    int sy = (int)((y + 0.5) * g.Alto / Alto);
                    for (int x = 0; x < Ancho; x++)
                    {
                        byte c = crudo[(long)sy * g.Ancho + (int)((x + 0.5) * g.Ancho / Ancho)];
                        m.Celdas[y * Ancho + x] = validos[c] ? c : (byte)0;
                    }
                }
            }
            m.SucioY0 = 0; m.SucioY1 = Alto - 1;
            return m;
        }
    }
}
