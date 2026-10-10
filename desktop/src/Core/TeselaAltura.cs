using System;

namespace KerbinMaps.Core
{
    /* Detail heights under the camera: tiles.

       The body's height map is loaded whole at a resolution that fits in memory (on Kerbin,
       SCANsat's 2048 one: 1.8 km per texel). Parallax ships Kerbin's at 8192 (460 m per texel),
       but uploading it whole would be 128 MB in RGBA. So its raw version is kept (R8, 1 byte
       per texel, with all its mipmap levels) and from it a window around the camera is cropped:
       a tile of N×N texels from a level that depends on the height (closer, more detail). The
       window is rebuilt in the background when the camera moves away from its center or changes
       level.

       The tile is read the same way on the CPU and in the ground shader (quintic curve between
       the four neighboring texels, like grisSuave) and is blended with the base map at its
       edge, so there's no step when entering or leaving it. */
    public sealed class FuenteAltura
    {
        public byte[] Datos;                      // R8, all levels, first row at the bottom
        public int Ancho, Alto, Mips;
        public bool Espejo;                       // horizontal mirror (like Parallax's maps)
        public double Offset;                     // longitude offset, in degrees
        public double Min, Max;                   // meters for gray 0 and gray 255

        public int AnchoNivel(int n) => Math.Max(1, Ancho >> n);
        public int AltoNivel(int n) => Math.Max(1, Alto >> n);

        long Inicio(int n)
        {
            long o = 0;
            for (int k = 0; k < n; k++) o += (long)AnchoNivel(k) * AltoNivel(k);
            return o;
        }

        /* A texel of level `n` in the viewer's orientation (row 0 at the north, no mirror). */
        internal byte Texel(long inicio, int w, int h, int x, int y)
        {
            x = ((x % w) + w) % w;
            y = Math.Clamp(y, 0, h - 1);
            int rx = Espejo ? w - 1 - x : x;
            int ry = h - 1 - y;
            long i = inicio + (long)ry * w + rx;
            return i < Datos.Length ? Datos[i] : (byte)0;
        }

        /* Separable Gaussian blur; the edge of `r` texels is left unused. */
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

        /* The `lado`×`lado` texel tile of level `n` centered on a point. */
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
            /* The gray is 8-bit: on Kerbin, steps of 32 m. At 460 m per texel they show as terraces on gentle slopes, where each step spans several
               texels. It's smoothed according to the surrounding relief: where there are barely two or three distinct grays (flat ground, which is
               where terraces appear) with σ = 2 texels; where there's real relief, not at all, and the mountains' detail is kept. It's stored at 16
               bits so the ramps don't step again. */
            const int R = 9;
            int ew = tw + 2 * R, eh = th + 2 * R;
            var crudo = new float[ew * eh];
            for (int y = 0; y < eh; y++)
                for (int x = 0; x < ew; x++)
                    crudo[y * ew + x] = Texel(ini, w, h, x0 + x - R, y0 + y - R);
            var fino = crudo;
            var grueso = Desenfocar(crudo, ew, eh, 2.0, R);
            // gray variation in a 7×7 window (separable min and max)
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
        public readonly int Nivel, W, H;          // size of the whole level
        public readonly int X0, Y0, Tw, Th;       // the window, in texels of the level
        public readonly float[] Metros;           // heights already in meters, smoothed
        public readonly double CentroLat, CentroLon;

        /* Width of the blend with the base map, in texels from the edge. */
        public double Margen => Math.Max(8, Math.Min(Tw, Th) / 8.0);

        public double Offset => Fuente.Offset;
        public double Min => Fuente.Min;
        public double Max => Fuente.Max;

        internal TeselaAltura(FuenteAltura f, int nivel, int w, int h, int x0, int y0, int tw, int th, float[] g, double lat, double lon)
        {
            Fuente = f; Nivel = nivel; W = w; H = h; X0 = x0; Y0 = y0; Tw = tw; Th = th; Metros = g;
            CentroLat = lat; CentroLon = lon;
        }

        /* Meters per texel at the equator. */
        public double MetrosPorTexel(double radio) => 2 * Math.PI * radio / W;

        /* Coordinates inside the window (in texels, with the same half-texel as the shader). */
        (double X, double Y) Local(double lat, double lon)
        {
            double u = (Geo.WrapLon(lon + Offset) + 180) / 360, v = (90 - lat) / 180;
            double tx = u * W - 0.5 - X0, ty = v * H - 0.5 - Y0;
            if (tx < -W / 2.0) tx += W;
            if (tx > W / 2.0) tx -= W;
            return (tx, ty);
        }

        /* Height in meters and how much the tile weighs there (1 inside, 0 outside, blended at
           the edge). Catmull-Rom interpolation between the 4×4 neighboring texels: it passes
           through the texel values and the slope is continuous. The base map's quintic goes
           flat at each texel and, at 460 m per texel, that shows as bands in the lighting of
           the slopes. */
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

        /* Whether a point is close enough to the center not to rebuild it. */
        public bool Cubre(double lat, double lon)
        {
            var (tx, ty) = Local(lat, lon);
            double mx = Tw / 4.0, my = Th / 4.0;
            // near the poles the window doesn't move in latitude: it's enough for it to be inside
            bool bordeY = Y0 == 0 || Y0 + Th >= H;
            return tx > mx && tx < Tw - mx && (bordeY ? ty > 0 && ty < Th - 1 : ty > my && ty < Th - my);
        }


    }
}
