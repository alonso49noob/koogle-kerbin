using System;
using System.Linq;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* Summaries of the sidebar's collapsed sections: with fifteen sections, knowing at a glance
       what's inside each one saves opening and closing them again. */
    public sealed partial class MainForm
    {
        void ActualizarEstadosSecciones()
        {
            if (secCuerpo == null) return;

            secCuerpo.Estado = Body.Current.Label;

            secScan.Estado = extras == null
                ? Lang.T("sin partida")
                : cov == null
                    ? Lang.T("sin escanear")
                    : Lang.F("{0} % · {1} de {2} anomalías",
                        Geo.F(cov.Percent(ScanType.Altura | ScanType.Biomas | ScanType.VisualBaja | ScanType.VisualAlta), 0),
                        anomalias.Count(a => a.Detectada), anomalias.Count);

            var rango = RangoFiltro();
            secAltim.Estado = !state.AltFilter ? Lang.T("apagado")
                : Lang.F("{0} a {1} m", Geo.F(rango.Min, 0), Geo.F(rango.Max, 0));

            var destino = SolarSystem.Find(trDestino?.SelectedId ?? state.TransferTo);
            secTransfer.Estado = destino == null ? null
                : transfer != null && transfer.Ok && transfer.Ventanas.Count > 0
                    ? Lang.F("{0} · {1} m/s", destino.Label, Geo.F(transfer.Ventanas[0].Dv, 0))
                    : destino.Label;

            secAterrizaje.Estado = plan != null && plan.Ok
                ? Lang.F("error {0}", Geo.FmtDist(plan.ErrorM))
                : state.LandLat != null ? Lang.T("con objetivo") : Lang.T("sin objetivo");

            secNaves.Estado = sv.Data == null ? Lang.T("sin partida")
                : Lang.F("{0} naves", sv.Marcas?.Count ?? 0);

            secMarcadores.Estado = Lang.F("{0}", MarkersAll().Count());

            EstadoFacciones();
        }
    }
}
