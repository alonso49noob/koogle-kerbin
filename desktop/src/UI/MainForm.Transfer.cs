using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* Transferencias a otros cuerpos y sus ventanas de lanzamiento.

       Se elige destino y el visor busca, a partir del instante de la barra de tiempo, las
       salidas más baratas: cuándo salir, el Δv de eyección desde la órbita de
       aparcamiento, el de captura al llegar, el tiempo de vuelo y los dos ángulos que
       hacen falta para montarla en el juego (fase entre los cuerpos y eyección respecto
       al prógrado). Todo balístico: una sola quemada, sin correcciones a medio camino. */
    public sealed partial class MainForm
    {
        DarkCombo trDestino;
        DarkTextBox trPark, trCaptura, trPlazo;
        DarkCheck trCapturar;
        DarkButton trBuscar;
        RichLabel trInfo;
        DrawList trList;
        TransferPlan transfer;
        bool buscandoTransfer;

        /* Los cuerpos a los que tiene sentido ir desde el actual: los que comparten
           cuerpo central (hermanos y sus lunas) y las lunas del propio cuerpo. */
        void RenderDestinos()
        {
            if (trDestino == null) return;
            var items = new List<(string, string)>();
            foreach (var (b, depth) in SolarSystem.Tree())
            {
                if (b == Body.Current || b.IsStar) continue;
                if (Transfer.Comun(Body.Current, b) == null) continue;
                items.Add((b.Name, new string(' ', depth * 4) + b.Label));
            }
            trDestino.SetItems(items);
            if (items.Any(i => i.Item1 == state.TransferTo)) trDestino.SelectedId = state.TransferTo;
            else if (items.Count > 0) { trDestino.SelectedId = items[0].Item1; state.TransferTo = items[0].Item1; }
        }

        async Task BuscarVentanas()
        {
            if (buscandoTransfer) return;
            var destino = SolarSystem.Find(trDestino?.SelectedId ?? state.TransferTo);
            if (destino == null) { Flash(Lang.T("Elige un destino.")); return; }

            buscandoTransfer = true;
            if (trBuscar != null) trBuscar.Enabled = false;
            try
            {
                var origen = Body.Current;
                double t0 = sv.Data != null ? sim.T : 0;
                double plazo = Math.Max(1, state.TransferSpan) * SolarSystem.Home.SolarDay;
                double park = Math.Max(0, state.TransferPark);
                double cap = Math.Max(0, state.TransferCapture);
                bool capturar = state.TransferCapturar;

                transfer = await Task.Run(() =>
                {
                    Body.Current = origen;
                    return Transfer.Buscar(origen, destino, t0, plazo, park, cap, capturar, 4);
                });

                trList?.SetItems((transfer.Ventanas ?? new List<TransferWindow>()).Cast<object>());
                RenderTransferInfo();
                ActualizarEstadosSecciones();
                RequestRender();
            }
            finally
            {
                buscandoTransfer = false;
                if (trBuscar != null) trBuscar.Enabled = true;
            }
        }

        void RenderTransferInfo()
        {
            if (trInfo == null) return;
            var destino = SolarSystem.Find(trDestino?.SelectedId ?? state.TransferTo);
            var partes = new List<string>();

            if (destino != null)
            {
                var central = Transfer.Comun(Body.Current, destino);
                partes.Add(Lang.F("De {0} a {1}, alrededor de {2}.", Body.Current.Label, destino.Label, central?.Label ?? "?"));
            }
            if (sv.Data == null) partes.Add(Lang.T("Sin partida: la búsqueda empieza en el año 1, día 1."));

            if (transfer != null && !transfer.Ok) partes.Add(Lang.T(transfer.Problema ?? "Sin resultados."));
            else if (transfer != null && transfer.Ventanas.Count > 0)
            {
                var w = transfer.Ventanas[0];
                partes.Add("");
                partes.Add(Lang.F("La más barata sale el <b>{0}</b>.", Geo.FechaKerbal(w.DepartUT)));
                partes.Add(Lang.F("Δv <b>{0} m/s</b> al salir{1}, {2} de vuelo.",
                    Geo.F(w.DvSalida, 0),
                    transfer.CaptureAlt >= 0 && w.DvLlegada > 0 ? Lang.F(" y {0} m/s al capturar", Geo.F(w.DvLlegada, 0)) : "",
                    Geo.FmtTime(w.Tof)));
                partes.Add(Lang.F("Ángulo de fase {0}°, eyección {1}° respecto al prógrado.",
                    Geo.F(w.AnguloFase, 1), double.IsNaN(w.AnguloEyeccion) ? "—" : Geo.F(w.AnguloEyeccion, 1)));
                partes.Add(Lang.F("Llega el {0}, a {1} m/s de exceso.", Geo.FechaKerbal(w.ArriveUT), Geo.F(w.VinfLlegada, 0)));
            }
            trInfo.SetText(string.Join("\n", partes));
        }

        void DrawTransferRow(Graphics g, Rectangle r, object item, bool hover)
        {
            var w = (TransferWindow)item;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int x = r.X + Theme.S(8);
            int wDv = Theme.S(74), wTof = Theme.S(68);
            TextRenderer.DrawText(g, Geo.FechaCorta(w.DepartUT), Theme.MonoSmall,
                new Rectangle(x, r.Y, r.Right - x - wDv - wTof - Theme.S(8), r.Height), Theme.Fg, flags | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Geo.F(w.Dv, 0) + " m/s", Theme.MonoSmall,
                new Rectangle(r.Right - wDv - wTof, r.Y, wDv, r.Height), Theme.Accent, flags | TextFormatFlags.Right);
            TextRenderer.DrawText(g, Geo.F(w.Tof / SolarSystem.Home.SolarDay, 0) + " d", Theme.MonoSmall,
                new Rectangle(r.Right - wTof, r.Y, wTof - Theme.S(6), r.Height), Theme.FgDim, flags | TextFormatFlags.Right);
        }

        /* Pinchar una ventana lleva la barra de tiempo al instante de la salida, para ver
           dónde están los cuerpos ese día. */
        void TransferRowClick(object item, Point pt, Rectangle rect)
        {
            var w = (TransferWindow)item;
            if (sv.Data == null) { Flash(Lang.T("Carga una partida para mover la barra de tiempo.")); return; }
            sim.T = w.DepartUT;
            sim.Running = false;
            sim.Dirty = true;
            RenderReloj();
            ActualizarSol();
            RequestRender();
        }
    }
}
