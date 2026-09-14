using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KerbinMaps.Core
{
    public sealed class Elements
    {
        public double Sma, Ecc, Inc, Lpe, Lan, Mna, Eph;
        public bool HasMna, HasEph;
    }

    /* Una pieza tal como la guarda la partida: su posición y giro respecto a la pieza
       raíz (en el marco de Unity) y las variantes elegidas. */
    public sealed class PartSnapshot
    {
        public string Name;
        public double[] Pos = { 0, 0, 0 };
        public double[] Rot = { 0, 0, 0, 1 };
        public string Variant;
        public readonly List<(string ModuleId, string Subtype)> B9 = new();
        /* Estado de los módulos que mueven piezas del modelo (paneles, antenas, patas),
           en el orden en que aparecen en la pieza. */
        public readonly List<(string Name, Dictionary<string, string> Fields)> Modules = new();
        /* Nodos de unión que tienen otra pieza enganchada. */
        public readonly HashSet<string> Attached = new(StringComparer.Ordinal);
    }

    public sealed class Vessel
    {
        public string Name, Type, Sit, BodyName;
        public double? Lat, Lon, Alt;
        public Elements Orbit;
        public double[] Rot = { 0, 0, 0, 1 };      // orientación respecto al planeta que gira
        public double[] CoM = { 0, 0, 0 };         // centro de masas en el marco de la nave
        public readonly List<PartSnapshot> Parts = new();
    }

    public sealed class SaveData
    {
        public double? Ut;
        public List<Vessel> Vessels = new();
    }

    public sealed class Calibration
    {
        public double Rot, Mad, R;
        public int N, Descartadas;
        public bool HasMad;
    }

    /* Lectura de una partida de KSP (.sfs) y calibración del marco de rotación.

       El formato es ConfigNode: un nombre en su línea, una llave que abre, pares
       clave = valor, y llave que cierra. Se recorre de una pasada. Solo interesan el
       UT de la partida y los nodos VESSEL con su subnodo ORBIT. */
    public static class SaveFile
    {
        /* Campos de VESSEL que se conservan; el resto (piezas, tripulación, recursos)
           se ignora, que es la mayor parte del fichero. */
        static readonly HashSet<string> Campos = new() { "name", "type", "sit", "landed", "splashed", "lat", "lon", "alt", "pid", "rot", "CoM" };
        static readonly HashSet<string> Orbita = new() { "SMA", "ECC", "INC", "LPE", "LAN", "MNA", "EPH", "REF", "IDENT" };

        public static readonly HashSet<string> ModulosAnimados = new()
        {
            "ModuleDeployableSolarPanel", "ModuleDeployableAntenna", "ModuleDeployableRadiator", "ModuleDeployableReflector",
            "ModuleDeployablePart", "ModuleAnimateGeneric", "ModuleWheelDeployment", "ModuleAnimationGroup",
            // y los que enseñan u ocultan partes: cubiertas de motor, cofias y estructuras de interetapa
            "ModuleJettison", "ModuleProceduralFairing", "ModuleStructuralNode", "ModuleDynamicNodes"
        };

        static readonly HashSet<string> CamposModulo = new()
        {
            "name", "moduleID", "currentSubtype", "deployState", "storedAnimationTime", "currentRotation",
            "animTime", "position", "isDeployed",
            "activejettisonName", "isJettisoned", "shroudHideOverride", "spawnState", "visibilityState", "fsm", "NodeSetIdx"
        };

        sealed class Raw
        {
            public readonly Dictionary<string, string> F = new();
            public Dictionary<string, string> Orbit;
            public int D;
            public readonly List<RawPart> Parts = new();
        }

        sealed class RawPart
        {
            public readonly Dictionary<string, string> F = new();
            public readonly List<Dictionary<string, string>> Modules = new();
            public readonly List<string> AttN = new();
        }

        static double[] Vec(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(',');
            if (parts.Length < n) return null;
            var r = new double[n];
            for (int i = 0; i < n; i++)
            {
                var x = Num(parts[i]);
                if (x == null) return null;
                r[i] = x.Value;
            }
            return r;
        }

        static double? Num(string s)
        {
            if (s == null) return null;
            if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)) return v;
            return null;
        }

        public static SaveData Parse(string text)
        {
            var lines = text.Split('\n');
            var stack = new List<string>();
            string pend = null;                 // nombre leído, a la espera de su llave
            double? ut = null;
            var vessels = new List<Raw>();
            Raw v = null;
            bool enOrbita = false;
            RawPart part = null;
            Dictionary<string, string> module = null;
            int partDepth = 0, moduleDepth = 0;

            foreach (var line in lines)
            {
                string s = line.Trim();
                if (s.Length == 0) continue;

                if (s == "{")
                {
                    stack.Add(pend ?? "");
                    /* Se apunta la profundidad de la nave: dentro de cada VESSEL hay PARTs y
                       módulos con sus propios «name», «type» u «ORBIT», y solo valen los que
                       cuelgan directamente de la nave. */
                    if (pend == "VESSEL" && v == null) { v = new Raw { D = stack.Count }; enOrbita = false; }
                    else if (v != null && pend == "ORBIT" && v.Orbit == null && stack.Count == v.D + 1)
                    {
                        v.Orbit = new Dictionary<string, string>(); enOrbita = true;
                    }
                    else if (v != null && pend == "PART" && stack.Count == v.D + 1)
                    {
                        part = new RawPart(); v.Parts.Add(part); partDepth = stack.Count;
                    }
                    else if (part != null && pend == "MODULE" && stack.Count == partDepth + 1)
                    {
                        module = new Dictionary<string, string>(); part.Modules.Add(module); moduleDepth = stack.Count;
                    }
                    pend = null;
                    continue;
                }
                if (s == "}")
                {
                    string fin = null;
                    if (stack.Count > 0) { fin = stack[^1]; stack.RemoveAt(stack.Count - 1); }
                    if (module != null && stack.Count == moduleDepth - 1) module = null;
                    if (part != null && stack.Count == partDepth - 1) part = null;
                    if (fin == "ORBIT" && enOrbita && v != null && stack.Count == v.D) enOrbita = false;
                    else if (fin == "VESSEL" && v != null && stack.Count == v.D - 1) { vessels.Add(v); v = null; }
                    continue;
                }

                int eq = s.IndexOf(" = ", StringComparison.Ordinal);
                if (eq < 0) { pend = s; continue; }

                string k = s.Substring(0, eq), val = s.Substring(eq + 3);
                if (ut == null && k == "UT") ut = Num(val);
                if (v == null) continue;
                if (enOrbita) { if (Orbita.Contains(k)) v.Orbit[k] = val; }
                else if (module != null && stack.Count == moduleDepth)
                {
                    if (CamposModulo.Contains(k) && !module.ContainsKey(k)) module[k] = val;
                }
                else if (part != null && stack.Count == partDepth)
                {
                    if ((k == "name" || k == "position" || k == "rotation" || k == "moduleVariantName") && !part.F.ContainsKey(k)) part.F[k] = val;
                    else if (k == "attN") part.AttN.Add(val);
                }
                else if (stack.Count == v.D && Campos.Contains(k) && !v.F.ContainsKey(k)) v.F[k] = val;
            }

            var data = new SaveData { Ut = ut };
            foreach (var r in vessels)
            {
                var n = Normalizar(r);
                if (n != null) data.Vessels.Add(n);
            }
            return data;
        }

        static Vessel Normalizar(Raw v)
        {
            if (!v.F.TryGetValue("name", out string name) || string.IsNullOrEmpty(name)) return null;
            var o = v.Orbit;
            var output = new Vessel
            {
                Name = name,
                Type = v.F.TryGetValue("type", out var ty) && ty.Length > 0 ? ty : "?",
                Sit = v.F.TryGetValue("sit", out var si) && si.Length > 0 ? si : "?",
                Lat = Num(v.F.GetValueOrDefault("lat")),
                Lon = Num(v.F.GetValueOrDefault("lon")),
                Alt = Num(v.F.GetValueOrDefault("alt")),
                BodyName = o != null && o.TryGetValue("IDENT", out var id) && id.Length > 0 ? id.Split('/').Last() : null
            };
            output.Rot = Vec(v.F.GetValueOrDefault("rot"), 4) ?? output.Rot;
            output.CoM = Vec(v.F.GetValueOrDefault("CoM"), 3) ?? output.CoM;
            foreach (var rp in v.Parts)
            {
                if (!rp.F.TryGetValue("name", out var pn)) continue;
                var ps = new PartSnapshot
                {
                    Name = pn,
                    Pos = Vec(rp.F.GetValueOrDefault("position"), 3) ?? new double[] { 0, 0, 0 },
                    Rot = Vec(rp.F.GetValueOrDefault("rotation"), 4) ?? new double[] { 0, 0, 0, 1 },
                    Variant = rp.F.TryGetValue("moduleVariantName", out var mv) && mv.Length > 0 ? mv : null
                };
                // «attN = bottom, 3»: el nodo y la pieza unida (-1 si no hay ninguna)
                foreach (var a in rp.AttN)
                {
                    int c = a.LastIndexOf(',');
                    if (c > 0 && int.TryParse(a.Substring(c + 1).Trim(), out int idx) && idx >= 0) ps.Attached.Add(a.Substring(0, c).Trim());
                }
                foreach (var m in rp.Modules)
                {
                    string mn = m.GetValueOrDefault("name");
                    if (mn == "ModuleB9PartSwitch" && m.TryGetValue("currentSubtype", out var st))
                        ps.B9.Add((m.GetValueOrDefault("moduleID"), st));
                    else if (mn != null && ModulosAnimados.Contains(mn))
                        ps.Modules.Add((mn, m));
                }
                output.Parts.Add(ps);
            }
            if (o != null)
            {
                double? sma = Num(o.GetValueOrDefault("SMA")), ecc = Num(o.GetValueOrDefault("ECC"));
                double? mna = Num(o.GetValueOrDefault("MNA")), eph = Num(o.GetValueOrDefault("EPH"));
                /* Las naves posadas llevan una órbita radial degenerada (e≈1, SMA = R/2)
                   que no describe ninguna trayectoria: se descarta. */
                if (sma != null && ecc != null && ecc < 0.99 && sma > 0)
                {
                    output.Orbit = new Elements
                    {
                        Sma = sma.Value, Ecc = ecc.Value,
                        Inc = Num(o.GetValueOrDefault("INC")) ?? 0,
                        Lpe = Num(o.GetValueOrDefault("LPE")) ?? 0,
                        Lan = Num(o.GetValueOrDefault("LAN")) ?? 0,
                        Mna = mna ?? 0, Eph = eph ?? 0,
                        HasMna = mna != null, HasEph = eph != null
                    };
                }
            }
            return output;
        }

        public readonly record struct Dir(double X, double Y, double Z, double R);

        /* Dirección unitaria en el marco inercial para unos elementos y una anomalía
           media dada. Norte = Z, comprobado contra 107 naves de una partida. */
        public static Dir DirInercial(Elements e, double M)
        {
            double ecc = e.Ecc;
            double E = ManualOrbit.EccentricAnomaly(M, ecc, 1e-13);
            double nu = 2 * Math.Atan2(Math.Sqrt(1 + ecc) * Math.Sin(E / 2), Math.Sqrt(1 - ecc) * Math.Cos(E / 2));
            double u = e.Lpe * Geo.D2R + nu, I = e.Inc * Geo.D2R, O = e.Lan * Geo.D2R;
            double r = e.Sma * (1 - ecc * Math.Cos(E));
            return new Dir(
                Math.Cos(O) * Math.Cos(u) - Math.Sin(O) * Math.Sin(u) * Math.Cos(I),
                Math.Sin(O) * Math.Cos(u) + Math.Cos(O) * Math.Sin(u) * Math.Cos(I),
                Math.Sin(u) * Math.Sin(I),
                r);
        }

        static double Mod360(double a) => (a % 360 + 360) % 360;

        /* Ángulo de rotación del cuerpo en el UT de la partida.

           No se usa una constante: se mide con las propias naves. Cada VESSEL guarda la
           lat/lon de su posición en SU época EPH; la diferencia entre la longitud
           inercial que dan sus elementos y la longitud del mapa ES el ángulo de rotación
           en esa época. Se propagan todas al UT y se promedian en el círculo, con
           recorte de las incoherentes. Hace falta medirlo porque tras cientos de miles
           de vueltas, 7e-5 s de error en el periodo ya desplaza un grado. */
        public static Calibration CalibrarRotacion(IEnumerable<Vessel> vessels, double ut, double periodo = 0)
        {
            double T = periodo > 0 ? periodo : Body.SiderealDay;
            var muestras = new List<double>();

            foreach (var v in vessels)
            {
                if (v.Orbit == null || v.Lat == null || v.Lon == null) continue;
                var e = v.Orbit;
                if (!e.HasEph || !e.HasMna) continue;
                var d = DirInercial(e, e.Mna);

                /* Solo valen las naves cuya lat/lon está sincronizada con su época. La
                   latitud no depende de la rotación, así que sirve de filtro limpio. */
                double latCalc = Math.Asin(Math.Max(-1, Math.Min(1, d.Z))) * Geo.R2D;
                if (Math.Abs(latCalc - v.Lat.Value) > 0.01) continue;

                double lonIner = Math.Atan2(d.Y, d.X) * Geo.R2D;
                double rotEnEph = lonIner - v.Lon.Value;
                muestras.Add(Mod360(rotEnEph + 360 * ((ut - e.Eph) / T)));
            }

            if (muestras.Count == 0) return new Calibration { Rot = 0, N = 0, Descartadas = 0, HasMad = false, R = 0 };

            static double Media(List<double> arr)
            {
                double sx = 0, sy = 0;
                foreach (var a in arr) { sx += Math.Cos(a * Geo.D2R); sy += Math.Sin(a * Geo.D2R); }
                return Mod360(Math.Atan2(sy, sx) * Geo.R2D);
            }
            static double Desv(double a, double c) => Math.Abs(((a - c + 540) % 360) - 180);
            static double Mediana(IEnumerable<double> arr)
            {
                var s = arr.OrderBy(x => x).ToList();
                return s[(s.Count - 1) / 2];
            }

            /* La media circular a secas no aguanta una nave cuya lat/lon sea de otro
               instante y se haya colado por el filtro de latitud. Se recorta en dos
               pasadas: lo que se aleje más de 5 veces la dispersión mediana (con un suelo
               de 2°) queda fuera, y se recalcula con el resto. */
            var usadas = muestras;
            double rot = Media(muestras);
            for (int pasada = 0; pasada < 2; pasada++)
            {
                double mad = Mediana(usadas.Select(a => Desv(a, rot)));
                double corte = Math.Max(2, 5 * mad);
                var quedan = muestras.Where(a => Desv(a, rot) <= corte).ToList();
                if (quedan.Count < 3) break;
                usadas = quedan;
                rot = Media(usadas);
            }

            double sx2 = 0, sy2 = 0;
            foreach (var a in usadas) { sx2 += Math.Cos(a * Geo.D2R); sy2 += Math.Sin(a * Geo.D2R); }
            return new Calibration
            {
                Rot = rot,
                N = usadas.Count,
                Descartadas = muestras.Count - usadas.Count,
                Mad = Mediana(usadas.Select(a => Desv(a, rot))),
                HasMad = true,
                R = Math.Sqrt(Math.Pow(sx2 / usadas.Count, 2) + Math.Pow(sy2 / usadas.Count, 2))
            };
        }

        /* Posición sobre el suelo (lat, lon, altitud) en un instante dado. */
        public static TrackPoint PosicionEn(Elements e, double t, double ut, double rotUT, double periodo = 0)
        {
            double T = periodo > 0 ? periodo : Body.SiderealDay;
            double n = Math.Sqrt(Body.Mu / Math.Pow(e.Sma, 3));
            var d = DirInercial(e, e.Mna + n * (t - e.Eph));
            double rot = rotUT + 360 * ((t - ut) / T);
            return new TrackPoint(
                Math.Asin(Math.Max(-1, Math.Min(1, d.Z))) * Geo.R2D,
                Geo.WrapLon(Math.Atan2(d.Y, d.X) * Geo.R2D - rot),
                d.R - Body.Radius,
                t);
        }

        public static double Periodo(Elements e) => 2 * Math.PI / Math.Sqrt(Body.Mu / Math.Pow(e.Sma, 3));

        /* Traza desde un instante cualquiera (el de la simulación). `porVuelta` fija la
           densidad: para dibujar todas las naves a la vez basta con menos puntos. */
        public static List<TrackPoint> TrazaDesde(Elements e, double t0, double ut, double rotUT, double orbitas, int porVuelta = 0, double periodo = 0)
        {
            double P = Periodo(e);
            double N = orbitas > 0 ? orbitas : 1;
            double baseN = porVuelta > 0 ? porVuelta : Math.Max(90, 180 * (1 + 2 * e.Ecc));
            int pasos = Math.Max(2, (int)Math.Round(baseN * N, MidpointRounding.AwayFromZero));
            var pts = new List<TrackPoint>(pasos + 1);
            for (int i = 0; i <= pasos; i++)
                pts.Add(PosicionEn(e, t0 + ((double)i / pasos) * N * P, ut, rotUT, periodo));
            return pts;
        }

        /* La órbita como anillo cerrado, en el marco fijo al cuerpo con la rotación del
           instante del guardado (rotUT). Se muestrea en anomalía excéntrica y no en
           tiempo: en tiempo, una órbita excéntrica amontona los puntos en el apoapsis y
           deja el periapsis hecho de tramos rectos. */
        public static List<TrackPoint> Anillo(Elements e, double rotUT, int puntos = 180)
        {
            var pts = new List<TrackPoint>(puntos + 1);
            for (int i = 0; i <= puntos; i++)
            {
                double E = ((double)i / puntos) * 2 * Math.PI;
                var d = DirInercial(e, E - e.Ecc * Math.Sin(E));
                pts.Add(new TrackPoint(
                    Math.Asin(Math.Max(-1, Math.Min(1, d.Z))) * Geo.R2D,
                    Geo.WrapLon(Math.Atan2(d.Y, d.X) * Geo.R2D - rotUT),
                    d.R - Body.Radius,
                    0));
            }
            return pts;
        }
    }
}
