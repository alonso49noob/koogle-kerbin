using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Flight recordings: the Koogle Recorder mod (its own repository, kooglerecorder) writes them
       from inside KSP, and the viewer replays them.

       The mod is separate on purpose: the viewer doesn't need it. This section says whether
       it's in the user's KSP, offers to install it (from the latest release, or from a zip),
       lists the recordings, and plays one back on the time bar: the path on the map and the
       globe, the vessel with its parts (it changes when it stages), followed by the camera,
       and its data as a list, a navball or webcast-style telemetry. A recording still being
       written is reread every couple of seconds, so a flight can be followed almost live. */
    public sealed partial class MainForm
    {
        const string UrlMod = "https://github.com/alonso49noob/kooglerecorder/releases/latest/download/KoogleRecorder.zip";
        const string UrlReleases = "https://github.com/alonso49noob/koogle-kerbin/releases";

        Section secGrabaciones;
        RichLabel grabInfo;
        DarkButton grabInstalar, grabInstalarZip, grabCerrar;
        DrawList grabList;
        readonly MapLayer replayLayer = new();

        Grabacion rep;                      // the recording being replayed
        bool repSel;                        // its data takes the place of the selected vessel's
        GlobePin repPin;
        MapDot repDot;
        List<TrackPoint> repTrack;
        double repRelectura;
        bool repLeyendo;

        static string ModDll(string gd) => Path.Combine(gd, "KoogleRecorder", "Plugins", "KoogleRecorder.dll");
        static string CarpetaGrabaciones(string gd) => Path.Combine(gd, "KoogleRecorder", "PluginData", "Grabaciones");

        void BuildGrabacionesSection()
        {
            var s = secGrabaciones = AddSection("Grabaciones de vuelo", false);
            grabInfo = s.Add(new RichLabel(RichMode.Hint) { HideWhenEmpty = false });
            grabInstalar = new DarkButton("Instalar el grabador en KSP", ButtonVariant.Primary, small: true) { Tip = "Descarga Koogle Recorder de la última versión publicada y lo copia en tu GameData" };
            grabInstalar.Click += async (o, e) => await InstalarMod(null);
            grabInstalarZip = new DarkButton("Desde un zip…", small: true);
            grabInstalarZip.Click += async (o, e) =>
            {
                using var dlg = new OpenFileDialog { Filter = "Koogle Recorder (*.zip)|*.zip" };
                if (dlg.ShowDialog(this) == DialogResult.OK) await InstalarMod(dlg.FileName);
            };
            s.Add(new BtnRow(grabInstalar, grabInstalarZip));
            grabList = s.Add(new DrawList(260) { RowHeight = Theme.S(40), RowGap = Theme.S(2) });
            grabList.DrawItem = DrawGrabacionRow;
            grabList.ItemClick = async (item, pt, r) => await AbrirGrabacion(((Grabacion)item).Ruta);
            grabList.IsSelected = item => rep != null && ((Grabacion)item).Ruta == rep.Ruta;
            var actualizar = new DarkButton("Actualizar", ButtonVariant.Ghost, small: true);
            actualizar.Click += (o, e) => RenderGrabaciones();
            var carpeta = new DarkButton("Abrir carpeta", ButtonVariant.Ghost, small: true);
            carpeta.Click += (o, e) =>
            {
                string gd = FindGameData();
                if (gd == null) return;
                Directory.CreateDirectory(CarpetaGrabaciones(gd));
                try { System.Diagnostics.Process.Start("explorer.exe", CarpetaGrabaciones(gd)); } catch { }
            };
            grabCerrar = new DarkButton("Cerrar la repetición", small: true);
            grabCerrar.Click += (o, e) => CerrarGrabacion();
            s.Add(new BtnRow(actualizar, carpeta, grabCerrar));
            s.Add(Hint("<b>Koogle Recorder</b> es un mod aparte para KSP: mientras juegas, graba cada vuelo de la nave activa desde " +
                       "que despega hasta que aterriza, cambias de nave o sales del vuelo. Aquí se reproduce con la barra de tiempo " +
                       "(pausa, más rápido, hacia atrás), con su traza, su modelo y sus datos como lista, navball o telemetría."));
            s.ExpandedChanged += (o, e) => { if (s.Expanded) RenderGrabaciones(); };
            RenderGrabaciones();
        }

        /* Whether the mod is installed, and the recordings, newest first. */
        void RenderGrabaciones()
        {
            if (grabInfo == null) return;
            string gd = FindGameData();
            bool instalado = gd != null && File.Exists(ModDll(gd));
            Vis.Set(grabInstalar, !instalado && gd != null);
            Vis.Set(grabInstalarZip, !instalado && gd != null);
            Vis.Set(grabCerrar, rep != null);
            grabCerrar.Parent?.PerformLayout();
            grabInstalar.Parent?.PerformLayout();
            var lista = new List<Grabacion>();
            if (gd != null && Directory.Exists(CarpetaGrabaciones(gd)))
                foreach (var f in Directory.EnumerateFiles(CarpetaGrabaciones(gd), "*.kgr", SearchOption.AllDirectories)
                                           .OrderByDescending(File.GetLastWriteTimeUtc).Take(200))
                    try { var g = Grabacion.Cabecera(f); if (g.Muestras.Count > 0) lista.Add(g); } catch { }
            grabList.SetItems(lista);
            Vis.Set(grabList, lista.Count > 0);
            if (gd == null)
                grabInfo.SetText("No encuentro tu instalación de KSP: indícala con «Carpeta de KSP…» en «Naves de una partida».");
            else if (!instalado)
                grabInfo.SetText("Para grabar tus vuelos y verlos aquí hace falta el mod <b>Koogle Recorder</b> en tu KSP. No viene con el visor: " +
                                 "se instala aparte, y el botón lo hace por ti (o descárgalo de la página de releases y descomprímelo en la carpeta de KSP).");
            else if (lista.Count == 0)
                grabInfo.SetText("Koogle Recorder está instalado. Aún no hay grabaciones: despega con cualquier nave y aparecerán aquí.");
            else
                grabInfo.SetText(Lang.F("Koogle Recorder está instalado. Grabaciones: {0}. Pincha una para verla.", lista.Count));
            if (secGrabaciones != null) secGrabaciones.Estado = rep != null ? Lang.T("reproduciendo") : !instalado ? Lang.T("sin el mod") : lista.Count.ToString();
        }

        void DrawGrabacionRow(Graphics g, Rectangle r, object item, bool hover)
        {
            var gr = (Grabacion)item;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int x = r.X + Theme.S(8), w = r.Width - Theme.S(16);
            string dur = Geo.FmtTime(Math.Max(0, gr.Duracion));
            int dw = TextRenderer.MeasureText(dur, Theme.MonoSmall, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, gr.Nave ?? Path.GetFileNameWithoutExtension(gr.Ruta), Theme.UISmall, new Rectangle(x, r.Y + Theme.S(4), w - dw - Theme.S(8), Theme.S(18)), Theme.Fg, flags);
            TextRenderer.DrawText(g, dur, Theme.MonoSmall, new Rectangle(x + w - dw, r.Y + Theme.S(4), dw, Theme.S(18)), Theme.FgDim, flags);
            string sub = (gr.Partida ?? "") + " · " + File.GetLastWriteTime(gr.Ruta).ToString("dd/MM HH:mm") + " · " + (gr.CuerpoInicial ?? "") +
                         (gr.Cerrada ? "" : " · " + Lang.T("en curso"));
            TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(x, r.Y + Theme.S(21), w, Theme.S(16)), Theme.FgDim, flags);
        }

        /* ------------------------------------------------------------ installing the mod */

        async Task InstalarMod(string zip)
        {
            string gd = FindGameData();
            if (gd == null) { Flash("No encuentro tu instalación de KSP: indícala antes con «Carpeta de KSP…»."); return; }
            string temp = null;
            UseWaitCursor = true;
            try
            {
                if (zip == null)
                {
                    temp = Path.Combine(Path.GetTempPath(), "KoogleRecorder-" + Guid.NewGuid().ToString("N") + ".zip");
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("KoogleKerbin");
                    using var resp = await http.GetAsync(UrlMod);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Flash(Lang.F("No pude descargar Koogle Recorder ({0}). Bájalo de la página de releases y usa «Desde un zip…».", (int)resp.StatusCode));
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UrlReleases) { UseShellExecute = true }); } catch { }
                        return;
                    }
                    await File.WriteAllBytesAsync(temp, await resp.Content.ReadAsByteArrayAsync());
                    zip = temp;
                }
                int n = await Task.Run(() => ExtraerMod(zip, Path.GetDirectoryName(gd)));
                if (n == 0) { Flash("Ese zip no trae GameData/KoogleRecorder: ¿es el de Koogle Recorder?"); return; }
                Flash("Koogle Recorder instalado. Se carga la próxima vez que abras KSP: tus vuelos se grabarán solos.");
            }
            catch (IOException ex) { Flash(Lang.T("No se pudo copiar (¿está KSP abierto? Ciérralo y vuelve a intentarlo): ") + ex.Message); }
            catch (Exception ex) { Flash(Lang.T("No se pudo instalar: ") + ex.Message); }
            finally
            {
                UseWaitCursor = false;
                if (temp != null) try { File.Delete(temp); } catch { }
                RenderGrabaciones();
            }
        }

        /* Only what's under GameData/KoogleRecorder/, and nothing that would land outside it. */
        static int ExtraerMod(string zip, string kspRaiz)
        {
            string destinoMod = Path.GetFullPath(Path.Combine(kspRaiz, "GameData", "KoogleRecorder")) + Path.DirectorySeparatorChar;
            int n = 0;
            using var z = ZipFile.OpenRead(zip);
            foreach (var e in z.Entries)
            {
                string nombre = e.FullName.Replace('\\', '/');
                if (nombre.EndsWith("/") || !nombre.StartsWith("GameData/KoogleRecorder/", StringComparison.OrdinalIgnoreCase)) continue;
                string destino = Path.GetFullPath(Path.Combine(kspRaiz, nombre.Replace('/', Path.DirectorySeparatorChar)));
                if (!destino.StartsWith(destinoMod, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destino));
                e.ExtractToFile(destino, true);
                n++;
            }
            return n;
        }

        /* ------------------------------------------------------------ replay */

        async Task AbrirGrabacion(string ruta)
        {
            Grabacion g;
            try { g = await Task.Run(() => Grabacion.Leer(ruta)); }
            catch (Exception ex) { Flash(Lang.T("No se pudo leer la grabación: ") + ex.Message); return; }
            if (g.Muestras.Count < 2) { Flash("Esa grabación aún no tiene datos."); return; }

            if (SolarSystem.Find(g.CuerpoInicial) is BodyDef b && b != Body.Current) SetBody(b.Name);
            // without a save loaded the time bar needs a clock: an empty one at the recording's start
            if (sv.Data == null)
            {
                sv.Data = new SaveData { Ut = g.Inicio };
                sv.Ut = g.Inicio;
                sv.Nombre = Lang.T("sin partida");
                FiltrarNavesDelCuerpo();
            }
            if (sv.Sel != null) SeleccionarNave(sv.Sel);       // deselects it
            rep = g;
            repSel = true;
            repRelectura = 0;
            sim.T = g.Inicio; sim.Warp = 1; sim.Running = true; sim.Last = 0; sim.Dirty = true;
            ConstruirTrazaReplay();
            Vis.Set(tbar, true);
            sim.Follow = true;
            if (is3D) globe.EnterFocus(PosReplay(rep.En(sim.T)));
            else if (!isSky) map.SetView(g.Muestras[0].Lat, g.Muestras[0].Lon, Math.Max(map.Zoom, 6));
            SyncGlobe();
            PushTrack();
            RenderReloj();
            RenderOrbitInfo();
            RenderGrabaciones();
            LayoutOverlays();
            RequestRender();
            Flash(Lang.F("Repitiendo «{0}»: {1}. La barra de tiempo controla la repetición.", g.Nave, Geo.FmtTime(g.Duracion)));
        }

        void CerrarGrabacion()
        {
            if (rep == null) return;
            rep = null;
            repSel = false;
            repPin = null;
            repDot = null;
            repTrack = null;
            replayLayer.Lines.Clear();
            replayLayer.Dots.Clear();
            sim.Follow = false;
            ClearModel();
            globe.ExitFocus();
            if (partidas.Count == 0)
            {
                // the empty clock was only for the replay
                sv.Data = null;
                sim.Running = false;
                Vis.Set(tbar, false);
            }
            SyncGlobe();
            PushTrack();
            RenderOrbitInfo();
            RenderGrabaciones();
            LayoutOverlays();
            RequestRender();
        }

        static double[] PosReplay(Grabacion.Muestra m) =>
            GlobeView.Sph(m.Lat, m.Lon, (Body.Radius + Math.Max(0, m.Alt)) / Body.Radius);

        void ConstruirTrazaReplay()
        {
            replayLayer.Lines.Clear();
            replayLayer.Dots.Clear();
            var cuerpo = Body.Name;
            var pts = new List<TrackPoint>();
            int paso = Math.Max(1, rep.Muestras.Count / 4000);
            for (int i = 0; i < rep.Muestras.Count; i += paso)
            {
                var m = rep.Muestras[i];
                if (m.Cuerpo == cuerpo) pts.Add(new TrackPoint(m.Lat, m.Lon, Math.Max(0, m.Alt), m.Ut));
            }
            repTrack = pts.Count >= 2 ? pts : null;
            if (repTrack != null) replayLayer.Lines.Add(MapLine.FromTrack(repTrack, ColorF.Hex("#ffb454", 0.95f), 2.2f));
            repDot = new MapDot { Lat = rep.Muestras[0].Lat, Lon = rep.Muestras[0].Lon, Style = DotStyle.Vessel, Fill = ColorF.Hex("#ffb454"), Label = rep.Nave, Tooltip = rep.Nave };
            replayLayer.Dots.Add(repDot);
            repPin = new GlobePin { Lat = repDot.Lat, Lon = repDot.Lon, Name = rep.Nave, Color = ColorF.Hex("#ffb454"), Vessel = true };
        }

        IEnumerable<GlobePin> PinesReplay() => repPin != null ? new[] { repPin } : Array.Empty<GlobePin>();

        /* Every frame: where the recorded vessel is at the time bar's moment. */
        void ReplayTick()
        {
            if (rep == null) return;
            if (sim.Running && sim.Warp > 0 && sim.T >= rep.Fin && rep.Cerrada) { sim.T = rep.Fin; sim.Running = false; RenderReloj(); }
            var m = rep.En(sim.T);
            bool aqui = m.Cuerpo == null || m.Cuerpo == Body.Name;
            repDot.Lat = m.Lat; repDot.Lon = m.Lon; repDot.Hidden = !aqui;
            repPin.Lat = m.Lat; repPin.Lon = m.Lon; repPin.R = (Body.Radius + Math.Max(0, m.Alt)) / Body.Radius; repPin.Hidden = !aqui;
            if (sim.Follow && repSel && aqui)
            {
                if (is3D) globe.FocusTarget = PosReplay(m);
                else if (!isSky && !isFree) map.CenterOn(m.Lat, m.Lon);
            }
            // the vessel with the parts it had at that moment, turned as it was
            var nave = rep.NaveEn(sim.T);
            if (nave != null && state.ShowVesselModels && is3D && sim.Follow && repSel)
            {
                if (vesselModels.TryGetValue(nave, out var a))
                {
                    if (globe.FocusModel != a)
                    {
                        bool primera = globe.FocusModel == null;
                        ApplyModel(nave, a);
                        // the first time, close enough to see it
                        if (primera) globe.ZoomToFocusModel();
                    }
                    a.Rot = m.Rot;
                }
                else RequestModel(nave);
            }
            // still being written: reread now and then and keep up with it
            if (!rep.Cerrada && !repLeyendo && clock.Elapsed.TotalSeconds - repRelectura > 2)
            {
                repRelectura = clock.Elapsed.TotalSeconds;
                repLeyendo = true;
                string ruta = rep.Ruta;
                var antes = rep;
                Task.Run(() => Grabacion.Leer(ruta)).ContinueWith(t =>
                {
                    repLeyendo = false;
                    if (t.IsFaulted || rep != antes || t.Result.Muestras.Count <= antes.Muestras.Count) return;
                    BeginInvoke(new Action(() => { if (rep == antes) { rep = t.Result; ConstruirTrazaReplay(); SyncGlobe(); PushTrack(); } }));
                });
            }
        }

        /* The replay's data in the selected-vessel panel (list, navball or telemetry). */
        bool RenderReplayInfo()
        {
            if (rep == null || !repSel) return false;
            var m = rep.En(sim.T);
            var d = new DatosNave
            {
                Nombre = rep.Nave,
                Partida = Lang.T("repetición") + (rep.Partida != null ? " · " + rep.Partida : ""),
                Situacion = Situacion(m.Situacion) + " · " + Lang.F("etapa {0}", m.Etapa),
                Alt = m.Alt,
                VelOrbita = Len(m.VelOrbita), VelSuperficie = Len(m.VelSuperficie), VelVertical = m.VelVertical,
                // a recording isn't an orbit: no periapsis, just the highest point reached
                Ap = rep.Muestras.Max(x => x.Alt), Pe = double.NaN,
                Prograde = Actitud.UnityALocal(m.VelOrbita, m.Lat, m.Lon),
            };
            (d.Nariz, d.Arriba) = Actitud.Local(m.Rot, m.Lat, m.Lon);
            (d.Pitch, d.Rumbo) = Actitud.Angulos(d.Nariz);
            if (RenderInstrumentos(state.ShowOrbitInfo, d)) { if (Vis.Shown(orbHud)) Vis.Set(orbHud, false); return true; }
            bool show = state.ShowOrbitInfo;
            if (Vis.Shown(orbHud) != show) Vis.Set(orbHud, show);
            if (!show) return true;
            orbHud.SetRows(
                ("objeto", rep.Nave),
                ("situación", Situacion(m.Situacion)),
                ("T+", Geo.FmtTime(Math.Max(0, sim.T - rep.Inicio))),
                ("altitud", NavballPanel.FmtAlt(m.Alt)),
                ("vel. orbital", Geo.F(d.VelOrbita, 1) + " m/s"),
                ("vel. superficie", Geo.F(d.VelSuperficie, 1) + " m/s"),
                ("vel. vertical", Geo.F(m.VelVertical, 1) + " m/s"),
                ("acelerador", Geo.F(m.Acelerador * 100, 0) + " %"),
                ("etapa", m.Etapa.ToString()),
                ("masa", Geo.F(m.Masa, 2) + " t"),
                ("aceleración", Geo.F(m.G, 2) + " g"),
                ("cabeceo / rumbo", Geo.F(d.Pitch, 0) + "° / " + Geo.F(d.Rumbo, 0) + "°"));
            PlaceOrbitInfo();
            return true;
        }

        static double Len(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
    }
}
