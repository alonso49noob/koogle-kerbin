using System;
using System.Collections.Generic;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;

namespace KerbinMaps.Views
{
    /* Las mallas y texturas de los modelos del juego ya subidas a la GPU: las de la nave
       enfocada y las de los edificios. Cada malla se sube una vez y cada textura también;
       la copia en memoria de la textura se suelta en cuanto está en la GPU. */
    public sealed class ModelGpu : IDisposable
    {
        public sealed class GpuMesh
        {
            public uint Vao, Vbo;
            public uint[] Ebo;
            public int[] Count;
        }

        readonly Dictionary<MuMesh, GpuMesh> meshes = new();
        readonly Dictionary<string, uint> textures = new(StringComparer.OrdinalIgnoreCase);

        public GpuMesh Upload(MuMesh m)
        {
            if (meshes.TryGetValue(m, out var g)) return g;
            int n = m.VertCount;
            var data = new float[n * 8];
            for (int i = 0; i < n; i++)
            {
                int o = i * 8;
                data[o] = m.Verts[i * 3]; data[o + 1] = m.Verts[i * 3 + 1]; data[o + 2] = m.Verts[i * 3 + 2];
                if (m.Normals != null) { data[o + 3] = m.Normals[i * 3]; data[o + 4] = m.Normals[i * 3 + 1]; data[o + 5] = m.Normals[i * 3 + 2]; }
                else data[o + 4] = 1;
                if (m.Uvs != null) { data[o + 6] = m.Uvs[i * 2]; data[o + 7] = m.Uvs[i * 2 + 1]; }
            }
            g = new GpuMesh { Vao = GL.GenVertexArray(), Vbo = GL.GenBuffer(), Ebo = new uint[m.Submeshes.Count], Count = new int[m.Submeshes.Count] };
            GL.BindVertexArray(g.Vao);
            GL.BindBuffer(GL.ARRAY_BUFFER, g.Vbo);
            GL.BufferData(GL.ARRAY_BUFFER, data, data.Length, GL.STATIC_DRAW);
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, GL.FLOAT, false, 32, 0);
            GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, GL.FLOAT, false, 32, 12);
            GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 2, GL.FLOAT, false, 32, 24);
            GL.BindVertexArray(0);
            for (int s = 0; s < m.Submeshes.Count; s++)
            {
                var idx = m.Submeshes[s];
                var u = new uint[idx.Length];
                for (int i = 0; i < idx.Length; i++) u[i] = (uint)Math.Clamp(idx[i], 0, Math.Max(0, n - 1));
                g.Ebo[s] = GL.GenBuffer();
                GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, g.Ebo[s]);
                GL.BufferData(GL.ELEMENT_ARRAY_BUFFER, u, u.Length, GL.STATIC_DRAW);
                g.Count[s] = u.Length;
            }
            GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, 0);
            meshes[m] = g;
            return g;
        }

        public uint Tex(AssembledVessel a, string path)
        {
            if (path == null) return 0;
            if (textures.TryGetValue(path, out var id)) return id;
            if (a.Textures == null || !a.Textures.TryGetValue(path, out var tf) || tf == null || tf.Levels.Count == 0)
                return textures[path] = 0;
            id = GL.GenTexture();
            GL.BindTexture(GL.TEXTURE_2D, id);
            GL.PixelStore(GL.UNPACK_ALIGNMENT, 1);
            if (tf.CompressedFormat != 0)
            {
                int w = tf.Width, h = tf.Height;
                for (int l = 0; l < tf.Levels.Count; l++)
                {
                    GL.CompressedTexImage2D(GL.TEXTURE_2D, l, tf.CompressedFormat, w, h, tf.Levels[l]);
                    w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                }
                GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MAX_LEVEL, tf.Levels.Count - 1);
                GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, tf.Levels.Count > 1 ? GL.LINEAR_MIPMAP_LINEAR : GL.LINEAR);
            }
            else
            {
                GL.TexImage2D(GL.TEXTURE_2D, 0, (int)GL.RGBA8, tf.Width, tf.Height, GL.RGBA, GL.UNSIGNED_BYTE, tf.Levels[0]);
                GL.GenerateMipmap(GL.TEXTURE_2D);
                GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, GL.LINEAR_MIPMAP_LINEAR);
            }
            GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, GL.LINEAR);
            GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, GL.REPEAT);
            GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, GL.REPEAT);
            if (GL.MaxAnisotropy > 0) GL.TexParameter(GL.TEXTURE_2D, GL.TEXTURE_MAX_ANISOTROPY, Math.Min(8f, GL.MaxAnisotropy));
            GL.BindTexture(GL.TEXTURE_2D, 0);
            a.Textures.Remove(path);              // ya está en la GPU: la copia en memoria sobra
            return textures[path] = id;
        }

        public void Dispose()
        {
            foreach (var g in meshes.Values)
            {
                foreach (var e in g.Ebo) GL.DeleteBuffer(e);
                GL.DeleteBuffer(g.Vbo);
                GL.DeleteVertexArray(g.Vao);
            }
            meshes.Clear();
            foreach (var t in textures.Values) GL.DeleteTexture(t);
            textures.Clear();
        }
    }
}
