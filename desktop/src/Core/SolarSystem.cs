using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KerbinMaps.Ksp;

namespace KerbinMaps.Core
{
    /* Órbita de un cuerpo alrededor de su padre, con los elementos tal como los escribe KSP. */
    public sealed class BodyOrbit
    {
        public string Parent;
        public double Sma, Ecc, Inc, Lan, ArgPe;           // m, -, °, °, °
        public double Mna, Epoch;                          // rad en la época, s
        public BodyOrbit Clone() => (BodyOrbit)MemberwiseClone();
    }

    /* Un cuerpo celeste. */
    public sealed class BodyDef
    {
        public string Name, DisplayName, Source = "stock";
        public double Radius, Mu, RotationPeriod, InitialRotation, Atmosphere, Soi;
        public bool TidallyLocked, Ocean, IsHome;
        public BodyOrbit Orbit;                            // null en la estrella
        public float[] Tint = { 0.45f, 0.45f, 0.45f };     // color del globo cuando no hay mapa
        public double[] AirColor;                          // Rayleigh al nivel del suelo (1/m ×1e6); null si el aire no se ve
        public double AirDensity = 1;
        public readonly List<(string Name, string Color)> Biomes = new();
        public bool SoiGiven;

        public bool IsStar => Orbit == null;
        public bool HasAir => Atmosphere > 0 && AirColor != null;
        public string Label => string.IsNullOrEmpty(DisplayName) ? Name : DisplayName;
        public BodyDef Parent => Orbit == null ? null : SolarSystem.Find(Orbit.Parent);

        public double OrbitalPeriod
        {
            get
            {
                var p = Parent;
                return Orbit == null || p == null || p.Mu <= 0 ? 0 : 2 * Math.PI * Math.Sqrt(Math.Pow(Orbit.Sma, 3) / p.Mu);
            }
        }

        /* Un cuerpo en rotación síncrona tarda en girar lo mismo que en dar la vuelta. */
        public double SiderealDay => TidallyLocked && Orbit != null ? OrbitalPeriod : RotationPeriod;

        /* El día solar: la rotación respecto al Sol, que va cambiando de sitio a lo largo del año
           del planeta (para una luna, el año es el de su planeta). */
        public double SolarDay
        {
            get
            {
                double sid = SiderealDay;
                var planet = this;
                while (planet.Parent != null && !planet.Parent.IsStar) planet = planet.Parent;
                double year = planet.IsStar ? 0 : planet.OrbitalPeriod;
                if (sid <= 0 || year <= 0 || Math.Abs(year - sid) < 1e-6) return sid;
                return Math.Abs(1 / (1 / sid - 1 / year));
            }
        }

        public BodyDef Clone(string name)
        {
            var c = (BodyDef)MemberwiseClone();
            // MemberwiseClone comparte las listas: se rehacen las que se modifican
            typeof(BodyDef).GetField(nameof(Biomes))!.SetValue(c, new List<(string, string)>(Biomes));
            c.Name = name;
            c.Orbit = Orbit?.Clone();
            c.Tint = (float[])Tint.Clone();
            c.AirColor = (double[])AirColor?.Clone();
            return c;
        }
    }

    /* Los cuerpos del sistema: los de KSP de serie y, si la instalación usa Kopernicus (RSS,
       SOL, OPM...), los que declare su configuración, heredando de la plantilla de serie
       que nombren. */
    public static class SolarSystem
    {
        static List<BodyDef> bodies = Stock();
        static readonly List<BodyDef> stock = Stock();

        public static IReadOnlyList<BodyDef> Bodies => bodies;
        public static string LoadedFrom { get; private set; }         // null: sistema de serie

        public static BodyDef Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.Contains('/') ? name.Substring(name.LastIndexOf('/') + 1) : name;
            return bodies.FirstOrDefault(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase))
                ?? bodies.FirstOrDefault(b => string.Equals(b.DisplayName, n, StringComparison.OrdinalIgnoreCase));
        }

        public static BodyDef Home => bodies.FirstOrDefault(b => b.IsHome) ?? Find("Kerbin") ?? bodies[0];
        public static BodyDef Star => bodies.FirstOrDefault(b => b.IsStar) ?? bodies[0];

        /* Los cuerpos en orden de árbol (estrella, planetas por distancia, cada uno con sus
           lunas), con su profundidad para sangrar la lista. */
        public static IEnumerable<(BodyDef Body, int Depth)> Tree()
        {
            IEnumerable<(BodyDef, int)> Walk(BodyDef b, int depth)
            {
                yield return (b, depth);
                foreach (var c in bodies.Where(x => x.Orbit != null && Find(x.Orbit.Parent) == b).OrderBy(x => x.Orbit.Sma))
                    foreach (var r in Walk(c, depth + 1)) yield return r;
            }
            var roots = bodies.Where(b => b.Orbit == null || Find(b.Orbit.Parent) == null).ToList();
            foreach (var r in roots)
                foreach (var x in Walk(r, 0)) yield return x;
        }

        /* Posición del cuerpo respecto a la estrella, en metros y en el marco inercial de las
           órbitas, sumando la cadena de padres. */
        public static double[] PositionAt(BodyDef b, double ut)
        {
            var p = new double[3];
            for (var c = b; c?.Orbit != null; c = c.Parent)
            {
                var parent = c.Parent;
                if (parent == null || parent.Mu <= 0) break;
                var e = new Elements { Sma = c.Orbit.Sma, Ecc = c.Orbit.Ecc, Inc = c.Orbit.Inc, Lan = c.Orbit.Lan, Lpe = c.Orbit.ArgPe, Mna = c.Orbit.Mna, Eph = c.Orbit.Epoch };
                double n = Math.Sqrt(parent.Mu / Math.Pow(e.Sma, 3));
                var d = SaveFile.DirInercial(e, e.Mna + n * (ut - e.Eph));
                p[0] += d.X * d.R; p[1] += d.Y * d.R; p[2] += d.Z * d.R;
            }
            return p;
        }

        /* ---------------------------------------------------------------- de serie */

        static BodyDef B(string name, string display, double radius, double mu, double rot, double initRot, double atm, double soi,
                         string parent, double sma, double ecc, double inc, double lan, double argPe, double mna,
                         float r, float g, float bl)
        {
            return new BodyDef
            {
                Name = name, DisplayName = display, Radius = radius, Mu = mu, RotationPeriod = rot, InitialRotation = initRot,
                Atmosphere = atm, Soi = soi, SoiGiven = true, Tint = new[] { r, g, bl },
                Orbit = parent == null ? null : new BodyOrbit { Parent = parent, Sma = sma, Ecc = ecc, Inc = inc, Lan = lan, ArgPe = argPe, Mna = mna, Epoch = 0 }
            };
        }

        /* Valores de KSP 1.12 de serie (los de la wiki del juego). La rotación inicial solo
           importa sin naves con las que medirla. */
        static List<BodyDef> Stock()
        {
            var inf = double.PositiveInfinity;
            var list = new List<BodyDef>
            {
                B("Sun", "Kerbol", 261600000, 1.1723328e18, 432000, 0, 600000, inf, null, 0, 0, 0, 0, 0, 0, 1f, 0.85f, 0.45f),
                B("Moho", null, 250000, 1.6860938e11, 1210000, 190, 0, 9646663, "Sun", 5263138304, 0.2, 7, 70, 15, 3.14, 0.55f, 0.45f, 0.38f),
                B("Eve", null, 700000, 8.1717302e12, 80500, 0, 90000, 85109365, "Sun", 9832684544, 0.01, 2.1, 15, 0, 3.14, 0.52f, 0.34f, 0.60f),
                B("Gilly", null, 13000, 8289449.8, 28255, 5, 0, 126123.27, "Eve", 31500000, 0.55, 12, 80, 10, 0.9, 0.50f, 0.44f, 0.40f),
                B("Kerbin", null, 600000, 3.5316e12, 21549.425, 90, 70000, 84159286, "Sun", 13599840256, 0, 0, 0, 0, 3.14, 0.28f, 0.42f, 0.36f),
                B("Mun", null, 200000, 6.5138398e10, 138984.38, 230, 0, 2429559.1, "Kerbin", 12000000, 0, 0, 0, 0, 1.7, 0.55f, 0.55f, 0.55f),
                B("Minmus", null, 60000, 1.7658e9, 40400, 230, 0, 2247428.4, "Kerbin", 47000000, 0, 6, 78, 38, 0.9, 0.62f, 0.78f, 0.72f),
                B("Duna", null, 320000, 3.0136321e11, 65517.859, 90, 50000, 47921949, "Sun", 20726155264, 0.051, 0.06, 135.5, 0, 3.14, 0.70f, 0.36f, 0.22f),
                B("Ike", null, 130000, 1.8568369e10, 65517.862, 0, 0, 1049598.9, "Duna", 3200000, 0.03, 0.2, 0, 0, 1.7, 0.45f, 0.45f, 0.46f),
                B("Dres", null, 138000, 2.1484489e10, 34800, 25, 0, 32832840, "Sun", 40839348203, 0.145, 5, 280, 90, 3.14, 0.55f, 0.52f, 0.50f),
                B("Jool", null, 6000000, 2.82528e14, 36000, 0, 200000, 2.4559852e9, "Sun", 68773560320, 0.05, 1.304, 52, 0, 0.1, 0.34f, 0.60f, 0.25f),
                B("Laythe", null, 500000, 1.962e12, 52980.879, 90, 50000, 3723645.8, "Jool", 27184000, 0, 0, 0, 0, 3.14, 0.30f, 0.42f, 0.60f),
                B("Vall", null, 300000, 2.074815e11, 105962.09, 0, 0, 2406401.4, "Jool", 43152000, 0, 0, 0, 0, 0.9, 0.60f, 0.66f, 0.72f),
                B("Tylo", null, 600000, 2.82528e12, 211926.36, 0, 0, 10856518, "Jool", 68500000, 0, 0.025, 0, 0, 3.14, 0.66f, 0.60f, 0.55f),
                B("Bop", null, 65000, 2.4868349e9, 544507.43, 0, 0, 1221060.9, "Jool", 128500000, 0.235, 15, 10, 25, 0.9, 0.45f, 0.38f, 0.30f),
                B("Pol", null, 44000, 7.2170208e8, 901902.62, 0, 0, 1042138.9, "Jool", 179890000, 0.171, 4.25, 2, 15, 0.9, 0.74f, 0.70f, 0.50f),
                B("Eeloo", null, 210000, 7.4410815e10, 19460, 0, 0, 1.1908294e8, "Sun", 90118820000, 0.26, 6.15, 50, 260, 3.14, 0.80f, 0.80f, 0.78f)
            };
            // aire: tono de Rayleigh y densidad relativa a Kerbin
            void Air(string n, double r, double g, double b, double dens, bool ocean = false)
            {
                var x = list.First(y => y.Name == n);
                x.AirColor = new[] { r, g, b };
                x.AirDensity = dens;
                x.Ocean = ocean;
            }
            Air("Kerbin", 5.802, 13.558, 33.1, 1, ocean: true);
            Air("Laythe", 5.802, 13.558, 33.1, 0.8, ocean: true);
            Air("Eve", 12, 8, 20, 1.4, ocean: true);
            Air("Duna", 16, 8, 4, 0.35);
            Air("Jool", 6, 16, 9, 1.5);
            list.First(y => y.Name == "Kerbin").IsHome = true;
            return list;
        }

        /* ---------------------------------------------------------------- Kopernicus */

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static bool Num(string s, out double v) =>
            double.TryParse((s ?? "").Trim(), NumberStyles.Float, Inv, out v) && double.IsFinite(v);

        static bool Bool(string s) => string.Equals((s ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);

        static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.StartsWith("#")) return null;      // claves de localización: se usa el nombre interno
            int caret = s.IndexOf('^');
            return (caret >= 0 ? s.Substring(0, caret) : s).Trim();
        }

        /* Lee el sistema de la caché de ModuleManager (ya con todos los parches de los packs).
           Devuelve cuántos cuerpos hay; 0 si la instalación no usa Kopernicus, y entonces se
           queda el sistema de serie. */
        public static int LoadKopernicus(string gameData)
        {
            try
            {
                string cache = Path.Combine(gameData ?? "", "ModuleManager.ConfigCache");
                if (!File.Exists(cache)) return 0;
                var kop = ReadKopernicusNode(cache);
                if (kop == null) return 0;

                var loaded = new List<BodyDef>();
                foreach (var bn in kop.Children("Body"))
                {
                    string name = bn.Get("name");
                    if (string.IsNullOrEmpty(name)) continue;
                    var tplNode = bn.Children("Template").FirstOrDefault();
                    string tpl = tplNode?.Get("name");
                    var baseDef = stock.FirstOrDefault(s => s.Name == (tpl ?? name)) ?? stock.FirstOrDefault(s => s.Name == name);
                    var b = baseDef != null ? baseDef.Clone(name) : new BodyDef { Name = name, Radius = 100000, Mu = 1e10, RotationPeriod = 21600 };
                    b.Source = "Kopernicus";
                    b.IsHome = false;
                    if (baseDef != null && name != baseDef.Name) { b.DisplayName = null; b.SoiGiven = false; }
                    if (tplNode != null && Bool(tplNode.Get("removeAtmosphere"))) b.Atmosphere = 0;

                    var pr = bn.Children("Properties").FirstOrDefault();
                    bool muGiven = false;
                    if (pr != null)
                    {
                        b.DisplayName = Clean(pr.Get("displayName")) ?? b.DisplayName;
                        if (Num(pr.Get("radius"), out double rad)) { b.Radius = rad; b.SoiGiven = false; }
                        if (Num(pr.Get("gravParameter"), out double gp)) { b.Mu = gp; muGiven = true; }
                        else if (Num(pr.Get("mass"), out double mass)) { b.Mu = mass * 6.67408e-11; muGiven = true; }
                        if (!muGiven && Num(pr.Get("geeASL"), out double gee)) { b.Mu = gee * 9.80665 * b.Radius * b.Radius; muGiven = true; }
                        if (muGiven) b.SoiGiven = false;
                        if (Num(pr.Get("rotationPeriod"), out double rp)) b.RotationPeriod = rp;
                        if (pr.Get("tidallyLocked") != null) b.TidallyLocked = Bool(pr.Get("tidallyLocked"));
                        if (Num(pr.Get("initialRotation"), out double ir)) b.InitialRotation = ir;
                        if (Num(pr.Get("sphereOfInfluence"), out double soi)) { b.Soi = soi; b.SoiGiven = true; }
                        if (pr.Get("isHomeWorld") != null) b.IsHome = Bool(pr.Get("isHomeWorld"));
                        var biomes = pr.Children("Biomes").FirstOrDefault();
                        if (biomes != null)
                        {
                            b.Biomes.Clear();
                            foreach (var bio in biomes.Children("Biome"))
                                b.Biomes.Add((Clean(bio.Get("displayName")) ?? bio.Get("name"), bio.Get("color")));
                        }
                    }
                    else if (name == "Kerbin") b.IsHome = true;

                    var or = bn.Children("Orbit").FirstOrDefault();
                    if (or != null)
                    {
                        b.Orbit ??= new BodyOrbit();
                        string parent = or.Get("referenceBody");
                        if (!string.IsNullOrEmpty(parent)) { b.Orbit.Parent = parent.Contains('/') ? parent.Substring(parent.LastIndexOf('/') + 1) : parent; b.SoiGiven = false; }
                        bool changed = false;
                        if (Num(or.Get("semiMajorAxis"), out double sma)) { b.Orbit.Sma = sma; changed = true; }
                        if (Num(or.Get("eccentricity"), out double ecc)) b.Orbit.Ecc = ecc;
                        if (Num(or.Get("inclination"), out double inc)) b.Orbit.Inc = inc;
                        if (Num(or.Get("longitudeOfAscendingNode"), out double lan)) b.Orbit.Lan = lan;
                        if (Num(or.Get("argumentOfPeriapsis"), out double ap)) b.Orbit.ArgPe = ap;
                        if (Num(or.Get("meanAnomalyAtEpoch"), out double mna)) b.Orbit.Mna = mna;
                        else if (Num(or.Get("meanAnomalyAtEpochD"), out double mnaD)) b.Orbit.Mna = mnaD * Math.PI / 180;
                        if (Num(or.Get("epoch"), out double ep)) b.Orbit.Epoch = ep;
                        if (changed) b.SoiGiven = false;
                        // un cuerpo nuevo (no la plantilla misma) se pinta con el color de su órbita
                        var col = ParseColor(or.Get("color"));
                        if (col != null && (baseDef == null || name != baseDef.Name)) b.Tint = col;
                    }

                    var atm = bn.Children("Atmosphere").FirstOrDefault();
                    if (atm != null)
                    {
                        if (atm.Get("enabled") != null && !Bool(atm.Get("enabled"))) b.Atmosphere = 0;
                        else if (Num(atm.Get("altitude"), out double alt) || Num(atm.Get("maxAltitude"), out alt) || Num(atm.Get("atmosphereDepth"), out alt))
                        {
                            b.Atmosphere = alt;
                            b.AirColor ??= new[] { 5.802, 13.558, 33.1 };
                        }
                    }
                    /* El aire de un cuerpo nuevo no es el de su plantilla (Sarnus no tiene el cielo
                       verde de Jool): se tiñe con su color, que es lo que dispersa. */
                    if (b.Atmosphere > 0 && (baseDef == null || name != baseDef.Name))
                    {
                        float mx = Math.Max(0.05f, b.Tint.Max());
                        b.AirColor = new double[] { 6 + 28 * b.Tint[0] / mx, 6 + 28 * b.Tint[1] / mx, 6 + 28 * b.Tint[2] / mx };
                    }
                    loaded.Add(b);
                }
                if (loaded.Count == 0) return 0;

                // la estrella se llama «Sun» dentro del juego aunque se muestre con otro nombre
                bodies = loaded;
                if (!bodies.Any(b => b.IsHome)) (Find("Kerbin") ?? bodies.FirstOrDefault(b => !b.IsStar))!.IsHome = true;
                // esferas de influencia que no vienen dadas: la de Laplace
                foreach (var b in bodies.Where(x => !x.SoiGiven && x.Orbit != null))
                {
                    var p = b.Parent;
                    if (p != null && p.Mu > 0 && b.Mu > 0) b.Soi = b.Orbit.Sma * Math.Pow(b.Mu / p.Mu, 0.4);
                }
                LoadedFrom = gameData;
                return bodies.Count;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[sistema] no se pudo leer Kopernicus: " + ex.Message);
                return 0;
            }
        }

        static float[] ParseColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("#") && s.Length >= 7 &&
                int.TryParse(s.Substring(1, 6), NumberStyles.HexNumber, Inv, out int rgb))
                return new[] { ((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f };
            var parts = s.Split(',');
            if (parts.Length < 3) return null;
            var c = new float[3];
            for (int i = 0; i < 3; i++) if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, Inv, out c[i])) return null;
            if (c.Max() > 1.5f) for (int i = 0; i < 3; i++) c[i] /= 255f;
            return c;
        }

        /* El nodo raíz «Kopernicus» de la caché, sin leer el resto del fichero (que son decenas
           de megas de piezas). */
        static ConfigNode ReadKopernicusNode(string cache)
        {
            var lines = new List<string>();
            bool inside = false;
            int depth = 0;
            foreach (var raw in File.ReadLines(cache, Encoding.UTF8))
            {
                string line = raw.TrimEnd('\r');
                if (!inside)
                {
                    if (line == "\tKopernicus") { inside = true; lines.Add("Kopernicus"); depth = 0; }
                    continue;
                }
                lines.Add(line);
                string t = line.Trim();
                if (t == "{") depth++;
                else if (t == "}" && --depth == 0) break;
            }
            if (lines.Count == 0) return null;
            var root = ConfigNode.Parse(lines);
            return root.Children("Kopernicus").FirstOrDefault() ?? (root.Name == "Kopernicus" ? root : null);
        }
    }
}
