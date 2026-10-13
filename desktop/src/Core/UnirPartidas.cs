using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KerbinMaps.Core
{
    /* Writes several saves as one: the save with the latest UT is the base (its game, career,
       scenarios and clock are kept) and the vessels of the others are added to it.

       Each added vessel keeps the state it had in its own save at the same «unified» moment: if
       its save is Δ seconds behind the base, the epoch of its orbit (EPH), its launch time (lct)
       and its last update (lastUT) move forward by Δ, so at the base's UT KSP puts it exactly
       where it was in its own save. Landed vessels don't depend on time.

       What has to be unique in a save is made unique: the vessel GUID (pid), the persistentIds
       of vessels and parts, and the parts' flightIDs (uid), along with the references to them
       (ref, dockUId). They only change when they clash with something already in use. Crew
       come along with their KERBAL entry from the other save's roster; a name the base already
       uses gets the save's letter, because a kerbal can't be in two vessels at once.

       Nothing else of the other saves is carried over (science, contracts, funds, SCANsat):
       they belong to each career, and the base's are the ones that remain. */
    public static class UnirPartidas
    {
        public sealed class Fuente
        {
            public string Texto, Etiqueta;        // the save's text and a short label («B», «C»...)
            public double Ut;
        }

        public sealed class Resultado
        {
            public string Texto;
            public int Naves, Tripulantes, Renombrados, IdsCambiados;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /* A node found in the text: where its header line starts and where its closing line ends. */
        readonly record struct Bloque(string Nombre, int Ini, int Fin);

        /* The VESSEL nodes under FLIGHTSTATE and the KERBAL nodes under ROSTER, plus the
           closing brace of both containers, walking the text once (saves run to tens of MB). */
        static (List<Bloque> Naves, List<Bloque> Kerbals, int CierreFlight, int CierreRoster) Buscar(string t)
        {
            var naves = new List<Bloque>(); var kerbals = new List<Bloque>();
            int cierreFlight = -1, cierreRoster = -1;
            var pila = new List<(string Nombre, int Ini)>();
            string pend = null; int pendIni = 0;
            int i = 0, n = t.Length;
            while (i < n)
            {
                int fin = t.IndexOf('\n', i);
                if (fin < 0) fin = n; else fin++;
                string linea = t.Substring(i, fin - i).Trim();
                if (linea == "{") { pila.Add((pend ?? "", pend != null ? pendIni : i)); pend = null; }
                else if (linea == "}")
                {
                    if (pila.Count > 0)
                    {
                        var (nombre, ini) = pila[^1];
                        pila.RemoveAt(pila.Count - 1);
                        string padre = pila.Count > 0 ? pila[^1].Nombre : null;
                        if (nombre == "VESSEL" && padre == "FLIGHTSTATE") naves.Add(new Bloque(nombre, ini, fin));
                        else if (nombre == "KERBAL" && padre == "ROSTER") kerbals.Add(new Bloque(nombre, ini, fin));
                        else if (nombre == "FLIGHTSTATE" && cierreFlight < 0) cierreFlight = i;
                        else if (nombre == "ROSTER" && cierreRoster < 0) cierreRoster = i;
                    }
                }
                else if (linea.Length > 0 && !linea.Contains('=') && !linea.StartsWith("//")) { pend = linea; pendIni = i; }
                i = fin;
            }
            return (naves, kerbals, cierreFlight, cierreRoster);
        }

        static IEnumerable<string> Valores(string t, string clave) =>
            Regex.Matches(t, @"^[ \t]*" + Regex.Escape(clave) + @" = ([^\r\n]*)", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim());

        public static Resultado Unir(string baseTexto, IReadOnlyList<Fuente> otras, double baseUt)
        {
            string nl = baseTexto.Contains("\r\n") ? "\r\n" : "\n";
            var b = Buscar(baseTexto);
            if (b.CierreFlight < 0) throw new InvalidOperationException("La partida base no tiene FLIGHTSTATE.");

            // what's already in use in the base
            var pids = new HashSet<string>(Valores(baseTexto, "pid"), StringComparer.OrdinalIgnoreCase);
            var persist = new HashSet<string>(Valores(baseTexto, "persistentId"));
            var uids = new HashSet<string>(Valores(baseTexto, "uid"));
            var nombres = new HashSet<string>(b.Kerbals.Select(k => Valores(baseTexto.Substring(k.Ini, k.Fin - k.Ini), "name").FirstOrDefault() ?? ""));

            var res = new Resultado();
            var naves = new StringBuilder();
            var kerbals = new StringBuilder();
            var rnd = new Random();
            string NuevoUint(HashSet<string> usados)
            {
                string v;
                do v = ((uint)rnd.Next(1 << 30) * 4u + (uint)rnd.Next(4)).ToString(Inv); while (v == "0" || usados.Contains(v));
                usados.Add(v);
                return v;
            }

            foreach (var o in otras)
            {
                double desfase = baseUt - o.Ut;
                var f = Buscar(o.Texto);
                var rosterOtro = new Dictionary<string, string>();
                foreach (var k in f.Kerbals)
                {
                    string bloque = o.Texto.Substring(k.Ini, k.Fin - k.Ini);
                    string nombre = Valores(bloque, "name").FirstOrDefault();
                    if (nombre != null) rosterOtro.TryAdd(nombre, bloque);
                }
                foreach (var nv in f.Naves)
                {
                    string bloque = o.Texto.Substring(nv.Ini, nv.Fin - nv.Ini);
                    naves.Append(Retocar(bloque, desfase, o.Etiqueta, pids, persist, uids, nombres, rosterOtro, kerbals, res, NuevoUint, nl));
                    res.Naves++;
                }
            }

            // added at the end of FLIGHTSTATE and ROSTER (from the back, so the first position holds)
            string texto = baseTexto;
            if (kerbals.Length > 0 && b.CierreRoster > 0 && b.CierreRoster > b.CierreFlight) texto = texto.Insert(b.CierreRoster, kerbals.ToString());
            texto = texto.Insert(b.CierreFlight, naves.ToString());
            if (kerbals.Length > 0 && b.CierreRoster > 0 && b.CierreRoster < b.CierreFlight) texto = texto.Insert(b.CierreRoster, kerbals.ToString());
            res.Texto = texto;
            return res;
        }

        /* One VESSEL block, line by line: times shifted, clashing IDs replaced and crew brought
           along. `prof` is the depth inside the block (1 = the vessel's own keys). */
        static string Retocar(string bloque, double desfase, string etiqueta, HashSet<string> pids, HashSet<string> persist,
                              HashSet<string> uids, HashSet<string> nombres, Dictionary<string, string> rosterOtro,
                              StringBuilder kerbals, Resultado res, Func<HashSet<string>, string> nuevoUint, string nl)
        {
            var lineas = bloque.Replace("\r\n", "\n").Split('\n').ToList();
            if (lineas.Count > 0 && lineas[^1].Length == 0) lineas.RemoveAt(lineas.Count - 1);
            var uidMap = new Dictionary<string, string>();
            var crewMap = new Dictionary<string, string>();
            var pila = new List<string>();
            string pend = null;

            /* First pass: which uids of the vessel's own parts clash, so «ref» (written before
               the parts) can be rewritten too. Parts stored in inventories are nested deeper and
               carry uid 0, «none»: those are left alone. */
            int prof1 = 0;
            string pend1 = null;
            var pila1 = new List<string>();
            foreach (var l in lineas)
            {
                string s1 = l.Trim();
                if (s1 == "{") { pila1.Add(pend1 ?? ""); pend1 = null; continue; }
                if (s1 == "}") { if (pila1.Count > 0) pila1.RemoveAt(pila1.Count - 1); continue; }
                if (!s1.Contains(" = ")) { if (s1.Length > 0) pend1 = s1; continue; }
                prof1 = pila1.Count;
                var m = Regex.Match(s1, @"^uid = (\S+)");
                if (m.Success && prof1 == 2 && pila1[^1] == "PART" && m.Groups[1].Value != "0" &&
                    uids.Contains(m.Groups[1].Value) && !uidMap.ContainsKey(m.Groups[1].Value))
                    uidMap[m.Groups[1].Value] = null;
            }
            foreach (var k in uidMap.Keys.ToList()) { uidMap[k] = nuevoUint(uids); res.IdsCambiados++; }

            for (int i = 0; i < lineas.Count; i++)
            {
                string l = lineas[i], s = l.Trim();
                if (s == "{") { pila.Add(pend ?? ""); pend = null; continue; }
                if (s == "}") { if (pila.Count > 0) pila.RemoveAt(pila.Count - 1); continue; }
                int eq = s.IndexOf(" = ", StringComparison.Ordinal);
                if (eq < 0) { if (s.Length > 0 && !s.StartsWith("//")) pend = s; continue; }
                string clave = s.Substring(0, eq), valor = s.Substring(eq + 3);
                string sangria = l.Substring(0, l.Length - l.TrimStart().Length);
                string nodo = pila.Count > 0 ? pila[^1] : "";
                int prof = pila.Count;
                string nuevo = null;

                if (prof == 1 && (clave == "lct" || clave == "lastUT") && desfase != 0 && Num(valor) is double tv)
                    nuevo = N(tv + desfase);
                else if (prof == 2 && nodo == "ORBIT" && clave == "EPH" && desfase != 0 && Num(valor) is double eph)
                    nuevo = N(eph + desfase);
                else if (prof == 1 && clave == "pid")
                {
                    if (pids.Contains(valor)) { nuevo = Guid.NewGuid().ToString("N"); res.IdsCambiados++; }
                    pids.Add(nuevo ?? valor);
                }
                else if (clave == "persistentId" && (nodo == "VESSEL" || nodo == "PART") && prof <= 2 && valor != "0")
                {
                    if (persist.Contains(valor)) { nuevo = nuevoUint(persist); res.IdsCambiados++; }
                    else persist.Add(valor);
                }
                else if (clave == "uid" && nodo == "PART" && prof == 2 && uidMap.TryGetValue(valor, out var nu)) nuevo = nu;
                else if (clave == "uid" && nodo == "PART" && prof == 2) uids.Add(valor);
                else if (((clave == "ref" && prof == 1) || clave.EndsWith("UId", StringComparison.Ordinal)) && valor != "0")
                {
                    if (uidMap.TryGetValue(valor, out var nr)) nuevo = nr;
                }
                else if (clave == "crew" && nodo == "PART")
                {
                    if (!crewMap.TryGetValue(valor, out var nombre))
                    {
                        nombre = valor;
                        if (nombres.Contains(nombre))
                        {
                            // a kerbal can't be in two vessels at once: this one is a namesake from another save
                            nombre = valor + " " + etiqueta;
                            for (int k = 2; nombres.Contains(nombre); k++) nombre = valor + " " + etiqueta + k;
                            res.Renombrados++;
                        }
                        nombres.Add(nombre);
                        crewMap[valor] = nombre;
                        if (rosterOtro.TryGetValue(valor, out var kb))
                        {
                            if (nombre != valor) kb = Regex.Replace(kb, @"^([ \t]*name = )[^\r\n]*", m => m.Groups[1].Value + nombre, RegexOptions.Multiline);
                            kerbals.Append(kb.Replace("\r\n", "\n").Replace("\n", nl));
                            res.Tripulantes++;
                        }
                    }
                    if (nombre != valor) nuevo = nombre;
                }
                if (nuevo != null) lineas[i] = sangria + clave + " = " + nuevo;
            }
            return string.Join(nl, lineas) + nl;
        }

        static double? Num(string s) =>
            double.TryParse(s, NumberStyles.Float, Inv, out double v) && double.IsFinite(v) ? v : null;

        static string N(double v) => v.ToString("R", Inv);
    }
}
