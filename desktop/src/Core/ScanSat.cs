using System;
using System.Collections.Generic;

namespace KerbinMaps.Core
{
    /* SCANsat's sensors, with the values the mod uses (SCANsat.SCAN_Data.SCANtype). Each map
       cell stores the sum of those that have already passed over it. */
    [Flags]
    public enum ScanType
    {
        Nada = 0,
        AlturaBaja = 1,
        AlturaAlta = 2,
        Altura = 3,
        VisualBaja = 4,
        Biomas = 8,
        Anomalia = 16,
        AnomaliaDetalle = 32,
        VisualAlta = 64,
        RecursoBajo = 128,
        RecursoAlto = 256,
        Todo = 511,
    }

    /* SCANsat coverage of a body: one cell per degree of latitude and longitude, with the
       sensors that have passed over it.

       The mod stores it in the save as an Int16[360,180] compressed with LZF and in base64 with
       «/» changed to «-» and «=» to «_». Inside the compressed block there's a .NET
       serialization (BinaryFormatter) of a byte[], whose header is 27 bytes and whose trailer
       is one more. */
    public sealed class ScanCoverage
    {
        public const int W = 360, H = 180;

        public string Body;
        public short[] Cells;                       // [lon+180, lat+90] in order lon*180+lat
        public double MinHeight = double.NaN, MaxHeight = double.NaN;   // the range of the mod's palette
        public string Palette;

        public bool Empty
        {
            get
            {
                foreach (var c in Cells) if (c != 0) return false;
                return true;
            }
        }

        public short At(double lat, double lon)
        {
            int y = (int)Math.Floor(lat) + 90, x = (int)Math.Floor(Geo.WrapLon(lon)) + 180;
            if (y < 0) y = 0; else if (y >= H) y = H - 1;
            x = ((x % W) + W) % W;
            return Cells[x * H + y];
        }

        /* As in the mod: it's enough for the cell to have any of the requested sensors.
           «Altura» is two (low and high) and either one already gives altimetry. */
        public bool Has(double lat, double lon, ScanType t) => ((ScanType)At(lat, lon) & t) != 0;

        public bool HasAll(double lat, double lon, ScanType t) => ((ScanType)At(lat, lon) & t) == t;

        /* Percentage of surface with that sensor, weighted by cos(lat): in a degree grid the
           polar cells are tiny and without weighting it would come out heavily inflated. */
        public double Percent(ScanType t)
        {
            double tot = 0, ok = 0;
            for (int y = 0; y < H; y++)
            {
                double w = Math.Cos((y + 0.5 - 90) * Geo.D2R);
                for (int x = 0; x < W; x++)
                {
                    tot += w;
                    if (((ScanType)Cells[x * H + y] & t) != 0) ok += w;
                }
            }
            return tot <= 0 ? 0 : ok / tot * 100;
        }

        /* The sensors that show up in some cell. */
        public ScanType Sensors()
        {
            int all = 0;
            foreach (var c in Cells) all |= (ushort)c;
            return (ScanType)(all & (int)ScanType.Todo);
        }

        public static ScanCoverage FromBlob(string body, string blob)
        {
            var cov = new ScanCoverage { Body = body, Cells = new short[W * H] };
            if (string.IsNullOrWhiteSpace(blob)) return cov;
            byte[] raw;
            try
            {
                byte[] packed = Convert.FromBase64String(blob.Trim().Replace('-', '/').Replace('_', '='));
                raw = Lzf.Decompress(packed);
            }
            catch { return cov; }

            int off = Payload(raw);
            if (off < 0 || raw.Length - off < W * H * 2) return cov;
            Buffer.BlockCopy(raw, off, cov.Cells, 0, W * H * 2);
            return cov;
        }

        /* Where the byte[] starts inside the .NET serialization. The normal header is 27 bytes
           (17 of stream header + primitive array record), but if that doesn't fit it's located
           from the end, which is the only thing that really matters. */
        static int Payload(byte[] d)
        {
            const int n = W * H * 2;
            if (d.Length >= 27 + n && d[17] == 0x0F && BitConverter.ToInt32(d, 22) == n) return 27;
            if (d.Length == n) return 0;
            return d.Length >= n ? d.Length - n - (d.Length > n && d[^1] == 0x0B ? 1 : 0) : -1;
        }
    }

    /* LZF (liblzf), which is what SCANsat uses to compress the coverage. Decompressing means
       reading control bytes: less than 32 means «copy that many literals»; the rest is a
       back-reference into what's already written. */
    public static class Lzf
    {
        public static byte[] Decompress(byte[] input)
        {
            var output = new byte[Math.Max(64, input.Length * 4)];
            int ip = 0, op = 0;

            while (ip < input.Length)
            {
                int ctrl = input[ip++];
                if (ctrl < 32)
                {
                    int len = ctrl + 1;
                    output = Ensure(output, op + len);
                    if (ip + len > input.Length) len = input.Length - ip;
                    Buffer.BlockCopy(input, ip, output, op, len);
                    ip += len; op += len;
                }
                else
                {
                    int len = ctrl >> 5;
                    int reference = op - ((ctrl & 0x1f) << 8) - 1;
                    if (ip >= input.Length) break;
                    if (len == 7) { len += input[ip++]; if (ip >= input.Length) break; }
                    reference -= input[ip++];
                    if (reference < 0) throw new InvalidOperationException("LZF: referencia fuera del flujo");
                    output = Ensure(output, op + len + 2);
                    for (int i = 0; i < len + 2; i++) output[op++] = output[reference++];
                }
            }
            if (op == output.Length) return output;
            var trimmed = new byte[op];
            Buffer.BlockCopy(output, 0, trimmed, 0, op);
            return trimmed;
        }

        static byte[] Ensure(byte[] buf, int need)
        {
            if (need <= buf.Length) return buf;
            int size = buf.Length;
            while (size < need) size *= 2;
            var bigger = new byte[size];
            Buffer.BlockCopy(buf, 0, bigger, 0, buf.Length);
            return bigger;
        }
    }
}
