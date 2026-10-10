using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* Icon of a body for lists: a disc of its color lit from the upper left, with a halo the
       color of its air if it has an atmosphere and a corona if it's the star. Its map isn't
       used because the viewer only ships Kerbin textures; the color is the same one the globe
       uses when there's no image (BodyDef.Tint), so the icon and the planet that shows up when
       you pick it look alike. */
    public static class BodyIcon
    {
        static readonly Dictionary<string, Bitmap> cache = new();

        /* When the system changes (Kopernicus) the colors are different. */
        public static void Clear()
        {
            foreach (var b in cache.Values) b.Dispose();
            cache.Clear();
        }

        public static Image Get(string name, int size)
        {
            var body = SolarSystem.Find(name);
            if (body == null || size < 6) return null;
            string key = name + "|" + size;
            if (cache.TryGetValue(key, out var hit)) return hit;
            var bmp = Draw(body, size);
            cache[key] = bmp;
            return bmp;
        }

        static Color Rgb(float[] t, double k, int alpha = 255) => Color.FromArgb(alpha,
            (int)Math.Clamp(t[0] * 255 * k, 0, 255), (int)Math.Clamp(t[1] * 255 * k, 0, 255), (int)Math.Clamp(t[2] * 255 * k, 0, 255));

        /* The sky color: the body's Rayleigh normalized to its strongest component. */
        static Color Air(double[] beta, int alpha)
        {
            double m = Math.Max(beta[0], Math.Max(beta[1], beta[2]));
            if (m <= 0) return Color.FromArgb(0, 0, 0, 0);
            return Color.FromArgb(alpha, (int)(beta[0] / m * 255), (int)(beta[1] / m * 255), (int)(beta[2] / m * 255));
        }

        static Bitmap Draw(BodyDef b, int size)
        {
            // drawn at triple size and scaled down: at 16 px the rim of a circle with only
            // GDI antialiasing is left with steps that stand out a lot in a list
            const int ss = 3;
            int n = size * ss;
            var big = new Bitmap(n, n);
            using (var g = Graphics.FromImage(big))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float margin = b.IsStar ? n * 0.22f : n * 0.11f;    // the star leaves room for the corona
                var disc = new RectangleF(margin, margin, n - 2 * margin, n - 2 * margin);
                float r = disc.Width / 2, cx = disc.X + r, cy = disc.Y + r;

                if (b.IsStar)
                {
                    // corona: three rings, each fainter
                    for (int i = 3; i >= 1; i--)
                    {
                        float k = 1 + i * 0.28f;
                        var halo = new RectangleF(cx - r * k, cy - r * k, r * 2 * k, r * 2 * k);
                        using var hb = new SolidBrush(Rgb(b.Tint, 1.0, 26 / i));
                        g.FillEllipse(hb, halo);
                    }
                }
                else if (b.HasAir)
                {
                    // the air, as a faint glow around it: an opaque ring reads as
                    // a selection border, not as an atmosphere
                    for (int i = 3; i >= 1; i--)
                    {
                        using var pen = new Pen(Air(b.AirColor, 26 + 14 * (3 - i)), n * 0.03f * i);
                        g.DrawEllipse(pen, RectangleF.Inflate(disc, n * 0.012f * i, n * 0.012f * i));
                    }
                }

                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(disc);
                    using var pg = new PathGradientBrush(path)
                    {
                        CenterPoint = new PointF(cx - r * 0.38f, cy - r * 0.38f),
                        CenterColor = b.IsStar ? Color.FromArgb(255, 255, 252, 226) : Rgb(b.Tint, 1.45),
                        SurroundColors = new[] { b.IsStar ? Rgb(b.Tint, 0.9) : Rgb(b.Tint, 0.34) },
                    };
                    g.FillPath(pg, path);
                }

                if (!b.IsStar)
                {
                    using var edge = new Pen(Rgb(b.Tint, 0.22, 190), n * 0.035f);
                    g.DrawEllipse(edge, disc);
                }
            }

            var small = new Bitmap(size, size);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(big, new Rectangle(0, 0, size, size));
            }
            big.Dispose();
            return small;
        }
    }
}
