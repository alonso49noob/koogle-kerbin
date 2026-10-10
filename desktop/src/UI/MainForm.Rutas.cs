using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Routes: how long it takes from one point to another by plane, by ship and by ground
       vehicle (see MallaRutas). Origin and destination are picked on the map or the globe, or
       chosen among the markers; the three routes are drawn at once and the fastest is
       highlighted, like in a car navigator. */
    public sealed partial class MainForm
    {
        Task<MallaRutas> mallaTask;
        object mallaDe;                           // which maps the grid comes from
        LatLon? rutaA, rutaB;
        string rutaANombre, rutaBNombre;
        Ruta[] rutasHechas;
        CancellationTokenSource rutaCts;
        int rutaGen;
        bool rutaCalculando;
        readonly MapLayer rutaLayer = new();

        DarkCombo rutaOrigen, rutaDestino;
        DarkButton rutaElegirBtn;
        DarkTextBox rvAire, rvMar, rvTierra, rvPend;
        RichLabel rutaInfo;

        const string ColAire = "#4ea3ff", ColMar = "#2dd4bf", ColTierra = "#ffb454";

        /* ------------------------------------------------------------ panel */

        void BuildRutasSection()
        {
            var s = AddSection("Rutas: aire, mar y tierra", false);

            rutaOrigen = new DarkCombo();
            rutaDestino = new DarkCombo();
            rutaOrigen.SelectedChanged += (o, e) => RutaDesdeCombo(rutaOrigen, true);
            rutaDestino.SelectedChanged += (o, e) => RutaDesdeCombo(rutaDestino, false);
            s.Add(Field("Origen", rutaOrigen));
            s.Add(Field("Destino", rutaDestino));

            rutaElegirBtn = new DarkButton("Elegir en el mapa", ButtonVariant.Primary);
            rutaElegirBtn.Click += (o, e) => SetToolMode("route");
            rutaElegirBtn.Tip = "Primer clic, el origen; segundo, el destino. También en el globo.";
            var invertir = new DarkButton("Invertir", ButtonVariant.Ghost);
            invertir.Click += (o, e) => InvertirRuta();
            var quitar = new DarkButton("Quitar", ButtonVariant.Ghost);
            quitar.Click += (o, e) => LimpiarRuta();
            s.Add(new BtnRow(rutaElegirBtn, invertir, quitar));

            rvAire = Num(state.RutaVAire); rvMar = Num(state.RutaVMar);
            rvTierra = Num(state.RutaVTierra); rvPend = Num(state.RutaPendiente);
            void Al(DarkTextBox t, Action<double> poner, double min, double def)
                => t.Committed += (o, e) => { poner(Math.Max(min, t.NumberOr(def))); SaveSettings(); CalcularRutas(); };
            Al(rvAire, v => state.RutaVAire = v, 1, 300);
            Al(rvMar, v => state.RutaVMar = v, 0.5, 15);
            Al(rvTierra, v => state.RutaVTierra = v, 0.5, 20);
            Al(rvPend, v => state.RutaPendiente = Math.Min(v, 80), 1, 30);
            s.Add(new Row2(Field("Avión (m/s)", rvAire), Field("Barco (m/s)", rvMar)));
            s.Add(new Row2(Field("Rover (m/s)", rvTierra), Field("Pendiente máx. (°)", rvPend)));

            rutaInfo = s.Add(Readout());
            s.Add(Hint("Como un navegador: elige el origen y el destino y salen las tres rutas con su tiempo. El " +
                       "<b>avión</b> va en línea recta por el gran círculo. El <b>barco</b> solo navega por el mar y el " +
                       "<b>rover</b> solo pisa tierra, rodeando las cuestas de más de la pendiente máxima y yendo más " +
                       "despacio cuanto más empinadas (a un 30 % en el límite). Si el barco sale de tierra adentro, " +
                       "la ruta empieza en el agua más cercana. Las velocidades son de crucero, sin despegues ni paradas."));
            RellenarCombosRuta();
            RenderRutaInfo();
        }

        void RellenarCombosRuta()
        {
            if (rutaOrigen == null) return;
            foreach (var (c, p, nombre) in new[] { (rutaOrigen, rutaA, rutaANombre), (rutaDestino, rutaB, rutaBNombre) })
            {
                var items = new List<(string, string)> { ("", "—") };
                if (p.HasValue && nombre == null) items.Add(("mapa", Geo.F(p.Value.Lat, 3) + ", " + Geo.F(p.Value.Lon, 3)));
                if (OnMapBody)
                    foreach (var m in MarkersAll()) items.Add(("m:" + m.Id, m.Name));
                c.SetItems(items);
                c.SelectedId = !p.HasValue ? "" : nombre == null ? "mapa"
                             : MarkersAll().FirstOrDefault(m => m.Name == nombre && m.Lat == p.Value.Lat && m.Lon == p.Value.Lon) is Marker mk ? "m:" + mk.Id : "";
            }
        }

        void RutaDesdeCombo(DarkCombo c, bool origen)
        {
            string id = c.SelectedId;
            if (id == "mapa") return;
            LatLon? p = null;
            string nombre = null;
            if (id != null && id.StartsWith("m:") && MarkersAll().FirstOrDefault(m => "m:" + m.Id == id) is Marker mk)
            {
                p = new LatLon(mk.Lat, mk.Lon);
                nombre = mk.Name;
            }
            if (origen) { rutaA = p; rutaANombre = nombre; } else { rutaB = p; rutaBNombre = nombre; }
            RellenarCombosRuta();
            CalcularRutas();
        }

        /* ------------------------------------------------------------ points */

        string RutaHint() => rutaA == null || rutaB != null ? "Haz clic en el origen. Esc para terminar." : "Ahora haz clic en el destino.";

        void RutaClick(LatLon ll)
        {
            if (rutaA == null || rutaB != null)
            {
                rutaA = ll; rutaANombre = null;
                rutaB = null; rutaBNombre = null;
                CalcularRutas();
                toolHint.SetText(RutaHint());
            }
            else
            {
                rutaB = ll; rutaBNombre = null;
                SetToolMode(null);
                CalcularRutas();
            }
            RellenarCombosRuta();
        }

        void InvertirRuta()
        {
            (rutaA, rutaB) = (rutaB, rutaA);
            (rutaANombre, rutaBNombre) = (rutaBNombre, rutaANombre);
            RellenarCombosRuta();
            CalcularRutas();
        }

        void LimpiarRuta()
        {
            rutaCts?.Cancel();
            rutaGen++;
            rutaCalculando = false;
            rutaA = rutaB = null;
            rutaANombre = rutaBNombre = null;
            rutasHechas = null;
            if (toolMode == "route") SetToolMode(null);
            RellenarCombosRuta();
            DibujarRutas();
            RenderRutaInfo();
        }

        /* ------------------------------------------------------------ calculation */

        /* The grid for the maps being shown, built only once per combination of maps. */
        Task<MallaRutas> MallaActual()
        {
            var alt = MapImg("height");
            var col = MapImg("color");
            var (hmin, hmax) = RangoAltura();
            double ho = HeightOffNow, co = ColorOffNow;
            bool mar = Body.Current.Ocean && (alt != null || col != null);
            var clave = (Body.Name, mar, alt, col, hmin, hmax, ho, co);
            if (mallaTask != null && !mallaTask.IsFaulted && Equals(mallaDe, clave)) return mallaTask;
            mallaDe = clave;
            double radio = Body.Radius;
            Func<double, double, double> altura = alt == null ? null : (la, lo) => alt.HeightSmooth(la, lo, hmin, hmax, ho);
            Func<double, double, bool> esTierra = col == null ? null
                : (la, lo) => { var c = col.SampleBilinear(la, lo, co); return c.B - Math.Max(c.R, c.G) < 0.055f; };
            return mallaTask = Task.Run(() => new MallaRutas(altura, esTierra, mar, radio));
        }

        async void CalcularRutas()
        {
            rutaCts?.Cancel();
            int gen = ++rutaGen;
            if (rutaA == null || rutaB == null)
            {
                rutasHechas = null;
                rutaCalculando = false;
                DibujarRutas();
                RenderRutaInfo();
                return;
            }
            var cts = rutaCts = new CancellationTokenSource();
            rutaCalculando = true;
            rutasHechas = null;
            DibujarRutas();
            RenderRutaInfo();
            try
            {
                var malla = await MallaActual();
                if (gen != rutaGen) return;
                LatLon a = rutaA.Value, b = rutaB.Value;
                double vA = state.RutaVAire, vM = state.RutaVMar, vT = state.RutaVTierra, pend = state.RutaPendiente;
                var res = await Task.Run(() => new[]
                {
                    malla.Aire(a, b, vA),
                    malla.Buscar(Medio.Mar, a, b, vM, pend, cts.Token),
                    malla.Buscar(Medio.Tierra, a, b, vT, pend, cts.Token),
                });
                if (gen != rutaGen) return;
                rutasHechas = res;
            }
            catch (Exception ex)
            {
                if (gen != rutaGen) return;
                Flash(Lang.T("No se pudo calcular la ruta: ") + ex.Message);
            }
            rutaCalculando = false;
            DibujarRutas();
            RenderRutaInfo();
        }

        static bool AireVale => Body.Current.Atmosphere > 0;

        /* The fastest of the ones that can be done. */
        Ruta MasRapida() => rutasHechas?.Where(r => r.Ok && (r.Medio != Medio.Aire || AireVale)).OrderBy(r => r.Tiempo).FirstOrDefault();

        /* ------------------------------------------------------------ drawing */

        void DibujarRutas()
        {
            rutaLayer.Clear();
            var globo = new List<(IReadOnlyList<LatLon>, ColorF)>();
            var rapida = MasRapida();
            if (rutasHechas != null)
                foreach (var r in rutasHechas.Where(r => r.Ok).OrderBy(r => r == rapida))   // the fastest one, on top
                {
                    bool aireMuerto = r.Medio == Medio.Aire && !AireVale;
                    var col = ColorF.Hex(ColorDe(r.Medio), r == rapida ? 1f : aireMuerto ? 0.4f : 0.75f);
                    rutaLayer.Lines.Add(new MapLine(r.Puntos, col, r == rapida ? 3.2f : 2f, dashed: r.Medio == Medio.Aire));
                    globo.Add((r.Puntos, col));
                    foreach (var puerto in new[] { r.PuertoSalida, r.PuertoLlegada })
                        if (puerto.HasValue)
                            rutaLayer.Dots.Add(new MapDot
                            {
                                Lat = puerto.Value.Lat, Lon = puerto.Value.Lon, Style = DotStyle.Point, Fill = col,
                                Tooltip = Lang.T(r.Medio == Medio.Mar ? "Al agua" : "A tierra"),
                            });
                }
            if (rutaA.HasValue)
                rutaLayer.Dots.Add(new MapDot { Lat = rutaA.Value.Lat, Lon = rutaA.Value.Lon, Style = DotStyle.Point, Fill = ColorF.Hex("#7ee787"), Label = rutaANombre ?? Lang.T("Origen") });
            if (rutaB.HasValue)
                rutaLayer.Dots.Add(new MapDot { Lat = rutaB.Value.Lat, Lon = rutaB.Value.Lon, Style = DotStyle.Point, Fill = ColorF.Hex("#ff6b6b"), Label = rutaBNombre ?? Lang.T("Destino") });
            globe.SetRutas(globo);
            RequestRender();
        }

        static string ColorDe(Medio m) => m == Medio.Aire ? ColAire : m == Medio.Mar ? ColMar : ColTierra;

        void RenderRutaInfo()
        {
            if (rutaInfo == null) return;
            if (rutaA == null || rutaB == null)
            {
                rutaInfo.SetText(Lang.T(rutaA == null ? "Elige el origen y el destino." : "Falta el destino."));
                return;
            }
            double recta = Geo.Distance(rutaA.Value.Lat, rutaA.Value.Lon, rutaB.Value.Lat, rutaB.Value.Lon);
            var rows = new List<string> { Lang.F("En línea recta <b>{0}</b>", Geo.FmtDist(recta)) };
            if (rutaCalculando || rutasHechas == null)
            {
                rows.Add(Lang.T("Calculando rutas…"));
                rutaInfo.SetText(string.Join("\n", rows));
                return;
            }
            var rapida = MasRapida();
            bool hayPartida = sv.Data != null;
            foreach (var r in rutasHechas)
            {
                string nombre = Lang.T(r.Medio == Medio.Aire ? "Avión" : r.Medio == Medio.Mar ? "Barco" : "Rover");
                string cab = "<sw=" + ColorDe(r.Medio) + "><b>" + nombre + "</b>";
                if (!r.Ok)
                {
                    rows.Add(cab + "  <m>" + RichLabel.Esc(Lang.T(r.Motivo ?? "Sin ruta.")) + "</m>");
                    continue;
                }
                if (r.Medio == Medio.Aire && !AireVale)
                {
                    rows.Add(cab + "  " + Geo.FmtDist(r.Distancia) + "  <m>" + Lang.T("sin atmósfera: las alas no sirven") + "</m>");
                    continue;
                }
                rows.Add(cab + "  " + Geo.FmtDist(r.Distancia) + "  <b>" + Geo.FmtTime(r.Tiempo) + "</b>" +
                         (r == rapida ? "  " + Lang.T("★ la más rápida") : ""));
                var det = new List<string>();
                if (r.Medio == Medio.Aire && r.CotaMax > double.MinValue)
                    det.Add(Lang.F("lo más alto debajo {0}", Geo.FmtAlt(r.CotaMax)));
                if (r.Salida > 0) det.Add(Lang.F("sale a {0} del origen", Geo.FmtDist(r.Salida)));
                if (r.Llegada > 0) det.Add(Lang.F("llega a {0} del destino", Geo.FmtDist(r.Llegada)));
                if (r.Medio == Medio.Tierra)
                {
                    det.Add(Lang.F("sube {0} m, baja {1} m", Geo.F(r.Subida, 0), Geo.F(r.Bajada, 0)));
                    det.Add(Lang.F("pendiente máx. {0}°", Geo.F(r.PendienteMax, 1)));
                }
                if (r.Distancia > recta * 1.02) det.Add(Lang.F("rodeo +{0} %", Geo.F((r.Distancia / recta - 1) * 100, 0)));
                if (hayPartida) det.Add(Lang.F("llegada {0}", Geo.FechaKerbal(sim.T + r.Tiempo)));
                if (det.Count > 0) rows.Add("<m>    " + string.Join(" · ", det) + "</m>");
            }
            rutaInfo.SetText(string.Join("\n", rows));
        }
    }
}
