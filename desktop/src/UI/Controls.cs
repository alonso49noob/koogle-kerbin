using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace KerbinMaps.UI
{
    /* Hand-drawn controls in the colors of the current theme. The stock WinForms ones can't be
       dressed up (corners, accent, light and dark mode), so they're painted here. */

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

    /* Segment: one option inside a group of joined buttons (the 2D, 3D... views). */
    public enum ButtonVariant { Normal, Primary, Ghost, Segment }

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
        bool Strong => IconFont == null && (variant == ButtonVariant.Primary || active);

        static Font smallStrong;
        static Font SmallStrong => smallStrong ??= new Font(Theme.Semibold.FontFamily, Theme.Small.Size, FontStyle.Regular, GraphicsUnit.Pixel);
        Font PaintFont => Strong ? (small ? SmallStrong : Theme.Semibold) : TextFont;

        public int PreferredWidth => TextRenderer.MeasureText(Text, PaintFont).Width + Theme.S(small ? 16 : 22);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var back = ParentBack;
            g.Clear(back);
            var pal = Theme.Current;
            Color fill = pal.Btn, border = pal.Line, text = pal.Fg;
            bool indicador = false;

            switch (variant)
            {
                case ButtonVariant.Primary:
                    fill = pressed ? Theme.Mix(pal.Accent, Color.Black, 0.12f)
                         : hover ? Theme.Mix(pal.Accent, pal.Dark ? Color.White : Color.Black, 0.08f) : pal.Accent;
                    border = fill; text = pal.OnAccent;
                    break;
                case ButtonVariant.Ghost:
                    fill = pressed ? Theme.Mix(back, pal.Fg, 0.12f) : hover ? Theme.Mix(back, pal.Fg, 0.07f) : Color.Transparent;
                    border = Color.Transparent;
                    text = hover ? pal.Fg : pal.FgDim;
                    if (active) { fill = pal.Selected; text = pal.Fg; }
                    break;
                case ButtonVariant.Segment:
                    border = Color.Transparent;
                    if (active) { fill = pal.Btn; border = pal.Dark ? Color.Transparent : pal.Line; text = pal.Fg; indicador = true; }
                    else
                    {
                        fill = pressed ? Theme.Mix(back, pal.Fg, 0.1f) : hover ? Theme.Mix(back, pal.Fg, 0.06f) : Color.Transparent;
                        text = hover ? pal.Fg : pal.FgDim;
                    }
                    break;
                default:
                    if (active) { fill = pal.Selected; border = Theme.Mix(pal.Line, pal.Accent, 0.6f); }
                    else if (pressed) { fill = Theme.Mix(pal.Btn, pal.Fg, 0.1f); border = pal.LineStrong; }
                    else if (hover) { fill = pal.Hover; border = pal.LineStrong; }
                    break;
            }
            if (!Enabled)
            {
                text = Theme.Mix(pal.FgDim, back, 0.35f);
                if (variant == ButtonVariant.Primary) { fill = pal.Btn; border = pal.Line; }
            }

            Theme.FillRound(g, fill, border, new RectangleF(0, 0, Width, Height), Theme.Sf(Theme.Radius));
            if (indicador)
            {
                // the chosen option gets a short accent bar at the bottom, like in Windows 11
                float iw = Theme.Sf(16), ih = Theme.Sf(3);
                Theme.FillRound(g, pal.Accent, Color.Transparent, new RectangleF((Width - iw) / 2f, Height - ih - Theme.Sf(2), iw, ih), ih / 2, 0);
            }
            TextRenderer.DrawText(g, Text, PaintFont, new Rectangle(0, indicador ? -Theme.S(1) : 0, Width, Height), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
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

        /* Changes the check without notifying anyone, to reflect state that comes from outside. */
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
            int textW = width - Theme.S(25) - (Swatch.HasValue ? Theme.S(15) : 0);
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
            var pal = Theme.Current;
            int box = Theme.S(16);
            int y = (Height - box) / 2;
            var r = new RectangleF(0, y, box, box);
            if (isChecked)
            {
                var c = !Enabled ? pal.LineStrong : hover ? Theme.Mix(pal.Accent, pal.Dark ? Color.White : Color.Black, 0.08f) : pal.Accent;
                Theme.FillRound(g, c, c, r, Theme.Sf(3));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(pal.OnAccent, Theme.Sf(1.6)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float s = box;
                g.DrawLines(pen, new[] { new PointF(s * 0.27f, y + s * 0.52f), new PointF(s * 0.44f, y + s * 0.68f), new PointF(s * 0.74f, y + s * 0.34f) });
            }
            else
            {
                var borde = hover && Enabled ? Theme.Mix(pal.LineStrong, pal.Fg, 0.35f) : pal.LineStrong;
                Theme.FillRound(g, pal.Well, borde, r, Theme.Sf(3));
            }
            int x = box + Theme.S(9);
            if (Swatch.HasValue)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int d = Theme.S(8);
                using var b = new SolidBrush(Swatch.Value);
                g.FillEllipse(b, x, (Height - d) / 2f, d, d);
                x += d + Theme.S(7);
            }
            TextRenderer.DrawText(g, Text, F, new Rectangle(x, 0, Width - x, Height), Enabled ? pal.Fg : pal.FgDim,
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

        int Thumb => Theme.S(16);

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
            var pal = Theme.Current;
            int t = Thumb;
            float f = max > min ? (float)(val - min) / (max - min) : 0;
            float cx = t / 2f + f * (Width - t);
            float cy = Height / 2f;
            float th = Theme.Sf(4);
            var accent = Enabled ? pal.Accent : pal.LineStrong;
            Theme.FillRound(g, pal.Track, Color.Transparent, new RectangleF(t / 2f, cy - th / 2, Width - t, th), th / 2, 0);
            Theme.FillRound(g, accent, Color.Transparent, new RectangleF(t / 2f, cy - th / 2, cx - t / 2f, th), th / 2, 0);
            // the Windows 11 thumb: a disc with a border and an accent dot that grows on hover
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var disco = new RectangleF(cx - t / 2f + 0.5f, cy - t / 2f + 0.5f, t - 1, t - 1);
            using (var b = new SolidBrush(pal.Thumb)) g.FillEllipse(b, disco);
            using (var pen = new Pen(pal.Dark ? Theme.Mix(pal.Thumb, pal.Fg, 0.12f) : pal.LineStrong, 1)) g.DrawEllipse(pen, disco);
            float rin = t * (hover || dragging ? 0.3f : 0.24f);
            using (var b = new SolidBrush(accent)) g.FillEllipse(b, cx - rin, cy - rin, rin * 2, rin * 2);
        }
    }

    /* Text box with a thin border and the accent bar at the bottom when focused. */
    public sealed class DarkTextBox : Control
    {
        public readonly TextBox Inner;
        bool compact;
        string committed = "", icon;

        /* Like the web's «change» event: on pressing Enter or leaving the field. */
        public event EventHandler Committed;
        public event EventHandler Edited;

        /* A glyph to the left of the text (the search magnifier). */
        public string Icon { get => icon; set { icon = value; PerformLayout(); Invalidate(); } }

        public DarkTextBox(bool mono = false, bool compact = false)
        {
            this.compact = compact;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Inner = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Well,
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

        /* The theme can change with the box already created: the inner TextBox isn't painted by
           us and has to be given the colors. */
        public void Retint()
        {
            Inner.BackColor = Theme.Well;
            Inner.ForeColor = Theme.Fg;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int padX = compact ? Theme.S(5) : Theme.S(9);
            int left = icon != null ? Theme.S(30) : padX;
            int h = Inner.PreferredHeight;
            Inner.SetBounds(left, Math.Max(1, (Height - h) / 2), Math.Max(10, Width - left - padX), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var pal = Theme.Current;
            g.Clear(Parent?.BackColor ?? pal.Bg2);
            float rad = Theme.Sf(compact ? 3 : Theme.Radius);
            var r = new RectangleF(0, 0, Width, Height);
            Theme.FillRound(g, pal.Well, Inner.Focused ? pal.LineStrong : pal.Line, r, rad);
            if (Inner.Focused)
            {
                // when focused, the accent bar at the bottom instead of a whole colored border
                using var clip = Theme.RoundRect(r, rad);
                var old = g.Clip;
                g.SetClip(clip);
                using (var b = new SolidBrush(pal.Accent)) g.FillRectangle(b, 0, Height - Theme.S(2), Width, Theme.S(2));
                g.Clip = old;
            }
            if (icon != null) Theme.DrawGlyph(g, icon, Theme.IconMid, new Rectangle(Theme.S(9), 0, Theme.S(16), Height), pal.FgDim);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Inner.Focus();
        }
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        static Color Sel => Theme.Mix(Theme.Bg2, Theme.Fg, Theme.Dark ? 0.08f : 0.06f);
        public override Color ToolStripDropDownBackground => Theme.Bg2;
        public override Color MenuBorder => Theme.Current.LineStrong;
        public override Color MenuItemBorder => Sel;
        public override Color MenuItemSelected => Sel;
        public override Color MenuItemSelectedGradientBegin => Sel;
        public override Color MenuItemSelectedGradientEnd => Sel;
        public override Color MenuItemPressedGradientBegin => Sel;
        public override Color MenuItemPressedGradientEnd => Sel;
        public override Color CheckBackground => Theme.Bg2;
        public override Color CheckSelectedBackground => Sel;
        public override Color CheckPressedBackground => Sel;
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
            e.TextColor = e.Item.Enabled ? Theme.Fg : Theme.FgDim;
            base.OnRenderItemText(e);
        }

        /* The option in use: the Windows check mark in the accent color. */
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            Theme.DrawGlyph(e.Graphics, Theme.Glyph.Check, Theme.IconSmall, new Rectangle(r.X - Theme.S(2), r.Y, r.Width + Theme.S(4), r.Height), Theme.Accent);
        }
    }

    /* Dropdown: the chosen value with an arrow, and the list in a themed menu. */
    public sealed class DarkCombo : DarkControl
    {
        readonly List<(string Id, string Label)> items = new();
        int index = -1;
        public event EventHandler SelectedChanged;

        /* Optional icon per id: if set, it shows to the left of the value and of each option in
           the list (celestial bodies use it). */
        public Func<string, int, Image> Icons;
        int IconSize => Theme.S(17);

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
            var menu = new ContextMenuStrip
            {
                Renderer = new DarkRenderer(), ShowImageMargin = Icons != null, ShowCheckMargin = Icons == null,
                Font = Theme.UI, BackColor = Theme.Bg2
            };
            if (Icons != null) menu.ImageScalingSize = new Size(IconSize, IconSize);
            for (int i = 0; i < items.Count; i++)
            {
                int k = i;
                var it = new ToolStripMenuItem(items[i].Label) { ForeColor = Theme.Fg, AutoSize = true, Padding = new Padding(0, Theme.S(3), 0, Theme.S(3)) };
                if (Icons != null) { it.Image = Icons(items[i].Id, IconSize); it.ImageScaling = ToolStripItemImageScaling.None; }
                // with icons there's no room for the check: the option in use goes semibold
                if (i == index) { if (Icons != null) it.Font = Theme.Semibold; else it.Checked = true; }
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
            var pal = Theme.Current;
            Theme.FillRound(g, hover ? Theme.Mix(pal.Well, pal.Fg, pal.Dark ? 0.04f : 0.025f) : pal.Well, hover ? pal.LineStrong : pal.Line,
                            new RectangleF(0, 0, Width, Height), Theme.Sf(Theme.Radius));
            string label = index >= 0 && index < items.Count ? items[index].Label : "";
            int pad = Theme.S(10), arrow = Theme.S(28);
            var ico = Icons == null ? null : Icons(SelectedId, IconSize);
            if (ico != null)
            {
                g.DrawImage(ico, pad, (Height - IconSize) / 2, IconSize, IconSize);
                pad += IconSize + Theme.S(7);
                label = label.TrimStart();          // the tree indentation has no place here
            }
            TextRenderer.DrawText(g, label, Theme.UI, new Rectangle(pad, 0, Width - pad - arrow, Height), pal.Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            Theme.DrawGlyph(g, Theme.Glyph.ChevronDown, Theme.IconSmall, new Rectangle(Width - arrow, 0, arrow - Theme.S(4), Height), pal.FgDim);
        }
    }

    /* Label row above a field: text on the left and value on the right. If both don't fit, the
       label is cut with an ellipsis and the value is shown whole. */
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
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            int vw = 0;
            if (value.Length > 0)
            {
                vw = TextRenderer.MeasureText(g, value, Theme.Semibold, Size.Empty, flags).Width;
                TextRenderer.DrawText(g, value, Theme.Semibold, new Rectangle(0, 0, Width, Height), Theme.Fg, flags | TextFormatFlags.Right);
                vw += Theme.S(10);
            }
            TextRenderer.DrawText(g, Text, Theme.Small, new Rectangle(0, 0, Math.Max(0, Width - vw), Height), Theme.FgDim,
                flags | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }
}
