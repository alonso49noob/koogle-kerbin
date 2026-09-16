using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace KerbinMaps.UI
{
    /* Visibilidad pedida para un control dentro de una pila. Visible no sirve para
       maquetar: devuelve false mientras cualquier antecesor esté oculto (una sección
       plegada, la ventana aún sin mostrar), y la pila mediría todo a cero. */
    public static class Vis
    {
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> hidden = new();

        public static bool Shown(Control c) => !hidden.TryGetValue(c, out _);

        public static void Set(Control c, bool on)
        {
            if (on) hidden.Remove(c); else hidden.AddOrUpdate(c, null);
            c.Visible = on;
            c.Parent?.PerformLayout();
        }
    }

    /* Apila sus hijos en vertical con un hueco fijo, como los .panel de la web, y
       ajusta su propia altura al contenido. */
    public class StackPanel : Panel, IHeightForWidth
    {
        public int Gap;
        bool laying;

        public StackPanel(int gap = 10)
        {
            Gap = Theme.S(gap);
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public static int ChildHeight(Control c, int w) => c is IHeightForWidth hw ? hw.HeightFor(w) : c.Height;

        public int HeightFor(int width)
        {
            int w = width - Padding.Horizontal, y = Padding.Top, n = 0;
            foreach (Control c in Controls)
            {
                if (!Vis.Shown(c)) continue;
                y += ChildHeight(c, w) + Gap; n++;
            }
            return (n > 0 ? y - Gap : y) + Padding.Bottom;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            if (laying) return;
            laying = true;
            try
            {
                int w = Width - Padding.Horizontal, y = Padding.Top, n = 0;
                foreach (Control c in Controls)
                {
                    if (!Vis.Shown(c)) continue;
                    int h = ChildHeight(c, w);
                    c.SetBounds(Padding.Left, y, w, h);
                    y += h + Gap; n++;
                }
                int total = (n > 0 ? y - Gap : y) + Padding.Bottom;
                if (Height != total) Height = total;
            }
            finally { laying = false; }
            base.OnLayout(e);
        }
    }

    /* Dos columnas del mismo ancho (.row2). */
    public sealed class Row2 : Control, IHeightForWidth
    {
        public Row2(Control a, Control b)
        {
            Controls.Add(a); Controls.Add(b);
        }

        public int HeightFor(int width)
        {
            int cw = (width - Theme.S(10)) / 2;
            return Controls.Cast<Control>().Max(c => StackPanel.ChildHeight(c, cw));
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            // Add() maqueta en cuanto entra el primer hijo, antes de que llegue el segundo
            if (Controls.Count < 2) return;
            int cw = (Width - Theme.S(10)) / 2;
            Controls[0].SetBounds(0, 0, cw, StackPanel.ChildHeight(Controls[0], cw));
            Controls[1].SetBounds(cw + Theme.S(10), 0, Width - cw - Theme.S(10), StackPanel.ChildHeight(Controls[1], cw));
            base.OnLayout(levent);
        }
    }

    /* Botones que se reparten el ancho y bajan de línea si no caben (.btn-row). */
    public sealed class BtnRow : Control, IHeightForWidth
    {
        public BtnRow(params DarkButton[] buttons)
        {
            foreach (var b in buttons) Controls.Add(b);
        }

        List<List<DarkButton>> Rows(int width)
        {
            var rows = new List<List<DarkButton>>();
            var cur = new List<DarkButton>();
            int used = 0, gap = Theme.S(8);
            foreach (var b in Controls.OfType<DarkButton>().Where(Vis.Shown))
            {
                int pw = b.PreferredWidth;
                if (cur.Count > 0 && used + gap + pw > width) { rows.Add(cur); cur = new List<DarkButton>(); used = 0; }
                used += (cur.Count > 0 ? gap : 0) + pw;
                cur.Add(b);
            }
            if (cur.Count > 0) rows.Add(cur);
            return rows;
        }

        public int HeightFor(int width)
        {
            var rows = Rows(width);
            if (rows.Count == 0) return 0;
            return rows.Sum(r => r.Max(b => b.PreferredHeight)) + (rows.Count - 1) * Theme.S(8);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            int gap = Theme.S(8), y = 0;
            foreach (var row in Rows(Width))
            {
                int h = row.Max(b => b.PreferredHeight);
                int totalPref = row.Sum(b => b.PreferredWidth);
                int free = Width - totalPref - gap * (row.Count - 1);
                int x = 0;
                for (int i = 0; i < row.Count; i++)
                {
                    int extra = i == row.Count - 1 ? Width - x - row[i].PreferredWidth : free / row.Count;
                    int w = row[i].PreferredWidth + Math.Max(0, extra);
                    if (i == row.Count - 1) w = Width - x;
                    row[i].SetBounds(x, y, w, h);
                    x += w + gap;
                }
                y += h + gap;
            }
            base.OnLayout(levent);
        }
    }

    /* Sección plegable del panel lateral (<details> con su <summary>). */
    public sealed class Section : Control, IHeightForWidth
    {
        readonly SectionHeader header;
        public readonly StackPanel Body;
        bool expanded;

        public bool Expanded
        {
            get => expanded;
            set { expanded = value; Body.Visible = value; header.Invalidate(); Parent?.PerformLayout(); PerformLayout(); }
        }

        public Section(string title, bool open)
        {
            BackColor = Theme.Bg2;
            header = new SectionHeader(this) { Text = Core.Lang.T(title).ToUpperInvariant() };
            Body = new StackPanel(10) { Padding = new Padding(Theme.S(16), Theme.S(4), Theme.S(16), Theme.S(16)), BackColor = Theme.Bg2 };
            Controls.Add(header);
            Controls.Add(Body);
            expanded = open;
            Body.Visible = open;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Body.SizeChanged += (s, e) => { if (expanded) Parent?.PerformLayout(); };
        }

        public T Add<T>(T c) where T : Control { Body.Controls.Add(c); return c; }

        public int HeightFor(int width) =>
            Theme.S(38) + (expanded ? Body.HeightFor(width) : 0) + 1;

        protected override void OnLayout(LayoutEventArgs levent)
        {
            header.SetBounds(0, 0, Width, Theme.S(38));
            if (expanded) Body.SetBounds(0, Theme.S(38), Width, Body.HeightFor(Width));
            base.OnLayout(levent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Bg2);
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }

        sealed class SectionHeader : DarkControl
        {
            readonly Section owner;
            public SectionHeader(Section s) { owner = s; Cursor = Cursors.Hand; }

            protected override void OnClick(EventArgs e) { owner.Expanded = !owner.Expanded; base.OnClick(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(hover ? Theme.Bg3 : Theme.Bg2);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = Theme.Sf(21), cy = Height / 2f, s = Theme.Sf(4);
                using (var b = new SolidBrush(Theme.Accent))
                {
                    if (owner.expanded) g.FillPolygon(b, new[] { new PointF(cx - s, cy - s * 0.6f), new PointF(cx + s, cy - s * 0.6f), new PointF(cx, cy + s * 0.8f) });
                    else g.FillPolygon(b, new[] { new PointF(cx - s * 0.6f, cy - s), new PointF(cx + s * 0.8f, cy), new PointF(cx - s * 0.6f, cy + s) });
                }
                TextRenderer.DrawText(g, Text, Theme.Header, new Rectangle(Theme.S(32), 0, Width - Theme.S(36), Height),
                    hover ? Theme.Fg : Theme.FgDim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }

    public enum RichMode { Hint, Readout, Popup, Banner, Plain }

    /* Texto con un marcado mínimo: <b>, <code>, <m> (monoespaciado atenuado) y
       <sw=#rrggbb> (muestra de color). Parte palabras, respeta saltos de línea y, en
       las lecturas, los espacios de alineación. */
    public sealed class RichLabel : Control, IHeightForWidth
    {
        readonly RichMode mode;
        string markup = "";
        List<(string text, int style, Color? swatch)> runs = new();
        readonly Dictionary<(string, int), int> widths = new();
        public bool HideWhenEmpty = true;

        public RichLabel(RichMode mode, string text = "")
        {
            this.mode = mode;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            SetText(text);
        }

        public string Markup => markup;

        public void SetText(string text)
        {
            // las ayudas son texto fijo y se traducen; los datos que se arman al vuelo, no
            markup = Core.Lang.T(text) ?? "";
            runs = Parse(markup);
            if (HideWhenEmpty && mode == RichMode.Readout && Vis.Shown(this) != markup.Length > 0) Vis.Set(this, markup.Length > 0);
            Parent?.PerformLayout();
            Invalidate();
        }

        public static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // estilo: bit 1 = negrita, bit 2 = code, bit 4 = mono atenuado
        static List<(string, int, Color?)> Parse(string s)
        {
            var list = new List<(string, int, Color?)>();
            int style = 0;
            foreach (Match m in Regex.Matches(s, @"<(/?)(b|code|m)>|<sw=(#[0-9a-fA-F]{6})>|[^<]+|<"))
            {
                if (m.Groups[2].Success)
                {
                    int bit = m.Groups[2].Value == "b" ? 1 : m.Groups[2].Value == "code" ? 2 : 4;
                    style = m.Groups[1].Value == "/" ? style & ~bit : style | bit;
                }
                else if (m.Groups[3].Success) list.Add(("", style, Theme.Hex(m.Groups[3].Value)));
                else list.Add((m.Value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&"), style, null));
            }
            return list;
        }

        (Font font, Color color) StyleOf(int st)
        {
            bool b = (st & 1) != 0, code = (st & 2) != 0, mono = (st & 4) != 0;
            switch (mode)
            {
                case RichMode.Hint:
                    if (code) return (Theme.MonoSmall, Theme.Accent);
                    return (b ? BoldOf(Theme.Small) : Theme.Small, Theme.FgDim);
                case RichMode.Readout:
                    return b ? (Theme.MonoBold, Theme.Fg) : (Theme.Mono, Theme.FgDim);
                case RichMode.Popup:
                    if (code || mono) return (Theme.Mono, Theme.FgDim);
                    return b ? (Theme.Semibold, Theme.Accent) : (Theme.UI, Theme.Fg);
                case RichMode.Banner:
                    return b ? (BoldOf(Theme.UISmall), Theme.Warn) : (Theme.UISmall, Theme.Fg);
                default:
                    return (b ? Theme.Semibold : Theme.UI, Theme.Fg);
            }
        }

        static readonly Dictionary<Font, Font> bolds = new();
        static Font BoldOf(Font f)
        {
            if (!bolds.TryGetValue(f, out var b)) bolds[f] = b = new Font(f, FontStyle.Bold);
            return b;
        }

        float LineHeightFactor => mode == RichMode.Readout ? 1.7f : mode == RichMode.Hint ? 1.5f : 1.45f;
        Padding Inner => mode == RichMode.Readout ? new Padding(Theme.S(10), Theme.S(8), Theme.S(10), Theme.S(8)) : Padding.Empty;

        int W(string t, int st)
        {
            if (!widths.TryGetValue((t, st), out int w))
            {
                var f = StyleOf(st).font;
                w = t.Length == 0 ? 0 : TextRenderer.MeasureText(t, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                if (t.Trim().Length == 0 && t.Length > 0)
                    w = TextRenderer.MeasureText("x" + t + "x", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width
                      - TextRenderer.MeasureText("xx", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                widths[(t, st)] = w;
            }
            return w;
        }

        /* Coloca cada trozo: devuelve (x, y, texto, estilo, muestra) y la altura total. */
        List<(int x, int y, string t, int st, Color? sw)> Flow(int width, out int height)
        {
            var output = new List<(int, int, string, int, Color?)>();
            var pad = Inner;
            int maxW = Math.Max(20, width - pad.Horizontal);
            int lineH = (int)Math.Ceiling(StyleOf(0).font.Height * LineHeightFactor);
            int x = 0, y = 0;
            int wordStart = -1, wordX = 0;
            foreach (var (text, st, sw) in runs)
            {
                if (sw.HasValue)
                {
                    int s = Theme.S(10);
                    if (x + s > maxW && x > 0) { x = 0; y += lineH; }
                    output.Add((x, y, null, st, sw)); x += s + Theme.S(4);
                    wordStart = -1;
                    continue;
                }
                var lines = text.Split('\n');
                for (int li = 0; li < lines.Length; li++)
                {
                    if (li > 0) { x = 0; y += lineH; wordStart = -1; }
                    foreach (Match tok in Regex.Matches(lines[li], @"\S+|\s+"))
                    {
                        string t = tok.Value;
                        int w = W(t, st);
                        bool space = t.Trim().Length == 0;
                        if (space) wordStart = -1;
                        if (x + w > maxW && x > 0)
                        {
                            if (space) { x = 0; y += lineH; continue; }
                            /* Una palabra hecha de varios estilos (un paréntesis pegado a un
                               <code>) baja entera: no se corta por la costura entre estilos. */
                            if (wordStart >= 0 && wordX > 0 && x - wordX + w <= maxW)
                            {
                                y += lineH;
                                for (int k = wordStart; k < output.Count; k++)
                                    output[k] = (output[k].Item1 - wordX, y, output[k].Item3, output[k].Item4, output[k].Item5);
                                x -= wordX;
                                wordX = 0;
                            }
                            else { x = 0; y += lineH; wordStart = -1; }
                        }
                        if (!space && wordStart < 0) { wordStart = output.Count; wordX = x; }
                        if (!(space && x == 0 && mode != RichMode.Readout)) output.Add((x, y, t, st, null));
                        x += (space && x == 0 && mode != RichMode.Readout) ? 0 : w;
                    }
                }
            }
            height = (runs.Count == 0 ? 0 : y + lineH) + pad.Vertical;
            return output;
        }

        public int HeightFor(int width)
        {
            if (markup.Length == 0 && HideWhenEmpty) return 0;
            Flow(width, out int h);
            return h;
        }

        public int NaturalWidth()
        {
            int best = 0;
            foreach (var line in markup.Split('\n'))
            {
                int w = 0;
                foreach (var (text, st, sw) in Parse(line)) w += sw.HasValue ? Theme.S(14) : W(text, st);
                best = Math.Max(best, w);
            }
            return best + Inner.Horizontal;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg2);
            var pad = Inner;
            if (mode == RichMode.Readout)
                Theme.FillRound(g, Theme.Bg, Theme.Line, new RectangleF(0, 0, Width, Height), Theme.Sf(7));
            int lineH = (int)Math.Ceiling(StyleOf(0).font.Height * LineHeightFactor);
            foreach (var (x, y, t, st, sw) in Flow(Width, out _))
            {
                if (sw.HasValue)
                {
                    int s = Theme.S(9);
                    var r = new Rectangle(pad.Left + x, pad.Top + y + (lineH - s) / 2, s, s);
                    using var b = new SolidBrush(sw.Value);
                    g.FillRectangle(b, r);
                    using var pen = new Pen(Color.FromArgb(102, 102, 102));
                    g.DrawRectangle(pen, r);
                    continue;
                }
                var (font, color) = StyleOf(st);
                TextRenderer.DrawText(g, t, font, new Rectangle(pad.Left + x, pad.Top + y, W(t, st) + 4, lineH), color,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }
    }

    /* Zona de arrastre con borde discontinuo; también se puede pinchar. */
    public sealed class DropZone : DarkControl, IHeightForWidth
    {
        readonly string bold, rest, small;
        bool over;
        public event Action<string[]> FilesDropped;

        public DropZone(string bold, string rest, string small)
        {
            this.bold = Core.Lang.T(bold); this.rest = Core.Lang.T(rest); this.small = Core.Lang.T(small);
            AllowDrop = true;
            Cursor = Cursors.Hand;
        }

        public int HeightFor(int width) => Theme.S(18 * 2) + Theme.UISmall.Height + Theme.Small.Height + Theme.S(4);

        protected override void OnDragEnter(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.Copy; over = true; Invalidate(); }
            base.OnDragEnter(e);
        }

        protected override void OnDragLeave(EventArgs e) { over = false; Invalidate(); base.OnDragLeave(e); }

        protected override void OnDragDrop(DragEventArgs e)
        {
            over = false; Invalidate();
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) FilesDropped?.Invoke(files);
            base.OnDragDrop(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(1, 1, Width - 2, Height - 2);
            using (var path = Theme.RoundRect(r, Theme.Sf(7)))
            {
                if (over || hover) using (var b = new SolidBrush(Color.FromArgb(20, 78, 163, 255))) g.FillPath(b, path);
                using var pen = new Pen(over || hover ? Theme.Accent : Theme.Line, Theme.Sf(1.5)) { DashStyle = DashStyle.Dash };
                g.DrawPath(pen, path);
            }
            var fontB = new Font(Theme.UISmall, FontStyle.Bold);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            int wb = TextRenderer.MeasureText(bold, fontB, Size.Empty, flags).Width;
            int wr = TextRenderer.MeasureText(rest, Theme.UISmall, Size.Empty, flags).Width;
            int y1 = Theme.S(18), x1 = (Width - wb - wr) / 2;
            Color c = over ? Theme.Fg : Theme.FgDim;
            TextRenderer.DrawText(g, bold, fontB, new Point(x1, y1), c, flags);
            TextRenderer.DrawText(g, rest, Theme.UISmall, new Point(x1 + wb, y1), c, flags);
            int ws = TextRenderer.MeasureText(small, Theme.Small, Size.Empty, flags).Width;
            TextRenderer.DrawText(g, small, Theme.Small, new Point((Width - ws) / 2, y1 + Theme.UISmall.Height + Theme.S(4)),
                Color.FromArgb(over ? 220 : 160, c), flags);
            fontB.Dispose();
        }
    }

    /* Lista dibujada a mano con su propio desplazamiento (.mk-list). Aguanta cientos
       de filas sin crear un control por cada una. */
    public sealed class DrawList : DarkControl, IHeightForWidth
    {
        public IList<object> Items = new List<object>();
        public Action<Graphics, Rectangle, object, bool> DrawItem;
        public Action<object, Point, Rectangle> ItemClick;
        public Func<object, bool> IsSelected = _ => false;
        public int MaxHeight;
        public int RowHeight = Theme.S(28);
        public int RowGap = Theme.S(3);
        int scroll, hoverIndex = -1;
        bool thumbDrag;
        int thumbGrabY, thumbGrabScroll;

        public DrawList(int maxHeight)
        {
            MaxHeight = Theme.S(maxHeight);
        }

        int Pitch => RowHeight + RowGap;
        int ContentHeight => Items.Count == 0 ? 0 : Items.Count * Pitch - RowGap;

        public void SetItems(IEnumerable<object> items)
        {
            Items = items.ToList();
            scroll = Math.Clamp(scroll, 0, Math.Max(0, ContentHeight - MaxHeight));
            Parent?.PerformLayout();
            Invalidate();
        }

        public int HeightFor(int width) => Math.Min(ContentHeight, MaxHeight);

        public void EnsureVisible(object item)
        {
            int i = Items.IndexOf(item);
            if (i < 0) return;
            int top = i * Pitch;
            if (top < scroll) scroll = top;
            else if (top + RowHeight > scroll + Height) scroll = top + RowHeight - Height;
            scroll = Math.Clamp(scroll, 0, Math.Max(0, ContentHeight - Height));
            Invalidate();
        }

        bool Overflow => ContentHeight > Height;
        public bool CanScroll => Overflow;
        int ScrollMax => Math.Max(0, ContentHeight - Height);

        Rectangle ThumbRect()
        {
            int trackH = Height;
            int th = Math.Max(Theme.S(24), trackH * Height / Math.Max(1, ContentHeight));
            int ty = ScrollMax == 0 ? 0 : (trackH - th) * scroll / ScrollMax;
            return new Rectangle(Width - Theme.S(6), ty, Theme.S(6), th);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!Overflow) { base.OnMouseWheel(e); return; }
            scroll = Math.Clamp(scroll - e.Delta / 120 * Pitch * 2, 0, ScrollMax);
            UpdateHover(e.Location);
            Invalidate();
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }

        void UpdateHover(Point p)
        {
            int i = (p.Y + scroll) / Pitch;
            int yIn = (p.Y + scroll) % Pitch;
            int nh = i >= 0 && i < Items.Count && yIn < RowHeight && (!Overflow || p.X < Width - Theme.S(8)) ? i : -1;
            if (nh != hoverIndex) { hoverIndex = nh; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (thumbDrag)
            {
                var tr = ThumbRect();
                int trackRange = Math.Max(1, Height - tr.Height);
                scroll = Math.Clamp(thumbGrabScroll + (e.Y - thumbGrabY) * ScrollMax / trackRange, 0, ScrollMax);
                Invalidate();
                return;
            }
            UpdateHover(e.Location);
            Cursor = hoverIndex >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Overflow && ThumbRect().Contains(e.Location)) { thumbDrag = true; thumbGrabY = e.Y; thumbGrabScroll = scroll; Capture = true; }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool wasDrag = thumbDrag;
            thumbDrag = false; Capture = false;
            base.OnMouseUp(e);
            if (wasDrag || e.Button != MouseButtons.Left) return;
            UpdateHover(e.Location);
            if (hoverIndex >= 0)
            {
                var rr = RowRect(hoverIndex);
                ItemClick?.Invoke(Items[hoverIndex], new Point(e.X - rr.X, e.Y - rr.Y), rr);
            }
        }

        Rectangle RowRect(int i) => new Rectangle(0, i * Pitch - scroll, Width - (Overflow ? Theme.S(9) : 0), RowHeight);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            int first = Math.Max(0, scroll / Pitch), last = Math.Min(Items.Count - 1, (scroll + Height) / Pitch);
            for (int i = first; i <= last; i++)
            {
                var r = RowRect(i);
                bool sel = IsSelected(Items[i]);
                if (sel || i == hoverIndex) Theme.FillRound(g, Theme.Bg3, sel ? Theme.Accent : Color.Transparent, r, Theme.Sf(7), sel ? 1 : 0);
                DrawItem?.Invoke(g, r, Items[i], i == hoverIndex);
            }
            if (Overflow) Theme.FillRound(g, Theme.Line, Color.Transparent, ThumbRect(), Theme.Sf(3), 0);
        }
    }

    /* Ranura de imagen: nombre, estado, grados de giro, Cargar y quitar (.slot). */
    public sealed class SlotRow : Control, IHeightForWidth
    {
        public readonly string SlotName;
        public readonly DarkTextBox Offset;
        public readonly DarkButton LoadBtn, ClearBtn;
        string state = "vacío";
        bool filled;
        readonly ToolTip tip = new();

        public SlotRow(string name)
        {
            SlotName = name;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Offset = new DarkTextBox(mono: true, compact: true) { Text = "0", TextAlign = HorizontalAlignment.Right };
            tip.SetToolTip(Offset.Inner, "Desfase de longitud en grados: gíralo si el mapa no cae donde debe");
            LoadBtn = new DarkButton("Cargar", small: true);
            ClearBtn = new DarkButton("×", ButtonVariant.Ghost, small: true) { Tip = "Quitar" };
            Controls.AddRange(new Control[] { Offset, LoadBtn, ClearBtn });
            BackColor = Theme.Bg;
        }

        public void SetState(bool isFilled, string stateText, string tooltip)
        {
            filled = isFilled; state = stateText;
            tip.SetToolTip(this, tooltip ?? "");
            Invalidate();
        }

        public int HeightFor(int width) => Theme.S(36);

        protected override void OnLayout(LayoutEventArgs levent)
        {
            int pad = Theme.S(7), gap = Theme.S(6);
            int h = Theme.S(24), y = (Height - h) / 2;
            int xClear = Width - pad - Theme.S(26);
            ClearBtn.SetBounds(xClear, y, Theme.S(26), h);
            int wLoad = LoadBtn.PreferredWidth;
            LoadBtn.SetBounds(xClear - gap - wLoad, y, wLoad, h);
            Offset.SetBounds(xClear - gap - wLoad - gap - Theme.S(54), y, Theme.S(54), h);
            base.OnLayout(levent);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg2);
            Theme.FillRound(g, Theme.Bg, filled ? Color.FromArgb(115, 126, 231, 135) : Theme.Line, new RectangleF(0, 0, Width, Height), Theme.Sf(7));
            int pad = Theme.S(7);
            TextRenderer.DrawText(g, SlotName, Theme.Semibold, new Rectangle(pad, 0, Theme.S(46), Height), Theme.Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int x = pad + Theme.S(46) + Theme.S(6);
            TextRenderer.DrawText(g, state, Theme.MonoSmall, new Rectangle(x, 0, Math.Max(0, Offset.Left - Theme.S(6) - x), Height),
                filled ? Theme.Accent2 : Theme.FgDim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    /* Panel opaco que flota sobre el mapa (HUD, barra de tiempo, avisos). */
    public class OverlayPanel : Panel
    {
        public Color BorderColor = Theme.Line;

        public OverlayPanel()
        {
            DoubleBuffered = true;
            BackColor = Theme.Bg2;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /* Pregunta de una línea con el tema oscuro, en lugar del prompt() de la web. */
    public sealed class InputBox : Form
    {
        readonly DarkTextBox box;

        InputBox(string message, string value)
        {
            Text = "Koogle Kerbin";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg2;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.S(400), Theme.S(150));

            var label = new RichLabel(RichMode.Plain, RichLabel.Esc(message)) { HideWhenEmpty = false };
            box = new DarkTextBox { Text = value ?? "" };
            var ok = new DarkButton("Aceptar", ButtonVariant.Primary);
            var cancel = new DarkButton("Cancelar", ButtonVariant.Ghost);
            ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            box.Inner.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DialogResult = DialogResult.OK; Close(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; Close(); }
            };
            Controls.AddRange(new Control[] { label, box, ok, cancel });

            int pad = Theme.S(16), w = ClientSize.Width - pad * 2;
            int lh = label.HeightFor(w);
            label.SetBounds(pad, pad, w, lh);
            box.SetBounds(pad, pad + lh + Theme.S(10), w, box.Height);
            int by = box.Bottom + Theme.S(14);
            ok.SetBounds(ClientSize.Width - pad - Theme.S(96), by, Theme.S(96), ok.Height);
            cancel.SetBounds(ok.Left - Theme.S(8) - Theme.S(96), by, Theme.S(96), cancel.Height);
            ClientSize = new Size(ClientSize.Width, by + ok.Height + pad);
            Shown += (s, e) => { box.Inner.Focus(); box.Inner.SelectAll(); };
        }

        public static string Ask(IWin32Window owner, string message, string value)
        {
            using var f = new InputBox(message, value);
            return f.ShowDialog(owner) == DialogResult.OK ? f.box.Text : null;
        }
    }
}
