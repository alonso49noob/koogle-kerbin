using System;
using System.Linq;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Asistente de aterrizaje: dónde frenar para caer en un punto.

       Se elige una nave en órbita del cuerpo y un objetivo en el suelo, y el visor busca
       el instante de la frenada retrógrada y su Δv, integra el descenso y dice dónde
       acabaría. El resultado se dibuja como traza en el mapa y en el globo. */
    public sealed partial class MainForm
    {
        readonly MapLayer landLayer = new();
        RichLabel landInfo;
        DarkTextBox landPeBox, landBcBox;
        DarkButton landCalc;
        LandingPlan plan;
        bool calculando;

        void RenderLandingInfo()
        {
            if (landInfo == null) return;
            var partes = new System.Collections.Generic.List<string>();

            partes.Add(state.LandLat is double la && state.LandLon is double lo
                ? Lang.F("Objetivo: {0}, {1}.", Geo.FmtLat(la), Geo.FmtLon(lo))
                : Lang.T("Sin objetivo: pon el centro de la vista donde quieras aterrizar y pulsa «Objetivo aquí»."));

            if (sv.Sel == null) partes.Add(Lang.T("Sin nave elegida: pincha una en «Naves de una partida»."));
            else if (sv.Sel.Orbit == null) partes.Add(Lang.F("{0} no tiene órbita guardada.", sv.Sel.Name));
            else
            {
                var est = SaveFile.EstadoEn(sv.Sel.Orbit, sim.T);
                partes.Add(Lang.F("Nave: <b>{0}</b>, a {1} km, {2} m/s.", sv.Sel.Name,
                    Geo.F((est.R - Body.Radius) / 1000, 1), Geo.F(est.V, 0)));
            }

            if (plan != null && plan.Ok)
            {
                partes.Add("");
                partes.Add(Lang.F("Frenar en <b>T+{0}</b> sobre {1}, {2} (a {3} km).",
                    Geo.FmtTime(plan.BurnUT - sim.T), Geo.FmtLat(plan.BurnLat), Geo.FmtLon(plan.BurnLon), Geo.F(plan.BurnAlt / 1000, 1)));
                partes.Add(Lang.F("Δv retrógrado <b>{0} m/s</b>, periapsis resultante {1} km.",
                    Geo.F(plan.Dv, 1), Geo.F(plan.PeriapsisTrasFrenar / 1000, 1)));
                if (!double.IsNaN(plan.EntradaUT))
                    partes.Add(Lang.F("Entra en atmósfera en T+{0}.", Geo.FmtTime(plan.EntradaUT - sim.T)));
                partes.Add(Lang.F("Toca suelo en <b>T+{0}</b> en {1}, {2}.",
                    Geo.FmtTime(plan.TouchUT - sim.T), Geo.FmtLat(plan.ImpactLat), Geo.FmtLon(plan.ImpactLon)));
                partes.Add(Lang.F("Se queda a <b>{0}</b> del objetivo.", Geo.FmtDist(plan.ErrorM)));
                partes.Add(Lang.F("Llega al suelo a {0} m/s (sin frenada final).", Geo.F(plan.VelocidadImpacto, 0)));
                if (plan.ConAire)
                    partes.Add(Lang.T("Con atmósfera esto es orientativo: el frenado depende de la nave."));
            }
            else if (plan != null && plan.Problema != null) partes.Add(Lang.T(plan.Problema));

            landInfo.SetText(string.Join("\n", partes));
        }

        void FijarObjetivoAqui()
        {
            double lat, lon;
            if (is3D) { var c = globe.Center(); lat = c.Lat; lon = c.Lon; }
            else { lat = map.CenterLat; lon = Geo.WrapLon(map.CenterLon); }
            state.LandLat = lat;
            state.LandLon = lon;
            SaveSettings();
            DibujarObjetivo();
            RenderLandingInfo();
            RequestRender();
        }

        void DibujarObjetivo()
        {
            landLayer.Dots.RemoveAll(d => "objetivo".Equals(d.Tag));
            if (state.LandLat is double la && state.LandLon is double lo)
                landLayer.Dots.Add(new MapDot
                {
                    Lat = la, Lon = lo, Style = DotStyle.Pin, Fill = ColorF.Hex("#ff6b6b"),
                    Tooltip = Lang.T("Objetivo de aterrizaje"), Tag = "objetivo",
                });
        }

        async Task CalcularAterrizaje()
        {
            if (calculando) return;
            if (sv.Sel?.Orbit == null) { Flash(Lang.T("Elige antes una nave con órbita.")); return; }
            if (state.LandLat == null || state.LandLon == null) { Flash(Lang.T("Pon primero un objetivo.")); return; }

            calculando = true;
            if (landCalc != null) landCalc.Enabled = false;
            try
            {
                var orb = sv.Sel.Orbit;
                double t0 = sim.T, ut = sv.Ut, rot = RotBase();
                double pe = state.LandPe, bc = Math.Max(1, state.LandBc);
                double objLat = state.LandLat.Value, objLon = state.LandLon.Value;
                var cuerpo = Body.Current;

                plan = await Task.Run(() =>
                {
                    Body.Current = cuerpo;              // el cálculo usa las constantes del cuerpo
                    return Landing.Planear(orb, t0, ut, rot, objLat, objLon, pe, bc);
                });

                landLayer.Lines.Clear();
                landLayer.Dots.RemoveAll(d => "impacto".Equals(d.Tag) || "frenada".Equals(d.Tag));
                if (plan.Ok)
                {
                    landLayer.Lines.Add(MapLine.FromTrack(plan.Descenso, ColorF.Hex("#ffb454", 0.95f), 2.2f));
                    landLayer.Dots.Add(new MapDot
                    {
                        Lat = plan.BurnLat, Lon = plan.BurnLon, Style = DotStyle.Point, Fill = ColorF.Hex("#4ea3ff"),
                        Tooltip = Lang.F("Frenada: {0} m/s", Geo.F(plan.Dv, 1)), Tag = "frenada",
                    });
                    landLayer.Dots.Add(new MapDot
                    {
                        Lat = plan.ImpactLat, Lon = plan.ImpactLon, Style = DotStyle.Point, Fill = ColorF.Hex("#ffb454"),
                        Tooltip = Lang.T("Punto de contacto"), Tag = "impacto",
                    });
                    globe.SetTrack(plan.Descenso, space: false);
                }
                DibujarObjetivo();
                RenderLandingInfo();
                RequestRender();
            }
            finally
            {
                calculando = false;
                if (landCalc != null) landCalc.Enabled = true;
            }
        }

        void LimpiarAterrizaje()
        {
            plan = null;
            landLayer.Clear();
            DibujarObjetivo();
            PushTrack();
            RenderLandingInfo();
            RequestRender();
        }
    }
}
