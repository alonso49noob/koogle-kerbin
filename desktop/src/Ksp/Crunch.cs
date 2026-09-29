using System;
using System.Collections.Generic;
using System.IO;

namespace KerbinMaps.Ksp
{
    /* Texturas Crunch de Unity (formatos 28, DXT1 crunched, y 29, DXT5 crunched).

       Crunch guarda una textura DXT como dos paletas (extremos y selectores de bloque) y,
       por cada bloque, índices a esas paletas codificados con Huffman. Unity usa desde la
       2017.3 su propia variante del formato: los bloques se agrupan de 2 en 2 con una
       «referencia» que dice si el bloque trae extremos nuevos, repite los del de la
       izquierda o los del de arriba, y los selectores se guardan como XOR con el anterior.

       Aquí se deshace eso hasta los bloques DXT de siempre, que van tal cual a la GPU. En
       el juego es el formato de las máscaras del suelo del KSC (dónde va hierba y dónde
       asfalto u hormigón en la pista, la rampa y los edificios). */
    public static class Crunch
    {
        public sealed class Resultado
        {
            public int Ancho, Alto;
            public bool Dxt5;
            public readonly List<(int W, int H, byte[] Bloques)> Niveles = new();
        }

        /* Los niveles de mipmap de no más de `maxAncho` de ancho, ya en bloques DXT. */
        public static Resultado Decodificar(byte[] d, int maxAncho)
        {
            if (d.Length < 74 || d[0] != 'H' || d[1] != 'x') throw new InvalidDataException("no es un fichero crunch");
            int U(int ofs, int n) { int v = 0; for (int i = 0; i < n; i++) v = v << 8 | d[ofs + i]; return v; }
            int tamDatos = U(6, 4);
            var r = new Resultado { Ancho = U(12, 2), Alto = U(14, 2) };
            int niveles = d[16], caras = d[17], formato = d[18];
            if (caras != 1) throw new InvalidDataException("crunch con " + caras + " caras");
            if (formato != 0 && formato != 2) throw new InvalidDataException("crunch de formato " + formato);
            r.Dxt5 = formato == 2;
            (int Ofs, int Tam, int Num) Paleta(int o) => (U(o, 3), U(o + 3, 3), U(o + 6, 2));
            var colE = Paleta(33); var colS = Paleta(41); var alfE = Paleta(49); var alfS = Paleta(57);
            int tamTablas = U(65, 2), ofsTablas = U(67, 3);
            if (tamDatos > d.Length) tamDatos = d.Length;

            // las tablas de Huffman de los bloques
            var t = new Bits(d, ofsTablas, tamTablas);
            var referencia = t.Modelo();
            Huffman extremoCol = null, selectorCol = null, extremoAlfa = null, selectorAlfa = null;
            if (colE.Num > 0) { extremoCol = t.Modelo(); selectorCol = t.Modelo(); }
            if (alfE.Num > 0) { extremoAlfa = t.Modelo(); selectorAlfa = t.Modelo(); }

            // la paleta de extremos de color: deltas de R, G y B de los dos colores 565
            var extremos = new uint[colE.Num];
            if (colE.Num > 0)
            {
                var b = new Bits(d, colE.Ofs, colE.Tam);
                var m0 = b.Modelo(); var m1 = b.Modelo();
                uint a = 0, bb = 0, c = 0, dd = 0, e = 0, f = 0;
                for (int i = 0; i < colE.Num; i++)
                {
                    a = (a + (uint)b.Leer(m0)) & 31;
                    bb = (bb + (uint)b.Leer(m1)) & 63;
                    c = (c + (uint)b.Leer(m0)) & 31;
                    dd = (dd + (uint)b.Leer(m0)) & 31;
                    e = (e + (uint)b.Leer(m1)) & 63;
                    f = (f + (uint)b.Leer(m0)) & 31;
                    extremos[i] = c | bb << 5 | a << 11 | f << 16 | e << 21 | dd << 27;
                }
            }

            // la de selectores: XOR por grupos de 4 bits con el anterior, en orden lineal
            // (0, 1/3, 2/3, 1), que se pasa al orden de DXT (0, 1, 1/3, 2/3)
            var selectores = new uint[colS.Num];
            if (colS.Num > 0)
            {
                var b = new Bits(d, colS.Ofs, colS.Tam);
                var m = b.Modelo();
                uint s = 0;
                for (int i = 0; i < colS.Num; i++)
                {
                    for (int j = 0; j < 32; j += 4) s ^= (uint)b.Leer(m) << j;
                    selectores[i] = ((s ^ s << 1) & 0xAAAAAAAA) | (s >> 1 & 0x55555555);
                }
            }

            // las de alfa (DXT5)
            var extremosAlfa = new ushort[alfE.Num];
            if (alfE.Num > 0)
            {
                var b = new Bits(d, alfE.Ofs, alfE.Tam);
                var m = b.Modelo();
                uint a = 0, bb = 0;
                for (int i = 0; i < alfE.Num; i++)
                {
                    a = (a + (uint)b.Leer(m)) & 255;
                    bb = (bb + (uint)b.Leer(m)) & 255;
                    extremosAlfa[i] = (ushort)(a | bb << 8);
                }
            }
            var selectoresAlfa = new ushort[alfS.Num * 3];
            if (alfS.Num > 0)
            {
                ReadOnlySpan<uint> dxt5DeLineal = stackalloc uint[] { 0, 2, 3, 4, 5, 6, 7, 1 };
                var b = new Bits(d, alfS.Ofs, alfS.Tam);
                var m = b.Modelo();
                uint l0 = 0, l1 = 0;
                for (int i = 0; i < alfS.Num; i++)
                {
                    uint s0 = 0, s1 = 0;
                    for (int j = 0; j < 24; j += 3)
                    {
                        l0 ^= (uint)b.Leer(m) << j;
                        s0 |= dxt5DeLineal[(int)(l0 >> j & 7)] << j;
                    }
                    for (int j = 0; j < 24; j += 3)
                    {
                        l1 ^= (uint)b.Leer(m) << j;
                        s1 |= dxt5DeLineal[(int)(l1 >> j & 7)] << j;
                    }
                    selectoresAlfa[i * 3] = (ushort)s0;
                    selectoresAlfa[i * 3 + 1] = (ushort)(s0 >> 16 | s1 << 8);
                    selectoresAlfa[i * 3 + 2] = (ushort)(s1 >> 8);
                }
            }

            // los niveles: cada uno se decodifica por su cuenta, así que los grandes se saltan
            for (int nivel = 0; nivel < niveles; nivel++)
            {
                int w = Math.Max(1, r.Ancho >> nivel), h = Math.Max(1, r.Alto >> nivel);
                if (w > maxAncho) continue;
                int ini = U(70 + nivel * 4, 4);
                int fin = nivel + 1 < niveles ? U(70 + (nivel + 1) * 4, 4) : tamDatos;
                var b = new Bits(d, ini, fin - ini);
                r.Niveles.Add((w, h, Bloques(b, (w + 3) / 4, (h + 3) / 4, r.Dxt5, referencia,
                    extremoCol, selectorCol, extremoAlfa, selectorAlfa, extremos, selectores, extremosAlfa, selectoresAlfa)));
            }
            return r;
        }

        static byte[] Bloques(Bits c, int bw, int bh, bool dxt5, Huffman referencia,
            Huffman extremoCol, Huffman selectorCol, Huffman extremoAlfa, Huffman selectorAlfa,
            uint[] extremos, uint[] selectores, ushort[] extremosAlfa, ushort[] selectoresAlfa)
        {
            int tamBloque = dxt5 ? 16 : 8;
            var o = new byte[bw * bh * tamBloque];
            int ancho = (bw + 1) & ~1, alto = (bh + 1) & ~1;
            var refAbajo = new int[ancho];
            var colAbajo = new int[ancho];
            var alfAbajo = new int[ancho];
            int col = 0, alf = 0, grupo = 0;
            for (int y = 0; y < alto; y++)
                for (int x = 0; x < ancho; x++)
                {
                    // cada 2×2 bloques, una referencia por bloque: 0 extremos nuevos, 1 los del
                    // de la izquierda, 2 los del de arriba
                    if ((y & 1) == 0 && (x & 1) == 0) grupo = c.Leer(referencia);
                    int re;
                    if ((y & 1) != 0) re = refAbajo[x];
                    else
                    {
                        re = grupo & 3; grupo >>= 2;
                        refAbajo[x] = grupo & 3; grupo >>= 2;
                    }
                    if (re == 0)
                    {
                        col += c.Leer(extremoCol);
                        if (col >= extremos.Length) col -= extremos.Length;
                        colAbajo[x] = col;
                        if (dxt5)
                        {
                            alf += c.Leer(extremoAlfa);
                            if (alf >= extremosAlfa.Length) alf -= extremosAlfa.Length;
                            alfAbajo[x] = alf;
                        }
                    }
                    else if (re == 1)
                    {
                        colAbajo[x] = col;
                        alfAbajo[x] = alf;
                    }
                    else
                    {
                        col = colAbajo[x];
                        alf = alfAbajo[x];
                    }
                    int sc = c.Leer(selectorCol);
                    int sa = dxt5 ? c.Leer(selectorAlfa) : 0;
                    if (x >= bw || y >= bh) continue;
                    int p = (y * bw + x) * tamBloque;
                    if (dxt5)
                    {
                        ushort ea = extremosAlfa[alf];
                        o[p] = (byte)ea; o[p + 1] = (byte)(ea >> 8);
                        for (int k = 0; k < 3; k++)
                        {
                            ushort v = selectoresAlfa[sa * 3 + k];
                            o[p + 2 + k * 2] = (byte)v; o[p + 3 + k * 2] = (byte)(v >> 8);
                        }
                        p += 8;
                    }
                    BitConverter.TryWriteBytes(o.AsSpan(p, 4), extremos[col]);
                    BitConverter.TryWriteBytes(o.AsSpan(p + 4, 4), selectores[sc]);
                }
            return o;
        }

        /* Código de Huffman canónico: los símbolos, ordenados por longitud y luego por
           número, reciben códigos consecutivos. */
        sealed class Huffman
        {
            readonly int[] primero = new int[18], indice = new int[18], cuantos = new int[18];
            readonly int[] simbolos;

            public Huffman(byte[] longitudes)
            {
                int n = 0;
                foreach (var l in longitudes) if (l > 0) { cuantos[l]++; n++; }
                simbolos = new int[n];
                int codigo = 0, idx = 0;
                var siguiente = new int[18];
                for (int l = 1; l <= 16; l++)
                {
                    primero[l] = codigo; indice[l] = idx; siguiente[l] = idx;
                    codigo = (codigo + cuantos[l]) << 1;
                    idx += cuantos[l];
                }
                for (int s = 0; s < longitudes.Length; s++)
                    if (longitudes[s] > 0) simbolos[siguiente[longitudes[s]]++] = s;
            }

            public int Leer(Bits b)
            {
                int codigo = 0;
                for (int l = 1; l <= 16; l++)
                {
                    codigo = codigo << 1 | (int)b.Bits_(1);
                    int k = codigo - primero[l];
                    if (k >= 0 && k < cuantos[l]) return simbolos[indice[l] + k];
                }
                throw new InvalidDataException("código de Huffman inválido");
            }
        }

        /* Bits de más significativo a menos, como los escribe crunch. */
        sealed class Bits
        {
            readonly byte[] d;
            int pos;
            readonly int fin;
            uint buf;
            int cuenta;

            public Bits(byte[] d, int ofs, int tam)
            {
                if (ofs < 0 || tam < 0 || ofs + tam > d.Length) throw new InvalidDataException("crunch: bloque fuera del fichero");
                this.d = d; pos = ofs; fin = ofs + tam;
            }

            public uint Bits_(int n)
            {
                if (n == 0) return 0;
                if (n > 16) { uint a = Bits_(n - 16); return a << 16 | Bits_(16); }
                while (cuenta < n)
                {
                    uint c = pos < fin ? d[pos++] : 0u;
                    cuenta += 8;
                    buf |= c << (32 - cuenta);
                }
                uint r = buf >> (32 - n);
                buf <<= n;
                cuenta -= n;
                return r;
            }

            public int Leer(Huffman h) => h.Leer(this);

            static readonly int[] probables = { 17, 18, 19, 20, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15, 16 };

            /* Un modelo de Huffman tal como lo manda crunch: las longitudes de código,
               comprimidas a su vez con otro Huffman y con series de ceros y repeticiones. */
            public Huffman Modelo()
            {
                int total = (int)Bits_(14);
                var longitudes = new byte[total];
                if (total == 0) return new Huffman(longitudes);
                int enviados = (int)Bits_(5);
                if (enviados < 1 || enviados > 21) throw new InvalidDataException("crunch: tabla de longitudes inválida");
                var lc = new byte[21];
                for (int i = 0; i < enviados; i++) lc[probables[i]] = (byte)Bits_(3);
                var dm = new Huffman(lc);
                int ofs = 0;
                while (ofs < total)
                {
                    int resto = total - ofs;
                    int code = dm.Leer(this);
                    if (code <= 16) longitudes[ofs++] = (byte)code;
                    else if (code == 17 || code == 18)
                    {
                        int len = code == 17 ? (int)Bits_(3) + 3 : (int)Bits_(7) + 11;
                        if (len > resto) throw new InvalidDataException("crunch: serie de ceros demasiado larga");
                        ofs += len;
                    }
                    else if (code == 19 || code == 20)
                    {
                        int len = code == 19 ? (int)Bits_(2) + 3 : (int)Bits_(6) + 7;
                        if (ofs == 0 || len > resto || longitudes[ofs - 1] == 0) throw new InvalidDataException("crunch: repetición inválida");
                        byte prev = longitudes[ofs - 1];
                        for (int k = 0; k < len; k++) longitudes[ofs++] = prev;
                    }
                    else throw new InvalidDataException("crunch: código de longitud inválido");
                }
                return new Huffman(longitudes);
            }
        }
    }
}
