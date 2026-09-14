using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KerbinMaps.UI
{
    /* Contenedor con desplazamiento vertical y barra fina de tema oscuro. La barra
       nativa de un Panel con AutoScroll sale clara aunque todo lo demás sea oscuro, y
       no hay forma documentada de teñirla. */
    public sealed class ScrollHost : Control, IMessageFilter
    {
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);

        readonly Control content;
        readonly Bar bar;
        int offset;

        public ScrollHost(Control content)
        {
            this.content = content;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            bar = new Bar(this);
            Controls.Add(content);
            Controls.Add(bar);
            bar.BringToFront();
            content.SizeChanged += (s, e) => Relayout();
        }

        int Reserve => Theme.S(10);
        int MaxOffset => Math.Max(0, content.Height - Height);

        public void ScrollTo(int y)
        {
            int v = Math.Clamp(y, 0, MaxOffset);
            if (v == offset) return;
            offset = v;
            content.Top = -offset;
            bar.Invalidate();
        }

        void Relayout()
        {
            if (content.Width != Width - Reserve) content.Width = Width - Reserve;
            offset = Math.Clamp(offset, 0, MaxOffset);
            content.Location = new Point(0, -offset);
            bar.SetBounds(Width - Theme.S(9), 0, Theme.S(8), Height);
            bar.Visible = MaxOffset > 0;
            bar.Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Application.AddMessageFilter(this);
        }

        protected override void Dispose(bool disposing)
        {
            Application.RemoveMessageFilter(this);
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(BackColor);

        /* La rueda sobre el panel desplaza el panel, salvo que el cursor esté sobre una
           lista que tiene su propio desplazamiento. */
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A || !Visible || !IsHandleCreated) return false;
            var pos = Cursor.Position;
            if (!RectangleToScreen(ClientRectangle).Contains(pos)) return false;
            var under = Control.FromChildHandle(WindowFromPoint(pos));
            for (var c = under; c != null && c != this; c = c.Parent)
                if (c is DrawList dl && dl.CanScroll) return false;
            int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
            ScrollTo(offset - delta * Theme.S(60) / 120);
            return true;
        }

        sealed class Bar : DarkControl
        {
            readonly ScrollHost host;
            bool drag;
            int grabY, grabOffset;

            public Bar(ScrollHost h) { host = h; }

            Rectangle Thumb()
            {
                int total = host.content.Height;
                if (total <= 0) return Rectangle.Empty;
                int th = Math.Max(Theme.S(30), Height * Height / total);
                int ty = host.MaxOffset == 0 ? 0 : (Height - th) * host.offset / host.MaxOffset;
                return new Rectangle(Theme.S(2), ty, Width - Theme.S(4), th);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                var t = Thumb();
                if (t.Contains(e.Location)) { drag = true; grabY = e.Y; grabOffset = host.offset; Capture = true; }
                else host.ScrollTo(host.offset + (e.Y < t.Top ? -host.Height : host.Height) * 9 / 10);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!drag) return;
                int range = Math.Max(1, Height - Thumb().Height);
                host.ScrollTo(grabOffset + (e.Y - grabY) * host.MaxOffset / range);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                drag = false; Capture = false;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(host.BackColor);
                var c = drag || hover ? Color.FromArgb(60, 78, 98) : Theme.Line;
                Theme.FillRound(e.Graphics, c, Color.Transparent, Thumb(), Theme.Sf(3), 0);
            }
        }
    }
}
