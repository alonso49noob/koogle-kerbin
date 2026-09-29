using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KerbinMaps.Ksp
{
    /* Lector de paquetes de Unity (UnityFS), lo justo para sacar texturas.

       Algunos mods (Parallax, por ejemplo) no dejan sus texturas sueltas en GameData sino
       dentro de un paquete de Unity de varios gigas. El paquete es una lista de bloques
       comprimidos con LZ4 de unos 128 KB; dentro van un fichero serializado de Unity (con
       la tabla de objetos) y un fichero de recursos con los bytes de las imágenes. Como los
       bloques se pueden descomprimir por separado, aquí solo se lee lo que hace falta: la
       cabecera, la tabla de objetos y las texturas que se piden, no los 2 GB.

       Formatos que se entienden: UnityFS versiones 6 y 7, ficheros serializados de la 17 a
       la 22 (Unity 2017 a 2020), bloques sin comprimir o con LZ4/LZ4HC. */
    /* De dónde salen los bytes de un fichero serializado: un paquete (descomprimiendo sus
       bloques) o un fichero suelto de los datos del juego. */
    public interface IUnityData
    {
        byte[] Read(long pos, int n);
    }

    /* Un fichero suelto de KSP_x64_Data (sharedassets9.assets, su .resS...), leído a trozos. */
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
            CString(r); CString(r);                         // «5.x.x» y la versión de Unity
            long total = BE64(r);
            int infoComp = BE32(r), infoUncomp = BE32(r), flags = BE32(r);
            if (version >= 7) Alinear(16);

            byte[] infoRaw;
            long datos;
            if ((flags & 0x80) != 0)
            {
                // la tabla de bloques va al final del fichero
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
                // con el bit 0x200 los datos empiezan en el siguiente múltiplo de 16
                if ((flags & 0x200) != 0) datos = (datos + 15) & ~15L;
            }
            byte[] info = Descomprimir(infoRaw, infoUncomp, flags & 0x3F);

            var ir = new BinaryReader(new MemoryStream(info));
            ir.ReadBytes(16);                               // hash de los datos sin comprimir
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
                BE32(ir);                                   // flags del nodo
                nodos.Add((off, size, CString(ir)));
            }
        }

        public void Dispose() => f.Dispose();

        /* Lee `n` bytes de la secuencia descomprimida a partir de `pos`. */
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

        /* LZ4 por bloques (no el formato de marco): cada secuencia es un token con la
           longitud de los literales y la de la copia, los literales y un desplazamiento. */
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
                if (ip >= src.Length) break;                // la última secuencia no tiene copia
                int off = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                int len = token & 15;
                if (len == 15) { int b; do { b = src[ip++]; len += b; } while (b == 255); }
                len += 4;
                int from = op - off;
                if (off >= len) { Buffer.BlockCopy(dst, from, dst, op, len); op += len; }
                else for (int i = 0; i < len; i++) dst[op++] = dst[from + i];     // copia solapada
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

    /* Un fichero serializado de Unity dentro del paquete: la tabla de objetos y lo justo
       para leer el índice del paquete (qué ruta es cada objeto) y las texturas. */
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
            h.I32(); h.I32();                               // tamaño de metadatos y del fichero (viejos)
            version = h.I32();
            int dataOff32 = h.I32();
            if (version >= 22)
            {
                h.Pos += 4;                                 // endianness y reservados
                h.I32();                                    // tamaño de metadatos
                h.I64();                                    // tamaño del fichero
                dataOffset = h.I64();
                h.I64();
            }
            else
            {
                dataOffset = dataOff32;
                h.Pos += 4;                                 // endianness y reservados
            }
            int cab = h.Pos;

            // los metadatos caben de sobra en los primeros megas
            int meta = (int)Math.Min(size - cab, Math.Max(dataOffset - cab, 1 << 20));
            var r = new Reader(b.Read(baseOff + cab, meta), bigEndian: false);
            r.CString();                                    // versión de Unity
            r.I32();                                        // plataforma
            bool arbol = r.U8() != 0;
            var clases = new List<int>();
            int nTipos = r.I32();
            for (int i = 0; i < nTipos; i++)
            {
                int classId = r.I32();
                r.U8();                                     // tipo recortado
                short script = r.I16();
                if (classId == 114 || script >= 0) r.Pos += 16;
                r.Pos += 16;                                // hash del tipo
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
            // los scripts y los ficheros externos: a qué fichero apunta cada fileID de un PPtr
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
                    r.CString();                            // vacío
                    r.Pos += 16 + 4;                        // guid y tipo
                    Externos.Add(r.CString());
                }
            }
            catch (Exception) { }
            LeerContenedor();
        }

        /* Los ficheros a los que apuntan los PPtr con fileID > 0, en orden (fileID 1 es el
           primero). Rutas como «sharedassets0.assets» o «library/unity default resources». */
        public readonly List<string> Externos = new();

        public int Version => version;

        /* Los bytes de un objeto, para leerlo según su clase. */
        public byte[] Leer(long pathId) => Objeto(pathId);

        byte[] Objeto(long pathId)
        {
            var o = Objetos[pathId];
            return b.Read(baseOff + dataOffset + o.Start, (int)o.Size);
        }

        /* El nombre de un objeto con nombre (textura, malla, material...): va al principio. */
        public string Nombre(long pathId)
        {
            var o = Objetos[pathId];
            if (o.Size < 4) return null;
            var cab = b.Read(baseOff + dataOffset + o.Start, 4);
            int n = BitConverter.ToInt32(cab, 0);
            if (n <= 0 || n > 512 || n > o.Size - 4) return null;
            return Encoding.UTF8.GetString(b.Read(baseOff + dataOffset + o.Start + 4, n));
        }

        /* El objeto AssetBundle (clase 142) lleva el índice: ruta de cada recurso -> objeto. */
        void LeerContenedor()
        {
            foreach (var kv in Objetos)
            {
                if (kv.Value.ClassId != 142) continue;
                var r = new Reader(Objeto(kv.Key), false);
                r.Str();                                    // nombre
                int pre = r.I32();
                r.Pos += pre * 12;                          // tabla de precarga: PPtr (int32 + int64)
                int n = r.I32();
                for (int i = 0; i < n; i++)
                {
                    string ruta = r.Str();
                    r.I32(); r.I32();                       // índice y tamaño de precarga
                    r.I32();                                // fileID
                    long pathId = r.I64();
                    Contenedor[ruta] = pathId;
                }
                break;
            }
        }

        /* Busca un recurso por el final de su ruta, sin distinguir mayúsculas ni extensión
           (el índice suele guardar «assets/...» en minúsculas). */
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

        /* Una textura: formato de Unity, tamaño, mipmaps y los bytes, que pueden venir en el
           propio objeto o en el fichero de recursos del paquete (.resS). */
        public sealed class Textura
        {
            public string Nombre;
            public int Ancho, Alto, Formato, Mips;
            public byte[] Datos;
        }

        public Textura LeerTextura(long pathId, Func<string, (long Offset, long Size)?> recurso) =>
            LeerTextura(pathId, (path, off, size) => recurso(path) is var res && res != null ? b.Read(res.Value.Offset + off, size) : null);

        /* Lo mismo con una función que lee los bytes del fichero de recursos: en un fichero
           suelto del juego el .resS es otro fichero. */
        public Textura LeerTextura(long pathId, Func<string, long, int, byte[]> recurso)
        {
            var datos = Objeto(pathId);
            // tras la cabecera fija hay un grupo de booleanos cuyo número cambia entre
            // versiones; se prueba cada posibilidad y se valida con lo que tiene que cumplir
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
            r.I32();                                        // formato de reserva
            r.U8(); r.Pos = (r.Pos + 3) & ~3;               // reducción de reserva
            t.Ancho = r.I32(); t.Alto = r.I32();
            int completo = r.I32();
            t.Formato = r.I32();
            t.Mips = r.I32();
            r.Pos += bools; r.Pos = (r.Pos + 3) & ~3;
            r.I32();                                        // prioridad de streaming
            int imagenes = r.I32(), dimension = r.I32();
            if (t.Ancho <= 0 || t.Alto <= 0 || t.Ancho > 16384 || t.Alto > 16384) return null;
            if (imagenes != 1 || dimension != 2 || t.Mips < 1 || t.Mips > 15) return null;
            r.Pos += 6 * 4;                                 // ajustes de filtrado y repetición
            r.I32(); r.I32();                               // lightmap y espacio de color
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

        /* Lectura de bytes con la endianness que toque. */
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
