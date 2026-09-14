using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace KerbinMaps.Ksp
{
    /* Una textura lista para subir a la GPU. Las DDS comprimidas (DXT1/3/5) se suben tal
       cual, con sus mipmaps; el resto se descomprime a RGBA.

       Orientación: OpenGL, como Unity, pone la primera fila de datos abajo. Los DDS de
       KSP ya vienen guardados pensando en eso y se suben sin tocar; PNG y JPG se leen de
       arriba abajo y hay que darles la vuelta. */
    public sealed class TextureFile
    {
        public const uint DXT1 = 0x83F1, DXT3 = 0x83F2, DXT5 = 0x83F3;

        public int Width, Height;
        public uint CompressedFormat;          // 0 = RGBA8 sin comprimir
        public readonly List<byte[]> Levels = new();
        public bool HasAlpha;

        static readonly string[] Extensions = { ".dds", ".png", ".tga", ".jpg", ".jpeg" };

        /* Busca la textura por su nombre sin extensión, como hace KSP. */
        public static string Find(string pathWithoutExt)
        {
            foreach (var ext in Extensions)
            {
                string p = pathWithoutExt + ext;
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static TextureFile Load(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".dds" => LoadDds(path),
                ".tga" => LoadTga(path),
                _ => LoadGdi(path)
            };
        }

        static TextureFile LoadDds(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ')
                throw new InvalidDataException("no es un DDS");
            int height = BitConverter.ToInt32(d, 12), width = BitConverter.ToInt32(d, 16);
            int mips = Math.Max(1, BitConverter.ToInt32(d, 28));
            uint pfFlags = BitConverter.ToUInt32(d, 80);
            string four = System.Text.Encoding.ASCII.GetString(d, 84, 4);
            int bits = BitConverter.ToInt32(d, 88);
            var tex = new TextureFile { Width = width, Height = height };
            int offset = 128;

            if ((pfFlags & 0x4) != 0)
            {
                int block;
                switch (four)
                {
                    case "DXT1": tex.CompressedFormat = DXT1; block = 8; break;
                    case "DXT3": tex.CompressedFormat = DXT3; block = 16; tex.HasAlpha = true; break;
                    case "DXT5": tex.CompressedFormat = DXT5; block = 16; tex.HasAlpha = true; break;
                    default: throw new InvalidDataException("DDS con compresión no soportada: " + four);
                }
                int w = width, h = height;
                for (int i = 0; i < mips; i++)
                {
                    int size = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block;
                    if (offset + size > d.Length) break;
                    var lvl = new byte[size];
                    Buffer.BlockCopy(d, offset, lvl, 0, size);
                    tex.Levels.Add(lvl);
                    offset += size;
                    w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                }
                return tex;
            }

            if ((pfFlags & 0x40) == 0 || (bits != 32 && bits != 24))
                throw new InvalidDataException("DDS sin comprimir de formato no soportado");
            uint rm = BitConverter.ToUInt32(d, 92), gm = BitConverter.ToUInt32(d, 96), bm = BitConverter.ToUInt32(d, 100), am = BitConverter.ToUInt32(d, 104);
            int Shift(uint mask) { int s = 0; if (mask == 0) return -1; while ((mask & 1) == 0) { mask >>= 1; s++; } return s; }
            int rs = Shift(rm), gs = Shift(gm), bs = Shift(bm), asft = (pfFlags & 0x1) != 0 ? Shift(am) : -1;
            tex.HasAlpha = asft >= 0;
            int bpp = bits / 8;
            int ww = width, hh = height;
            for (int i = 0; i < mips; i++)
            {
                int count = ww * hh;
                if (offset + count * bpp > d.Length) break;
                var lvl = new byte[count * 4];
                for (int p = 0; p < count; p++)
                {
                    uint px = bpp == 4 ? BitConverter.ToUInt32(d, offset + p * 4) : (uint)(d[offset + p * 3] | d[offset + p * 3 + 1] << 8 | d[offset + p * 3 + 2] << 16);
                    lvl[p * 4] = (byte)(px >> rs);
                    lvl[p * 4 + 1] = (byte)(px >> gs);
                    lvl[p * 4 + 2] = (byte)(px >> bs);
                    lvl[p * 4 + 3] = asft >= 0 ? (byte)(px >> asft) : (byte)255;
                }
                tex.Levels.Add(lvl);
                offset += count * bpp;
                ww = Math.Max(1, ww / 2); hh = Math.Max(1, hh / 2);
            }
            return tex;
        }

        static TextureFile LoadTga(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            int idLen = d[0], type = d[2];
            int width = BitConverter.ToUInt16(d, 12), height = BitConverter.ToUInt16(d, 14);
            int bpp = d[16] / 8;
            bool topDown = (d[17] & 0x20) != 0;
            if ((type != 2 && type != 10 && type != 3 && type != 11) || bpp < 1)
                throw new InvalidDataException("TGA no soportado");
            int pos = 18 + idLen + (d[1] != 0 ? BitConverter.ToUInt16(d, 5) * ((d[7] + 7) / 8) : 0);
            var rgba = new byte[width * height * 4];
            int n = width * height, i = 0;
            void Put(int at)
            {
                byte b = d[at], g = bpp >= 3 ? d[at + 1] : b, r = bpp >= 3 ? d[at + 2] : b, a = bpp == 4 ? d[at + 3] : (byte)255;
                if (bpp == 1) { r = g = b; }
                rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a;
                i++;
            }
            if (type == 2 || type == 3)
                for (; i < n && pos + bpp <= d.Length; pos += bpp) Put(pos);
            else
                while (i < n && pos < d.Length)
                {
                    int hdr = d[pos++];
                    int count = (hdr & 0x7F) + 1;
                    if ((hdr & 0x80) != 0) { int at = pos; for (int k = 0; k < count && i < n; k++) Put(at); pos += bpp; }
                    else for (int k = 0; k < count && i < n; k++, pos += bpp) Put(pos);
                }
            if (topDown) FlipRows(rgba, width, height);
            var tex = new TextureFile { Width = width, Height = height, HasAlpha = bpp == 4 };
            tex.Levels.Add(rgba);
            return tex;
        }

        static unsafe TextureFile LoadGdi(string path)
        {
            using var bmp = new Bitmap(path);
            int w = bmp.Width, h = bmp.Height;
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var rgba = new byte[w * h * 4];
            bool alpha = false;
            try
            {
                byte* src = (byte*)bd.Scan0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = src + (long)(h - 1 - y) * bd.Stride;      // de abajo arriba
                    int o = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        rgba[o] = row[2]; rgba[o + 1] = row[1]; rgba[o + 2] = row[0]; rgba[o + 3] = row[3];
                        if (row[3] != 255) alpha = true;
                        row += 4; o += 4;
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
            var tex = new TextureFile { Width = w, Height = h, HasAlpha = alpha };
            tex.Levels.Add(rgba);
            return tex;
        }

        static void FlipRows(byte[] rgba, int w, int h)
        {
            var tmp = new byte[w * 4];
            for (int y = 0; y < h / 2; y++)
            {
                int a = y * w * 4, b = (h - 1 - y) * w * 4;
                Buffer.BlockCopy(rgba, a, tmp, 0, w * 4);
                Buffer.BlockCopy(rgba, b, rgba, a, w * 4);
                Buffer.BlockCopy(tmp, 0, rgba, b, w * 4);
            }
        }
    }
}
