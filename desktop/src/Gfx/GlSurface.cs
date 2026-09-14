using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KerbinMaps.Gfx
{
    /* Control con un contexto OpenGL 3.3 core y antialias multimuestra.

       El formato de píxel de una ventana solo se puede fijar una vez, y para pedir
       uno con multimuestra hacen falta funciones WGL que solo existen con un contexto
       ya creado. Así que primero se monta un contexto de usar y tirar en una ventana
       auxiliar, con él se elige el formato bueno y se crea el contexto definitivo. */
    public sealed unsafe class GlSurface : Control
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PFD
        {
            public ushort nSize, nVersion; public uint dwFlags;
            public byte cPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift,
                        cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
                        cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
            public uint dwLayerMask, dwVisibleMask, dwDamageMask;
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(IntPtr hdc, ref PFD pfd);
        [DllImport("gdi32.dll")] static extern int DescribePixelFormat(IntPtr hdc, int format, uint size, ref PFD pfd);
        [DllImport("gdi32.dll")] static extern bool SetPixelFormat(IntPtr hdc, int format, ref PFD pfd);
        [DllImport("gdi32.dll")] static extern bool SwapBuffers(IntPtr hdc);
        [DllImport("opengl32.dll")] static extern IntPtr wglCreateContext(IntPtr hdc);
        [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(IntPtr hdc, IntPtr ctx);
        [DllImport("opengl32.dll")] static extern bool wglDeleteContext(IntPtr ctx);
        [DllImport("opengl32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr wglGetProcAddress(string name);

        IntPtr hdc, ctx;

        public int Samples { get; private set; }
        public string Error { get; private set; }
        public bool Ready => ctx != IntPtr.Zero && Error == null;

        public event Action ContextCreated;
        public event Action RenderFrame;

        public GlSurface()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            TabStop = true;
            BackColor = Color.FromArgb(7, 11, 17);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20 | 0x1 | 0x2;            // CS_OWNDC | CS_VREDRAW | CS_HREDRAW
                cp.Style |= 0x04000000 | 0x02000000;           // WS_CLIPSIBLINGS | WS_CLIPCHILDREN
                return cp;
            }
        }

        static PFD BasicPfd() => new PFD
        {
            nSize = 40, nVersion = 1,
            dwFlags = 0x4 | 0x20 | 0x1,                        // DRAW_TO_WINDOW | SUPPORT_OPENGL | DOUBLEBUFFER
            cPixelType = 0, cColorBits = 32, cDepthBits = 24, cStencilBits = 8
        };

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                CreateContext();
                ContextCreated?.Invoke();
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
        }

        void CreateContext()
        {
            var helper = new NativeWindow();
            helper.CreateHandle(new CreateParams { ClassName = "STATIC", Style = unchecked((int)0x80000000), Width = 1, Height = 1 });
            IntPtr hdcH = GetDC(helper.Handle);
            var pfd = BasicPfd();
            int f = ChoosePixelFormat(hdcH, ref pfd);
            SetPixelFormat(hdcH, f, ref pfd);
            IntPtr legacy = wglCreateContext(hdcH);
            if (legacy == IntPtr.Zero || !wglMakeCurrent(hdcH, legacy))
                throw new GlException("No se pudo crear un contexto OpenGL. ¿Están instalados los controladores de la tarjeta gráfica?");

            try
            {
                var choose = (delegate* unmanaged<IntPtr, int*, float*, uint, int*, uint*, int>)(void*)wglGetProcAddress("wglChoosePixelFormatARB");
                var createAttribs = (delegate* unmanaged<IntPtr, IntPtr, int*, IntPtr>)(void*)wglGetProcAddress("wglCreateContextAttribsARB");
                if (createAttribs == null)
                    throw new GlException("El controlador de vídeo no ofrece OpenGL 3.3, que es lo que necesita el visor.");

                hdc = GetDC(Handle);
                int format = 0;
                if (choose != null)
                {
                    int* attrs = stackalloc int[23];
                    foreach (int samples in new[] { 4, 2, 0 })
                    {
                        int i = 0;
                        void A(int k, int v) { attrs[i++] = k; attrs[i++] = v; }
                        A(0x2001, 1);            // DRAW_TO_WINDOW
                        A(0x2010, 1);            // SUPPORT_OPENGL
                        A(0x2011, 1);            // DOUBLE_BUFFER
                        A(0x2013, 0x202B);       // PIXEL_TYPE = RGBA
                        A(0x2014, 32);           // COLOR_BITS
                        A(0x2022, 24);           // DEPTH_BITS
                        A(0x2023, 8);            // STENCIL_BITS
                        A(0x2003, 0x2027);       // ACCELERATION = FULL
                        A(0x2041, samples > 0 ? 1 : 0);
                        A(0x2042, samples);
                        attrs[i] = 0;
                        int fmtOut; uint count;
                        if (choose(hdc, attrs, null, 1, &fmtOut, &count) != 0 && count > 0) { format = fmtOut; Samples = samples; break; }
                    }
                }
                var pfd2 = BasicPfd();
                if (format == 0) format = ChoosePixelFormat(hdc, ref pfd2);
                DescribePixelFormat(hdc, format, 40, ref pfd2);
                if (!SetPixelFormat(hdc, format, ref pfd2))
                    throw new GlException("No se pudo fijar el formato de píxel de la ventana.");

                int* ca = stackalloc int[] { 0x2091, 3, 0x2092, 3, 0x9126, 1, 0 };
                ctx = createAttribs(hdc, IntPtr.Zero, ca);
                if (ctx == IntPtr.Zero)
                    throw new GlException("El controlador de vídeo no ofrece OpenGL 3.3, que es lo que necesita el visor.");
            }
            finally
            {
                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                wglDeleteContext(legacy);
                ReleaseDC(helper.Handle, hdcH);
                helper.DestroyHandle();
            }

            MakeCurrent();
            GL.Load();
            GL.SwapInterval(1);
        }

        public bool MakeCurrent() => ctx != IntPtr.Zero && wglMakeCurrent(hdc, ctx);
        public void Swap() => SwapBuffers(hdc);

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Ready) RenderFrame?.Invoke();
            else
            {
                e.Graphics.Clear(BackColor);
                if (Error != null)
                    TextRenderer.DrawText(e.Graphics, Error, Font, ClientRectangle, Color.FromArgb(255, 180, 84),
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (ctx != IntPtr.Zero)
            {
                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                wglDeleteContext(ctx);
                ctx = IntPtr.Zero;
            }
            base.Dispose(disposing);
        }
    }
}
