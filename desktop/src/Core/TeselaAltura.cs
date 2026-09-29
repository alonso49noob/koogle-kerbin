using System;

namespace KerbinMaps.Core
{
    /* Alturas de detalle bajo la cámara: teselas.

       El mapa de alturas del cuerpo se carga entero a una resolución que quepa en memoria
       (en Kerbin, el de SCANsat de 2048: 1,8 km por texel). Parallax trae el de Kerbin a
       8192 (460 m por texel), pero subirlo entero serían 128 MB en RGBA. Así que se guarda
       su versión cruda (R8, 1 byte por texel, con todos sus niveles de mipmap) y de ella se
       recorta una ventana alrededor de la cámara: una tesela de N×N texeles de un nivel que
       depende de la altura (más de cerca, más detalle). La ventana se rehace en segundo
       plano cuando la cámara se aleja de su centro o cambia de nivel.

       La tesela se lee igual en la CPU y en el shader del suelo (curva quíntica entre los
       cuatro texeles vecinos, como grisSuave) y se funde con el mapa base en su borde, para
       que no haya escalón al entrar o salir de ella. */
    public sealed class FuenteAltura
    {
        public byte[] Datos;                      // R8, todos los niveles, la primera fila abajo
        public int Ancho, Alto, Mips;
        public bool Espejo;                       // espejo horizontal (como los mapas de Parallax)
        public double Offset;                     // desfase de longitud, en grados
        public double Min, Max;                   // metros del gris 0 y del 255

        public int AnchoNivel(int n) => Math.Max(1, Ancho >> n);
        public int AltoNivel(int n) => Math.Max(1, Alto >> n);

        long Inicio(int n)
        {
            long o = 0;
            for (int k = 0; k < n; k++) o += (long)AnchoNivel(k) * AltoNivel(k);
            return o;
        }

        /* Un texel del nivel `n` en la orientación del visor (fila 0 al norte, sin espejo). */
        internal byte Texel(long inicio, int w, int h, int x, int y)
        {
            x = ((x % w) + w) % w;
            y = Math.Clamp(y, 0, h - 1);
            int rx = Espejo ? w - 1 - x : x;
            int ry = h - 1 - y;
            long i = inicio + (long)ry * w + rx;
            return i < Datos.Length ? Datos[i] : (byte)0;
        }

        /* Desenfoque gaussiano separable; el borde de `r` texeles queda sin usar. */
        static float[] Desenfocar(float[] src, int w, int h, double sigma, int r)
        {
            int kr = Math.Min(r, (int)Math.Ceiling(sigma * 3));
            var k = new float[2 * kr + 1];
            float suma = 0;
            for (int i = -kr; i <= kr; i++) suma += k[i + kr] = (float)Math.Exp(-i * i / (2 * sigma * sigma));
            for (int i = 0; i < k.Length; i++) k[i] /= suma;
            var tmp = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int j = -kr; j <= kr; j++) s += k[j + kr] * src[y * w + Math.Clamp(x + j, 0, w - 1)];
                    tmp[y * w + x] = s;
                }
            var o = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int j = -kr; j <= kr; j++) s += k[j + kr] * tmp[Math.Clamp(y + j, 0, h - 1) * w + x];
                    o[y * w + x] = s;
                }
            return o;
        }

        /* La tesela de `lado`×`lado` texeles del nivel `n` centrada en un punto. */
        public TeselaAltura Tesela(int n, double lat, double lon, int lado)
        {
            n = Math.Clamp(n, 0, Math.Max(0, Mips - 1));
            int w = AnchoNivel(n), h = AltoNivel(n);
            int tw = Math.Min(lado, w), th = Math.Min(lado, h);
            double u = (Geo.WrapLon(lon + Offset) + 180) / 360, v = (90 - lat) / 180;
            int x0 = (int)Math.Floor(u * w) - tw / 2;
            int y0 = Math.Clamp((int)Math.Floor(v * h) - th / 2, 0, h - th);
            x0 = ((x0 % w) + w) % w;
            long ini = Inicio(n);
            if (ini + (long)w * h > Datos.Length) return null;
            /* El gris es de 8 bits: en Kerbin, escalones de 32 m. A 460 m por texel se ven
               como terrazas en las laderas suaves, donde cada escalón ocupa varios texeles.
               Se suaviza según el relieve de alrededor: donde apenas hay dos o tres grises
               distintos (llano, que es donde salen las terrazas) con σ = 2 texeles; donde hay
               relieve de verdad, nada, y el detalle de las montañas se conserva. Se guarda a 16 bits para que las rampas no vuelvan a escalonarse. */
            const int R = 9;
            int ew = tw + 2 * R, eh = th + 2 * R;
            var crudo = new float[ew * eh];
            for (int y = 0; y < eh; y++)
                for (int x = 0; x < ew; x++)
                    crudo[y * ew + x] = Texel(ini, w, h, x0 + x - R, y0 + y - R);
            var fino = crudo;
            var grueso = Desenfocar(crudo, ew, eh, 2.0, R);
            // variación de gris en una ventana de 7×7 (mínimo y máximo separables)
            var mn = new float[ew * eh]; var mx = new float[ew * eh];
            for (int y = 0; y < eh; y++)
                for (int x = 0; x < ew; x++)
                {
                    float a = float.MaxValue, b = float.MinValue;
                    for (int d = -3; d <= 3; d++)
                    {
                        float c = crudo[y * ew + Math.Clamp(x + d, 0, ew - 1)];
                        if (c < a) a = c;
                        if (c > b) b = c;
                    }
                    mn[y * ew + x] = a; mx[y * ew + x] = b;
                }
            var g = new float[tw * th];
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    int cx = x + R, cy = y + R;
                    float a = float.MaxValue, b = float.MinValue;
                    for (int d = -3; d <= 3; d++)
                    {
                        int k = (cy + d) * ew + cx;
                        if (mn[k] < a) a = mn[k];
                        if (mx[k] > b) b = mx[k];
                    }
                    double rel = Math.Clamp((b - a - 2) / 6.0, 0, 1);
                    rel = rel * rel * (3 - 2 * rel);
                    double s = grueso[cy * ew + cx] + (fino[cy * ew + cx] - grueso[cy * ew + cx]) * rel;
                    g[y * tw + x] = (float)(Min + s / 255 * (Max - Min));
                }
            return new TeselaAltura(this, n, w, h, x0, y0, tw, th, g, lat, lon);
        }
    }

    public sealed class TeselaAltura
    {
        public readonly FuenteAltura Fuente;
        public readonly int Nivel, W, H;          // tamaño del nivel entero
        public readonly int X0, Y0, Tw, Th;       // la ventana, en texeles del nivel
        public readonly float[] Metros;           // alturas ya en metros, suavizadas
        public readonly double CentroLat, CentroLon;

        /* Ancho del fundido con el mapa base, en texeles desde el borde. */
        public double Margen => Math.Max(8, Math.Min(Tw, Th) / 8.0);

        public double Offset => Fuente.Offset;
        public double Min => Fuente.Min;
        public double Max => Fuente.Max;

        internal TeselaAltura(FuenteAltura f, int nivel, int w, int h, int x0, int y0, int tw, int th, float[] g, double lat, double lon)
        {
            Fuente = f; Nivel = nivel; W = w; H = h; X0 = x0; Y0 = y0; Tw = tw; Th = th; Metros = g;
            CentroLat = lat; CentroLon = lon;
        }

        /* Metros por texel en el ecuador. */
        public double MetrosPorTexel(double radio) => 2 * Math.PI * radio / W;

        /* Coordenadas dentro de la ventana (en texeles, con el mismo medio texel que el
           shader). */
        (double X, double Y) Local(double lat, double lon)
        {
            double u = (Geo.WrapLon(lon + Offset) + 180) / 360, v = (90 - lat) / 180;
            double tx = u * W - 0.5 - X0, ty = v * H - 0.5 - Y0;
            if (tx < -W / 2.0) tx += W;
            if (tx > W / 2.0) tx -= W;
            return (tx, ty);
        }

        /* Altura en metros y cuánto pesa la tesela ahí (1 dentro, 0 fuera, fundido en el
           borde). Interpolación Catmull-Rom entre los 4×4 texeles vecinos: pasa por los
           valores de los texeles y la pendiente es continua. La quíntica del mapa base se
           queda plana en cada texel y, a 460 m por texel, eso se ve como bandas en la luz de
           las laderas. */
        public bool Altura(double lat, double lon, out double h, out double peso)
        {
            h = 0; peso = 0;
            var (tx, ty) = Local(lat, lon);
            if (tx < 1 || ty < 1 || tx > Tw - 3 || ty > Th - 3) return false;
            int ix = (int)Math.Floor(tx), iy = (int)Math.Floor(ty);
            double fx = tx - ix, fy = ty - iy;
            Span<double> wx = stackalloc double[4], wy = stackalloc double[4];
            Pesos(fx, wx); Pesos(fy, wy);
            double s = 0;
            for (int j = 0; j < 4; j++)
            {
                int row = (iy - 1 + j) * Tw;
                double fila = 0;
                for (int i = 0; i < 4; i++) fila += wx[i] * Metros[row + ix - 1 + i];
                s += wy[j] * fila;
            }
            h = s;
            double borde = Math.Min(Math.Min(tx - 1, Tw - 3 - tx), Math.Min(ty - 1, Th - 3 - ty));
            double t = Math.Clamp(borde / Margen, 0, 1);
            peso = t * t * (3 - 2 * t);
            return true;
        }

        static void Pesos(double f, Span<double> w)
        {
            double f2 = f * f, f3 = f2 * f;
            w[0] = (-f + 2 * f2 - f3) / 2;
            w[1] = (2 - 5 * f2 + 3 * f3) / 2;
            w[2] = (f + 4 * f2 - 3 * f3) / 2;
            w[3] = (-f2 + f3) / 2;
        }

        /* Si un punto está lo bastante cerca del centro como para no rehacerla. */
        public bool Cubre(double lat, double lon)
        {
            var (tx, ty) = Local(lat, lon);
            double mx = Tw / 4.0, my = Th / 4.0;
            // cerca de los polos la ventana no se mueve en latitud: basta con que esté dentro
            bool bordeY = Y0 == 0 || Y0 + Th >= H;
            return tx > mx && tx < Tw - mx && (bordeY ? ty > 0 && ty < Th - 1 : ty > my && ty < Th - my);
        }


    }
}
