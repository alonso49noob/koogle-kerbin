using System;
using System.Collections.Generic;
using System.Globalization;

namespace KerbinMaps.Gfx
{
    public sealed class ShaderProgram : IDisposable
    {
        public uint Id { get; private set; }
        readonly Dictionary<string, int> locs = new();

        public ShaderProgram(string vs, string fs, params (string name, uint loc)[] attribs)
        {
            uint v = Compile(GL.VERTEX_SHADER, vs), f = Compile(GL.FRAGMENT_SHADER, fs);
            Id = GL.CreateProgram();
            GL.AttachShader(Id, v);
            GL.AttachShader(Id, f);
            /* Los índices se fijan a mano en vez de dejar que el enlazador los reparta:
               varios programas comparten el mismo VAO, y si cada uno los numerase a su
               manera, alguno leería basura. */
            foreach (var a in attribs) GL.BindAttribLocation(Id, a.loc, a.name);
            GL.LinkProgram(Id);
            if (GL.GetProgram(Id, GL.LINK_STATUS) == 0)
                throw new GlException("Error enlazando el programa: " + GL.GetProgramInfoLog(Id));
            GL.DeleteShader(v);
            GL.DeleteShader(f);
        }

        static uint Compile(uint type, string src)
        {
            uint s = GL.CreateShader(type);
            GL.ShaderSource(s, src);
            GL.CompileShader(s);
            if (GL.GetShader(s, GL.COMPILE_STATUS) == 0)
                throw new GlException("Error compilando el shader: " + GL.GetShaderInfoLog(s));
            return s;
        }

        public void Use() => GL.UseProgram(Id);

        public int Loc(string name)
        {
            if (!locs.TryGetValue(name, out int l)) { l = GL.GetUniformLocation(Id, name); locs[name] = l; }
            return l;
        }

        public void Int(string n, int v) => GL.Uniform(Loc(n), v);
        public void Float(string n, double v) => GL.Uniform(Loc(n), (float)v);
        public void Vec2(string n, double a, double b) => GL.Uniform(Loc(n), (float)a, (float)b);
        public void Vec3(string n, double a, double b, double c) => GL.Uniform(Loc(n), (float)a, (float)b, (float)c);
        public void Vec4(string n, double a, double b, double c, double d) => GL.Uniform(Loc(n), (float)a, (float)b, (float)c, (float)d);
        public void Mat(string n, float[] m) => GL.UniformMatrix4(Loc(n), m);

        public void Dispose()
        {
            if (Id != 0) { GL.DeleteProgram(Id); Id = 0; }
        }
    }

    public enum TexFilter { Nearest, Linear, Mipmap }

    public sealed class Texture : IDisposable
    {
        public uint Id { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public static Texture FromRgba(byte[] rgba, int w, int h, TexFilter filter, bool repeatS)
        {
            uint id = GL.GenTexture();
            GL.BindTexture(GL.TEXTURE_2D, id);
            GL.PixelStore(GL.UNPACK_ALIGNMENT, 1);
            GL.TexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA8, w, h, GL.RGBA, GL.UNSIGNED_BYTE, rgba);
            GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, repeatS ? GL.REPEAT : GL.CLAMP_TO_EDGE);
            GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, GL.CLAMP_TO_EDGE);
            switch (filter)
            {
                case TexFilter.Nearest:
                    GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, GL.NEAREST);
                    GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, GL.NEAREST);
                    break;
                case TexFilter.Linear:
                    GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, GL.LINEAR);
                    GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, GL.LINEAR);
                    break;
                default:
                    GL.GenerateMipmap(GL.TEXTURE_2D);
                    GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, GL.LINEAR_MIPMAP_LINEAR);
                    GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, GL.LINEAR);
                    if (GL.MaxAnisotropy > 0)
                        GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MAX_ANISOTROPY, Math.Min(8f, GL.MaxAnisotropy));
                    break;
            }
            GL.BindTexture(GL.TEXTURE_2D, 0);
            return new Texture { Id = id, Width = w, Height = h };
        }

        public void Dispose()
        {
            if (Id != 0) { GL.DeleteTexture(Id); Id = 0; }
        }
    }

    public readonly struct ColorF
    {
        public readonly float R, G, B, A;
        public ColorF(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }

        public static ColorF Hex(string hex, float alpha = 1f)
        {
            string h = (hex ?? "").TrimStart('#');
            if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                return new ColorF(0.8f, 0.8f, 0.8f, alpha);
            return new ColorF(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f, alpha);
        }

        public static ColorF Rgba(int r, int g, int b, float a) => new ColorF(r / 255f, g / 255f, b / 255f, a);
        public ColorF WithAlpha(float a) => new ColorF(R, G, B, a);
        public ColorF Premul => new ColorF(R * A, G * A, B * A, A);
        public static readonly ColorF White = new ColorF(1, 1, 1, 1);
    }

    /* Matrices 4x4 en columnas, el mismo convenio que usaba la versión WebGL. */
    public static class Mat4
    {
        public static float[] Create() => new float[16];

        public static float[] Perspective(float[] o, double fovy, double aspect, double near, double far)
        {
            double f = 1 / Math.Tan(fovy / 2), nf = 1 / (near - far);
            Array.Clear(o);
            o[0] = (float)(f / aspect); o[5] = (float)f;
            o[10] = (float)((far + near) * nf); o[11] = -1;
            o[14] = (float)(2 * far * near * nf);
            return o;
        }

        public static float[] LookAt(float[] o, double[] eye, double[] center, double[] up)
        {
            double z0 = eye[0] - center[0], z1 = eye[1] - center[1], z2 = eye[2] - center[2];
            double len = Math.Sqrt(z0 * z0 + z1 * z1 + z2 * z2); if (len == 0) len = 1;
            z0 /= len; z1 /= len; z2 /= len;

            double x0 = up[1] * z2 - up[2] * z1, x1 = up[2] * z0 - up[0] * z2, x2 = up[0] * z1 - up[1] * z0;
            len = Math.Sqrt(x0 * x0 + x1 * x1 + x2 * x2);
            if (len == 0) { x0 = 1; x1 = 0; x2 = 0; } else { x0 /= len; x1 /= len; x2 /= len; }

            double y0 = z1 * x2 - z2 * x1, y1 = z2 * x0 - z0 * x2, y2 = z0 * x1 - z1 * x0;

            o[0] = (float)x0; o[1] = (float)y0; o[2] = (float)z0; o[3] = 0;
            o[4] = (float)x1; o[5] = (float)y1; o[6] = (float)z1; o[7] = 0;
            o[8] = (float)x2; o[9] = (float)y2; o[10] = (float)z2; o[11] = 0;
            o[12] = (float)-(x0 * eye[0] + x1 * eye[1] + x2 * eye[2]);
            o[13] = (float)-(y0 * eye[0] + y1 * eye[1] + y2 * eye[2]);
            o[14] = (float)-(z0 * eye[0] + z1 * eye[1] + z2 * eye[2]);
            o[15] = 1;
            return o;
        }
    }
}
