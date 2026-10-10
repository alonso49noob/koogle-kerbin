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

    /* An equirectangular image decoded to RGBA in memory. On the desktop there's no need to
       draw on a 1x1 canvas to read a pixel: it's read from the array. */
    public sealed class ImageData
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Rgba { get; private set; }

        byte[] mask;            // land/water silhouette at 1° per cell, computed once

        /* From RGBA pixels already in memory (textures from Unity bundles, for example). */
        public static ImageData FromRgba(byte[] rgba, int w, int h) => new ImageData { Width = w, Height = h, Rgba = rgba };

        /* Vertical flip in place: Unity and OpenGL store the first row at the bottom. */
        public ImageData FlipY()
        {
            int fila = Width * 4;
            var tmp = new byte[fila];
            for (int y = 0; y < Height / 2; y++)
            {
                int a = y * fila, b = (Height - 1 - y) * fila;
                Buffer.BlockCopy(Rgba, a, tmp, 0, fila);
                Buffer.BlockCopy(Rgba, b, Rgba, a, fila);
                Buffer.BlockCopy(tmp, 0, Rgba, b, fila);
            }
            mask = null;
            return this;
        }

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

        /* Horizontal mirror, in place. The body textures KSP stores are like this relative to a
           usual equirectangular map: longitude grows the other way. */
        public ImageData MirrorX()
        {
            int w = Width, h = Height;
            Parallel.For(0, h, y =>
            {
                long row = (long)y * w * 4;
                for (int x = 0; x < w / 2; x++)
                {
                    long a = row + (long)x * 4, b = row + (long)(w - 1 - x) * 4;
                    for (int k = 0; k < 4; k++) (Rgba[a + k], Rgba[b + k]) = (Rgba[b + k], Rgba[a + k]);
                }
            });
            mask = null;
            return this;
        }

        /* Pixel under a coordinate, with its slot's longitude offset. */
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

        /* Height in meters from the gray. Luminance is used in case the PNG has a slight color
           tint. */
        public double Height_(double lat, double lon, double min, double max, double lonOffset)
        {
            Sample(lat, lon, lonOffset, out var r, out var g, out var b, out _);
            return min + Luminance(r, g, b) * (max - min);
        }

        /* Height interpolated exactly as the flight shader interpolates it (grisSuave): between
           the four neighboring texels with a quintic curve, longitude wrapping around and
           latitude clamped. Whatever is placed on the ground (the camera, the grass, the trees)
           has to use this one and not the nearest pixel's: on Kerbin a texel is 460 m and the
           difference between the two reaches tens of meters. */
        public double HeightSmooth(double lat, double lon, double min, double max, double lonOffset)
        {
            double u = (Geo.WrapLon(lon + lonOffset) + 180) / 360;
            double v = (90 - lat) / 180;
            double tx = u * Width - 0.5, ty = v * Height - 0.5;
            double fx = tx - Math.Floor(tx), fy = ty - Math.Floor(ty);
            int ix = (int)Math.Floor(tx), iy = (int)Math.Floor(ty);
            fx = fx * fx * fx * (fx * (fx * 6 - 15) + 10);
            fy = fy * fy * fy * (fy * (fy * 6 - 15) + 10);
            double a = Lerp(Gris(ix, iy), Gris(ix + 1, iy), fx);
            double b = Lerp(Gris(ix, iy + 1), Gris(ix + 1, iy + 1), fx);
            return min + Lerp(a, b, fy) * (max - min);
        }

        double Gris(int x, int y)
        {
            x = ((x % Width) + Width) % Width;
            y = Math.Clamp(y, 0, Height - 1);
            long i = ((long)y * Width + x) * 4;
            return Luminance(Rgba[i], Rgba[i + 1], Rgba[i + 2]);
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /* Color interpolated between the four neighboring texels, from 0 to 1. For tinting the
           grass with the ground color: with the nearest pixel, each texel hundreds of meters
           wide would show as a patch of another color. */
        public (float R, float G, float B) SampleBilinear(double lat, double lon, double lonOffset)
        {
            double tx = (Geo.WrapLon(lon + lonOffset) + 180) / 360 * Width - 0.5;
            double ty = (90 - lat) / 180 * Height - 0.5;
            int ix = (int)Math.Floor(tx), iy = (int)Math.Floor(ty);
            double fx = tx - ix, fy = ty - iy;
            float r = 0, g = 0, b = 0;
            for (int k = 0; k < 4; k++)
            {
                int x = ix + (k & 1), y = iy + (k >> 1);
                double w = ((k & 1) == 1 ? fx : 1 - fx) * ((k >> 1) == 1 ? fy : 1 - fy);
                x = ((x % Width) + Width) % Width;
                y = Math.Clamp(y, 0, Height - 1);
                long i = ((long)y * Width + x) * 4;
                r += (float)(Rgba[i] * w); g += (float)(Rgba[i + 1] * w); b += (float)(Rgba[i + 2] * w);
            }
            return (r / 255f, g / 255f, b / 255f);
        }

        /* Raw color of the biome map; null if the pixel is transparent. */
        public string BiomeHex(double lat, double lon, double lonOffset)
        {
            Sample(lat, lon, lonOffset, out var r, out var g, out var b, out var a);
            if (a == 0) return null;
            return "#" + r.ToString("x2") + g.ToString("x2") + b.ToString("x2");
        }

        /* Rescale without interpolation, like drawImage with imageSmoothingEnabled = false. */
        long SrcIndex(int x, int y, int w, int h)
        {
            int sx = Math.Min(Width - 1, (int)((x + 0.5) * Width / w));
            int sy = Math.Min(Height - 1, (int)((y + 0.5) * Height / h));
            return ((long)sy * Width + sx) * 4;
        }

        /* All the colors of the biome map with the real area of each one. No interpolation,
           because an averaged color isn't any biome; and each row weighs cos(lat), because near
           the pole a row of pixels is much less area than at the equator. */
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

        /* Mean saturation. Catches the mistake of loading a palette-colored figure as a
           heightmap: a real gray gives ~0, and a blue-green-yellow-red palette goes far above. */
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

        /* The lightest gray in the image. A dump of the game's textures stops at 145 (see
           BodyMaps.GrisTope); a SCANsat export reaches 255. */
        public int MaxGray()
        {
            int mx = 0;
            for (long i = 0; i < Rgba.LongLength; i += 4)
            {
                int v = Math.Max(Rgba[i], Math.Max(Rgba[i + 1], Rgba[i + 2]));
                if (v > mx) mx = v;
            }
            return mx;
        }

        /* Land/water mask at 1° per cell, in the image's own space. */
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
                    m[y * W + x] = (byte)((b > r + 20 && b > g + 10) ? 0 : 1);   // dominant blue = water
                }
            return mask = m;
        }

        /* Finds the longitude rotation that makes two maps of the same planet line up. It
           compares continent silhouettes, so it works even if the palettes have nothing to do
           with each other. */
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
                for (int y = 20; y < H - 20; y++)            // the polar caps don't tell anything apart
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

        /* The rotation is only accepted if it clearly stands out over the average: otherwise
           they aren't the same planet, or one of the two isn't equirectangular. */
        public static bool IsClearMatch(OffsetMatch m) => m.Pct >= 75 && m.Pct - m.Media >= 15;
    }
}
