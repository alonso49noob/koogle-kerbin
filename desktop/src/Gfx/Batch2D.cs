using System;
using System.Collections.Generic;

namespace KerbinMaps.Gfx
{
    /* Dibujo 2D en píxeles de pantalla: rectángulos, líneas con grosor, círculos y
       texturas (texto incluido). Todo va a un único búfer que se vacía cuando cambia
       la textura o se llena. Los colores se pasan normales y se premultiplican aquí. */
    public sealed class Batch2D : IDisposable
    {
        const int Stride = 8;

        const string VS = @"#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aCol;
uniform vec2 uView;
out vec2 vUv;
out vec4 vCol;
void main() {
  vec2 p = aPos / uView * 2.0 - 1.0;
  gl_Position = vec4(p.x, -p.y, 0.0, 1.0);
  vUv = aUv;
  vCol = aCol;
}";

        const string FS = @"#version 330 core
in vec2 vUv;
in vec4 vCol;
uniform sampler2D uTex;
out vec4 frag;
void main() { frag = vCol * texture(uTex, vUv); }";

        readonly ShaderProgram prog;
        readonly uint vao, vbo;
        float[] buf = new float[Stride * 32768];
        int n;
        readonly Texture white;
        Texture tex;
        int vw, vh;

        public Batch2D()
        {
            prog = new ShaderProgram(VS, FS, ("aPos", 0), ("aUv", 1), ("aCol", 2));
            vao = GL.GenVertexArray();
            vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(GL.ARRAY_BUFFER, vbo);
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 2, GL.FLOAT, false, Stride * 4, 0);
            GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 2, GL.FLOAT, false, Stride * 4, 8);
            GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 4, GL.FLOAT, false, Stride * 4, 16);
            GL.BindVertexArray(0);
            white = Texture.FromRgba(new byte[] { 255, 255, 255, 255 }, 1, 1, TexFilter.Nearest, false);
        }

        public void Begin(int width, int height)
        {
            vw = width; vh = height; n = 0; tex = white;
            GL.Disable(GL.DEPTH_TEST);
            GL.Disable(GL.CULL_FACE);
            GL.Enable(GL.BLEND);
            GL.BlendFunc(GL.ONE, GL.ONE_MINUS_SRC_ALPHA);
        }

        public void Flush()
        {
            if (n == 0) return;
            prog.Use();
            prog.Vec2("uView", vw, vh);
            GL.ActiveTexture(GL.TEXTURE0);
            GL.BindTexture(GL.TEXTURE_2D, tex.Id);
            prog.Int("uTex", 0);
            GL.BindVertexArray(vao);
            GL.BindBuffer(GL.ARRAY_BUFFER, vbo);
            GL.BufferData(GL.ARRAY_BUFFER, buf, n * Stride, GL.STREAM_DRAW);
            GL.DrawArrays(GL.TRIANGLES, 0, n);
            GL.BindVertexArray(0);
            n = 0;
        }

        public void End()
        {
            Flush();
            GL.Disable(GL.BLEND);
        }

        void UseTex(Texture t)
        {
            t ??= white;
            if (t != tex) { Flush(); tex = t; }
        }

        void Ensure(int verts)
        {
            if ((n + verts) * Stride <= buf.Length) return;
            Flush();
            if (verts * Stride > buf.Length) Array.Resize(ref buf, verts * Stride * 2);
        }

        void V(float x, float y, float u, float v, in ColorF c)
        {
            int i = n * Stride;
            buf[i] = x; buf[i + 1] = y; buf[i + 2] = u; buf[i + 3] = v;
            buf[i + 4] = c.R; buf[i + 5] = c.G; buf[i + 6] = c.B; buf[i + 7] = c.A;
            n++;
        }

        public void Rect(double x, double y, double w, double h, ColorF color)
        {
            if (w <= 0 || h <= 0) return;
            UseTex(null); Ensure(6);
            var c = color.Premul;
            float x0 = (float)x, y0 = (float)y, x1 = (float)(x + w), y1 = (float)(y + h);
            V(x0, y0, 0, 0, c); V(x1, y0, 0, 0, c); V(x1, y1, 0, 0, c);
            V(x0, y0, 0, 0, c); V(x1, y1, 0, 0, c); V(x0, y1, 0, 0, c);
        }

        public void RectOutline(double x, double y, double w, double h, double t, ColorF color)
        {
            Rect(x, y, w, t, color);
            Rect(x, y + h - t, w, t, color);
            Rect(x, y + t, t, h - 2 * t, color);
            Rect(x + w - t, y + t, t, h - 2 * t, color);
        }

        public void Image(Texture t, double x, double y, double w, double h, ColorF tint)
        {
            if (t == null) return;
            UseTex(t); Ensure(6);
            var c = tint.Premul;
            float x0 = (float)x, y0 = (float)y, x1 = (float)(x + w), y1 = (float)(y + h);
            V(x0, y0, 0, 0, c); V(x1, y0, 1, 0, c); V(x1, y1, 1, 1, c);
            V(x0, y0, 0, 0, c); V(x1, y1, 1, 1, c); V(x0, y1, 0, 1, c);
        }

        public void Text(TextTex t, double x, double y, float alpha = 1f)
        {
            if (t == null) return;
            Image(t.Tex, Math.Round(x) - t.Pad, Math.Round(y) - t.Pad, t.W, t.H, new ColorF(1, 1, 1, alpha));
        }

        public void Line(double x1, double y1, double x2, double y2, double width, ColorF color)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return;
            double hw = width / 2;
            double ux = dx / len, uy = dy / len;
            double nx = -uy * hw, ny = ux * hw;
            // un poco de prolongación tapa las rendijas en las esquinas de las polilíneas
            double ex = ux * hw * 0.5, ey = uy * hw * 0.5;
            float ax = (float)(x1 - ex), ay = (float)(y1 - ey), bx = (float)(x2 + ex), by = (float)(y2 + ey);
            UseTex(null); Ensure(6);
            var c = color.Premul;
            float fnx = (float)nx, fny = (float)ny;
            V(ax + fnx, ay + fny, 0, 0, c); V(bx + fnx, by + fny, 0, 0, c); V(bx - fnx, by - fny, 0, 0, c);
            V(ax + fnx, ay + fny, 0, 0, c); V(bx - fnx, by - fny, 0, 0, c); V(ax - fnx, ay - fny, 0, 0, c);
        }

        /* Línea discontinua; `fase` lleva la cuenta entre tramos para que el patrón
           no vuelva a empezar en cada vértice. */
        public void DashedLine(double x1, double y1, double x2, double y2, double width, ColorF color, double dash, double gap, ref double fase)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return;
            double period = dash + gap, pos = 0;
            while (pos < len)
            {
                double inPeriod = fase % period;
                double remaining;
                if (inPeriod < dash)
                {
                    remaining = Math.Min(dash - inPeriod, len - pos);
                    double a = pos / len, b = (pos + remaining) / len;
                    Line(x1 + dx * a, y1 + dy * a, x1 + dx * b, y1 + dy * b, width, color);
                }
                else remaining = Math.Min(period - inPeriod, len - pos);
                pos += remaining;
                fase += remaining;
            }
        }

        public void Circle(double cx, double cy, double r, ColorF color, int segs = 0)
        {
            if (r <= 0) return;
            if (segs <= 0) segs = Math.Clamp((int)(r * 2.5), 12, 48);
            UseTex(null); Ensure(segs * 3);
            var c = color.Premul;
            float fx = (float)cx, fy = (float)cy;
            for (int i = 0; i < segs; i++)
            {
                double a0 = i * 2 * Math.PI / segs, a1 = (i + 1) * 2 * Math.PI / segs;
                V(fx, fy, 0, 0, c);
                V((float)(cx + Math.Cos(a0) * r), (float)(cy + Math.Sin(a0) * r), 0, 0, c);
                V((float)(cx + Math.Cos(a1) * r), (float)(cy + Math.Sin(a1) * r), 0, 0, c);
            }
        }

        public void Ring(double cx, double cy, double r, double width, ColorF color, int segs = 0)
        {
            if (r <= 0) return;
            if (segs <= 0) segs = Math.Clamp((int)(r * 2.5), 12, 64);
            UseTex(null); Ensure(segs * 6);
            var c = color.Premul;
            double ri = r - width / 2, ro = r + width / 2;
            for (int i = 0; i < segs; i++)
            {
                double a0 = i * 2 * Math.PI / segs, a1 = (i + 1) * 2 * Math.PI / segs;
                float c0 = (float)Math.Cos(a0), s0 = (float)Math.Sin(a0), c1 = (float)Math.Cos(a1), s1 = (float)Math.Sin(a1);
                float ix0 = (float)(cx + c0 * ri), iy0 = (float)(cy + s0 * ri), ox0 = (float)(cx + c0 * ro), oy0 = (float)(cy + s0 * ro);
                float ix1 = (float)(cx + c1 * ri), iy1 = (float)(cy + s1 * ri), ox1 = (float)(cx + c1 * ro), oy1 = (float)(cy + s1 * ro);
                V(ix0, iy0, 0, 0, c); V(ox0, oy0, 0, 0, c); V(ox1, oy1, 0, 0, c);
                V(ix0, iy0, 0, 0, c); V(ox1, oy1, 0, 0, c); V(ix1, iy1, 0, 0, c);
            }
        }

        public void Diamond(double cx, double cy, double half, ColorF color)
        {
            UseTex(null); Ensure(6);
            var c = color.Premul;
            float x = (float)cx, y = (float)cy, h = (float)half;
            V(x, y - h, 0, 0, c); V(x + h, y, 0, 0, c); V(x, y + h, 0, 0, c);
            V(x, y - h, 0, 0, c); V(x, y + h, 0, 0, c); V(x - h, y, 0, 0, c);
        }

        public void Dispose()
        {
            prog.Dispose();
            white.Dispose();
            GL.DeleteBuffer(vbo);
            GL.DeleteVertexArray(vao);
        }
    }
}
