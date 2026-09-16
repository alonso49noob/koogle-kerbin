using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace KerbinMaps.UI
{
    /* Texto de una línea sin fondo propio, que mide su ancho. */
    public sealed class TextLabel : DarkControl
    {
        public Font TextFont = Theme.UI;
        public Color TextColor = Theme.Fg;
        public int MinWidth;
        public bool Center;

        public TextLabel(string text) { Text = text; }

        public int PreferredWidth =>
            Math.Max(MinWidth, TextRenderer.MeasureText(Text, TextFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Theme.S(2));

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(ParentBack);
            TextRenderer.DrawText(e.Graphics, Text, TextFont, ClientRectangle, TextColor,
                (Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) |
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /* Cabecera del panel lateral: «KerbinMaps» y el subtítulo. */
    public sealed class SidebarHeader : Control
    {
        public SidebarHeader()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(16 + 26 + 3 + 17 + 12);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg2);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            int x = Theme.S(16), y = Theme.S(14);
            int wk = TextRenderer.MeasureText("Koogle", Theme.Title, Size.Empty, flags).Width;
            TextRenderer.DrawText(g, "Koogle", Theme.Title, new Point(x, y), Theme.Fg, flags);
            TextRenderer.DrawText(g, "Kerbin", Theme.Title, new Point(x + wk, y), Theme.Accent, flags);
            TextRenderer.DrawText(g, Core.Lang.T("Visor de superficie · KSP stock"), Theme.Small, new Point(x, y + Theme.Title.Height + Theme.S(3)), Theme.FgDim, flags);
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

    /* Lectura de coordenadas bajo el cursor, arriba a la derecha. */
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
                w = Math.Max(w, TextRenderer.MeasureText(l, Theme.Mono).Width + TextRenderer.MeasureText(v, Theme.MonoBold).Width + Theme.S(32));
            var size = new Size(w, Rows.Count * lineH + Theme.S(14));
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
            int y = Theme.S(7), padX = Theme.S(10);
            var f = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            foreach (var (l, v) in Rows)
            {
                var r = new Rectangle(padX, y, Width - padX * 2, lineH);
                TextRenderer.DrawText(g, l, Theme.Mono, r, Theme.FgDim, f | TextFormatFlags.Left);
                TextRenderer.DrawText(g, v, Theme.MonoBold, r, Theme.Fg, f | TextFormatFlags.Right);
                y += lineH;
            }
            using var pen = new Pen(Theme.Line);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /* Globo de información anclado a un punto del mapa (el popup de Leaflet). */
    public sealed class MapPopup : OverlayPanel
    {
        readonly RichLabel body = new(RichMode.Popup) { HideWhenEmpty = false };
        readonly DarkButton close = new("×", ButtonVariant.Ghost, small: true);
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
            close.SetBounds(Width - Theme.S(24), Theme.S(3), Theme.S(21), Theme.S(21));
        }
    }
}
