using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;

namespace KerbinMaps.UI
{
    /* An interface palette. The role names are the usual ones: Bg is the deepest background,
       Bg2 the panel surface, Bg3 what rises above it (buttons, rows on hover), Well the
       background of fields and readouts. */
    public sealed class Palette
    {
        public string Id, Name;
        public bool Dark;
        public Color Bg, Bg2, Bg3, Well, Btn, Line, Fg, FgDim, Accent, Accent2, Warn, Danger, MapBg, OnAccent;

        // derived: no need to write them theme by theme
        public Color Hover => Theme.Mix(Btn, Fg, Dark ? 0.07f : 0.045f);
        public Color LineStrong => Theme.Mix(Line, Fg, Dark ? 0.18f : 0.22f);
        public Color Track => Theme.Mix(Line, Fg, Dark ? 0.1f : 0.06f);
        public Color Thumb => Dark ? Theme.Mix(Bg3, Fg, 0.22f) : Color.White;
        public Color Selected => Theme.Mix(Bg2, Accent, Dark ? 0.2f : 0.13f);
    }

    /* Interface colors, fonts and sizes. Sizes are given in CSS pixels and scaled with the DPI.
       Colors come from the current palette and change live. */
    public static class Theme
    {
        public static float Scale { get; private set; } = 1f;

        public static void Init(float dpi)
        {
            Scale = Math.Max(1f, dpi / 96f);
            var families = new InstalledFontCollection().Families.Select(f => f.Name).ToHashSet();
            string mono = families.Contains("Cascadia Mono") ? "Cascadia Mono" : "Consolas";
            // Segoe UI Variable is Windows 11's; on Windows 10 the usual Segoe UI remains
            bool variable = families.Contains("Segoe UI Variable Text") && families.Contains("Segoe UI Variable Text Semibold");
            string ui = variable ? "Segoe UI Variable Text" : "Segoe UI";
            string semi = variable ? "Segoe UI Variable Text Semibold" : "Segoe UI Semibold";
            IconFamily = families.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            UIFamily = ui;
            MonoFamily = mono;
            UI = Make(ui, 13);
            UISmall = Make(ui, 12.5f);
            Small = Make(ui, 11.5f);
            Tiny = Make(ui, 10.5f);
            Semibold = Make(semi, 12.5f);
            Header = Make(semi, 13);
            Title = Make(semi, 15);
            Icon = Make(IconFamily, 14);
            IconMid = Make(IconFamily, 12);
            IconSmall = Make(IconFamily, 10);
            Mono = Make(mono, 11.5f);
            MonoBold = Make(mono, 11.5f, FontStyle.Bold);
            MonoSmall = Make(mono, 10.5f);
            MonoInput = Make(mono, 12.5f);
        }

        public static int S(double px) => (int)Math.Round(px * Scale);
        public static float Sf(double px) => (float)(px * Scale);

        public static Font Make(string family, float px, FontStyle style = FontStyle.Regular) =>
            new Font(family, px * Scale, style, GraphicsUnit.Pixel);

        public static string MonoFamily = "Consolas", UIFamily = "Segoe UI", IconFamily = "Segoe MDL2 Assets";
        public static Font UI, UISmall, Small, Tiny, Semibold, Header, Title, Icon, IconMid, IconSmall, Mono, MonoBold, MonoSmall, MonoInput;

        /* Segoe Fluent Icons / MDL2 Assets glyphs (both share code points). */
        public static class Glyph
        {
            public const string Menu = "", Search = "", ChevronDown = "", ChevronRight = "",
                                Close = "", Check = "", Theme = "", Play = "", Pause = "",
                                Rewind = "", Forward = "";
        }

        /* Corner radius: small, and the same everywhere. */
        public const float Radius = 4;

        public static Color Hex(string hex)
        {
            int v = Convert.ToInt32(hex.TrimStart('#'), 16);
            return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

        static Palette P(string id, string name, bool dark, string bg, string bg2, string bg3, string well, string btn, string line,
                         string fg, string fgDim, string accent, string accent2, string warn, string danger, string mapBg, string onAccent) => new()
        {
            Id = id, Name = name, Dark = dark,
            Bg = Hex(bg), Bg2 = Hex(bg2), Bg3 = Hex(bg3), Well = Hex(well), Btn = Hex(btn), Line = Hex(line),
            Fg = Hex(fg), FgDim = Hex(fgDim), Accent = Hex(accent), Accent2 = Hex(accent2), Warn = Hex(warn), Danger = Hex(danger),
            MapBg = Hex(mapBg), OnAccent = Hex(onAccent)
        };

        //                                     Bg         Bg2        Bg3        Well       Btn        Line       Fg         FgDim      Accent     Accent2    Warn       Danger     MapBg      OnAccent
        public static readonly IReadOnlyList<Palette> Palettes = new[]
        {
            P("clasico",    "Clásico",         true,  "#0b1017", "#121a25", "#1a2432", "#0d131c", "#1a2432", "#263444", "#dbe6f2", "#8a9bb0", "#4ea3ff", "#7ee787", "#ffb454", "#ff6b6b", "#070b11", "#04121f"),
            P("claro",      "Claro",           false, "#e8ecf0", "#f6f7f9", "#eaedf1", "#ffffff", "#ffffff", "#d3d9e0", "#1c2530", "#5d6977", "#1a66d2", "#1d7f3a", "#a65f00", "#c2362f", "#d9dfe5", "#ffffff"),
            P("sepia",      "Sepia",           false, "#e6dabd", "#f3ead4", "#e9dec2", "#fbf6e9", "#fbf6e9", "#d5c49d", "#3a2e20", "#76654f", "#97552a", "#4d7a2a", "#9c5d00", "#ad3f2c", "#ddcfac", "#fff8ec"),
            P("oscuro",     "Oscuro",          true,  "#181818", "#202020", "#2b2b2b", "#191919", "#2d2d2d", "#363636", "#e4e4e4", "#9a9a9a", "#62b0f0", "#6cc66a", "#eeb04a", "#ff7a6e", "#121212", "#06192a"),
            P("profundo",   "Deep Dark",       true,  "#000000", "#0a0a0a", "#151515", "#050505", "#141414", "#222222", "#d2d2d2", "#7e7e7e", "#7aa2f7", "#73c991", "#e0af68", "#f7768e", "#000000", "#020611"),
            P("solarizado", "Solarized Dark",  true,  "#00212b", "#002b36", "#073642", "#00222b", "#073642", "#0d4654", "#93a1a1", "#6a8389", "#268bd2", "#859900", "#b58900", "#dc322f", "#001d25", "#fdf6e3"),
        };

        public static Palette Current { get; private set; } = Palettes[0];

        public static Palette Find(string id) => Palettes.FirstOrDefault(p => p.Id == id) ?? Palettes[0];

        /* Switches the palette; the caller repaints (MainForm.AplicarTema). */
        public static void Use(string id) => Current = Find(id);

        public static Color Bg => Current.Bg;
        public static Color Bg2 => Current.Bg2;
        public static Color Bg3 => Current.Bg3;
        public static Color Well => Current.Well;
        public static Color Btn => Current.Btn;
        public static Color Line => Current.Line;
        public static Color Fg => Current.Fg;
        public static Color FgDim => Current.FgDim;
        public static Color Accent => Current.Accent;
        public static Color Accent2 => Current.Accent2;
        public static Color Warn => Current.Warn;
        public static Color Danger => Current.Danger;
        public static Color MapBg => Current.MapBg;
        public static Color OnAccent => Current.OnAccent;
        public static bool Dark => Current.Dark;

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

        /* An icon glyph centered in a rectangle. */
        public static void DrawGlyph(Graphics g, string glyph, Font font, Rectangle r, Color c) =>
            System.Windows.Forms.TextRenderer.DrawText(g, glyph, font, r, c,
                System.Windows.Forms.TextFormatFlags.HorizontalCenter | System.Windows.Forms.TextFormatFlags.VerticalCenter |
                System.Windows.Forms.TextFormatFlags.SingleLine | System.Windows.Forms.TextFormatFlags.NoPadding);
    }
}
