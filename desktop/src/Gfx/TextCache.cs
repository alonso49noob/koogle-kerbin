using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;

namespace KerbinMaps.Gfx
{
    public sealed class TextTex
    {
        public Texture Tex;
        public int W, H, Pad;
        /* Tamaño del texto en sí, sin el margen de la textura. */
        public int TextW, TextH;
        public long Used;
    }

    public readonly record struct TextStyle(string Family, float Px, bool Bold, int Argb, bool Shadow);

    /* Rótulos del mapa y del globo: cada texto se rasteriza una vez con GDI+ y se
       sube como textura. Los nombres cambian poco, así que la caché casi siempre
       acierta; si crece demasiado se tiran los menos usados. */
    public sealed class TextCache : IDisposable
    {
        readonly Dictionary<(string, TextStyle), TextTex> map = new();
        readonly Dictionary<TextStyle, Font> fonts = new();
        readonly Bitmap probe = new Bitmap(1, 1);
        readonly Graphics probeG;
        long tick;

        public TextCache()
        {
            probeG = Graphics.FromImage(probe);
            probeG.TextRenderingHint = TextRenderingHint.AntiAlias;
        }

        Font FontFor(TextStyle st)
        {
            var key = st with { Argb = 0, Shadow = false };
            if (!fonts.TryGetValue(key, out var f))
            {
                f = new Font(st.Family, st.Px, st.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        public SizeF Measure(string text, TextStyle st)
        {
            if (string.IsNullOrEmpty(text)) return SizeF.Empty;
            var font = FontFor(st);
            var s = probeG.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            return new SizeF(s.Width, font.GetHeight());
        }

        public TextTex Get(string text, TextStyle st)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var key = (text, st);
            if (map.TryGetValue(key, out var t)) { t.Used = ++tick; return t; }
            if (map.Count > 900) Purge();
            t = Render(text, st);
            t.Used = ++tick;
            map[key] = t;
            return t;
        }

        unsafe TextTex Render(string text, TextStyle st)
        {
            var font = FontFor(st);
            var size = probeG.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            int pad = st.Shadow ? 4 : 2;
            int tw = (int)Math.Ceiling(size.Width) + 1, th = (int)Math.Ceiling(font.GetHeight());
            int w = tw + pad * 2, h = th + pad * 2;

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                if (st.Shadow)
                {
                    /* text-shadow: 0 1px 3px negro casi opaco. Un desenfoque de verdad no
                       compensa aquí: unas pocas copias desplazadas dan el mismo halo. */
                    using var sb = new SolidBrush(Color.FromArgb(62, 0, 0, 0));
                    for (int dy = -1; dy <= 3; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            if (dx * dx + (dy - 1) * (dy - 1) <= 5)
                                g.DrawString(text, font, sb, pad + dx, pad + dy, StringFormat.GenericTypographic);
                }
                using var brush = new SolidBrush(Color.FromArgb(st.Argb));
                g.DrawString(text, font, brush, pad, pad, StringFormat.GenericTypographic);
            }

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var rgba = new byte[w * h * 4];
            try
            {
                byte* src = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = src + y * data.Stride;
                    int o = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        rgba[o] = row[x * 4 + 2];
                        rgba[o + 1] = row[x * 4 + 1];
                        rgba[o + 2] = row[x * 4];
                        rgba[o + 3] = row[x * 4 + 3];
                        o += 4;
                    }
                }
            }
            finally { bmp.UnlockBits(data); }

            return new TextTex
            {
                Tex = Texture.FromRgba(rgba, w, h, TexFilter.Linear, false),
                W = w, H = h, Pad = pad, TextW = tw, TextH = th
            };
        }

        void Purge()
        {
            foreach (var kv in map.OrderBy(k => k.Value.Used).Take(map.Count / 2).ToList())
            {
                kv.Value.Tex.Dispose();
                map.Remove(kv.Key);
            }
        }

        public void Dispose()
        {
            foreach (var t in map.Values) t.Tex.Dispose();
            map.Clear();
            foreach (var f in fonts.Values) f.Dispose();
            probeG.Dispose();
            probe.Dispose();
        }
    }
}
