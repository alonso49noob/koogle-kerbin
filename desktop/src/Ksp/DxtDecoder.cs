using System;
using System.Threading.Tasks;

namespace KerbinMaps.Ksp
{
    /* Descompresión de texturas DXT1 (BC1) y DXT5 (BC3) a RGBA en la CPU.

       Para pintar basta con subirlas comprimidas a la GPU, pero los mapas de los cuerpos
       también se leen en la CPU (la sonda del HUD, el suelo bajo la cámara, el filtro de
       altimetría), y eso pide los píxeles. Cada bloque de 4×4 lleva dos colores y un índice
       de dos bits por píxel entre ellos; DXT5 añade un bloque de alfa igual de sencillo. */
    public static class DxtDecoder
    {
        public static byte[] Decode(byte[] data, int w, int h, bool dxt5)
        {
            var o = new byte[w * h * 4];
            int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
            int tam = dxt5 ? 16 : 8;
            Parallel.For(0, bh, by =>
            {
                Span<byte> alfa = stackalloc byte[16];
                Span<byte> pal = stackalloc byte[16];
                for (int bx = 0; bx < bw; bx++)
                {
                    int off = (by * bw + bx) * tam;
                    if (dxt5) { Alfa(data, off, alfa); off += 8; }
                    else alfa.Fill(255);

                    ushort c0 = (ushort)(data[off] | (data[off + 1] << 8));
                    ushort c1 = (ushort)(data[off + 2] | (data[off + 3] << 8));
                    uint idx = BitConverter.ToUInt32(data, off + 4);
                    Rgb565(c0, pal, 0); Rgb565(c1, pal, 4);
                    if (c0 > c1 || dxt5)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            pal[8 + k] = (byte)((2 * pal[k] + pal[4 + k]) / 3);
                            pal[12 + k] = (byte)((pal[k] + 2 * pal[4 + k]) / 3);
                        }
                    }
                    else
                    {
                        for (int k = 0; k < 3; k++) { pal[8 + k] = (byte)((pal[k] + pal[4 + k]) / 2); pal[12 + k] = 0; }
                    }

                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= h) break;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= w) continue;
                            int i = py * 4 + px;
                            int c = (int)((idx >> (2 * i)) & 3);
                            int d = (y * w + x) * 4;
                            o[d] = pal[c * 4]; o[d + 1] = pal[c * 4 + 1]; o[d + 2] = pal[c * 4 + 2];
                            o[d + 3] = (!dxt5 && c0 <= c1 && c == 3) ? (byte)0 : alfa[i];
                        }
                    }
                }
            });
            return o;
        }

        static void Rgb565(ushort c, Span<byte> p, int at)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            p[at] = (byte)((r << 3) | (r >> 2));
            p[at + 1] = (byte)((g << 2) | (g >> 4));
            p[at + 2] = (byte)((b << 3) | (b >> 2));
            p[at + 3] = 255;
        }

        static void Alfa(byte[] d, int off, Span<byte> a)
        {
            int a0 = d[off], a1 = d[off + 1];
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)d[off + 2 + i] << (8 * i);
            Span<int> t = stackalloc int[8];
            t[0] = a0; t[1] = a1;
            if (a0 > a1) for (int i = 1; i < 7; i++) t[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            else { for (int i = 1; i < 5; i++) t[i + 1] = ((5 - i) * a0 + i * a1) / 5; t[6] = 0; t[7] = 255; }
            for (int i = 0; i < 16; i++) a[i] = (byte)t[(int)((bits >> (3 * i)) & 7)];
        }
    }
}
