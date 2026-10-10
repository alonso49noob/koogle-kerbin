using System;
using System.Drawing;
using System.Windows.Forms;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* El tema de la interfaz: el menú del botón de la barra, las tarjetas de la sección
       «Apariencia» y el cambio en caliente, sin reiniciar el visor. */
    public sealed partial class MainForm
    {
        ThemePicker themePicker;

        void BuildAparienciaSection()
        {
            var s = AddSection("Apariencia", false);
            themePicker = new ThemePicker();
            themePicker.Picked += AplicarTema;
            s.Add(Field("Tema de la interfaz", themePicker));
            s.Add(Hint("También desde el botón de la derecha de la barra de arriba. El mapa, el globo y el cielo " +
                       "no cambian: solo los paneles, los botones y los rótulos."));
            s.Estado = Lang.T(Theme.Current.Name);
            temaSection = s;
        }

        Section temaSection;

        ContextMenuStrip CrearMenuTema()
        {
            var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, ShowCheckMargin = true, Font = Theme.UI, BackColor = Theme.Bg2 };
            foreach (var p in Theme.Palettes)
            {
                string id = p.Id;
                var it = new ToolStripMenuItem(Lang.T(p.Name)) { Checked = p == Theme.Current, Padding = new Padding(0, Theme.S(3), Theme.S(12), Theme.S(3)) };
                it.Click += (s, e) => AplicarTema(id);
                menu.Items.Add(it);
            }
            return menu;
        }

        void MenuTema()
        {
            var menu = CrearMenuTema();
            menu.Closed += (s, a) => BeginInvoke(new Action(menu.Dispose));
            // alineado por la derecha con el botón, que está en el borde de la ventana
            menu.Show(btnTema, new Point(btnTema.Width - menu.PreferredSize.Width, btnTema.Height + Theme.S(4)));
        }

        void AplicarTema(string id)
        {
            var antes = Theme.Current;
            Theme.Use(id);
            var ahora = Theme.Current;
            if (antes == ahora) return;
            state.Theme = ahora.Id;
            SaveSettings();

            SuspendLayout();
            Recolorear(this, antes, ahora);
            ResumeLayout();
            TintTitleBar(Handle);
            if (temaSection != null) temaSection.Estado = Lang.T(ahora.Name);
            Invalidate(true);
            RequestRender();
        }

        /* Los controles guardan su BackColor; se pasa cada color de la paleta vieja a su
           papel en la nueva. Primero los hijos: uno que hereda el color del padre lo lee
           todavía viejo y se queda con el nuevo fijado. */
        static void Recolorear(Control c, Palette a, Palette b)
        {
            foreach (Control h in c.Controls) Recolorear(h, a, b);
            if (c is SegmentGroup) { }
            else
            {
                var bc = Equivalente(c.BackColor, a, b);
                if (bc.HasValue) c.BackColor = bc.Value;
            }
            var fc = Equivalente(c.ForeColor, a, b);
            if (fc.HasValue) c.ForeColor = fc.Value;
            if (c is DarkTextBox t) t.Retint();
        }

        static Color? Equivalente(Color c, Palette a, Palette b)
        {
            int v = c.ToArgb();
            if (c.A == 0) return null;
            if (v == a.Bg2.ToArgb()) return b.Bg2;
            if (v == a.Bg.ToArgb()) return b.Bg;
            if (v == a.Bg3.ToArgb()) return b.Bg3;
            if (v == a.Well.ToArgb()) return b.Well;
            if (v == a.Btn.ToArgb()) return b.Btn;
            if (v == a.MapBg.ToArgb()) return b.MapBg;
            if (v == a.Line.ToArgb()) return b.Line;
            if (v == a.Fg.ToArgb()) return b.Fg;
            if (v == a.FgDim.ToArgb()) return b.FgDim;
            return null;
        }
    }

    /* Las paletas en tarjetas: una miniatura de la ventana con sus colores y el nombre. */
    public sealed class ThemePicker : DarkControl, IHeightForWidth
    {
        public event Action<string> Picked;
        int hoverIndex = -1;

        const int Cols = 3;
        int Gap => Theme.S(8);
        int CardH => Theme.S(66);

        public ThemePicker() { Cursor = Cursors.Hand; }

        public int HeightFor(int width)
        {
            int rows = (Theme.Palettes.Count + Cols - 1) / Cols;
            return rows * CardH + (rows - 1) * Gap;
        }

        Rectangle Card(int i)
        {
            int cw = (Width - Gap * (Cols - 1)) / Cols;
            return new Rectangle((i % Cols) * (cw + Gap), (i / Cols) * (CardH + Gap), cw, CardH);
        }

        int At(Point p)
        {
            for (int i = 0; i < Theme.Palettes.Count; i++) if (Card(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = At(e.Location);
            if (h != hoverIndex) { hoverIndex = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int i = At(e.Location);
            if (e.Button == MouseButtons.Left && i >= 0) Picked?.Invoke(Theme.Palettes[i].Id);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            var cur = Theme.Current;
            for (int i = 0; i < Theme.Palettes.Count; i++)
            {
                var p = Theme.Palettes[i];
                var r = Card(i);
                bool sel = p == cur;
                float rad = Theme.Sf(Theme.Radius + 1);

                // la miniatura: panel lateral, barra de arriba y la vista con un panel flotante
                var mini = new Rectangle(r.X + Theme.S(5), r.Y + Theme.S(5), r.Width - Theme.S(10), Theme.S(36));
                using (var clip = Theme.RoundRect(mini, Theme.Sf(3)))
                {
                    var old = g.Clip;
                    g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
                    Fill(g, p.MapBg, mini);
                    int side = mini.Width * 36 / 100, bar = Theme.S(8);
                    Fill(g, p.Bg2, new Rectangle(mini.X, mini.Y, side, mini.Height));
                    Fill(g, p.Bg2, new Rectangle(mini.X + side, mini.Y, mini.Width - side, bar));
                    Fill(g, p.Line, new Rectangle(mini.X + side, mini.Y, 1, mini.Height));
                    Fill(g, p.Line, new Rectangle(mini.X + side, mini.Y + bar, mini.Width - side, 1));
                    int lx = mini.X + Theme.S(4), lw = side - Theme.S(8);
                    Fill(g, p.Fg, new Rectangle(lx, mini.Y + Theme.S(12), lw * 3 / 4, Theme.S(2)));
                    Fill(g, p.FgDim, new Rectangle(lx, mini.Y + Theme.S(18), lw, Theme.S(2)));
                    Fill(g, p.FgDim, new Rectangle(lx, mini.Y + Theme.S(23), lw * 2 / 3, Theme.S(2)));
                    Fill(g, p.Accent, new Rectangle(lx, mini.Y + Theme.S(29), lw / 2, Theme.S(3)));
                    var hud = new Rectangle(mini.Right - Theme.S(20), mini.Y + bar + Theme.S(5), Theme.S(15), Theme.S(11));
                    Fill(g, p.Bg2, hud);
                    using (var pen = new Pen(p.Line)) g.DrawRectangle(pen, hud);
                    g.Clip = old;
                }

                Theme.FillRound(g, Color.Transparent, sel ? cur.Accent : hoverIndex == i ? cur.LineStrong : cur.Line, r, rad, sel ? Theme.Sf(2) : 1);
                using (var pen = new Pen(Color.FromArgb(60, cur.Fg)))
                    g.DrawRectangle(pen, mini.X, mini.Y, mini.Width - 1, mini.Height - 1);

                var nameR = new Rectangle(r.X + Theme.S(6), mini.Bottom + Theme.S(2), r.Width - Theme.S(12), r.Bottom - mini.Bottom - Theme.S(3));
                TextRenderer.DrawText(g, Lang.T(p.Name), sel ? Theme.Semibold : Theme.Small, nameR, sel ? cur.Fg : cur.FgDim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        static void Fill(Graphics g, Color c, Rectangle r)
        {
            using var b = new SolidBrush(c);
            g.FillRectangle(b, r);
        }
    }
}
