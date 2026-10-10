using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KerbinMaps.Ksp
{
    /* Unity bundle reader (UnityFS), just enough to get textures out.

       Some mods (Parallax, for example) don't leave their textures loose in GameData but inside
       a Unity bundle of several gigabytes. The bundle is a list of LZ4-compressed blocks of
       about 128 KB; inside there's a Unity serialized file (with the object table) and a
       resource file with the bytes of the images. Since blocks can be decompressed separately,
       only what's needed is read here: the header, the object table and the requested textures,
       not the 2 GB.

       Supported formats: UnityFS versions 6 and 7, serialized files from 17 to 22 (Unity 2017
       to 2020), uncompressed or LZ4/LZ4HC blocks. */
    /* Where a serialized file's bytes come from: a bundle (decompressing its blocks) or a loose
       file in the game's data. */
    public interface IUnityData
    {
        byte[] Read(long pos, int n);
    }

    /* A loose file in KSP_x64_Data (sharedassets9.assets, its .resS...), read in pieces. */
    public sealed class UnityFile : IUnityData, IDisposable
    {
        readonly FileStream f;
        public readonly string Path;
        public long Length => f.Length;

        public UnityFile(string path)
        {
            Path = path;
            f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        }

        public byte[] Read(long pos, int n)
        {
            var o = new byte[n];
            lock (f)
            {
                f.Seek(pos, SeekOrigin.Begin);
                int leido = 0;
                while (leido < n)
                {
                    int k = f.Read(o, leido, n - leido);
                    if (k <= 0) throw new EndOfStreamException("lectura fuera del fichero");
                    leido += k;
                }
            }
            return o;
        }

        public void Dispose() => f.Dispose();
    }

    public sealed class UnityBundle : IUnityData, IDisposable
    {
        readonly FileStream f;
        readonly List<(long Comp, long CompSize, long Uncomp, int UncompSize, int Flags)> bloques = new();
        readonly List<(long Offset, long Size, string Path)> nodos = new();
        readonly Dictionary<int, byte[]> cache = new();
        readonly LinkedList<int> orden = new();
        const int MaxCache = 48;

        public IReadOnlyList<(long Offset, long Size, string Path)> Nodos => nodos;

        public UnityBundle(string path)
        {
            f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var r = new BinaryReader(f);
            if (CString(r) != "UnityFS") throw new InvalidDataException("no es un paquete UnityFS");
            int version = BE32(r);
            CString(r); CString(r);                         // «5.x.x» and the Unity version
            long total = BE64(r);
            int infoComp = BE32(r), infoUncomp = BE32(r), flags = BE32(r);
            if (version >= 7) Alinear(16);

            byte[] infoRaw;
            long datos;
            if ((flags & 0x80) != 0)
            {
                // the block table is at the end of the file
                long pos = f.Position;
                f.Seek(total - infoComp, SeekOrigin.Begin);
                infoRaw = r.ReadBytes(infoComp);
                f.Seek(pos, SeekOrigin.Begin);
                datos = pos;
            }
            else
            {
                infoRaw = r.ReadBytes(infoComp);
                datos = f.Position;
                // with bit 0x200 the data starts at the next multiple of 16
                if ((flags & 0x200) != 0) datos = (datos + 15) & ~15L;
            }
            byte[] info = Descomprimir(infoRaw, infoUncomp, flags & 0x3F);

            var ir = new BinaryReader(new MemoryStream(info));
            ir.ReadBytes(16);                               // hash of the uncompressed data
            int nBloques = BE32(ir);
            long comp = datos, uncomp = 0;
            for (int i = 0; i < nBloques; i++)
            {
                int u = BE32(ir), c = BE32(ir);
                short bf = BE16(ir);
                bloques.Add((comp, c, uncomp, u, bf));
                comp += (uint)c; uncomp += (uint)u;
            }
            int nNodos = BE32(ir);
            for (int i = 0; i < nNodos; i++)
            {
                long off = BE64(ir), size = BE64(ir);
                BE32(ir);                                   // node flags
                nodos.Add((off, size, CString(ir)));
            }
        }

        public void Dispose() => f.Dispose();

        /* Reads `n` bytes of the decompressed stream starting at `pos`. */
        public byte[] Read(long pos, int n)
        {
            var o = new byte[n];
            int hecho = 0;
            int b = BloqueDe(pos);
            while (hecho < n && b < bloques.Count)
            {
                var blk = bloques[b];
                byte[] d = Bloque(b);
                int ini = (int)(pos + hecho - blk.Uncomp);
                int k = Math.Min(n - hecho, blk.UncompSize - ini);
                Buffer.BlockCopy(d, ini, o, hecho, k);
                hecho += k;
                b++;
            }
            if (hecho < n) throw new EndOfStreamException("lectura fuera del paquete");
            return o;
        }

        int BloqueDe(long pos)
        {
            int lo = 0, hi = bloques.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (bloques[mid].Uncomp <= pos) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        byte[] Bloque(int i)
        {
            if (cache.TryGetValue(i, out var d)) return d;
            var blk = bloques[i];
            f.Seek(blk.Comp, SeekOrigin.Begin);
            var raw = new byte[blk.CompSize];
            int leido = 0;
            while (leido < raw.Length)
            {
                int k = f.Read(raw, leido, raw.Length - leido);
                if (k <= 0) throw new EndOfStreamException();
                leido += k;
            }
            d = Descomprimir(raw, blk.UncompSize, blk.Flags & 0x3F);
            cache[i] = d;
            orden.AddLast(i);
            if (orden.Count > MaxCache) { cache.Remove(orden.First.Value); orden.RemoveFirst(); }
            return d;
        }

        static byte[] Descomprimir(byte[] src, int tam, int tipo)
        {
            switch (tipo)
            {
                case 0: return src;
                case 2: case 3: return Lz4(src, tam);
                default: throw new NotSupportedException("compresión de paquete no soportada: " + tipo);
            }
        }

        /* Block LZ4 (not the frame format): each sequence is a token with the literal length
           and the match length, the literals and an offset. */
        public static byte[] Lz4(byte[] src, int tam)
        {
            var dst = new byte[tam];
            int ip = 0, op = 0;
            while (ip < src.Length)
            {
                int token = src[ip++];
                int lit = token >> 4;
                if (lit == 15) { int b; do { b = src[ip++]; lit += b; } while (b == 255); }
                Buffer.BlockCopy(src, ip, dst, op, lit);
                ip += lit; op += lit;
                if (ip >= src.Length) break;                // the last sequence has no match
                int off = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                int len = token & 15;
                if (len == 15) { int b; do { b = src[ip++]; len += b; } while (b == 255); }
                len += 4;
                int from = op - off;
                if (off >= len) { Buffer.BlockCopy(dst, from, dst, op, len); op += len; }
                else for (int i = 0; i < len; i++) dst[op++] = dst[from + i];     // overlapping copy
            }
            return dst;
        }

        void Alinear(int n) { long p = f.Position; long a = (p + n - 1) / n * n; f.Seek(a, SeekOrigin.Begin); }

        static string CString(BinaryReader r)
        {
            var sb = new StringBuilder();
            int c;
            while ((c = r.ReadByte()) != 0) sb.Append((char)c);
            return sb.ToString();
        }

        static int BE32(BinaryReader r) { var b = r.ReadBytes(4); return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]; }
        static short BE16(BinaryReader r) { var b = r.ReadBytes(2); return (short)((b[0] << 8) | b[1]); }
        static long BE64(BinaryReader r) { long hi = (uint)BE32(r), lo = (uint)BE32(r); return (hi << 32) | lo; }
    }

    /* A Unity serialized file inside the bundle: the object table and just enough to read the
       bundle's index (which path each object is) and the textures. */
    public sealed class UnitySerialized
    {
        readonly IUnityData b;
        readonly long baseOff;
        long dataOffset;
        int version;
        public readonly Dictionary<long, (long Start, long Size, int ClassId)> Objetos = new();
        public readonly Dictionary<string, long> Contenedor = new(StringComparer.OrdinalIgnoreCase);

        public UnitySerialized(IUnityData bundle, long offset, long size)
        {
            b = bundle; baseOff = offset;
            var h = new Reader(b.Read(baseOff, (int)Math.Min(size, 64)), bigEndian: true);
            h.I32(); h.I32();                               // metadata and file size (old versions)
            version = h.I32();
            int dataOff32 = h.I32();
            if (version >= 22)
            {
                h.Pos += 4;                                 // endianness and reserved
                h.I32();                                    // metadata size
                h.I64();                                    // file size
                dataOffset = h.I64();
                h.I64();
            }
            else
            {
                dataOffset = dataOff32;
                h.Pos += 4;                                 // endianness and reserved
            }
            int cab = h.Pos;

            // the metadata fits easily in the first few megabytes
            int meta = (int)Math.Min(size - cab, Math.Max(dataOffset - cab, 1 << 20));
            var r = new Reader(b.Read(baseOff + cab, meta), bigEndian: false);
            r.CString();                                    // Unity version
            r.I32();                                        // platform
            bool arbol = r.U8() != 0;
            var clases = new List<int>();
            int nTipos = r.I32();
            for (int i = 0; i < nTipos; i++)
            {
                int classId = r.I32();
                r.U8();                                     // stripped type
                short script = r.I16();
                if (classId == 114 || script >= 0) r.Pos += 16;
                r.Pos += 16;                                // type hash
                if (arbol)
                {
                    int nodos = r.I32(), cadenas = r.I32();
                    r.Pos += nodos * (version >= 19 ? 32 : 24) + cadenas;
                    if (version >= 21) { int dep = r.I32(); r.Pos += dep * 4; }
                }
                clases.Add(classId);
            }
            int nObj = r.I32();
            for (int i = 0; i < nObj; i++)
            {
                r.Pos = (r.Pos + 3) & ~3;
                long pathId = r.I64();
                long start = version >= 22 ? r.I64() : (uint)r.I32();
                long sz = (uint)r.I32();
                int t = r.I32();
                Objetos[pathId] = (start, sz, t >= 0 && t < clases.Count ? clases[t] : -1);
            }
            // scripts and external files: which file each PPtr fileID points to
            try
            {
                int nScripts = r.I32();
                for (int i = 0; i < nScripts; i++)
                {
                    r.I32();
                    r.Pos = (r.Pos + 3) & ~3;
                    r.I64();
                }
                int nExt = r.I32();
                for (int i = 0; i < nExt; i++)
                {
                    r.CString();                            // empty
                    r.Pos += 16 + 4;                        // guid and type
                    Externos.Add(r.CString());
                }
            }
            catch (Exception) { }
            LeerContenedor();
        }

        /* The files pointed to by PPtrs with fileID > 0, in order (fileID 1 is the first).
           Paths like «sharedassets0.assets» or «library/unity default resources». */
        public readonly List<string> Externos = new();

        public int Version => version;
        public long DataOffset => dataOffset;

        /* An object's bytes, to read it according to its class. */
        public byte[] Leer(long pathId) => Objeto(pathId);

        /* Only the start of an object: to look at the name of a large one without reading it whole. */
        public byte[] LeerInicio(long pathId, int n)
        {
            var o = Objetos[pathId];
            return b.Read(baseOff + dataOffset + o.Start, (int)Math.Min(n, o.Size));
        }

        byte[] Objeto(long pathId)
        {
            var o = Objetos[pathId];
            return b.Read(baseOff + dataOffset + o.Start, (int)o.Size);
        }

        /* The name of a named object (texture, mesh, material...): it comes first. */
        public string Nombre(long pathId)
        {
            var o = Objetos[pathId];
            if (o.Size < 4) return null;
            var cab = b.Read(baseOff + dataOffset + o.Start, 4);
            int n = BitConverter.ToInt32(cab, 0);
            if (n <= 0 || n > 512 || n > o.Size - 4) return null;
            return Encoding.UTF8.GetString(b.Read(baseOff + dataOffset + o.Start + 4, n));
        }

        /* The AssetBundle object (class 142) carries the index: path of each resource -> object. */
        void LeerContenedor()
        {
            foreach (var kv in Objetos)
            {
                if (kv.Value.ClassId != 142) continue;
                var r = new Reader(Objeto(kv.Key), false);
                r.Str();                                    // name
                int pre = r.I32();
                r.Pos += pre * 12;                          // preload table: PPtr (int32 + int64)
                int n = r.I32();
                for (int i = 0; i < n; i++)
                {
                    string ruta = r.Str();
                    r.I32(); r.I32();                       // preload index and size
                    r.I32();                                // fileID
                    long pathId = r.I64();
                    Contenedor[ruta] = pathId;
                }
                break;
            }
        }

        /* Finds a resource by the end of its path, ignoring case and extension (the index
           usually stores «assets/...» in lowercase). */
        public long? Buscar(string ruta)
        {
            string clave = Normalizar(ruta);
            foreach (var kv in Contenedor)
            {
                string k = Normalizar(kv.Key);
                if (k.EndsWith(clave, StringComparison.Ordinal) || clave.EndsWith(k, StringComparison.Ordinal)) return kv.Value;
            }
            return null;
        }

        static string Normalizar(string s)
        {
            s = s.Replace('\\', '/').ToLowerInvariant();
            int punto = s.LastIndexOf('.');
            if (punto > s.LastIndexOf('/')) s = s.Substring(0, punto);
            return s;
        }

        /* A texture: Unity format, size, mipmaps and the bytes, which can come in the object
           itself or in the bundle's resource file (.resS). */
        public sealed class Textura
        {
            public string Nombre;
            public int Ancho, Alto, Formato, Mips;
            public byte[] Datos;
        }

        public Textura LeerTextura(long pathId, Func<string, (long Offset, long Size)?> recurso) =>
            LeerTextura(pathId, (path, off, size) => recurso(path) is var res && res != null ? b.Read(res.Value.Offset + off, size) : null);

        /* Same thing with a function that reads bytes from the resource file: in a loose game
           file the .resS is another file. */
        public Textura LeerTextura(long pathId, Func<string, long, int, byte[]> recurso)
        {
            var datos = Objeto(pathId);
            // after the fixed header there's a group of booleans whose count changes between
            // versions; each possibility is tried and validated against what has to hold
            foreach (int bools in new[] { 4, 5, 3, 6 })
            {
                try
                {
                    var t = Probar(datos, bools, recurso);
                    if (t != null) return t;
                }
                catch { }
            }
            throw new InvalidDataException("no reconozco la estructura de la textura");
        }

        Textura Probar(byte[] d, int bools, Func<string, long, int, byte[]> recurso)
        {
            var r = new Reader(d, false);
            var t = new Textura { Nombre = r.Str() };
            r.I32();                                        // fallback format
            r.U8(); r.Pos = (r.Pos + 3) & ~3;               // fallback reduction
            t.Ancho = r.I32(); t.Alto = r.I32();
            int completo = r.I32();
            t.Formato = r.I32();
            t.Mips = r.I32();
            r.Pos += bools; r.Pos = (r.Pos + 3) & ~3;
            r.I32();                                        // streaming priority
            int imagenes = r.I32(), dimension = r.I32();
            if (t.Ancho <= 0 || t.Alto <= 0 || t.Ancho > 16384 || t.Alto > 16384) return null;
            if (imagenes != 1 || dimension != 2 || t.Mips < 1 || t.Mips > 15) return null;
            r.Pos += 6 * 4;                                 // filtering and wrap settings
            r.I32(); r.I32();                               // lightmap and color space
            int n = r.I32();
            if (n > 0)
            {
                t.Datos = r.Bytes(n);
            }
            else
            {
                r.Pos = (r.Pos + 3) & ~3;
                long off = version >= 22 ? r.I64() : (uint)r.I32();
                long size = (uint)r.I32();
                string path = r.Str();
                if (size <= 0 || size > (long)completo * 2 + 1024) return null;
                t.Datos = recurso(path, off, (int)size);
                if (t.Datos == null) return null;
            }
            return t;
        }

        /* Byte reading with the appropriate endianness. */
        sealed class Reader
        {
            readonly byte[] d; readonly bool be;
            public int Pos;
            public Reader(byte[] data, bool bigEndian) { d = data; be = bigEndian; }
            public byte U8() => d[Pos++];
            public short I16() { short v = be ? (short)((d[Pos] << 8) | d[Pos + 1]) : BitConverter.ToInt16(d, Pos); Pos += 2; return v; }
            public int I32()
            {
                int v = be ? (d[Pos] << 24) | (d[Pos + 1] << 16) | (d[Pos + 2] << 8) | d[Pos + 3] : BitConverter.ToInt32(d, Pos);
                Pos += 4; return v;
            }
            public long I64()
            {
                long v;
                if (be) { long hi = (uint)I32(), lo = (uint)I32(); return (hi << 32) | lo; }
                v = BitConverter.ToInt64(d, Pos); Pos += 8; return v;
            }
            public byte[] Bytes(int n) { var o = new byte[n]; Buffer.BlockCopy(d, Pos, o, 0, n); Pos += n; Pos = (Pos + 3) & ~3; return o; }
            public string Str()
            {
                int n = I32();
                string s = Encoding.UTF8.GetString(d, Pos, n);
                Pos += n; Pos = (Pos + 3) & ~3;
                return s;
            }
            public string CString()
            {
                int ini = Pos;
                while (d[Pos] != 0) Pos++;
                string s = Encoding.ASCII.GetString(d, ini, Pos - ini);
                Pos++;
                return s;
            }
        }
    }
}
