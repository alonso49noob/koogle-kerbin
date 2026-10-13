using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* Several saves at once: everybody's vessels in the viewer, without loading them into KSP.

       Each save has its own clock (one at year 4, another at year 54...), so they're put on a
       unified one: UNI 0 is the moment each save was taken, and from there they all advance
       together. Underneath, the save with the latest UT is the reference: the viewer's clock
       and the planet's rotation are its own, and the vessels of the others have their orbit's
       epoch moved by the difference (Vessel.EnOtraHora), so at UNI t each one is where its own
       save would have it at its own UT + t. A panel next to the time bar shows every save's
       date at that moment.

       The merge can be exported as a new save (UnirPartidas): the reference save with all the
       other vessels added, at its UT, the latest of them all. */
    public sealed partial class MainForm
    {
        sealed class PartidaCargada
        {
            public string Nombre, Ruta, Texto;
            public double Ut;
            public SaveData Data;
            public SaveExtras Extras;
        }

        readonly List<PartidaCargada> partidas = new();
        HudPanel relojes;
        DrawList partidasList;
        DarkButton btnAñadirPartida, btnExportarUnida;
        DarkCombo vistaNaveCombo;
        NavballPanel navball;
        TelemetriaPanel telemetria;

        /* The save whose clock the viewer runs on: the one with the latest UT. */
        int PartidaRef => partidas.Count == 0 ? 0 : partidas.IndexOf(partidas.OrderByDescending(p => p.Ut).First());

        static string Etiqueta(int i) => ((char)('A' + i % 26)).ToString();

        static readonly string[] ColoresPartida = { "#4ea3ff", "#ffb454", "#7ee787", "#c77dff", "#ff6b6b", "#5ed6d6" };
        static Color ColorPartida(int i) => Theme.Hex(ColoresPartida[i % ColoresPartida.Length]);

        /* A save's name: its folder (saves/<name>/persistent.sfs), or the file's. */
        static string NombrePartida(string path)
        {
            string carpeta = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            return Path.GetFileName(path).Equals("persistent.sfs", StringComparison.OrdinalIgnoreCase) && carpeta.Length > 0
                ? carpeta : Path.GetFileNameWithoutExtension(path);
        }

        static bool MismoFichero(string a, string b) =>
            a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        static string CopiaExtra(int k) => Path.Combine(Path.GetDirectoryName(Store.SaveCopyPath) ?? "", "partida-extra-" + k + ".sfs");

        /* ------------------------------------------------------------ merging */

        void AñadirPartida(string path, string texto, SaveData d, SaveExtras extras, bool restoring, string nombre = null)
        {
            if (partidas.Any(p => MismoFichero(p.Ruta, path)) && !restoring) { Flash("Esa partida ya está cargada."); return; }
            partidas.Add(new PartidaCargada { Nombre = nombre ?? NombrePartida(path), Ruta = path, Texto = texto, Data = d, Extras = extras, Ut = d.Ut.Value });
            if (!restoring) GuardarCopiasExtra();
            Recomponer();
            if (!restoring)
                Flash(Lang.F("«{0}» añadida: {1} partidas en la hora unificada UNI. La de referencia es «{2}», la de hora más alta.",
                             partidas[^1].Nombre, partidas.Count, partidas[PartidaRef].Nombre));
        }

        void QuitarPartida(int i)
        {
            if (partidas.Count < 2 || i < 0 || i >= partidas.Count) return;
            string n = partidas[i].Nombre;
            partidas.RemoveAt(i);
            // the first one is the save copy restored on startup; if it went, the next takes its place
            if (i == 0) GuardarCopia(partidas[0].Ruta, partidas[0].Texto);
            GuardarCopiasExtra();
            Recomponer();
            Flash(Lang.F("«{0}» quitada.", n));
        }

        /* Puts every save on the reference save's clock, keeping the time bar's UNI moment. */
        void Recomponer()
        {
            double uni = sim.T - sv.Ut;
            int r = PartidaRef;
            var refP = partidas[r];
            var naves = new List<Vessel>();
            for (int i = 0; i < partidas.Count; i++)
                foreach (var v in partidas[i].Data.Vessels)
                {
                    if (i == r) { v.Partida = i; naves.Add(v); }
                    else naves.Add(v.EnOtraHora(refP.Ut - partidas[i].Ut, i));
                }
            sv.Data = new SaveData { Ut = refP.Ut, Vessels = naves };
            sv.Ut = refP.Ut;
            sim.T = sv.Ut + uni;
            sim.Dirty = true;
            sv.Nombre = partidas.Count > 1 ? Lang.F("{0} partidas unidas", partidas.Count) : partidas[0].Nombre;
            // other vessel objects: the assembled models are rebuilt
            vesselModels.Clear();
            navesCargando.Clear();
            ClearModel();
            SetModelStatus(null);
            // SCANsat, waypoints and KSC levels are the reference save's
            AplicarExtras(refP.Extras, proponerModo: false);
            ActualizarEstadosSecciones();
            FiltrarNavesDelCuerpo();
            Vis.Set(tbar, true);
            RenderReloj();
            RenderPartidas();
            LayoutOverlays();
            RequestRender();
        }

        /* The extra saves are copied to the local folder like the main one, so they come back
           on startup even if the originals change or disappear. */
        void GuardarCopiasExtra()
        {
            try
            {
                for (int k = 0; k < 64 && File.Exists(CopiaExtra(k)); k++) File.Delete(CopiaExtra(k));
                var rutas = new List<string>();
                for (int i = 1; i < partidas.Count; i++)
                {
                    File.WriteAllText(CopiaExtra(i - 1), partidas[i].Texto);
                    rutas.Add(partidas[i].Ruta);
                }
                state.PartidasExtra = rutas.Count > 0 ? rutas : null;
                SaveSettings();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[partidas] copias: " + ex.Message); }
        }

        async Task RestaurarPartidasExtra()
        {
            var rutas = state.PartidasExtra;
            if (rutas == null || rutas.Count == 0 || partidas.Count == 0) return;
            for (int k = 0; k < rutas.Count; k++)
            {
                string copia = CopiaExtra(k);
                if (!File.Exists(copia)) continue;
                try
                {
                    string texto = await File.ReadAllTextAsync(copia);
                    var d = await Task.Run(() => SaveFile.Parse(texto));
                    var ex = await Task.Run(() => SaveExtras.Parse(texto));
                    if (d.Ut is double u && u > 0) AñadirPartida(rutas[k], texto, d, ex, restoring: true, nombre: NombrePartida(rutas[k]));
                }
                catch (Exception e) { System.Diagnostics.Debug.WriteLine("[partidas] restaurar: " + e.Message); }
            }
            // the moment on the unified clock when the viewer was closed
            if (state.SimUni is double uni) { sim.T = sv.Ut + uni; sim.Dirty = true; RenderReloj(); }
        }

        async Task PickPartidasParaAñadir()
        {
            if (partidas.Count == 0) { await PickSaveFile(); return; }
            using var dlg = new OpenFileDialog { Filter = "Partidas de KSP (*.sfs)|*.sfs;*.loadmeta|Todos|*.*", Multiselect = true, Title = Lang.T("Añadir partidas a la vista") };
            string gd = FindGameData();
            string saves = gd == null ? null : Path.Combine(Path.GetDirectoryName(gd) ?? "", "saves");
            if (saves != null && Directory.Exists(saves)) dlg.InitialDirectory = saves;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            foreach (var f in dlg.FileNames) await CargarSave(f, añadir: true);
        }

        /* ------------------------------------------------------------ export */

        async void ExportarUnida()
        {
            if (partidas.Count < 2) { Flash("Carga al menos dos partidas para unirlas."); return; }
            int r = PartidaRef;
            var refP = partidas[r];
            using var dlg = new SaveFileDialog
            {
                Filter = "Partidas de KSP (*.sfs)|*.sfs", FileName = "persistent.sfs", OverwritePrompt = true,
                Title = Lang.T("Guardar la partida unida (mejor en una carpeta nueva dentro de saves)")
            };
            string gd = FindGameData();
            string saves = gd == null ? null : Path.Combine(Path.GetDirectoryName(gd) ?? "", "saves");
            if (saves != null && Directory.Exists(saves)) dlg.InitialDirectory = saves;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string destino = dlg.FileName;
            if (partidas.Any(p => MismoFichero(p.Ruta, destino)) || MismoFichero(state.SavePath, destino) ||
                state.PartidasExtra?.Any(x => MismoFichero(x, destino)) == true)
            {
                Flash("No sobrescribo ninguna de las partidas originales: guárdala en otra carpeta (por ejemplo, una nueva dentro de saves).");
                return;
            }
            var otras = partidas.Select((p, i) => (p, i)).Where(x => x.i != r)
                .Select(x => new UnirPartidas.Fuente { Texto = x.p.Texto, Etiqueta = Etiqueta(x.i), Ut = x.p.Ut }).ToList();
            UseWaitCursor = true;
            try
            {
                var res = await Task.Run(() => UnirPartidas.Unir(refP.Texto, otras, refP.Ut));
                Directory.CreateDirectory(Path.GetDirectoryName(destino) ?? ".");
                await File.WriteAllTextAsync(destino + ".tmp", res.Texto);
                File.Move(destino + ".tmp", destino, true);
                Flash(Lang.F("Partida unida guardada: {0} naves añadidas a «{1}», a su hora ({2}).", res.Naves, refP.Nombre, Geo.FechaKerbal(refP.Ut)) +
                      (res.Renombrados > 0 ? " " + Lang.F("{0} kerbals con el mismo nombre que otro llevan ahora la letra de su partida.", res.Renombrados) : ""));
            }
            catch (Exception ex) { Flash(Lang.T("No se pudo unir: ") + ex.Message); }
            finally { UseWaitCursor = false; }
        }

        /* ------------------------------------------------------------ panel and clocks */

        void BuildPartidasControls(Section naves)
        {
            partidasList = naves.Add(new DrawList(170) { RowHeight = Theme.S(26), RowGap = Theme.S(2) });
            partidasList.DrawItem = DrawPartidaRow;
            partidasList.ItemClick = (item, pt, r) =>
            {
                int i = partidas.IndexOf((PartidaCargada)item);
                if (partidas.Count > 1 && pt.X > r.Width - Theme.S(28)) QuitarPartida(i);
            };
            btnAñadirPartida = new DarkButton("Añadir otra partida…", small: true) { Tip = "Junta las naves de varias partidas (de otras personas, por ejemplo) en la misma vista" };
            btnAñadirPartida.Click += async (s, e) => await PickPartidasParaAñadir();
            btnExportarUnida = new DarkButton("Exportar unida…", small: true) { Tip = "Guarda una partida nueva con todas las naves, a la hora más alta" };
            btnExportarUnida.Click += (s, e) => ExportarUnida();
            naves.Add(new BtnRow(btnAñadirPartida, btnExportarUnida));
            naves.Add(Hint("Varias partidas a la vez (de otras personas, por ejemplo) van en la hora unificada <b>UNI</b>: " +
                           "UNI 0 es el momento de cada partida y desde ahí avanzan juntas; junto a la barra de tiempo sale la " +
                           "fecha de cada una. «Exportar unida» guarda una partida nueva con todas las naves, a la hora más alta."));
            RenderPartidas();
        }

        void RenderPartidas()
        {
            if (partidasList == null) return;
            partidasList.SetItems(partidas.Cast<object>());
            Vis.Set(partidasList, partidas.Count > 1);
            Vis.Set(btnAñadirPartida, partidas.Count > 0);
            Vis.Set(btnExportarUnida, partidas.Count > 1);
            btnAñadirPartida.Parent?.PerformLayout();
            RenderRelojesPartidas();
        }

        void DrawPartidaRow(Graphics g, Rectangle r, object item, bool hover)
        {
            var p = (PartidaCargada)item;
            int i = partidas.IndexOf(p);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int x = r.X + Theme.S(6);
            DibujarLetraPartida(g, i, x, r.Y + r.Height / 2);
            x += Theme.S(22);
            string fecha = Geo.FechaKerbal(p.Ut + (sim.T - sv.Ut));
            int fw = TextRenderer.MeasureText(fecha, Theme.MonoSmall, Size.Empty, TextFormatFlags.NoPadding).Width;
            int xQuitar = r.Right - Theme.S(24);
            // the reference save (the viewer's clock) in bold
            TextRenderer.DrawText(g, p.Nombre, i == PartidaRef ? Theme.Semibold : Theme.UISmall,
                new Rectangle(x, r.Y, Math.Max(0, xQuitar - fw - Theme.S(8) - x), r.Height), Theme.Fg, flags);
            TextRenderer.DrawText(g, fecha, Theme.MonoSmall, new Rectangle(xQuitar - fw - Theme.S(4), r.Y, fw, r.Height), Theme.FgDim, flags);
            Theme.DrawGlyph(g, Theme.Glyph.Close, Theme.IconSmall, new Rectangle(xQuitar, r.Y, Theme.S(20), r.Height), hover ? Theme.Danger : Theme.FgDim);
        }

        /* The save's letter in its colour, also next to each vessel in the list. */
        void DibujarLetraPartida(Graphics g, int i, int x, int cy)
        {
            int s = Theme.S(16);
            var rr = new RectangleF(x, cy - s / 2f, s, s);
            Theme.FillRound(g, ColorPartida(i), Color.Transparent, rr, Theme.Sf(3), 0);
            Theme.DrawGlyph(g, Etiqueta(i), Theme.Semibold, Rectangle.Round(rr), Color.FromArgb(20, 24, 30));
        }

        /* Every save's own date at the time bar's UNI moment, next to the time bar. */
        void RenderRelojesPartidas()
        {
            if (relojes == null) return;
            // during a flight replay the clock is the recording's: the saves' dates would only confuse
            bool ver = partidas.Count > 1 && Vis.Shown(tbar) && rep == null;
            if (Vis.Shown(relojes) != ver) Vis.Set(relojes, ver);
            if (!ver) return;
            double uni = sim.T - sv.Ut;
            var filas = new List<(string, string)> { ("UNI", (uni < 0 ? "−" : "+") + Geo.FmtTime(Math.Abs(uni))) };
            for (int i = 0; i < partidas.Count; i++)
            {
                string n = partidas[i].Nombre.Length > 16 ? partidas[i].Nombre.Substring(0, 15) + "…" : partidas[i].Nombre;
                filas.Add((Etiqueta(i) + " · " + n, Geo.FechaKerbal(partidas[i].Ut + uni)));
            }
            relojes.SetRows(filas.ToArray());
            PlaceRelojesPartidas();
        }

        /* To the right of the time bar if there's room; if not, above it. */
        void PlaceRelojesPartidas()
        {
            if (relojes == null || tbar == null || !Vis.Shown(relojes)) { PlaceInstrumentos(); return; }
            int gap = Theme.S(8);
            if (tbar.Right + gap + relojes.Width <= mapArea.Width - gap)
                relojes.Location = new Point(tbar.Right + gap, tbar.Bottom - relojes.Height);
            else
                relojes.Location = new Point(Math.Max(gap, tbar.Right - relojes.Width), tbar.Top - gap - relojes.Height);
            PlaceInstrumentos();
        }

        /* ------------------------------------------------------------ instruments */

        void BuildInstrumentos()
        {
            relojes = new HudPanel { Visible = false };
            navball = new NavballPanel();
            telemetria = new TelemetriaPanel();
        }

        void BuildVistaNaveControl(Section naves)
        {
            vistaNaveCombo = new DarkCombo();
            vistaNaveCombo.SetItems(new[] { ("lista", "Lista de datos"), ("navball", "Navball (estilo KSP)"), ("telemetria", "Telemetría (estilo retransmisión)") });
            vistaNaveCombo.SelectedId = VistaNave;
            vistaNaveCombo.SelectedChanged += (s, e) =>
            {
                state.VistaNave = vistaNaveCombo.SelectedId;
                SaveSettings();
                RenderOrbitInfo();
                LayoutOverlays();
            };
            naves.Add(Field("Datos de la nave elegida", vistaNaveCombo));
        }

        string VistaNave => state.VistaNave is "navball" or "telemetria" ? state.VistaNave : "lista";

        /* Shows the selected vessel's data in the chosen style; false if the style is the list. */
        bool RenderInstrumentos(bool show, DatosNave datos = null)
        {
            if (navball == null) return false;
            string modo = VistaNave;
            bool nb = show && modo == "navball", tl = show && modo == "telemetria";
            if (Vis.Shown(navball) != nb) Vis.Set(navball, nb);
            if (Vis.Shown(telemetria) != tl) Vis.Set(telemetria, tl);
            if (!nb && !tl) return false;
            var d = datos ?? DatosDeNave(sv.Sel);
            if (nb) navball.Mostrar(d); else telemetria.Mostrar(d);
            PlaceInstrumentos();
            return true;
        }

        /* Centred above the time bar (or at the bottom, without it). */
        void PlaceInstrumentos()
        {
            if (navball == null) return;
            int gap = Theme.S(10);
            int baseY = tbar != null && Vis.Shown(tbar) ? tbar.Top : mapArea.Height - Theme.S(16);
            // the saves' clocks, when they had to go above the time bar, stay under the instruments
            if (relojes != null && Vis.Shown(relojes) && tbar != null && relojes.Bottom <= tbar.Top) baseY = Math.Min(baseY, relojes.Top);
            foreach (Control c in new Control[] { navball, telemetria })
                if (Vis.Shown(c)) c.Location = new Point((mapArea.Width - c.Width) / 2, baseY - gap - c.Height);
        }

        /* The selected vessel's state at the time bar's moment, in the local horizon frame. */
        DatosNave DatosDeNave(Vessel v)
        {
            var e = v.Orbit;
            double t = sim.T;
            double rot = RotBase() + 360 * ((t - sv.Ut) / Body.SiderealDay);
            var p = SaveFile.PosicionEn(e, t, sv.Ut, RotBase());
            var (R, V) = SaveFile.Vectores(e, t);
            var Rb = Actitud.AFijo(R, rot);
            var Vb = Actitud.AFijo(V, rot);
            double w = 2 * Math.PI / Body.SiderealDay;
            var Vs = new[] { Vb[0] + w * Rb[1], Vb[1] - w * Rb[0], Vb[2] };   // relative to the rotating ground
            double r = Math.Sqrt(Actitud.Dot(Rb, Rb));
            var (nariz, arriba) = Actitud.Local(v.Rot, p.Lat, p.Lon);
            var pro = Actitud.ALocal(Vb, p.Lat, p.Lon);
            var (pitch, rumbo) = Actitud.Angulos(nariz);
            return new DatosNave
            {
                Nombre = v.Name,
                Partida = partidas.Count > 1 ? Etiqueta(v.Partida) + " · " + partidas[Math.Clamp(v.Partida, 0, partidas.Count - 1)].Nombre : Lang.T("en directo"),
                Situacion = v.Type + " · " + Situacion(v.Sit),
                Alt = p.Alt,
                VelOrbita = Math.Sqrt(Actitud.Dot(V, V)),
                VelSuperficie = Math.Sqrt(Actitud.Dot(Vs, Vs)),
                VelVertical = Actitud.Dot(Vb, Rb) / r,
                Ap = e.Sma * (1 + e.Ecc) - Body.Radius,
                Pe = e.Sma * (1 - e.Ecc) - Body.Radius,
                Nariz = nariz, Arriba = arriba, Prograde = pro,
                Pitch = pitch, Rumbo = rumbo,
            };
        }
    }
}
