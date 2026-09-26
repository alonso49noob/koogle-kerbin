using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KerbinMaps.Core
{
    public sealed class Anomaly
    {
        public string Body, Name;
        public double Lat, Lon;

        /* Estado según la partida cargada. */
        public bool Detectada;              // la celda tiene el sensor de anomalías
        public bool Identificada;           // y además el de detalle: en el juego sale su nombre
        public double? DistanciaNave;       // a la nave más cercana posada, en metros

        public string Estado => Identificada ? "identificada" : Detectada ? "detectada" : "sin detectar";
    }

    /* Catálogo de anomalías (data/anomalies.json) cruzado con la cobertura de SCANsat de
       la partida: el juego las enseña cuando el escáner de anomalías ha pasado por encima,
       y da el nombre cuando además ha pasado el de detalle. Aquí se hace lo mismo. */
    public static class Anomalies
    {
        static List<Anomaly> all;
        public static string Source { get; private set; }

        public static IReadOnlyList<Anomaly> All => all ??= Load();

        public static List<Anomaly> OfBody(string body) =>
            All.Where(a => string.Equals(a.Body, body, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.Name).ToList();

        public static IEnumerable<string> Bodies() => All.Select(a => a.Body).Distinct();

        /* Marca cuáles están detectadas con la cobertura del cuerpo. Sin partida o sin
           SCANsat quedan todas sin detectar. */
        public static void Aplicar(IEnumerable<Anomaly> lista, ScanCoverage cov)
        {
            foreach (var a in lista)
            {
                a.Detectada = cov != null && cov.Has(a.Lat, a.Lon, ScanType.Anomalia);
                a.Identificada = cov != null && cov.Has(a.Lat, a.Lon, ScanType.AnomaliaDetalle);
            }
        }

        static List<Anomaly> Load()
        {
            var lista = new List<Anomaly>();
            try
            {
                string path = Path.Combine(Store.DataDir ?? ".", "anomalies.json");
                if (!File.Exists(path)) return lista;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("_fuente", out var f) && f.TryGetProperty("proyecto", out var p))
                    Source = p.GetString();
                if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return lista;
                foreach (var it in items.EnumerateArray())
                {
                    if (!it.TryGetProperty("lat", out var lat) || !it.TryGetProperty("lon", out var lon)) continue;
                    lista.Add(new Anomaly
                    {
                        Body = it.TryGetProperty("body", out var b) ? b.GetString() : null,
                        Name = it.TryGetProperty("name", out var n) ? n.GetString() : "?",
                        Lat = lat.GetDouble(),
                        Lon = lon.GetDouble(),
                    });
                }
            }
            catch (Exception ex) { Debug.WriteLine("[anomalías] " + ex.Message); }
            return lista;
        }
    }
}
