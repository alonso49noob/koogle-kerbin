using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;

namespace KerbinMaps.UI
{
    /* Los colores y tipos de la versión web (css/app.css), para que las dos se vean
       iguales. Las medidas se dan en píxeles CSS y se escalan con los DPI. */
    public static class Theme
    {
        public static float Scale { get; private set; } = 1f;

        public static void Init(float dpi)
        {
            Scale = Math.Max(1f, dpi / 96f);
            string mono = new InstalledFontCollection().Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas";
            MonoFamily = mono;
            UI = Make("Segoe UI", 13);
            UISmall = Make("Segoe UI", 12.5f);
            Small = Make("Segoe UI", 11.5f);
            Tiny = Make("Segoe UI", 10.5f);
            Semibold = Make("Segoe UI Semibold", 12.5f);
            Header = Make("Segoe UI Semibold", 12);
            Title = Make("Segoe UI Semibold", 19);
            Icon = Make("Segoe UI Symbol", 15);
            Mono = Make(mono, 11.5f);
            MonoBold = Make(mono, 11.5f, FontStyle.Bold);
            MonoSmall = Make(mono, 10.5f);
            MonoInput = Make(mono, 12.5f);
        }

        public static int S(double px) => (int)Math.Round(px * Scale);
        public static float Sf(double px) => (float)(px * Scale);

        public static Font Make(string family, float px, FontStyle style = FontStyle.Regular) =>
            new Font(family, px * Scale, style, GraphicsUnit.Pixel);

        public static string MonoFamily = "Consolas";
        public static Font UI, UISmall, Small, Tiny, Semibold, Header, Title, Icon, Mono, MonoBold, MonoSmall, MonoInput;

        public static Color Hex(string hex)
        {
            int v = Convert.ToInt32(hex.TrimStart('#'), 16);
            return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        public static readonly Color Bg = Hex("#0b1017");
        public static readonly Color Bg2 = Hex("#121a25");
        public static readonly Color Bg3 = Hex("#1a2432");
        public static readonly Color Line = Hex("#263444");
        public static readonly Color Fg = Hex("#dbe6f2");
        public static readonly Color FgDim = Hex("#8a9bb0");
        public static readonly Color Accent = Hex("#4ea3ff");
        public static readonly Color Accent2 = Hex("#7ee787");
        public static readonly Color Warn = Hex("#ffb454");
        public static readonly Color Danger = Hex("#ff6b6b");
        public static readonly Color MapBg = Hex("#070b11");
        public static readonly Color OnAccent = Hex("#04121f");

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color fill, Color border, RectangleF r, float radius, float borderW = 1)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rr = new RectangleF(r.X + borderW / 2, r.Y + borderW / 2, r.Width - borderW, r.Height - borderW);
            using (var path = RoundRect(rr, radius))
            {
                if (fill.A > 0) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border.A > 0 && borderW > 0) using (var pen = new Pen(border, borderW)) g.DrawPath(pen, path);
            }
            g.SmoothingMode = old;
        }
    }
}
