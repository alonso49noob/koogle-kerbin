using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* What the instruments show of the selected vessel. Vectors are local: east, north, up. */
    public sealed class DatosNave
    {
        public string Nombre, Partida, Situacion;
        public double Alt, VelOrbita, VelSuperficie, VelVertical, Ap, Pe;
        public double[] Nariz, Arriba, Prograde;
        public double Pitch, Rumbo;
    }

    /* The navball, drawn pixel by pixel as a lit sphere: sky above the horizon, ground below,
       pitch circles every 10° and heading meridians every 45°, seen from the vessel along its
       nose with its top up. KSP's colours and markers; Moderno is the flat, quieter version the
       telemetry uses. */
    public static class Navball
    {
        public static void Dibujar(Graphics g, Rectangle r, DatosNave d, bool moderno)
        {
            int diam = Math.Min(r.Width, r.Height);
            if (diam < 8 || d?.Nariz == null) return;
            var F = Norm(d.Nariz);
            var Up = Norm(Restar(d.Arriba, Escalar(F, Actitud.Dot(d.Arriba, F))));
            if (Len(Up) < 0.5) Up = Norm(Restar(new[] { 0.0, 0, 1 }, Escalar(F, F[2])));
            var Rt = Cruz(F, Up);   // screen right = forward × up

            using var bmp = new Bitmap(diam, diam, PixelFormat.Format32bppArgb);
            var datos = bmp.LockBits(new Rectangle(0, 0, diam, diam), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var px = new int[diam * diam];
            double rad = diam / 2.0;
            Color cielo = moderno ? Color.FromArgb(52, 112, 170) : Color.FromArgb(40, 120, 196);
            Color suelo = moderno ? Color.FromArgb(120, 82, 52) : Color.FromArgb(150, 92, 38);
            for (int y = 0; y < diam; y++)
                for (int x = 0; x < diam; x++)
                {
                    double sx = (x + 0.5 - rad) / rad, sy = (rad - y - 0.5) / rad, rho2 = sx * sx + sy * sy;
                    if (rho2 > 1) continue;
                    double z = Math.Sqrt(1 - rho2);
                    // the point of the ball seen there, as a direction in the local frame
                    double dx = sx * Rt[0] + sy * Up[0] + z * F[0], dy = sx * Rt[1] + sy * Up[1] + z * F[1], dz = sx * Rt[2] + sy * Up[2] + z * F[2];
                    double el = Math.Asin(Math.Clamp(dz, -1, 1)) * Geo.R2D;
                    double az = Math.Atan2(dx, dy) * Geo.R2D;
                    var c = dz >= 0 ? cielo : suelo;
                    // one pixel, in degrees on the ball: thin lines stay a pixel wide everywhere
                    double paso = Geo.R2D / (rad * Math.Max(0.15, z));
                    double lin = 0;
                    double mEl = Math.Abs(el - Math.Round(el / 10) * 10);
                    if (Math.Abs(el) < paso * 1.2) lin = 1;                                   // horizon
                    else if (mEl < paso * 0.6 && Math.Abs(el) < 85) lin = Math.Abs(Math.Round(el / 10) * 10) % 30 == 0 ? 0.75 : 0.4;
                    double coslat = Math.Max(0.05, Math.Cos(el * Geo.D2R));
                    double mAz = Math.Abs(az - Math.Round(az / 45) * 45) * coslat;
                    if (mAz < paso * 0.6 && Math.Abs(el) < 80) lin = Math.Max(lin, Math.Abs(Math.Round(az / 45) * 45) % 90 == 0 ? 0.7 : 0.4);
                    if (lin > 0) c = Mezcla(c, moderno ? Color.FromArgb(225, 232, 240) : Color.White, lin);
                    // lit like a ball: darker toward the rim
                    double luz = moderno ? 0.78 + 0.22 * z : 0.5 + 0.5 * Math.Pow(z, 0.6);
                    px[y * diam + x] = Color.FromArgb(255, (int)(c.R * luz), (int)(c.G * luz), (int)(c.B * luz)).ToArgb();
                }
            Marshal.Copy(px, 0, datos.Scan0, px.Length);
            bmp.UnlockBits(datos);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var dest = new Rectangle(r.X + (r.Width - diam) / 2, r.Y + (r.Height - diam) / 2, diam, diam);
            using (var clip = new GraphicsPath())
            {
                clip.AddEllipse(dest);
                var old = g.Clip;
                g.SetClip(clip, CombineMode.Intersect);
                g.DrawImage(bmp, dest);
                g.Clip = old;
            }

            float cx = dest.X + diam / 2f, cy = dest.Y + diam / 2f;
            PointF? Pantalla(double[] v)
            {
                var u = Norm(v);
                double f = Actitud.Dot(u, F);
                if (f <= 0.05) return null;
                return new PointF(cx + (float)(Actitud.Dot(u, Rt) * rad), cy - (float)(Actitud.Dot(u, Up) * rad));
            }

            // cardinal points on the horizon, when they're on the near side
            using (var f = new Font(Theme.UIFamily, Math.Max(8f, diam / 15f), FontStyle.Bold, GraphicsUnit.Pixel))
                foreach (var (txt, az) in new[] { ("N", 0.0), ("E", 90.0), ("S", 180.0), ("W", 270.0) })
                {
                    double a = az * Geo.D2R;
                    var p = Pantalla(new[] { Math.Sin(a), Math.Cos(a), 0.0 });
                    if (p == null) continue;
                    var sz = g.MeasureString(txt, f);
                    using var br = new SolidBrush(Color.FromArgb(moderno ? 210 : 255, 255, 255, 255));
                    g.DrawString(txt, f, br, p.Value.X - sz.Width / 2, p.Value.Y - sz.Height - 1);
                }

            // prograde and retrograde (orbital)
            if (d.Prograde != null)
            {
                float m = Math.Max(5f, diam / 22f);
                var amarillo = moderno ? Color.FromArgb(255, 214, 102) : Color.FromArgb(230, 200, 40);
                if (Pantalla(d.Prograde) is PointF pg)
                {
                    using var pen = new Pen(amarillo, Math.Max(1.5f, diam / 90f));
                    g.DrawEllipse(pen, pg.X - m, pg.Y - m, 2 * m, 2 * m);
                    g.DrawLine(pen, pg.X, pg.Y - m, pg.X, pg.Y - m * 1.8f);
                    g.DrawLine(pen, pg.X - m, pg.Y, pg.X - m * 1.8f, pg.Y);
                    g.DrawLine(pen, pg.X + m, pg.Y, pg.X + m * 1.8f, pg.Y);
                    g.FillEllipse(new SolidBrush(amarillo), pg.X - 1.5f, pg.Y - 1.5f, 3, 3);
                }
                if (Pantalla(Escalar(d.Prograde, -1)) is PointF rg)
                {
                    using var pen = new Pen(amarillo, Math.Max(1.5f, diam / 90f));
                    g.DrawEllipse(pen, rg.X - m, rg.Y - m, 2 * m, 2 * m);
                    float k = m * 0.6f;
                    g.DrawLine(pen, rg.X - k, rg.Y - k, rg.X + k, rg.Y + k);
                    g.DrawLine(pen, rg.X - k, rg.Y + k, rg.X + k, rg.Y - k);
                    g.DrawLine(pen, rg.X, rg.Y - m, rg.X, rg.Y - m * 1.8f);
                }
            }

            // the vessel's fixed mark in the middle
            float w = diam / 7f;
            var naranja = moderno ? Color.FromArgb(240, 240, 240) : Color.FromArgb(255, 150, 20);
            using (var pen = new Pen(naranja, Math.Max(2f, diam / 60f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLines(pen, new[] { new PointF(cx - w, cy), new PointF(cx - w / 2.5f, cy), new PointF(cx, cy + w / 3), new PointF(cx + w / 2.5f, cy), new PointF(cx + w, cy) });
            g.FillEllipse(new SolidBrush(naranja), cx - 2, cy - 2, 4, 4);

            // bezel
            using (var pen = new Pen(moderno ? Color.FromArgb(90, Theme.Fg) : Color.FromArgb(70, 70, 76), moderno ? 1.5f : Math.Max(3f, diam / 28f)))
                g.DrawEllipse(pen, dest.X + 0.5f, dest.Y + 0.5f, diam - 1, diam - 1);
        }

        static Color Mezcla(Color a, Color b, double t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        static double Len(double[] v) => Math.Sqrt(Actitud.Dot(v, v));
        static double[] Norm(double[] v) { double l = Len(v); return l < 1e-12 ? new[] { 0.0, 0, 1 } : new[] { v[0] / l, v[1] / l, v[2] / l }; }
        static double[] Escalar(double[] v, double k) => new[] { v[0] * k, v[1] * k, v[2] * k };
        static double[] Restar(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        static double[] Cruz(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    }

    /* KSP's lower centre: altitude on top, the navball, and the speed below with its mode. */
    public sealed class NavballPanel : OverlayPanel
    {
        DatosNave d;

        public NavballPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(Theme.S(230), Theme.S(304));
            Visible = false;
        }

        public void Mostrar(DatosNave datos) { d = datos; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg2);
            if (d == null) return;
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            int pad = Theme.S(10);
            TextRenderer.DrawText(g, d.Nombre, Theme.Semibold, new Rectangle(pad, Theme.S(6), Width - 2 * pad, Theme.S(18)), Theme.Fg, flags);
            // altitude, as on KSP's top bar
            var caja = new Rectangle(Theme.S(40), Theme.S(28), Width - Theme.S(80), Theme.S(24));
            Theme.FillRound(g, Theme.Well, Theme.Line, caja, Theme.Sf(3));
            TextRenderer.DrawText(g, FmtAlt(d.Alt), Theme.MonoBold, caja, Color.FromArgb(120, 230, 120), flags);

            // heading over the ball, like KSP's
            TextRenderer.DrawText(g, Lang.F("{0}°", Geo.F(d.Rumbo, 0).PadLeft(3, '0')), Theme.MonoSmall,
                new Rectangle(0, Theme.S(56), Width, Theme.S(14)), Theme.FgDim, flags);
            int top = Theme.S(72), alto = Height - top - Theme.S(62);
            Navball.Dibujar(g, new Rectangle(pad, top, Width - 2 * pad, alto), d, moderno: false);

            var vel = new Rectangle(Theme.S(30), Height - Theme.S(56), Width - Theme.S(60), Theme.S(24));
            Theme.FillRound(g, Theme.Well, Theme.Line, vel, Theme.Sf(3));
            TextRenderer.DrawText(g, Lang.T("Órbita") + "  " + Geo.F(d.VelOrbita, 1) + " m/s", Theme.MonoBold, vel, Color.FromArgb(120, 230, 120), flags);
            TextRenderer.DrawText(g, Lang.F("vertical {0} m/s · superficie {1} m/s", Geo.F(d.VelVertical, 1), Geo.F(d.VelSuperficie, 0)),
                Theme.Tiny, new Rectangle(0, Height - Theme.S(28), Width, Theme.S(16)), Theme.FgDim, flags);
        }

        public static string FmtAlt(double m) => Math.Abs(m) >= 1e6 ? Geo.F(m / 1000, 0) + " km" : Math.Abs(m) >= 1e5 ? Geo.F(m / 1000, 1) + " km" : Geo.F(m, 0) + " m";
    }

    /* The look of a launch webcast's telemetry: speed and altitude in two ring gauges, the
       vessel's name and situation, and a small, quiet attitude ball. */
    public sealed class TelemetriaPanel : OverlayPanel
    {
        DatosNave d;
        double escalaAlt = 100000;

        public TelemetriaPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(Theme.S(560), Theme.S(124));
            Visible = false;
        }

        public void Mostrar(DatosNave datos)
        {
            if (datos != null && (d == null || d.Nombre != datos.Nombre))
                // the altitude ring fills up to the apoapsis, at least 100 km
                escalaAlt = Math.Max(100000, datos.Ap);
            d = datos;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg2);
            if (d == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Theme.S(16), h = Height;
            var izq = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

            // left: what it is
            int anchoTexto = Theme.S(170);
            TextRenderer.DrawText(g, (d.Partida ?? "").ToUpperInvariant(), Theme.Tiny, new Rectangle(pad, Theme.S(20), anchoTexto, Theme.S(16)), Theme.Accent, izq);
            using (var grande = new Font(Theme.Semibold.FontFamily, Theme.Sf(17), FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, d.Nombre, grande, new Rectangle(pad, Theme.S(38), anchoTexto, Theme.S(26)), Theme.Fg, izq);
            TextRenderer.DrawText(g, d.Situacion, Theme.Small, new Rectangle(pad, Theme.S(66), anchoTexto, Theme.S(18)), Theme.FgDim, izq);
            string orbita = double.IsNaN(d.Pe) ? Lang.F("altura máx. {0}", NavballPanel.FmtAlt(d.Ap))
                                               : Lang.F("Ap {0} · Pe {1}", NavballPanel.FmtAlt(d.Ap), NavballPanel.FmtAlt(d.Pe));
            TextRenderer.DrawText(g, orbita, Theme.MonoSmall,
                new Rectangle(pad, Theme.S(86), anchoTexto, Theme.S(16)), Theme.FgDim, izq);

            // two gauges: speed in km/h, like the webcasts, and altitude in km
            int dg = h - Theme.S(20);
            int x0 = pad + anchoTexto + Theme.S(8);
            Aro(g, new Rectangle(x0, Theme.S(10), dg, dg), d.VelOrbita * 3.6 / 30000, Geo.FmtIntEs((long)Math.Round(d.VelOrbita * 3.6)), Lang.T("VELOCIDAD"), "km/h");
            Aro(g, new Rectangle(x0 + dg + Theme.S(16), Theme.S(10), dg, dg), d.Alt / escalaAlt,
                Geo.F(d.Alt / 1000, d.Alt < 100000 ? 1 : 0), Lang.T("ALTITUD"), "km");

            // right: the quiet attitude ball and the numbers that go with it
            int db = h - Theme.S(44);
            var rb = new Rectangle(Width - pad - db, Theme.S(10), db, db);
            Navball.Dibujar(g, rb, d, moderno: true);
            var cen = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int ancho = Theme.S(150);
            var rt = new Rectangle(Math.Min(Width - Theme.S(6) - ancho, rb.X + rb.Width / 2 - ancho / 2), rb.Bottom + Theme.S(4), ancho, Theme.S(16));
            TextRenderer.DrawText(g, Lang.F("RUMBO {0}° · CABECEO {1}°", Geo.F(d.Rumbo, 0), Geo.F(d.Pitch, 0)), Theme.Tiny, rt, Theme.FgDim, cen);
        }

        void Aro(Graphics g, Rectangle r, double fraccion, string valor, string titulo, string unidad)
        {
            float grosor = Theme.Sf(5);
            var rr = new RectangleF(r.X + grosor, r.Y + grosor, r.Width - 2 * grosor, r.Height - 2 * grosor);
            using (var fondo = new Pen(Theme.Current.Track, grosor)) g.DrawArc(fondo, rr, 135, 270);
            float barrido = (float)(270 * Math.Clamp(fraccion, 0, 1));
            if (barrido > 0.5f) using (var pen = new Pen(Theme.Fg, grosor) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, rr, 135, barrido);
            var cen = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int cy = r.Y + r.Height / 2;
            using (var num = new Font(Theme.MonoFamily, Theme.Sf(valor.Length > 6 ? 14 : 17), FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, valor, num, new Rectangle(r.X, cy - Theme.S(12), r.Width, Theme.S(22)), Theme.Fg, cen);
            TextRenderer.DrawText(g, unidad, Theme.Tiny, new Rectangle(r.X, cy + Theme.S(9), r.Width, Theme.S(14)), Theme.FgDim, cen);
            TextRenderer.DrawText(g, titulo, Theme.Tiny, new Rectangle(r.X, r.Bottom - Theme.S(16), r.Width, Theme.S(14)), Theme.FgDim, cen);
        }
    }
}
