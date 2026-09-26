using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* Waypoints nativos: los que el juego enseña en el mapa y en el navball.

       KSP los guarda en la partida, dentro del escenario ScenarioCustomWaypoints, con
       nombre, cuerpo, latitud, longitud y un identificador. El visor sabe escribirlos
       ahí, así que un punto que marques aquí (o una anomalía que quieras visitar) sale
       en el juego sin instalar ningún mod.

       Tocar la partida es cosa seria: siempre se hace copia de seguridad al lado, se
       escribe en un fichero temporal y se sustituye al final, y hay que confirmar. Con
       el juego abierto no sirve de nada: KSP tiene la partida en memoria y la sobrescribe
       al guardar, así que se avisa. */
    public sealed partial class MainForm
    {
        RichLabel wpInfo;

        /* Los waypoints que el visor escribiría para lo que tengas marcado. */
        List<Waypoint> WaypointsDeMarcadores() =>
            MarkersAll().Select(m => new Waypoint { Name = m.Name, Body = Body.Name, Lat = m.Lat, Lon = m.Lon, Mine = true }).ToList();

        List<Waypoint> WaypointsDeAnomalias() =>
            Visibles().Select(a => new Waypoint { Name = a.Name, Body = Body.Name, Lat = a.Lat, Lon = a.Lon, Mine = true }).ToList();

        async void ExportarWaypoints(bool anomalias)
        {
            var lista = anomalias ? WaypointsDeAnomalias() : WaypointsDeMarcadores();
            if (lista.Count == 0)
            {
                Flash(anomalias
                    ? Lang.T("No hay anomalías que exportar en este cuerpo.")
                    : Lang.T("No hay marcadores que exportar."));
                return;
            }

            string destino = ElegirPartidaDestino();
            if (destino == null) return;

            string aviso = Lang.F("Se van a escribir {0} waypoints de {1} en:\n{2}\n\n" +
                                  "KSP los enseñará en el mapa y en el navball. Se guarda antes una copia de seguridad " +
                                  "al lado del fichero.\n\nCierra el juego antes de seguir: si está abierto, sobrescribirá " +
                                  "la partida al guardar y se perderán.\n\n¿Seguir?",
                                  lista.Count, Body.Current.Label, destino);
            if (MessageBox.Show(this, aviso, "Koogle Kerbin", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            try
            {
                string texto = await File.ReadAllTextAsync(destino);
                string bak = SaveExtras.Respaldar(destino);
                string nuevo = SaveExtras.ConWaypoints(texto, lista);
                string tmp = destino + ".tmp";
                await File.WriteAllTextAsync(tmp, nuevo);
                File.Move(tmp, destino, true);

                // para que el panel enseñe ya lo que hay en la partida
                if (extras != null) { extras.Waypoints.RemoveAll(w => lista.Any(l => l.Name == w.Name && l.Body == w.Body)); extras.Waypoints.AddRange(lista); }
                RenderWaypointInfo();
                Flash(Lang.F("Escritos {0} waypoints. Copia de seguridad: {1}", lista.Count, Path.GetFileName(bak)));
            }
            catch (Exception ex)
            {
                Flash(Lang.F("No se pudo escribir en la partida: {0}", ex.Message));
            }
        }

        /* Trae a marcadores los waypoints que ya tenga la partida cargada. */
        void ImportarWaypoints()
        {
            if (extras == null) { Flash(Lang.T("Carga una partida primero.")); return; }
            var mios = extras.Waypoints.Where(w => string.Equals(w.Body, Body.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mios.Count == 0) { Flash(Lang.F("La partida no tiene waypoints en {0}.", Body.Current.Label)); return; }

            int nuevos = 0;
            foreach (var w in mios)
            {
                if (user.Any(m => m.Name == w.Name && Math.Abs(m.Lat - w.Lat) < 1e-6 && Math.Abs(m.Lon - w.Lon) < 1e-6)) continue;
                user.Add(new Marker
                {
                    Id = "wp-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Name = w.Name,
                    Cat = "user",
                    Lat = w.Lat,
                    Lon = w.Lon,
                    Desc = Lang.T("Waypoint de la partida"),
                });
                nuevos++;
            }
            SaveMarkers();
            RenderMarkers();
            Flash(nuevos == 0
                ? Lang.T("Esos waypoints ya estaban como marcadores.")
                : Lang.F("Importados {0} waypoints como marcadores.", nuevos));
        }

        /* El .sfs donde escribir: el original de la partida cargada, o el que elijas. */
        string ElegirPartidaDestino()
        {
            string sugerido = state.SavePath != null && File.Exists(state.SavePath) ? state.SavePath : null;
            using var dlg = new OpenFileDialog
            {
                Title = Lang.T("Partida donde escribir los waypoints"),
                Filter = "KSP (*.sfs)|*.sfs",
                FileName = sugerido != null ? Path.GetFileName(sugerido) : "persistent.sfs",
                InitialDirectory = sugerido != null ? Path.GetDirectoryName(sugerido) : null,
                CheckFileExists = true,
            };
            return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
        }

        void RenderWaypointInfo()
        {
            if (wpInfo == null) return;
            if (extras == null) { wpInfo.SetText(Lang.T("Sin partida cargada.")); return; }
            int aqui = extras.Waypoints.Count(w => string.Equals(w.Body, Body.Name, StringComparison.OrdinalIgnoreCase));
            wpInfo.SetText(Lang.F("La partida tiene <b>{0}</b> waypoints, <b>{1}</b> en {2}.",
                extras.Waypoints.Count, aqui, Body.Current.Label));
        }
    }
}
