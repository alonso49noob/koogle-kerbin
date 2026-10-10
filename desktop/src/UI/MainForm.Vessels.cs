using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Vessels from a save: reading the .sfs, simulation clock with time warp, tracks, rings on
       the globe and following a vessel. */
    public sealed partial class MainForm
    {
        sealed class Marca
        {
            public Vessel V;
            public TrackPoint P;
            public MapDot Dot;
            public GlobePin Pin;
            public bool Oculto;
        }

        sealed class SvState
        {
            public SaveData Data;                 // the whole save: when the body changes the vessels are filtered again
            public string Nombre;
            public double Ut, Rot, MaxR = 1.2;
            public Calibration Calib;
            public List<Vessel> Naves = new();
            public SortedDictionary<string, bool> Tipos = new(StringComparer.Ordinal);
            public Dictionary<string, int> Cuenta = new();
            public Vessel Sel;
            public List<TrackPoint> TrackPts;
            public List<Marca> Marcas = new();
            public Dictionary<Vessel, Marca> PorNave = new();
            public bool Todas;
        }

        sealed class SimState
        {
            public double T, Warp = 1, Last, Lento;
            public bool Running, Follow, Dirty;
        }

        readonly SvState sv = new();
        readonly SimState sim = new();
        List<TrackPoint> lastTrack;           // the orbit drawn by hand in its panel

        OverlayPanel tbar;
        DarkButton tNow, tBack, tPlay, tFwd, tFollow, tOrb;
        HudPanel orbHud;
        TextLabel tWarp, tFecha;

        static readonly Dictionary<string, string> ColorTipo = new()
        {
            ["Relay"] = "#7ee787", ["Probe"] = "#4ea3ff", ["Station"] = "#ffb454", ["Base"] = "#ffb454",
            ["Ship"] = "#ff6b6b", ["Lander"] = "#ff6b6b", ["Rover"] = "#c77dff",
            ["Debris"] = "#8a9bb0", ["SpaceObject"] = "#6b7f95", ["EVA"] = "#ffffff", ["Flag"] = "#c77dff"
        };
        static string ColorDe(string t) => t != null && ColorTipo.TryGetValue(t, out var c) ? c : "#8a9bb0";

        /* The same steps as KSP's time warp, in both directions: a single signed scale lets you
           slow down backwards step by step. */
        static readonly double[] Warps = { 1, 5, 10, 50, 100, 1000, 10000, 100000 };
        static readonly double[] Escala = Warps.Reverse().Select(w => -w).Concat(Warps).ToArray();

        bool HasVessels => sv.Naves.Count > 0;
        bool Following => sim.Follow;
        bool SimWantsFrames => HasVessels && (sim.Running || sim.Dirty);
        void SimResetClock() => sim.Last = 0;
        void SimDirty() { sim.Dirty = true; RequestRender(); }
        double RotBase() => sv.Rot + svRot.NumberOr(0);

        /* Where the Sun is at the time on the time bar, with the same rotation that moves the
           vessels. Without vessels on this body to measure it with, the game's. */
        Sun.Position SunNow()
        {
            var (ut, rot) = InstanteYGiro();
            return Sun.Subsolar(ut, rot);
        }
        IEnumerable<GlobePin> VesselPins() => sv.Marcas.Select(m => m.Pin);

        /* ------------------------------------------------------------ time bar */

        void BuildTimeBar()
        {
            var sym = Theme.IconSmall;
            tbar = new OverlayPanel { Visible = false };
            tNow = new DarkButton("Guardado", small: true) { Tip = "Volver al instante del guardado" };
            tBack = new DarkButton(Theme.Glyph.Rewind, small: true) { IconFont = sym, Tip = "Más rápido hacia atrás (,)" };
            tPlay = new DarkButton(Theme.Glyph.Play, small: true) { IconFont = sym, Tip = "Continuar / pausa (espacio)" };
            tFwd = new DarkButton(Theme.Glyph.Forward, small: true) { IconFont = sym, Tip = "Más rápido hacia delante (.)" };
            tFollow = new DarkButton("Seguir", small: true) { Tip = "La cámara sigue a la nave seleccionada (F)", Visible = false };
            tWarp = new TextLabel("▶ ×1") { TextFont = Theme.Make(Theme.MonoFamily, 12), MinWidth = Theme.S(118), Center = true };
            tFecha = new TextLabel("") { TextFont = Theme.Mono, Dim = true };
            tNow.Click += (s, e) => VolverAlGuardado();
            tBack.Click += (s, e) => CambiarWarp(-1);
            tFwd.Click += (s, e) => CambiarWarp(+1);
            tPlay.Click += (s, e) => AlternarPausa();
            tFollow.Click += (s, e) => Seguir(!sim.Follow);
            tOrb = new DarkButton("Órbita", small: true) { Tip = "Datos de la órbita de la nave seleccionada (I)", Visible = false };
            tOrb.Click += (s, e) => ToggleOrbitInfo();
            tbar.Controls.AddRange(new Control[] { tNow, tBack, tPlay, tFwd, tWarp, tFecha, tFollow, tOrb });
            orbHud = new HudPanel { Visible = false };
        }

        /* ------------------------------------------------------------ orbital info */

        /* The selected vessel's orbit at the time on the time bar, in a panel under the HUD. It
           opens and closes with «Órbita» or the I key. */
        void RenderOrbitInfo()
        {
            if (orbHud == null) return;
            bool show = state.ShowOrbitInfo && HasVessels && sv.Sel?.Orbit != null;
            if (Vis.Shown(orbHud) != show) Vis.Set(orbHud, show);
            if (!show) return;

            var v = sv.Sel;
            var e = v.Orbit;
            var st = SaveFile.EstadoEn(e, sim.T);
            var p = SaveFile.PosicionEn(e, sim.T, sv.Ut, RotBase());
            double R = Body.Radius;
            double pe = e.Sma * (1 - e.Ecc) - R, ap = e.Sma * (1 + e.Ecc) - R;
            string name = v.Name.Length > 28 ? v.Name.Substring(0, 27) + "…" : v.Name;
            var rows = new List<(string, string)>
            {
                ("objeto", name),
                ("tipo", v.Type + " · " + Situacion(v.Sit)),
                ("altitud", Km(p.Alt)),
                ("velocidad", Geo.F(st.V, 1) + " m/s"),
                ("apoapsis", Km(ap)),
                ("periapsis", Km(pe)),
                ("tiempo a Ap", Geo.FmtTime(st.TAp)),
                ("tiempo a Pe", Geo.FmtTime(st.TPe)),
                ("periodo", Geo.FmtTime(st.Periodo)),
                ("semieje mayor", Km(e.Sma)),
                ("excentricidad", Geo.F(e.Ecc, 4)),
                ("inclinación", Geo.F(e.Inc, 2) + "°"),
                ("nodo asc. (LAN)", Geo.F(e.Lan, 2) + "°"),
                ("arg. periapsis", Geo.F(e.Lpe, 2) + "°"),
                ("anomalía verd.", Geo.F(st.Nu, 1) + "°"),
                ("posición", Geo.FmtLat(p.Lat) + "  " + Geo.FmtLon(p.Lon))
            };
            if (pe < 0) rows.Add(("aviso", "Pe bajo el suelo"));
            else if (pe < Body.Atmosphere) rows.Add(("aviso", "Pe dentro de la atmósfera"));
            if (ap > Body.Soi - R) rows.Add(("aviso", "sale de la SOI de " + Body.Name));
            orbHud.SetRows(rows.ToArray());
            PlaceOrbitInfo();
        }

        static string Km(double m) => Geo.F(m / 1000, Math.Abs(m) < 1e7 ? 1 : 0) + " km";

        static string Situacion(string sit) => sit switch
        {
            "ORBITING" => "en órbita",
            "SUB_ORBITAL" => "suborbital",
            "ESCAPING" => "escapando",
            "FLYING" => "en vuelo",
            "LANDED" => "posada",
            "SPLASHED" => "en el agua",
            "PRELAUNCH" => "en la rampa",
            "DOCKED" => "acoplada",
            _ => sit?.ToLowerInvariant() ?? "?"
        };

        void PlaceOrbitInfo()
        {
            if (orbHud == null || !Vis.Shown(orbHud)) return;
            orbHud.Location = new Point(mapArea.Width - Theme.S(12) - orbHud.Width, hud.Bottom + Theme.S(8));
        }

        void ToggleOrbitInfo()
        {
            if (sv.Sel == null) { Flash("Pincha una nave o un satélite para ver los datos de su órbita."); return; }
            state.ShowOrbitInfo = !state.ShowOrbitInfo;
            SaveSettings();
            RenderOrbitInfo();
            RenderReloj();
        }

        void LayoutTimeBar()
        {
            if (tbar == null) return;
            int gap = Theme.S(6), padX = Theme.S(10), padY = Theme.S(6);
            int bh = Theme.S(24);
            var items = new List<(Control c, int w)>
            {
                (tNow, tNow.PreferredWidth), (tBack, Math.Max(Theme.S(34), tBack.PreferredWidth)),
                (tPlay, Theme.S(34)), (tFwd, Math.Max(Theme.S(34), tFwd.PreferredWidth)),
                (tWarp, tWarp.PreferredWidth), (tFecha, tFecha.PreferredWidth)
            };
            if (Vis.Shown(tFollow)) items.Add((tFollow, tFollow.PreferredWidth));
            if (Vis.Shown(tOrb)) items.Add((tOrb, tOrb.PreferredWidth));
            int total = padX * 2 + items.Sum(i => i.w) + gap * (items.Count - 1);
            int maxW = mapArea.Width - Theme.S(32);
            if (total > maxW)
            {
                int i = items.FindIndex(x => x.c == tFecha);
                items[i] = (tFecha, Math.Max(Theme.S(60), items[i].w - (total - maxW)));
                total = maxW;
            }
            int x0 = padX;
            foreach (var (c, w) in items)
            {
                c.SetBounds(x0, padY, w, bh);
                x0 += w + gap;
            }
            tbar.SetBounds((mapArea.Width - total) / 2, mapArea.Height - Theme.S(16) - (bh + padY * 2), total, bh + padY * 2);
        }

        void CambiarWarp(int paso)
        {
            int i = Array.IndexOf(Escala, sim.Warp);
            if (i < 0) i = Array.IndexOf(Escala, 1.0);
            sim.Warp = Escala[Math.Clamp(i + paso, 0, Escala.Length - 1)];
            if (!sim.Running) sim.Last = 0;
            sim.Running = true;             // changing the speed starts it, as in the game
            RenderReloj();
            RequestRender();
        }

        void AlternarPausa()
        {
            sim.Running = !sim.Running;
            if (sim.Running) sim.Last = 0;
            sim.Dirty = true;
            RenderReloj();
            RequestRender();
        }

        void VolverAlGuardado()
        {
            sim.T = sv.Ut;
            sim.Dirty = true;
            RenderReloj();
            RequestRender();
        }

        void RenderReloj()
        {
            tPlay.Text = sim.Running ? Theme.Glyph.Pause : Theme.Glyph.Play;
            tWarp.Text = (sim.Warp < 0 ? "◀ ×" : "▶ ×") + Geo.FmtIntEs((long)Math.Abs(sim.Warp)) + (sim.Running ? "" : Lang.T(" · pausa"));
            double dt = sim.T - sv.Ut;
            tFecha.Text = Lang.F("{0}  ({1}{2} desde el guardado)", Geo.FechaKerbal(sim.T), dt < 0 ? "−" : "+", Geo.FmtTime(Math.Abs(dt)));
            if (Vis.Shown(tFollow) != (sv.Sel != null)) Vis.Set(tFollow, sv.Sel != null);
            tFollow.Active = sim.Follow;
            if (Vis.Shown(tOrb) != (sv.Sel != null)) Vis.Set(tOrb, sv.Sel != null);
            tOrb.Active = state.ShowOrbitInfo;
            LayoutTimeBar();
        }

        /* ------------------------------------------------------------ loop */

        /* If the window stops painting for a while (minimized, being dragged), it shouldn't
           jump hours at once when it comes back; but the cap can't be per frame, or with few
           frames per second ×1 would stop being real time. */
        void SimTick(double now)
        {
            // time runs even if this body has no vessels: the Sun keeps moving
            if (sv.Data == null) return;
            double dt = sim.Last > 0 ? Math.Min(2, now - sim.Last) : 0;
            sim.Last = now;
            if (sim.Running)
            {
                sim.T += dt * sim.Warp;
                if (sim.T < 0) { sim.T = 0; sim.Running = false; }
            }
            if (sim.Running || sim.Dirty) Fotograma(now);
        }

        /* The cheap stuff always runs: moving markers, rotating rings, following the vessel.
           The expensive stuff (rebuilding tracks, texts) a few times per second. */
        void Fotograma(double now)
        {
            double rot = RotBase(), R = Body.Radius;
            TrackPoint? posSel = null;
            foreach (var m in sv.Marcas)
            {
                var p = SaveFile.PosicionEn(m.V.Orbit, sim.T, sv.Ut, rot);
                m.P = p;
                // Kepler doesn't brake: an orbit that cuts the ground goes through it, so it's hidden
                m.Oculto = p.Alt < 0;
                m.Dot.Lat = p.Lat; m.Dot.Lon = p.Lon; m.Dot.Hidden = m.Oculto;
                m.Pin.Lat = p.Lat; m.Pin.Lon = p.Lon; m.Pin.R = (R + p.Alt) / R; m.Pin.Hidden = m.Oculto;
                if (m.V == sv.Sel) posSel = p;
            }
            globe.SetOrbitShift(360 * ((sim.T - sv.Ut) / Body.SiderealDay));

            if (sim.Follow && posSel.HasValue)
            {
                var p = posSel.Value;
                // with the focus on the vessel, the camera goes along with it rotating around it
                if (is3D) globe.FocusTarget = GlobeView.Sph(p.Lat, p.Lon, (R + p.Alt) / R);
                else if (!isSky) map.CenterOn(p.Lat, p.Lon);
            }

            if (sim.Dirty || now - sim.Lento >= 0.15)
            {
                sim.Lento = now;
                svList.Invalidate();
                DibujarTrazaNave();
                DibujarTodas();
                RenderReloj();
                RenderOrbitInfo();
                RefreshSkyHud();
            }
            sim.Dirty = false;
        }

        /* ------------------------------------------------------------ save */

        async Task PickSaveFile()
        {
            using var dlg = new OpenFileDialog { Filter = "Partidas de KSP (*.sfs)|*.sfs;*.loadmeta|Todos|*.*" };
            // the saves folder of whichever KSP installation is found (see FindGameData)
            string gd = FindGameData();
            string saves = gd == null ? null : Path.Combine(Path.GetDirectoryName(gd) ?? "", "saves");
            if (saves != null && Directory.Exists(saves)) dlg.InitialDirectory = saves;
            if (dlg.ShowDialog(this) == DialogResult.OK) await CargarSave(dlg.FileName);
        }

        /* The loaded save is copied to the viewer's local folder: when reopened it comes back
           on its own, even if the game overwrote it or the folder is gone. */
        void GuardarCopia(string original, string texto)
        {
            try
            {
                string copia = Store.SaveCopyPath;
                Directory.CreateDirectory(Path.GetDirectoryName(copia));
                File.WriteAllText(copia + ".tmp", texto);
                File.Move(copia + ".tmp", copia, true);
                state.SavePath = original;
                state.SimT = null;
                SaveSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[partida] no se pudo guardar la copia: " + ex.Message);
            }
        }

        /* restoring: the saved copy is read at startup, which isn't copied again and keeps the
           time on the time bar when it was closed. */
        async Task CargarSave(string path, bool restoring = false)
        {
            SaveData d;
            SaveExtras nuevosExtras = null;
            string texto;
            UseWaitCursor = true;
            try
            {
                texto = await File.ReadAllTextAsync(path);
                d = await Task.Run(() => SaveFile.Parse(texto));
                nuevosExtras = await Task.Run(() => SaveExtras.Parse(texto));
            }
            catch (IOException ex) { Flash("No se pudo leer el fichero: " + ex.Message); return; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                Flash("Ese fichero no parece un .sfs de KSP.");
                return;
            }
            finally { UseWaitCursor = false; }

            if (d.Ut == null || d.Ut == 0 || d.Vessels.Count == 0)
            {
                Flash("No encontré ni UT ni naves ahí dentro. ¿Es un persistent.sfs?");
                return;
            }

            sv.Data = d;
            sv.Ut = d.Ut.Value;
            sv.Nombre = restoring ? Lang.F("{0} · copia guardada", Path.GetFileName(state.SavePath ?? path)) : Path.GetFileName(path);
            sim.T = sv.Ut; sim.Warp = 1; sim.Running = false; sim.Follow = false; sim.Dirty = true; sim.Last = 0;
            if (restoring && state.SimT is double tCierre && tCierre >= 0) sim.T = tCierre;
            else if (!restoring) GuardarCopia(path, texto);
            // another save's vessels are other objects: the assembled models are no longer valid
            // (with the copy, KSP is looked for next to the original)
            loadedSavePath = restoring ? state.SavePath : path;
            vesselModels.Clear();
            navesCargando.Clear();
            ClearModel();
            SetModelStatus(null);

            AplicarExtras(nuevosExtras, proponerModo: !restoring);
            ActualizarEstadosSecciones();
            FiltrarNavesDelCuerpo();
            Vis.Set(tbar, true);
            RenderReloj();
            LayoutOverlays();
            RequestRender();
        }

        /* The save's vessels orbiting the body being viewed; the rest are counted but not
           drawn, because their latitude and longitude belong elsewhere. The body's rotation is
           measured with them; with none, the game's. */
        void FiltrarNavesDelCuerpo()
        {
            var d = sv.Data;
            if (d == null) return;
            var delCuerpo = d.Vessels.Where(v => SolarSystem.Find(v.BodyName) == Body.Current).ToList();
            var deAqui = delCuerpo.Where(v => v.Orbit != null).ToList();
            int otras = d.Vessels.Count - deAqui.Count;

            sv.Naves = deAqui;
            sv.Calib = SaveFile.CalibrarRotacion(delCuerpo, sv.Ut);
            sv.Rot = sv.Calib.N > 0 ? sv.Calib.Rot : Sun.DefaultRotation(sv.Ut);
            sv.Sel = null;
            sim.Follow = false;
            globe.ExitFocus();
            // how far the camera should be allowed to move away: the save's farthest orbit
            sv.MaxR = deAqui.Aggregate(1.2, (mx, v) => Math.Max(mx, v.Orbit.Sma * (1 + v.Orbit.Ecc) / Body.Radius));

            sv.Tipos.Clear();
            sv.Cuenta.Clear();
            foreach (var v in deAqui)
            {
                if (!sv.Tipos.ContainsKey(v.Type)) sv.Tipos[v.Type] = v.Type != "Debris";
                sv.Cuenta[v.Type] = sv.Cuenta.GetValueOrDefault(v.Type) + 1;
            }

            svRot.SetNumber(0);
            RenderSaveInfo(sv.Nombre, d.Vessels.Count, otras);
            svTipos.SetItems(sv.Tipos.Keys);
            RenderNaves();
        }

        void RenderSaveInfo(string nombre, int total, int otras)
        {
            var c = sv.Calib;
            double dias = sv.Ut / Body.SolarDay;
            var lineas = new List<string>
            {
                nombre,
                Lang.F("UT {0} s  (día {1})", Geo.F(sv.Ut, 0), Geo.FmtIntEs((long)Math.Floor(dias))),
                Lang.F("{0} naves en la partida · {1} orbitando {2}", total, sv.Naves.Count, Body.Name) +
                    (otras > 0 ? Lang.F(" · {0} en otros cuerpos", otras) : "")
            };
            if (c != null && c.N >= 3)
            {
                /* Median spread and not standard deviation: the standard one gets inflated by
                   the single odd vessel that slips in and gives a false idea of reliability. */
                lineas.Add(Lang.F("Rotación medida con {0} naves: {1}°  (dispersión mediana {2}°{3})",
                    c.N, Geo.F(c.Rot, 2), Geo.F(c.Mad, 2),
                    c.Descartadas > 0 ? Lang.F(", {0} descartadas por incoherentes", c.Descartadas) : ""));
            }
            else if (c != null && c.N > 0)
                lineas.Add(Lang.F("Rotación medida con solo {0} nave(s): poco fiable, ajústala a mano si las trazas no cuadran.", c.N));
            else
                lineas.Add(Lang.T("Ninguna nave con posición y época sincronizadas: no se pudo medir la rotación. Las longitudes serán arbitrarias hasta que la ajustes."));
            svInfo.SetText(RichLabel.Esc(string.Join("\n", lineas)));
        }

        void DrawTipoRow(Graphics g, Rectangle r, object item, bool hover)
        {
            string t = (string)item;
            bool on = sv.Tipos.GetValueOrDefault(t);
            int box = Theme.S(16), y = r.Y + (r.Height - box) / 2, x = r.X + Theme.S(2);
            var br = new RectangleF(x, y, box, box);
            // the same checkbox as DarkCheck
            if (on)
            {
                Theme.FillRound(g, Theme.Accent, Theme.Accent, br, Theme.Sf(3));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pen = new Pen(Theme.OnAccent, Theme.Sf(1.6)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                g.DrawLines(pen, new[] { new PointF(x + box * 0.27f, y + box * 0.52f), new PointF(x + box * 0.44f, y + box * 0.68f), new PointF(x + box * 0.74f, y + box * 0.34f) });
            }
            else Theme.FillRound(g, Theme.Well, hover ? Theme.Mix(Theme.Current.LineStrong, Theme.Fg, 0.35f) : Theme.Current.LineStrong, br, Theme.Sf(3));
            x += box + Theme.S(8);
            DrawDot(g, Theme.Hex(ColorDe(t)), x, r.Y + r.Height / 2, Theme.S(8));
            x += Theme.S(14);
            TextRenderer.DrawText(g, t + " (" + sv.Cuenta.GetValueOrDefault(t) + ")", Theme.UISmall, new Rectangle(x, r.Y, r.Right - x, r.Height), Theme.Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        void TipoClick(string t)
        {
            sv.Tipos[t] = !sv.Tipos.GetValueOrDefault(t);
            svTipos.Invalidate();
            RenderNaves();
        }

        void RenderNaves()
        {
            vesselLayer.Clear();
            double rot = RotBase(), R = Body.Radius;
            sv.Marcas = sv.Naves.Where(v => sv.Tipos.GetValueOrDefault(v.Type)).Select(v =>
            {
                var p = SaveFile.PosicionEn(v.Orbit, sim.T, sv.Ut, rot);
                bool oculto = p.Alt < 0;
                var col = ColorF.Hex(ColorDe(v.Type));
                var m = new Marca
                {
                    V = v, P = p, Oculto = oculto,
                    Dot = new MapDot { Lat = p.Lat, Lon = p.Lon, Style = DotStyle.Vessel, Fill = col, Tooltip = v.Name, Tag = v, Hidden = oculto },
                    Pin = new GlobePin { Lat = p.Lat, Lon = p.Lon, R = (R + p.Alt) / R, Name = v.Name, Color = col, Vessel = true, Hidden = oculto, Tag = v }
                };
                vesselLayer.Dots.Add(m.Dot);
                return m;
            }).ToList();
            sv.PorNave = sv.Marcas.ToDictionary(m => m.V);

            svList.SetItems(sv.Marcas.OrderBy(m => m.V.Name, StringComparer.CurrentCulture).Take(250).Select(m => (object)m.V));

            DibujarTrazaNave();
            DibujarTodas();
            ConstruirAnillos();
            ColocarNavesEnSuelo();
            SyncGlobe();
            RequestRender();
        }

        /* ------------------------------------------------------------ vessels on the ground */

        readonly HashSet<Vessel> navesCargando = new();

        /* The landed or splashed-down vessels of the viewed body, with their parts, to paint
           them in flight and sky views like the Kerbal Konstructs buildings (see
           GlobeView.Statics.cs). Only those of the types checked in the list. */
        void ColocarNavesEnSuelo()
        {
            globe.GroundVessels.Clear();
            if (sv.Data == null || !state.ShowVesselModels) { RequestRender(); return; }
            double R = Body.Radius;
            /* From the whole save, not from sv.Naves: only those with an orbit go in there, and
               landed ones get theirs (degenerate radial) discarded when read. */
            foreach (var v in sv.Data.Vessels)
            {
                if (v.Sit != "LANDED" && v.Sit != "SPLASHED" && v.Sit != "PRELAUNCH") continue;
                if (v.Lat == null || v.Lon == null || SolarSystem.Find(v.BodyName) != Body.Current) continue;
                // the types switched off in the list; one that isn't in it (because no vessel
                // of that type orbits) is shown
                if (sv.Tipos.TryGetValue(v.Type, out bool tipoOn) && !tipoOn) continue;

                /* The altitude: splashed down, at sea level; on a known pad or runway (its own
                   «landedAt», or prelaunch, which always is), the game's, which is exact there;
                   landed anywhere else, this map's own terrain plus the height above the ground
                   the save stored, so it rests on the ground that's really shown here and not
                   on the one the game had. */
                // splashed down, the game's: the sea is at the same height here as there, and with
                // 0 the center of mass ended up underwater. An «hgt» of −1 means the game
                // didn't know it: then its altitude is used.
                double alt;
                if (v.Sit == "SPLASHED") alt = v.Alt ?? 0;
                else if (v.Sit == "PRELAUNCH" || !string.IsNullOrEmpty(v.LandedAt) || v.Hgt is not double hg || hg < 0) alt = v.Alt ?? 0;
                else alt = AlturaDelSuelo(v.Lat.Value, v.Lon.Value) + hg;

                var n = KkDatabase.NVec(v.Lat.Value, v.Lon.Value);
                var pos = new[] { n[0] * (R + alt), n[1] * (R + alt), n[2] * (R + alt) };
                var m = Mat.Mul(Mat.Translate(pos),
                    Mat.Mul(Mat.Rotate(v.Rot), Mat.Translate(new[] { -v.CoM[0], -v.CoM[1], -v.CoM[2] })));
                globe.GroundVessels.Add((m, ModeloNave(v), v));
            }
            RequestRender();
        }

        /* The assembled model of a landed vessel: like ModeloKK, requested in the background
           the first time (null until then) and shared with the focused vessel's. */
        AssembledVessel ModeloNave(Vessel v)
        {
            if (vesselModels.TryGetValue(v, out var a)) return a;
            string gd = FindGameData();
            if (gd == null || !navesCargando.Add(v)) return null;
            Task.Run(() =>
            {
                try
                {
                    lock (vesselModels)
                    {
                        if (kspCatalog == null || kspCatalogDir != gd)
                        {
                            kspCatalog = PartCatalog.Load(gd);
                            kspAssembler = new VesselAssembler(kspCatalog);
                            kspCatalogDir = gd;
                        }
                    }
                    var r = kspAssembler.Build(v);
                    r.LoadTextures();
                    return r;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[naves] " + v.Name + ": " + ex.Message);
                    return new AssembledVessel();
                }
            }).ContinueWith(t =>
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (!navesCargando.Remove(v)) return;   // it was discarded in the meantime
                        vesselModels[v] = t.Result;
                        ColocarNavesEnSuelo();
                    }));
                }
                catch (InvalidOperationException) { }
            });
            return null;
        }

        void DrawVesselRow(Graphics g, Rectangle r, object item, bool hover)
        {
            var v = (Vessel)item;
            int x = r.X + Theme.S(7);
            DrawDot(g, Theme.Hex(ColorDe(v.Type)), x, r.Y + r.Height / 2, Theme.S(8));
            x += Theme.S(15);
            string alt = sv.PorNave.TryGetValue(v, out var m) ? Geo.F(m.P.Alt / 1000, 0) + " km" : "";
            int aw = TextRenderer.MeasureText(alt, Theme.MonoSmall, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(4);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, v.Name, Theme.UISmall, new Rectangle(x, r.Y, r.Right - x - aw - Theme.S(10), r.Height), Theme.Fg, flags | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, alt, Theme.MonoSmall, new Rectangle(r.Right - aw - Theme.S(7), r.Y, aw, r.Height), Theme.FgDim, flags | TextFormatFlags.Right);
        }

        /* Clicking a vessel selects it and follows it right away; clicking it again releases
           it. */
        void SeleccionarNave(Vessel v)
        {
            bool nueva = sv.Sel != v;
            if (!nueva) Seguir(false);
            sv.Sel = nueva ? v : null;
            RenderNaves();
            if (sv.Sel != null)
            {
                if (!is3D && !isSky)
                {
                    var p = SaveFile.PosicionEn(v.Orbit, sim.T, sv.Ut, RotBase());
                    map.SetView(p.Lat, p.Lon, Math.Max(map.Zoom, 3));
                }
                svList.EnsureVisible(v);
                Seguir(true);
            }
            svList.Invalidate();
            if (HasVessels) RenderReloj();
            RenderOrbitInfo();
            RenderLandingInfo();
            RequestRender();
        }

        /* Position of the selected vessel in the globe's frame, in Kerbin radii. */
        double[] SelPos()
        {
            var p = SaveFile.PosicionEn(sv.Sel.Orbit, sim.T, sv.Ut, RotBase());
            return GlobeView.Sph(p.Lat, p.Lon, (Body.Radius + p.Alt) / Body.Radius);
        }

        /* Follow the vessel. On the globe the camera focus moves from Kerbin's center to the
           vessel: dragging rotates around it and the wheel moves toward or away from it. On the
           flat map the map recenters on the vessel and dragging cancels it. */
        void Seguir(bool on)
        {
            sim.Follow = on && sv.Sel != null;
            if (is3D)
            {
                if (sim.Follow) globe.EnterFocus(SelPos());
                else globe.ExitFocus();
            }
            if (sim.Follow) RequestModel(sv.Sel);
            else ClearModel();
            sim.Dirty = true;
            if (HasVessels) RenderReloj();
            RequestRender();
        }

        void SetSvAll(bool on)
        {
            sv.Todas = on;
            DibujarTodas();
            ConstruirAnillos();
            RequestRender();
        }

        void OnSvOrbitsChanged()
        {
            DibujarTrazaNave();
            DibujarTodas();
            RequestRender();
        }

        void OnSvRotCommitted()
        {
            if (HasVessels) RenderNaves();
        }

        /* ------------------------------------------------------------ tracks */

        /* The globe has a single track slot and two things want it: the orbit drawn by hand and
           the clicked vessel's. The last one requested wins. */
        void PushTrack()
        {
            if (!glOk || !surface.MakeCurrent()) return;
            // the vessel already has its ring: of its track only the ground footprint is painted
            if (sv.TrackPts != null) globe.SetTrack(sv.TrackPts, space: false);
            else globe.SetTrack(lastTrack);
            RequestRender();
        }

        /* In 2D, «all orbits» are ground tracks from the simulated time. */
        void DibujarTodas()
        {
            allLayer.Lines.Clear();
            if (!sv.Todas || is3D || isSky) return;
            double rot = RotBase();
            int n = Math.Min(3, svOrbits.Value);
            foreach (var m in sv.Marcas)
            {
                if (m.V == sv.Sel || m.Oculto) continue;
                var t = SaveFile.TrazaDesde(m.V.Orbit, sim.T, sv.Ut, rot, n, 96);
                allLayer.Lines.Add(MapLine.FromTrack(t, ColorF.Hex(ColorDe(m.V.Type), 0.45f), 1));
            }
        }

        void DibujarTrazaNave()
        {
            trackLayer.Clear();
            sv.TrackPts = null;
            if (sv.Sel == null) { PushTrack(); return; }

            double rot = RotBase();
            var t = SaveFile.TrazaDesde(sv.Sel.Orbit, sim.T, sv.Ut, rot, svOrbits.Value);
            trackLayer.Lines.Add(MapLine.FromTrack(t, ColorF.Hex(ColorDe(sv.Sel.Type), 0.9f), 2));
            sv.TrackPts = t;
            // clicking a vessel replaces the manual orbit
            lastTrack = null;
            orbitLayer.Clear();
            PushTrack();

            var e = sv.Sel.Orbit;
            double pe = e.Sma * (1 - e.Ecc) - Body.Radius, ap = e.Sma * (1 + e.Ecc) - Body.Radius;
            orbOut.SetText(string.Join("\n",
                RichLabel.Esc(sv.Sel.Name),
                "Pe / Ap      <b>" + Geo.F(pe / 1000, 0) + " / " + Geo.F(ap / 1000, 0) + " km</b>",
                "Inclinación  <b>" + Geo.F(e.Inc, 2) + "°</b>",
                "Excentricid. <b>" + Geo.F(e.Ecc, 4) + "</b>",
                "Periodo      <b>" + Geo.FmtTime(SaveFile.Periodo(e)) + "</b>"));
        }

        /* In 3D orbits are closed rings: all of them if requested, and always the selected
           vessel's, more opaque and drawn last so it stays on top. */
        void ConstruirAnillos()
        {
            if (!glOk || !surface.MakeCurrent()) return;
            double rot = RotBase();
            var lista = new List<(OrbitRing ring, bool sel)>();
            foreach (var m in sv.Marcas)
            {
                bool sel = m.V == sv.Sel;
                if (!sv.Todas && !sel) continue;
                lista.Add((new OrbitRing { Points = SaveFile.Anillo(m.V.Orbit, rot, 180), Color = ColorF.Hex(ColorDe(m.V.Type)), Alpha = sel ? 0.95f : 0.4f }, sel));
            }
            globe.SetOrbits(lista.OrderBy(x => x.sel).Select(x => x.ring).ToList());
            globe.SetOrbitShift(360 * ((sim.T - sv.Ut) / Body.SiderealDay));
            globe.SetScene(sv.MaxR);
            RequestRender();
        }
    }
}
