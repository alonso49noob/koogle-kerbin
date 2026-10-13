using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KerbinMaps.Core
{
    /* A flight recorded by the Koogle Recorder mod (recorder/ in this repository): samples of
       position, orientation and velocities, copies of the vessel each time its parts change,
       and events. The format is described in recorder/src/KoogleRecorder.cs. */
    public sealed class Grabacion
    {
        public sealed class Muestra
        {
            public double Ut, Lat, Lon, Alt, VelVertical;
            public double[] Rot = { 0, 0, 0, 1 }, VelOrbita = new double[3], VelSuperficie = new double[3];
            public float Acelerador, Masa, G;
            public int Etapa;
            public string Cuerpo, Situacion;
        }

        public string Ruta, Nave, Partida, Version, Tipo;
        public double Inicio;
        public bool Cerrada;                       // has its END line: the flight finished
        public readonly List<Muestra> Muestras = new();
        public readonly List<(double Ut, Vessel Nave)> Fotos = new();
        public readonly List<(double Ut, string Texto)> Eventos = new();

        public double Fin => Muestras.Count > 0 ? Muestras[^1].Ut : Inicio;
        public double Duracion => Fin - Inicio;
        public string CuerpoInicial => Muestras.FirstOrDefault()?.Cuerpo;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /* Only the header and the first and last samples: enough for the list, without
           reading the whole file. */
        public static Grabacion Cabecera(string ruta)
        {
            var g = new Grabacion { Ruta = ruta };
            string cuerpo = null;
            string ultima = null;
            int leidas = 0;
            using var sr = new StreamReader(new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            string l;
            while ((l = sr.ReadLine()) != null)
            {
                if (l.StartsWith("S ")) { if (g.Muestras.Count == 0) g.Muestras.Add(ParseMuestra(l, cuerpo)); ultima = l; }
                else if (l.StartsWith("B ")) cuerpo = l.Substring(2).Trim();
                else if (l.StartsWith("END ")) g.Cerrada = true;
                else if (leidas < 12) LeerClave(g, l);
                leidas++;
            }
            if (ultima != null && g.Muestras.Count > 0) g.Muestras.Add(ParseMuestra(ultima, cuerpo));
            return g;
        }

        public static Grabacion Leer(string ruta)
        {
            var g = new Grabacion { Ruta = ruta };
            string cuerpo = null;
            StringBuilder foto = null;
            double fotoUt = 0;
            // shared read: the mod may still be writing it
            using var sr = new StreamReader(new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            string l;
            while ((l = sr.ReadLine()) != null)
            {
                if (foto != null)
                {
                    if (l.Trim() == "ENDSNAP")
                    {
                        var v = NaveDeFoto(foto.ToString());
                        if (v != null) g.Fotos.Add((fotoUt, v));
                        foto = null;
                    }
                    else foto.AppendLine(l);
                    continue;
                }
                if (l.StartsWith("S ")) { var m = ParseMuestra(l, cuerpo); if (m != null) g.Muestras.Add(m); }
                else if (l.StartsWith("B ")) cuerpo = l.Substring(2).Trim();
                else if (l.StartsWith("SNAP ")) { foto = new StringBuilder(); fotoUt = Num(l.Substring(5)); }
                else if (l.StartsWith("EVT "))
                {
                    var p = l.Substring(4).Split(new[] { ' ' }, 2);
                    g.Eventos.Add((Num(p[0]), p.Length > 1 ? p[1] : ""));
                }
                else if (l.StartsWith("END ")) g.Cerrada = true;
                else LeerClave(g, l);
            }
            return g;
        }

        static void LeerClave(Grabacion g, string l)
        {
            int eq = l.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0) return;
            string k = l.Substring(0, eq), v = l.Substring(eq + 3);
            switch (k)
            {
                case "vessel": g.Nave = v; break;
                case "save": g.Partida = v; break;
                case "recorder": g.Version = v; break;
                case "type": g.Tipo = v; break;
                case "start": g.Inicio = Num(v); break;
            }
        }

        /* The vessel copy as KSP writes it. ConfigNode.ToString may leave out the node's own
           name, so it's wrapped in a VESSEL node if it doesn't start with one. */
        static Vessel NaveDeFoto(string texto)
        {
            string t = texto.TrimStart();
            if (!t.StartsWith("VESSEL")) t = "VESSEL\n" + (t.StartsWith("{") ? t : "{\n" + t + "\n}");
            try { return SaveFile.Parse("UT = 1\n" + t).Vessels.FirstOrDefault(); }
            catch { return null; }
        }

        static Muestra ParseMuestra(string l, string cuerpo)
        {
            var p = l.Substring(2).Split(';');
            if (p.Length < 20) return null;
            return new Muestra
            {
                Ut = Num(p[0]), Lat = Num(p[1]), Lon = Num(p[2]), Alt = Num(p[3]),
                Rot = new[] { Num(p[4]), Num(p[5]), Num(p[6]), Num(p[7]) },
                VelOrbita = new[] { Num(p[8]), Num(p[9]), Num(p[10]) },
                VelSuperficie = new[] { Num(p[11]), Num(p[12]), Num(p[13]) },
                VelVertical = Num(p[14]), Acelerador = (float)Num(p[15]),
                Etapa = int.TryParse(p[16], NumberStyles.Integer, Inv, out int e) ? e : 0,
                Masa = (float)Num(p[17]), G = (float)Num(p[18]), Situacion = p[19].Trim(), Cuerpo = cuerpo,
            };
        }

        static double Num(string s) =>
            double.TryParse(s.Trim(), NumberStyles.Float, Inv, out double v) && double.IsFinite(v) ? v : 0;

        /* The state at time `ut`, interpolated between the two samples around it (and held at
           the ends). Longitude goes the short way round and the rotation is a normalized lerp. */
        public Muestra En(double ut)
        {
            if (Muestras.Count == 0) return null;
            if (ut <= Muestras[0].Ut) return Muestras[0];
            if (ut >= Muestras[^1].Ut) return Muestras[^1];
            int lo = 0, hi = Muestras.Count - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Muestras[mid].Ut <= ut) lo = mid; else hi = mid; }
            var a = Muestras[lo]; var b = Muestras[hi];
            if (a.Cuerpo != b.Cuerpo) return ut - a.Ut < b.Ut - ut ? a : b;
            double f = (ut - a.Ut) / Math.Max(1e-9, b.Ut - a.Ut);
            double dLon = ((b.Lon - a.Lon + 540) % 360) - 180;
            double qd = a.Rot[0] * b.Rot[0] + a.Rot[1] * b.Rot[1] + a.Rot[2] * b.Rot[2] + a.Rot[3] * b.Rot[3];
            double s = qd < 0 ? -1 : 1;                 // the same rotation can have either sign
            var q = new double[4];
            for (int k = 0; k < 4; k++) q[k] = a.Rot[k] + (s * b.Rot[k] - a.Rot[k]) * f;
            double n = Math.Sqrt(q.Sum(x => x * x));
            if (n > 1e-9) for (int k = 0; k < 4; k++) q[k] /= n;
            double L(double x, double y) => x + (y - x) * f;
            double[] V(double[] x, double[] y) => new[] { L(x[0], y[0]), L(x[1], y[1]), L(x[2], y[2]) };
            return new Muestra
            {
                Ut = ut, Lat = L(a.Lat, b.Lat), Lon = Geo.WrapLon(a.Lon + dLon * f), Alt = L(a.Alt, b.Alt), Rot = q,
                VelOrbita = V(a.VelOrbita, b.VelOrbita), VelSuperficie = V(a.VelSuperficie, b.VelSuperficie),
                VelVertical = L(a.VelVertical, b.VelVertical), Acelerador = (float)L(a.Acelerador, b.Acelerador),
                Masa = (float)L(a.Masa, b.Masa), G = (float)L(a.G, b.G), Etapa = a.Etapa, Situacion = a.Situacion, Cuerpo = a.Cuerpo,
            };
        }

        /* The vessel as it was at `ut`: the last copy taken before then. */
        public Vessel NaveEn(double ut)
        {
            Vessel v = null;
            foreach (var (t, n) in Fotos) { if (t <= ut + 1e-6 || v == null) v = n; else break; }
            return v;
        }
    }
}
