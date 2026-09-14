using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace KerbinMaps.Core
{
    public sealed class PaletteEntry
    {
        public string Hex;
        public double Pct;
    }

    public sealed class PaletteResult
    {
        public List<PaletteEntry> Colors;
        public string Sampled, Native;
        public int DroppedCount;
        public double DroppedPct;
    }

    public readonly record struct OffsetMatch(int Shift, double Pct, double Media);

    /* Una imagen equirectangular decodificada a RGBA en memoria. En escritorio no hay
       que andar dibujando en un canvas de 1x1 para leer un píxel: se lee del array. */
    public sealed class ImageData
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Rgba { get; private set; }

        byte[] mask;            // silueta tierra/agua a 1° por celda, calculada una vez

        public static ImageData Decode(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var bmp = new Bitmap(ms);
            return FromBitmap(bmp);
        }

        public static unsafe ImageData FromBitmap(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var data = new byte[(long)w * h * 4];
            try
            {
                IntPtr scan0 = bd.Scan0;
                int stride = bd.Stride;
                fixed (byte* dstBase = data)
                {
                    byte* dst0 = dstBase;
                    Parallel.For(0, h, y =>
                    {
                        byte* row = (byte*)scan0 + (long)y * stride;
                        byte* d = dst0 + (long)y * w * 4;
                        for (int x = 0; x < w; x++)
                        {
                            d[0] = row[2]; d[1] = row[1]; d[2] = row[0]; d[3] = row[3];
                            row += 4; d += 4;
                        }
                    });
                }
            }
            finally { bmp.UnlockBits(bd); }
            return new ImageData { Width = w, Height = h, Rgba = data };
        }

        /* Píxel bajo una coordenada, con el desfase de longitud de su ranura. */
        public bool Sample(double lat, double lon, double lonOffset, out byte r, out byte g, out byte b, out byte a)
        {
            int sx = (int)Math.Floor(((Geo.WrapLon(lon + lonOffset) + 180) / 360) * Width);
            int sy = (int)Math.Floor(((90 - lat) / 180) * Height);
            sx = Math.Clamp(sx, 0, Width - 1);
            sy = Math.Clamp(sy, 0, Height - 1);
            long i = ((long)sy * Width + sx) * 4;
            r = Rgba[i]; g = Rgba[i + 1]; b = Rgba[i + 2]; a = Rgba[i + 3];
            return true;
        }

        public static double Luminance(byte r, byte g, byte b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;

        /* Altura en metros a partir del gris. Se usa luminancia por si el PNG trae un
           leve tinte de color. */
        public double Height_(double lat, double lon, double min, double max, double lonOffset)
        {
            Sample(lat, lon, lonOffset, out var r, out var g, out var b, out _);
            return min + Luminance(r, g, b) * (max - min);
        }

        /* Color crudo del mapa de biomas; null si el píxel es transparente. */
        public string BiomeHex(double lat, double lon, double lonOffset)
        {
            Sample(lat, lon, lonOffset, out var r, out var g, out var b, out var a);
            if (a == 0) return null;
            return "#" + r.ToString("x2") + g.ToString("x2") + b.ToString("x2");
        }

        /* Reescalado sin interpolar, como drawImage con imageSmoothingEnabled = false. */
        long SrcIndex(int x, int y, int w, int h)
        {
            int sx = Math.Min(Width - 1, (int)((x + 0.5) * Width / w));
            int sy = Math.Min(Height - 1, (int)((y + 0.5) * Height / h));
            return ((long)sy * Width + sx) * 4;
        }

        /* Todos los colores del mapa de biomas con la superficie real de cada uno.
           Sin interpolar, porque un color promediado no es ningún bioma; y cada fila
           pesa cos(lat), porque cerca del polo una fila de píxeles es mucha menos
           superficie que en el ecuador. */
        public PaletteResult Palette(int maxW = 1024, double minPct = BiomeConfig.MinAreaPct)
        {
            double scale = Math.Min(1, (double)maxW / Width);
            int w = Math.Max(1, (int)Math.Round(Width * scale));
            int h = Math.Max(1, (int)Math.Round(Height * scale));

            var acc = new Dictionary<int, double>();
            double total = 0;
            for (int y = 0; y < h; y++)
            {
                double lat = 90 - ((y + 0.5) / h) * 180;
                double wgt = Math.Cos(lat * Math.PI / 180);
                for (int x = 0; x < w; x++)
                {
                    long i = SrcIndex(x, y, w, h);
                    if (Rgba[i + 3] == 0) continue;
                    int key = (Rgba[i] << 16) | (Rgba[i + 1] << 8) | Rgba[i + 2];
                    acc[key] = acc.GetValueOrDefault(key) + wgt;
                    total += wgt;
                }
            }
            if (total == 0) return null;

            var all = acc.Select(kv => new PaletteEntry { Hex = "#" + kv.Key.ToString("x6"), Pct = kv.Value / total * 100 })
                         .OrderByDescending(e => e.Pct).ToList();
            var colors = all.Where(c => c.Pct >= minPct).ToList();
            var dropped = all.Where(c => c.Pct < minPct).ToList();
            return new PaletteResult
            {
                Colors = colors,
                Sampled = w + "×" + h,
                Native = Width + "×" + Height,
                DroppedCount = dropped.Count,
                DroppedPct = dropped.Sum(d => d.Pct)
            };
        }

        /* Saturación media. Pilla el error de cargar como heightmap una figura
           coloreada por paleta: un gris de verdad da ~0 y una paleta azul-verde-
           amarillo-rojo se va muy por encima. */
        public double? Saturation(int maxW = 256)
        {
            int w = Math.Max(1, Math.Min(maxW, Width));
            int h = Math.Max(1, (int)Math.Round((double)w * Height / Width));
            double sum = 0; int n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    long i = SrcIndex(x, y, w, h);
                    if (Rgba[i + 3] == 0) continue;
                    int r = Rgba[i], g = Rgba[i + 1], b = Rgba[i + 2];
                    int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                    if (mx > 0) { sum += (double)(mx - mn) / mx; n++; }
                }
            return n > 0 ? sum / n : null;
        }

        /* Máscara tierra/agua a 1° por celda, en el espacio de la propia imagen. */
        public byte[] LandMask()
        {
            if (mask != null) return mask;
            const int W = 360, H = 180;
            var m = new byte[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    long i = SrcIndex(x, y, W, H);
                    int r = Rgba[i], g = Rgba[i + 1], b = Rgba[i + 2];
                    m[y * W + x] = (byte)((b > r + 20 && b > g + 10) ? 0 : 1);   // azul dominante = agua
                }
            return mask = m;
        }

        /* Busca el giro en longitud que hace que dos mapas del mismo planeta encajen.
           Compara siluetas de continentes, así que funciona aunque las paletas no
           tengan nada que ver entre sí. */
        public static OffsetMatch DetectOffset(ImageData a, ImageData b, int offB)
        {
            const int W = 360, H = 180;
            var A = a.LandMask();
            var B = b.LandMask();
            offB = ((offB % W) + W) % W;

            int bestShift = 0; double bestPct = -1, sum = 0;
            var pcts = new double[W];
            Parallel.For(0, W, s =>
            {
                int ok = 0, tot = 0;
                for (int y = 20; y < H - 20; y++)            // los casquetes no distinguen nada
                    for (int i = 0; i < W; i++)
                    {
                        if (A[y * W + (i + s) % W] == B[y * W + (i + offB) % W]) ok++;
                        tot++;
                    }
                pcts[s] = (double)ok / tot * 100;
            });
            for (int s = 0; s < W; s++)
            {
                sum += pcts[s];
                if (pcts[s] > bestPct) { bestPct = pcts[s]; bestShift = s; }
            }
            return new OffsetMatch(bestShift, bestPct, sum / W);
        }

        /* El giro solo se da por bueno si destaca claramente sobre el promedio: si no,
           no son el mismo planeta o una de las dos no es equirectangular. */
        public static bool IsClearMatch(OffsetMatch m) => m.Pct >= 75 && m.Pct - m.Media >= 15;
    }
}
