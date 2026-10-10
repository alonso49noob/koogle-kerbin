using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KerbinMaps.Ksp;

namespace KerbinMaps.Core
{
    /* What the save knows about a body: ProgressTracking milestones, which is what the game
       uses for the progress tree and for statistics. */
    public sealed class ProgressBody
    {
        public string Name;
        public bool Reached, Flyby, Orbit, Landing, Escape, Suborbit, Docking;
        public double? ReachedAt;
        public bool Visitado => Reached || Flyby || Orbit || Landing || Suborbit;

        public string Resumen()
        {
            if (Landing) return "aterrizaje";
            if (Orbit) return "órbita";
            if (Suborbit) return "suborbital";
            if (Flyby) return "sobrevuelo";
            if (Reached) return "alcanzado";
            return "sin visitar";
        }
    }

    /* A waypoint of the kind the game shows on the navball and the map. */
    public sealed class Waypoint
    {
        public string Name, Body, Id;
        public double Lat, Lon;
        public bool Mine;                 // created by the viewer
    }

    /* What a save holds besides the vessels: game mode, SCANsat coverage, milestones per body
       and waypoints. It's read in a single pass, keeping only the SCENARIO nodes we care about:
       the rest of the file (vessels and parts, which is almost everything) isn't even stored. */
    public sealed class SaveExtras
    {
        public string Mode;                                          // SANDBOX, CAREER, SCIENCE_SANDBOX
        public readonly Dictionary<string, ScanCoverage> Scan = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ProgressBody> Progress = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Waypoint> Waypoints = new();
        /* Level of each KSC facility («SpaceCenter/LaunchPad» → 0, 0.5 or 1). */
        public readonly Dictionary<string, double> NivelesKsc = new(StringComparer.OrdinalIgnoreCase);

        public bool HasScan => Scan.Count > 0;
        public bool Sandbox => string.IsNullOrEmpty(Mode) || Mode.Equals("SANDBOX", StringComparison.OrdinalIgnoreCase);

        public ScanCoverage Cobertura(string body) => body != null && Scan.TryGetValue(body, out var c) ? c : null;
        public ProgressBody Hitos(string body) => body != null && Progress.TryGetValue(body, out var p) ? p : null;

        static readonly HashSet<string> Interesa = new(StringComparer.Ordinal)
        { "SCANcontroller", "ProgressTracking", "ScenarioCustomWaypoints", "ScenarioUpgradeableFacilities" };

        public static SaveExtras Parse(string text) => Parse(text.Split('\n'));

        public static SaveExtras Parse(IEnumerable<string> lines)
        {
            var extras = new SaveExtras();
            int depth = 0;
            string pend = null;
            List<string> buf = null;                 // the SCENARIO being copied
            int bufDepth = 0;

            foreach (var raw in lines)
            {
                string s = raw.Trim();
                if (s.Length == 0) continue;
                if (buf != null) buf.Add(s);

                if (s == "{")
                {
                    depth++;
                    if (buf == null && pend == "SCENARIO") { buf = new List<string> { "SCENARIO", "{" }; bufDepth = depth; }
                    pend = null;
                    continue;
                }
                if (s == "}")
                {
                    if (buf != null && depth == bufDepth) { Escenario(extras, buf); buf = null; }
                    depth--;
                    pend = null;
                    continue;
                }
                int eq = s.IndexOf(" = ", StringComparison.Ordinal);
                if (eq < 0) { pend = s; continue; }
                if (buf == null && depth == 1 && s.StartsWith("Mode = ", StringComparison.Ordinal))
                    extras.Mode = s.Substring(7).Trim();
            }
            return extras;
        }

        static void Escenario(SaveExtras extras, List<string> lines)
        {
            string nombre = null;
            foreach (var l in lines)
                if (l.StartsWith("name = ", StringComparison.Ordinal)) { nombre = l.Substring(7).Trim(); break; }
            if (nombre == null || !Interesa.Contains(nombre)) return;

            var node = ConfigNode.Parse(lines).Nodes.FirstOrDefault();
            if (node == null) return;

            switch (nombre)
            {
                case "SCANcontroller":
                    foreach (var prog in node.Children("Progress"))
                        foreach (var b in prog.Children("Body"))
                        {
                            string bn = b.Get("Name");
                            if (string.IsNullOrEmpty(bn)) continue;
                            var cov = ScanCoverage.FromBlob(bn, b.Get("Map"));
                            cov.MinHeight = Num(b.Get("MinHeightRange")) ?? double.NaN;
                            cov.MaxHeight = Num(b.Get("MaxHeightRange")) ?? double.NaN;
                            cov.Palette = b.Get("PaletteName");
                            extras.Scan[bn] = cov;
                        }
                    break;

                case "ProgressTracking":
                    foreach (var prog in node.Children("Progress"))
                        foreach (var b in prog.Nodes)
                        {
                            // the node carries general milestones besides the bodies (FirstLaunch,
                            // RecordsAltitude, RecordsDepth...): only the ones that are a body count
                            if (SolarSystem.Find(b.Name) == null) continue;
                            if (b.Get("reached") == null && !b.Nodes.Any(n => Hito(n.Name))) continue;
                            var p = new ProgressBody { Name = b.Name, ReachedAt = Num(b.Get("reached")) };
                            p.Reached = p.ReachedAt != null;
                            foreach (var h in b.Nodes)
                                switch (h.Name)
                                {
                                    case "Flyby": p.Flyby = true; break;
                                    case "Orbit": p.Orbit = true; break;
                                    case "Landing": p.Landing = true; break;
                                    case "Escape": p.Escape = true; break;
                                    case "Suborbit": p.Suborbit = true; break;
                                    case "Docking": p.Docking = true; break;
                                }
                            extras.Progress[b.Name] = p;
                        }
                    break;

                case "ScenarioUpgradeableFacilities":
                    foreach (var f in node.Nodes)
                        if (Num(f.Get("lvl")) is double lvl) extras.NivelesKsc[f.Name] = lvl;
                    break;

                case "ScenarioCustomWaypoints":
                    foreach (var w in node.Children("WAYPOINT"))
                    {
                        double? lat = Num(w.Get("latitude")), lon = Num(w.Get("longitude"));
                        if (lat == null || lon == null) continue;
                        extras.Waypoints.Add(new Waypoint
                        {
                            Name = w.Get("name") ?? "waypoint",
                            Body = w.Get("celestialName"),
                            Lat = lat.Value,
                            Lon = lon.Value,
                            Id = w.Get("navigationId"),
                        });
                    }
                    break;
            }
        }

        static bool Hito(string n) => n is "Flyby" or "Orbit" or "Landing" or "Escape" or "Suborbit" or "Docking";

        static double? Num(string s) =>
            s != null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;

        /* ---------------------------------------------------------------- writing */

        /* Puts waypoints into a save's ScenarioCustomWaypoints node, which is where the game
           takes them from. Returns the new text; doesn't touch the disk.

           Replace: those with the same name and body are removed, so exporting twice doesn't
           duplicate them. */
        public static string ConWaypoints(string text, IEnumerable<Waypoint> nuevos, bool reemplazar = true)
        {
            var lista = nuevos.Select(w => new Waypoint { Name = w.Name, Body = w.Body, Lat = w.Lat, Lon = w.Lon, Id = w.Id, Mine = w.Mine }).ToList();
            if (lista.Count == 0) return text;

            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

            int ini = -1, fin = -1, depth = 0, scenIni = -1, scenDepth = 0;
            string pend = null;
            bool esNuestro = false;

            for (int i = 0; i < lines.Count; i++)
            {
                string s = lines[i].Trim();
                if (s == "{")
                {
                    depth++;
                    if (pend == "SCENARIO" && scenIni < 0) { scenIni = i; scenDepth = depth; esNuestro = false; }
                    pend = null;
                    continue;
                }
                if (s == "}")
                {
                    if (scenIni >= 0 && depth == scenDepth)
                    {
                        if (esNuestro) { ini = scenIni; fin = i; break; }
                        scenIni = -1;
                    }
                    depth--;
                    pend = null;
                    continue;
                }
                if (scenIni >= 0 && depth == scenDepth && s == "name = ScenarioCustomWaypoints") esNuestro = true;
                if (!s.Contains(" = ", StringComparison.Ordinal)) pend = s;
            }

            string tab = "\t\t";
            List<string> Texto()
            {
                var t = new List<string>();
                foreach (var w in lista)
                {
                    t.Add(tab + "WAYPOINT");
                    t.Add(tab + "{");
                    t.Add(tab + "\tname = " + w.Name);
                    t.Add(tab + "\tcelestialName = " + (w.Body ?? Body.Name));
                    t.Add(tab + "\tlatitude = " + w.Lat.ToString("R", CultureInfo.InvariantCulture));
                    t.Add(tab + "\tlongitude = " + w.Lon.ToString("R", CultureInfo.InvariantCulture));
                    t.Add(tab + "\tnavigationId = " + (string.IsNullOrEmpty(w.Id) ? Guid.NewGuid().ToString() : w.Id));
                    t.Add(tab + "}");
                }
                return t;
            }

            if (ini < 0)
            {
                // the save doesn't have the node (happens in very old saves): it's created at the end of GAME
                int cierre = lines.FindLastIndex(l => l.Trim() == "}");
                if (cierre < 0) return text;
                var nodo = new List<string> { "\tSCENARIO", "\t{", "\t\tname = ScenarioCustomWaypoints", "\t\tscene = 7, 8, 21" };
                nodo.AddRange(Texto());
                nodo.Add("\t}");
                lines.InsertRange(cierre, nodo);
                return string.Join(nl, lines);
            }

            if (reemplazar)
            {
                var quitar = new HashSet<string>(lista.Select(w => Clave(w.Name, w.Body)), StringComparer.OrdinalIgnoreCase);
                for (int i = fin - 1; i > ini; i--)
                {
                    if (lines[i].Trim() != "WAYPOINT") continue;
                    int cierra = i + 1, d = 0;
                    for (; cierra < fin; cierra++)
                    {
                        string t = lines[cierra].Trim();
                        if (t == "{") d++;
                        else if (t == "}") { d--; if (d == 0) break; }
                    }
                    string nom = null, cuerpo = null, id = null;
                    for (int j = i; j <= cierra && j < lines.Count; j++)
                    {
                        string t = lines[j].Trim();
                        if (t.StartsWith("name = ", StringComparison.Ordinal)) nom = t.Substring(7).Trim();
                        else if (t.StartsWith("celestialName = ", StringComparison.Ordinal)) cuerpo = t.Substring(16).Trim();
                        else if (t.StartsWith("navigationId = ", StringComparison.Ordinal)) id = t.Substring(15).Trim();
                    }
                    if (nom != null && quitar.Contains(Clave(nom, cuerpo)))
                    {
                        /* The identifier of the one being replaced is inherited: if you had
                           that waypoint set as a target in the game, it still is. */
                        var mismo = lista.FirstOrDefault(w => Clave(w.Name, w.Body) == Clave(nom, cuerpo));
                        if (mismo != null && string.IsNullOrEmpty(mismo.Id)) mismo.Id = id;
                        lines.RemoveRange(i, cierra - i + 1);
                        fin -= cierra - i + 1;
                    }
                }
            }

            lines.InsertRange(fin, Texto());
            return string.Join(nl, lines);
        }

        static string Clave(string nombre, string cuerpo) => (cuerpo ?? "") + "|" + (nombre ?? "");

        /* Backup before touching a save: partida.sfs -> partida.sfs.bak-<date> */
        public static string Respaldar(string path)
        {
            string bak = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(path, bak, false);
            return bak;
        }
    }
}
