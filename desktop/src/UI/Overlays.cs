using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace KerbinMaps.UI
{
    /* Single-line text with no background of its own, that measures its width. */
    public sealed class TextLabel : DarkControl
    {
        public Font TextFont = Theme.UI;
        public bool Dim;
        public int MinWidth;
        public bool Center;

        public TextLabel(string text) { Text = text; }

        public int PreferredWidth =>
            Math.Max(MinWidth, TextRenderer.MeasureText(Text, TextFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Theme.S(2));

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(ParentBack);
            TextRenderer.DrawText(e.Graphics, Text, TextFont, ClientRectangle, Dim ? Theme.FgDim : Theme.Fg,
                (Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) |
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /* Sidebar header: the logo and the name, at the height of the toolbar so both form a single
       band at the top. */
    public sealed class SidebarHeader : Control
    {
        static Image logo;
        static Image Logo
        {
            get
            {
                if (logo != null) return logo;
                try
                {
                    using var st = typeof(SidebarHeader).Assembly.GetManifestResourceStream("app.png");
                    if (st != null) logo = Image.FromStream(st);
                }
                catch { }
                return logo;
            }
        }

        public SidebarHeader()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Toolbar.BarHeight;
            new ToolTip { InitialDelay = 500 }.SetToolTip(this, Core.Lang.T("Visor de superficie · KSP stock"));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg2);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            int x = Theme.S(14), s = Theme.S(22);
            if (Logo != null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(Logo, new Rectangle(x, (Height - s) / 2, s, s));
                x += s + Theme.S(9);
            }
            TextRenderer.DrawText(g, "Koogle Kerbin", Theme.Title, new Rectangle(x, 0, Width - x, Height), Theme.Fg, flags);
            int wt = TextRenderer.MeasureText(g, "Koogle Kerbin", Theme.Title, Size.Empty, flags).Width;
            var v = Core.Updater.Current;
            TextRenderer.DrawText(g, v.Major + "." + v.Minor + "." + Math.Max(0, v.Build), Theme.Tiny,
                new Rectangle(x + wt + Theme.S(8), Theme.S(1), Width, Height), Theme.FgDim, flags);
            using var pen = new Pen(Theme.Line);
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }

    public sealed class SidebarPanel : Panel
    {
        public SidebarPanel()
        {
            SetStyle(ControlStyles.ResizeRedraw, true);
            Padding = new Padding(0, 0, 1, 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        }
    }

    /* The view's top band: sidebar button, views, search and theme. It's a fixed bar, not
       buttons floating over the map. */
    public sealed class Toolbar : Panel
    {
        public static int BarHeight => Theme.S(48);

        public Toolbar()
        {
            DoubleBuffered = true;
            BackColor = Theme.Bg2;
            Height = BarHeight;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }

    /* Group of buttons joined in a frame (the views). */
    public sealed class SegmentGroup : Panel
    {
        /* The group's well, deeper than the bar: the chosen option rises out of it. */
        public override Color BackColor { get => Theme.Dark ? Theme.Bg : Theme.Bg3; set { } }

        public SegmentGroup()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg2);
            Theme.FillRound(g, BackColor, Theme.Line, new RectangleF(0, 0, Width, Height), Theme.Sf(Theme.Radius + 1));
        }
    }

    /* Coordinate readout under the cursor, top right. */
    public sealed class HudPanel : OverlayPanel
    {
        public readonly List<(string label, string value)> Rows = new();

        public HudPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public void SetRows(params (string, string)[] rows)
        {
            for (int i = 0; i < rows.Length; i++) rows[i] = (Core.Lang.T(rows[i].Item1), rows[i].Item2);
            bool same = rows.Length == Rows.Count;
            for (int i = 0; same && i < rows.Length; i++) same = rows[i] == Rows[i];
            if (same) return;
            Rows.Clear();
            Rows.AddRange(rows);
            int lineH = (int)(Theme.Mono.Height * 1.45);
            int w = Theme.S(150);
            foreach (var (l, v) in Rows)
                w = Math.Max(w, TextRenderer.MeasureText(l, Theme.Small).Width + TextRenderer.MeasureText(v, Theme.Mono).Width + Theme.S(36));
            var size = new Size(w, Rows.Count * lineH + Theme.S(16));
            if (Size != size)
            {
                int right = Right;
                Size = size;
                if (Parent != null) Left = right - Width;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg2);
            int lineH = (int)(Theme.Mono.Height * 1.45);
            int y = Theme.S(8), padX = Theme.S(12);
            var f = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            foreach (var (l, v) in Rows)
            {
                var r = new Rectangle(padX, y, Width - padX * 2, lineH);
                TextRenderer.DrawText(g, l, Theme.Small, r, Theme.FgDim, f | TextFormatFlags.Left);
                TextRenderer.DrawText(g, v, Theme.Mono, r, Theme.Fg, f | TextFormatFlags.Right);
                y += lineH;
            }
            using var pen = new Pen(Theme.Current.LineStrong);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /* Info bubble anchored to a point on the map (Leaflet's popup). */
    public sealed class MapPopup : OverlayPanel
    {
        readonly RichLabel body = new(RichMode.Popup) { HideWhenEmpty = false };
        readonly DarkButton close = new(Theme.Glyph.Close, ButtonVariant.Ghost, small: true) { IconFont = Theme.IconSmall };
        readonly List<DarkButton> buttons = new();
        public Core.LatLon AnchorLatLon { get; private set; }

        public MapPopup()
        {
            Controls.Add(body);
            Controls.Add(close);
            close.Click += (s, e) => Hide();
            Visible = false;
        }

        public void Open(Core.LatLon anchor, string markup, params (string label, Action<DarkButton> act)[] actions)
        {
            AnchorLatLon = anchor;
            body.SetText(markup);
            foreach (var b in buttons) { Controls.Remove(b); b.Dispose(); }
            buttons.Clear();
            foreach (var (label, act) in actions)
            {
                var b = new DarkButton(label, small: true);
                b.Click += (s, e) => act(b);
                buttons.Add(b);
                Controls.Add(b);
            }
            Relayout();
            Visible = true;
            BringToFront();
        }

        public void Relayout()
        {
            int padX = Theme.S(14), padY = Theme.S(10);
            int textW = Math.Min(Theme.S(300), Math.Max(Theme.S(120), body.NaturalWidth() + Theme.S(4)));
            int btnW = 0;
            foreach (var b in buttons) btnW += b.PreferredWidth + Theme.S(6);
            textW = Math.Max(textW, Math.Min(Theme.S(300), btnW));
            int bh = body.HeightFor(textW);
            body.SetBounds(padX, padY, textW, bh);
            int y = padY + bh;
            if (buttons.Count > 0)
            {
                y += Theme.S(8);
                int x = padX, rowH = buttons[0].PreferredHeight;
                foreach (var b in buttons)
                {
                    int w = b.PreferredWidth;
                    if (x > padX && x + w > padX + textW) { x = padX; y += rowH + Theme.S(6); }
                    b.SetBounds(x, y, w, rowH);
                    x += w + Theme.S(6);
                }
                y += rowH;
            }
            Size = new Size(textW + padX * 2 + Theme.S(14), y + padY);
            close.SetBounds(Width - Theme.S(28), Theme.S(6), Theme.S(22), Theme.S(22));
        }
    }
}
