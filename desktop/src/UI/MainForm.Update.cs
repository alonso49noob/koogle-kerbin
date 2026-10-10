using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* Automatic updates: the panel section and the notice on startup. The GitHub logic, the
       download and the verification are in Core/Updater.cs; the new installer does the rest
       (see /update in installer/Instalador.cs). */
    public sealed partial class MainForm
    {
        static readonly TimeSpan updateEvery = TimeSpan.FromHours(12);

        RichLabel updInfo;
        DarkCheck chkAutoUpd;
        DarkButton updCheck, updInstall;
        UpdateInfo updPending;
        bool updBusy, updInstalling;

        void BuildUpdateSection()
        {
            var s = AddSection("Actualizaciones", false);
            updInfo = s.Add(Readout());
            updInfo.SetText(Lang.F("Versión instalada: <b>{0}</b>", Updater.Current));
            chkAutoUpd = new DarkCheck("Buscar versiones nuevas al abrir", state.AutoUpdate);
            chkAutoUpd.CheckedChanged += (o, e) => { state.AutoUpdate = chkAutoUpd.Checked; SaveSettings(); };
            s.Add(Checks(chkAutoUpd));
            updCheck = new DarkButton("Buscar ahora");
            updCheck.Click += async (o, e) => await ComprobarActualizacion(true);
            updInstall = new DarkButton("Actualizar", ButtonVariant.Primary);
            updInstall.Click += async (o, e) => await InstalarActualizacion();
            Vis.Set(updInstall, false);
            s.Add(new BtnRow(updCheck, updInstall));
            s.Add(Hint("Se consulta la última release en GitHub, se baja el instalador, se comprueba su huella SHA-256 y se instala " +
                       "en la misma carpeta: el visor se cierra y vuelve a abrirse solo. Tus ajustes y tu partida se conservan."));
        }

        /* On startup it checks at most every 12 hours and silently: it only bothers you if there's something
           new. */
        async Task ComprobarActualizacion(bool manual)
        {
            if (updBusy) return;
            if (!manual)
            {
                if (!state.AutoUpdate) return;
                if (state.LastUpdateCheck is DateTime last && DateTime.UtcNow - last < updateEvery && updPending == null) return;
                await Task.Delay(2500);                           // let the window finish starting up
            }

            updBusy = true;
            updCheck.Enabled = false;
            if (manual) updInfo.SetText(Lang.T("Buscando…"));
            try
            {
                var u = await Updater.CheckAsync();
                state.LastUpdateCheck = DateTime.UtcNow;
                SaveSettings();
                updPending = u;
                if (u == null)
                {
                    updInfo.SetText(Lang.F("Versión instalada: <b>{0}</b> · estás al día", Updater.Current));
                    Vis.Set(updInstall, false);
                    if (manual) Flash("Ya tienes la última versión.");
                    return;
                }
                updInfo.SetText(Lang.F("Versión instalada: <b>{0}</b> · nueva: <b>{1}</b>", Updater.Current, u.Version));
                updInstall.Text = Lang.T(Updater.CanInstall(u) ? "Actualizar" : "Ver en GitHub");
                Vis.Set(updInstall, true);
                if (manual || state.SkippedVersion != u.Tag) await PreguntarActualizacion(u);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[update] " + ex.Message);
                if (manual) updInfo.SetText(Lang.F("No se pudo consultar GitHub: {0}", RichLabel.Esc(ex.Message)));
            }
            finally
            {
                updBusy = false;
                updCheck.Enabled = !updInstalling;
            }
        }

        async Task PreguntarActualizacion(UpdateInfo u)
        {
            bool auto = Updater.CanInstall(u);
            var ahora = new TaskDialogButton(Lang.T(auto ? "Actualizar ahora" : "Ver en GitHub"));
            var luego = new TaskDialogButton(Lang.T("Más tarde"));
            var omitir = new TaskDialogButton(Lang.T("Omitir esta versión"));
            var page = new TaskDialogPage
            {
                Caption = "Koogle Kerbin",
                Heading = Lang.F("Koogle Kerbin {0} está disponible", u.Version),
                Text = Lang.F("Tienes la versión {0}.", Updater.Current) + (auto ? "\n" + Lang.T("Al actualizar, el visor se cierra y vuelve a abrirse solo.") : ""),
                Icon = TaskDialogIcon.Information,
                AllowCancel = true,
                Buttons = { ahora, luego, omitir },
                DefaultButton = ahora,
            };
            if (!string.IsNullOrWhiteSpace(u.Notes))
            {
                string notas = u.Notes.Trim();
                page.Expander = new TaskDialogExpander(notas.Length > 1500 ? notas.Substring(0, 1500) + "…" : notas)
                { CollapsedButtonText = Lang.T("Novedades"), ExpandedButtonText = Lang.T("Novedades") };
            }

            var r = TaskDialog.ShowDialog(this, page);
            if (r == omitir) { state.SkippedVersion = u.Tag; SaveSettings(); }
            else if (r == ahora) await InstalarActualizacion();
        }

        async Task InstalarActualizacion()
        {
            var u = updPending;
            if (u == null || updInstalling) return;
            if (!Updater.CanInstall(u))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.PageUrl) { UseShellExecute = true }); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[update] " + ex.Message); }
                return;
            }

            updInstalling = true;
            updCheck.Enabled = updInstall.Enabled = false;
            try
            {
                var progreso = new Progress<(long Hecho, long Total)>(p =>
                    updInfo.SetText(Lang.F("Descargando {0}… {1} %", u.Version, p.Total > 0 ? (int)(100.0 * p.Hecho / p.Total) : 0)));
                string exe = await Updater.DownloadAsync(u, (a, b) => ((IProgress<(long, long)>)progreso).Report((a, b)));

                // same as when changing language: what's there is saved before closing
                state.SimT = HasVessels ? sim.T : null;
                SaveView();
                if (!Updater.Launch(exe)) throw new InvalidOperationException(Lang.T("No se pudo abrir el instalador."));
                updInfo.SetText(Lang.T("Instalando… el visor se reabrirá solo."));
                Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[update] " + ex.Message);
                updInfo.SetText(Lang.F("No se pudo actualizar: {0}", RichLabel.Esc(ex.Message)));
                updInstalling = false;
                updCheck.Enabled = updInstall.Enabled = true;
            }
        }
    }
}
