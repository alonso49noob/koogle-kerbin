using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace KerbinMaps.UI
{
    /* Controles de tema oscuro dibujados a mano. Los de serie de WinForms no se dejan
       vestir como la web (bordes redondeados, acento azul), así que se pintan aquí. */

    public interface IHeightForWidth
    {
        int HeightFor(int width);
    }

    public abstract class DarkControl : Control
    {
        protected bool hover, pressed;

        protected DarkControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            ForeColor = Theme.Fg;
        }

        protected Color ParentBack => Parent?.BackColor ?? Theme.Bg2;

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    }

    public enum ButtonVariant { Normal, Primary, Ghost }

    public sealed class DarkButton : DarkControl
    {
        ButtonVariant variant;
        bool small, active;

        public ButtonVariant Variant { get => variant; set { variant = value; Invalidate(); } }
        public bool Small { get => small; set { small = value; Height = PreferredHeight; Invalidate(); } }
        public bool Active { get => active; set { if (active != value) { active = value; Invalidate(); } } }
        public Font IconFont;
        public string Tip { set => new ToolTip { InitialDelay = 500 }.SetToolTip(this, Core.Lang.T(value)); }

        public DarkButton(string text, ButtonVariant v = ButtonVariant.Normal, bool small = false)
        {
            Text = Core.Lang.T(text); variant = v; this.small = small;
            Cursor = Cursors.Hand;
            Height = PreferredHeight;
        }

        public int PreferredHeight => small ? Theme.S(24) : Theme.S(32);
        Font TextFont => IconFont ?? (small ? Theme.Small : Theme.UISmall);

        public int PreferredWidth
        {
            get
            {
                var sz = TextRenderer.MeasureText(Text, variant == ButtonVariant.Primary || active ? Theme.Semibold : TextFont);
                return sz.Width + Theme.S(small ? 16 : 22);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            Color fill = Theme.Bg3, border = Theme.Line, text = Theme.Fg;
            Font font = TextFont;
            bool on = active || variant == ButtonVariant.Primary;

            if (variant == ButtonVariant.Ghost && !active) { fill = Color.Transparent; text = hover ? Theme.Fg : Theme.FgDim; }
            if (hover && !on) { border = Theme.Accent; if (variant != ButtonVariant.Ghost) text = Color.White; }
            if (on)
            {
                fill = hover ? Lighten(Theme.Accent, 0.1f) : Theme.Accent;
                border = fill; text = Theme.OnAccent;
                if (IconFont == null) font = small ? new Font(Theme.Semibold.FontFamily, Theme.Small.Size, FontStyle.Bold, GraphicsUnit.Pixel) : Theme.Semibold;
            }
            if (pressed) fill = on ? Theme.Accent : Theme.Line;
            if (!Enabled) { text = Theme.FgDim; border = Theme.Line; }

            Theme.FillRound(g, fill, border, new RectangleF(0, 0, Width, Height), Theme.Sf(7));
            TextRenderer.DrawText(g, Text, font, new Rectangle(0, 0, Width, Height), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            if (font != TextFont && font != Theme.Semibold) font.Dispose();
        }

        static Color Lighten(Color c, float f) =>
            Color.FromArgb(Math.Min(255, (int)(c.R + (255 - c.R) * f)), Math.Min(255, (int)(c.G + (255 - c.G) * f)), Math.Min(255, (int)(c.B + (255 - c.B) * f)));
    }

    public sealed class DarkCheck : DarkControl, IHeightForWidth
    {
        bool isChecked;
        public event EventHandler CheckedChanged;
        public Color? Swatch;
        public Font TextFont = null;

        public bool Checked
        {
            get => isChecked;
            set { if (isChecked != value) { isChecked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }
        }

        /* Cambia la marca sin avisar a nadie, para reflejar un estado que viene de fuera. */
        public void SetSilently(bool v) { isChecked = v; Invalidate(); }

        public DarkCheck(string text, bool check = false)
        {
            Text = Core.Lang.T(text); isChecked = check;
            Cursor = Cursors.Hand;
            Height = Theme.S(22);
        }

        Font F => TextFont ?? Theme.UI;

        public int HeightFor(int width)
        {
            int textW = width - Theme.S(23) - (Swatch.HasValue ? Theme.S(15) : 0);
            var sz = TextRenderer.MeasureText(Text, F, new Size(Math.Max(10, textW), 0), TextFormatFlags.WordBreak);
            return Math.Max(Theme.S(22), sz.Height + Theme.S(4));
        }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            int box = Theme.S(15);
            int y = (Height - box) / 2;
            var r = new RectangleF(0.5f, y + 0.5f, box - 1, box - 1);
            Color dim = Enabled ? Theme.Fg : Theme.FgDim;
            if (isChecked)
            {
                Theme.FillRound(g, Enabled ? Theme.Accent : Theme.Line, Enabled ? Theme.Accent : Theme.Line, r, Theme.Sf(3));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(Color.White, Theme.Sf(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float s = box;
                g.DrawLines(pen, new[] { new PointF(s * 0.25f, y + s * 0.52f), new PointF(s * 0.43f, y + s * 0.7f), new PointF(s * 0.76f, y + s * 0.32f) });
            }
            else
            {
                Theme.FillRound(g, Theme.Bg, hover && Enabled ? Theme.Accent : Theme.FgDim, r, Theme.Sf(3));
            }
            int x = box + Theme.S(8);
            if (Swatch.HasValue)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int d = Theme.S(8);
                using var b = new SolidBrush(Swatch.Value);
                g.FillEllipse(b, x, (Height - d) / 2f, d, d);
                x += d + Theme.S(7);
            }
            TextRenderer.DrawText(g, Text, F, new Rectangle(x, 0, Width - x, Height), dim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }

    public sealed class DarkSlider : DarkControl
    {
        int min, max = 100, val;
        bool dragging;
        public event EventHandler ValueChanged;

        public int Minimum { get => min; set { min = value; Invalidate(); } }
        public int Maximum { get => max; set { max = value; Invalidate(); } }

        public int Value
        {
            get => val;
            set
            {
                int v = Math.Clamp(value, min, max);
                if (v != val) { val = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); }
            }
        }

        public void SetSilently(int v) { val = Math.Clamp(v, min, max); Invalidate(); }

        public DarkSlider(int min, int max, int value)
        {
            this.min = min; this.max = max; val = Math.Clamp(value, min, max);
            Height = Theme.S(20);
            Cursor = Cursors.Hand;
        }

        int Thumb => Theme.S(15);

        void SetFromX(int x)
        {
            int t = Thumb;
            double f = Math.Clamp((x - t / 2.0) / Math.Max(1, Width - t), 0, 1);
            Value = (int)Math.Round(min + f * (max - min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            dragging = true; Capture = true; SetFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false; Capture = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int t = Thumb;
            float f = max > min ? (float)(val - min) / (max - min) : 0;
            float cx = t / 2f + f * (Width - t);
            float cy = Height / 2f;
            float th = Theme.Sf(4);
            var track = new RectangleF(t / 2f, cy - th / 2, Width - t, th);
            Theme.FillRound(g, Color.FromArgb(58, 70, 86), Color.Transparent, track, th / 2, 0);
            Theme.FillRound(g, Theme.Accent, Color.Transparent, new RectangleF(t / 2f, cy - th / 2, cx - t / 2f, th), th / 2, 0);
            using var b = new SolidBrush(hover || dragging ? Color.FromArgb(120, 185, 255) : Theme.Accent);
            g.FillEllipse(b, cx - t / 2f, cy - t / 2f, t, t);
        }
    }

    /* Caja de texto con el borde redondeado de la web y el acento al enfocar. */
    public sealed class DarkTextBox : Control
    {
        public readonly TextBox Inner;
        bool compact;
        string committed = "";

        /* Como el evento «change» de la web: al pulsar Enter o salir del campo. */
        public event EventHandler Committed;
        public event EventHandler Edited;

        public DarkTextBox(bool mono = false, bool compact = false)
        {
            this.compact = compact;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Inner = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Bg,
                ForeColor = Theme.Fg,
                Font = mono ? (compact ? Theme.MonoSmall : Theme.MonoInput) : (compact ? Theme.Small : Theme.UI)
            };
            Controls.Add(Inner);
            Height = compact ? Theme.S(24) : Theme.S(32);
            Inner.GotFocus += (s, e) => Invalidate();
            Inner.LostFocus += (s, e) => { Invalidate(); Commit(); };
            Inner.TextChanged += (s, e) => Edited?.Invoke(this, EventArgs.Empty);
            Inner.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); }
            };
        }

        void Commit()
        {
            if (Inner.Text == committed) return;
            committed = Inner.Text;
            Committed?.Invoke(this, EventArgs.Empty);
        }

        public override string Text
        {
            get => Inner?.Text ?? "";
            set { Inner.Text = value ?? ""; committed = Inner.Text; }
        }

        public string Placeholder { set => Inner.PlaceholderText = Core.Lang.T(value); }
        public HorizontalAlignment TextAlign { set => Inner.TextAlign = value; }

        public double NumberOr(double fallback)
        {
            string s = Inner.Text.Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : fallback;
        }

        public void SetNumber(double v) => Text = v.ToString("0.####", CultureInfo.InvariantCulture);

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int padX = compact ? Theme.S(5) : Theme.S(9);
            int h = Inner.PreferredHeight;
            Inner.SetBounds(padX, Math.Max(1, (Height - h) / 2), Math.Max(10, Width - padX * 2), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg2);
            Theme.FillRound(e.Graphics, Theme.Bg, Inner.Focused ? Theme.Accent : Theme.Line, new RectangleF(0, 0, Width, Height), Theme.Sf(compact ? 5 : 7));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Inner.Focus();
        }
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Bg2;
        public override Color MenuBorder => Theme.Line;
        public override Color MenuItemBorder => Theme.Accent;
        public override Color MenuItemSelected => Theme.Accent;
        public override Color MenuItemSelectedGradientBegin => Theme.Accent;
        public override Color MenuItemSelectedGradientEnd => Theme.Accent;
        public override Color ImageMarginGradientBegin => Theme.Bg2;
        public override Color ImageMarginGradientMiddle => Theme.Bg2;
        public override Color ImageMarginGradientEnd => Theme.Bg2;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Line;
    }

    public sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Theme.OnAccent : Theme.Fg;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
    }

    /* Desplegable: el valor elegido con una flecha, y la lista en un menú oscuro. */
    public sealed class DarkCombo : DarkControl
    {
        readonly List<(string Id, string Label)> items = new();
        int index = -1;
        public event EventHandler SelectedChanged;

        public DarkCombo()
        {
            Height = Theme.S(32);
            Cursor = Cursors.Hand;
        }

        public void SetItems(IEnumerable<(string id, string label)> list)
        {
            items.Clear();
            foreach (var (id, label) in list) items.Add((id, Core.Lang.T(label)));
            if (index >= items.Count) index = items.Count - 1;
            Invalidate();
        }

        public string SelectedId
        {
            get => index >= 0 && index < items.Count ? items[index].Id : null;
            set { index = items.FindIndex(i => i.Id == value); Invalidate(); }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (items.Count == 0) return;
            var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = Theme.UI, BackColor = Theme.Bg2 };
            for (int i = 0; i < items.Count; i++)
            {
                int k = i;
                var it = new ToolStripMenuItem(items[i].Label) { ForeColor = Theme.Fg, AutoSize = true, Padding = new Padding(0, Theme.S(3), 0, Theme.S(3)) };
                if (i == index) it.Font = Theme.Semibold;
                it.Click += (s, a) =>
                {
                    if (index == k) return;
                    index = k; Invalidate();
                    SelectedChanged?.Invoke(this, EventArgs.Empty);
                };
                menu.Items.Add(it);
            }
            menu.MinimumSize = new Size(Width, 0);
            menu.Closed += (s, a) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(this, new Point(0, Height + Theme.S(2)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            Theme.FillRound(g, Theme.Bg, hover ? Theme.Accent : Theme.Line, new RectangleF(0, 0, Width, Height), Theme.Sf(7));
            string label = index >= 0 && index < items.Count ? items[index].Label : "";
            int pad = Theme.S(9), arrow = Theme.S(18);
            TextRenderer.DrawText(g, label, Theme.UI, new Rectangle(pad, 0, Width - pad - arrow, Height), Theme.Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width - Theme.Sf(13), cy = Height / 2f, s = Theme.Sf(3.5);
            using var pen = new Pen(Theme.FgDim, Theme.Sf(1.5)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
        }
    }

    /* Fila de etiqueta sobre un campo: texto a la izquierda y valor a la derecha. */
    public sealed class FieldHeader : DarkControl
    {
        string value = "";
        public string Value { get => value; set { this.value = value ?? ""; Invalidate(); } }

        public FieldHeader(string text, string value = "")
        {
            Text = Core.Lang.T(text); this.value = value;
            Height = Theme.S(18);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            TextRenderer.DrawText(g, Text, Theme.Small, new Rectangle(0, 0, Width, Height), Theme.FgDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (value.Length > 0)
            {
                using var bold = new Font(Theme.Semibold.FontFamily, Theme.Small.Size, FontStyle.Bold, GraphicsUnit.Pixel);
                TextRenderer.DrawText(g, value, bold, new Rectangle(0, 0, Width, Height), Theme.Fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }
}
