using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Naves de una partida: lectura del .sfs, reloj de simulación con aceleración
       de tiempo, trazas, anillos en el globo y seguir a una nave. */
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
            public SaveData Data;                 // la partida entera: al cambiar de cuerpo se vuelven a filtrar las naves
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
        List<TrackPoint> lastTrack;           // la órbita dibujada a mano en su panel

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

        /* Los mismos escalones que la aceleración de tiempo de KSP, en los dos sentidos:
           un único escalafón con signo permite frenar hacia atrás paso a paso. */
        static readonly double[] Warps = { 1, 5, 10, 50, 100, 1000, 10000, 100000 };
        static readonly double[] Escala = Warps.Reverse().Select(w => -w).Concat(Warps).ToArray();

        bool HasVessels => sv.Naves.Count > 0;
        bool Following => sim.Follow;
        bool SimWantsFrames => HasVessels && (sim.Running || sim.Dirty);
        void SimResetClock() => sim.Last = 0;
        void SimDirty() { sim.Dirty = true; RequestRender(); }
        double RotBase() => sv.Rot + svRot.NumberOr(0);

        /* Dónde está el Sol en el instante de la barra de tiempo, con la misma rotación que
           mueve las naves. Sin naves en este cuerpo con las que medirla, la del juego. */
        Sun.Position SunNow()
        {
            if (sv.Data == null) return Sun.Subsolar(0, Sun.DefaultRotation(0));
            return Sun.Subsolar(sim.T, RotBase() + 360 * ((sim.T - sv.Ut) / Body.SiderealDay));
        }
        IEnumerable<GlobePin> VesselPins() => sv.Marcas.Select(m => m.Pin);

        /* ------------------------------------------------------------ barra de tiempo */

        void BuildTimeBar()
        {
            var sym = Theme.Make("Segoe UI Symbol", 11.5f);
            tbar = new OverlayPanel { Visible = false };
            tNow = new DarkButton("Guardado", small: true) { Tip = "Volver al instante del guardado" };
            tBack = new DarkButton("◀◀", small: true) { IconFont = sym, Tip = "Más rápido hacia atrás (,)" };
            tPlay = new DarkButton("▶", small: true) { IconFont = sym, Tip = "Continuar / pausa (espacio)" };
            tFwd = new DarkButton("▶▶", small: true) { IconFont = sym, Tip = "Más rápido hacia delante (.)" };
            tFollow = new DarkButton("Seguir", small: true) { Tip = "La cámara sigue a la nave seleccionada (F)", Visible = false };
            tWarp = new TextLabel("▶ ×1") { TextFont = Theme.Make(Theme.MonoFamily, 12), MinWidth = Theme.S(118), Center = true };
            tFecha = new TextLabel("") { TextFont = Theme.Mono, TextColor = Theme.FgDim };
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

        /* ------------------------------------------------------------ información orbital */

        /* La órbita de la nave seleccionada en el instante de la barra de tiempo, en un
           panel bajo el HUD. Se abre y se cierra con «Órbita» o con la tecla I. */
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
            sim.Running = true;             // cambiar la velocidad arranca, como en el juego
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
            tPlay.Text = sim.Running ? "❚❚" : "▶";
            tWarp.Text = (sim.Warp < 0 ? "◀ ×" : "▶ ×") + Geo.FmtIntEs((long)Math.Abs(sim.Warp)) + (sim.Running ? "" : Lang.T(" · pausa"));
            double dt = sim.T - sv.Ut;
            tFecha.Text = Lang.F("{0}  ({1}{2} desde el guardado)", Geo.FechaKerbal(sim.T), dt < 0 ? "−" : "+", Geo.FmtTime(Math.Abs(dt)));
            if (Vis.Shown(tFollow) != (sv.Sel != null)) Vis.Set(tFollow, sv.Sel != null);
            tFollow.Active = sim.Follow;
            if (Vis.Shown(tOrb) != (sv.Sel != null)) Vis.Set(tOrb, sv.Sel != null);
            tOrb.Active = state.ShowOrbitInfo;
            LayoutTimeBar();
        }

        /* ------------------------------------------------------------ bucle */

        /* Si la ventana deja de pintar un rato (minimizada, arrastrando), al volver no
           debe saltar horas de golpe; pero el tope no puede ser por fotograma, o con
           pocos fotogramas por segundo el ×1 dejaría de ser tiempo real. */
        void SimTick(double now)
        {
            // el tiempo corre aunque en este cuerpo no haya naves: el Sol se sigue moviendo
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

        /* Lo barato va siempre: mover marcadores, girar anillos, seguir a la nave. Lo
           caro (rehacer trazas, textos) unas pocas veces por segundo. */
        void Fotograma(double now)
        {
            double rot = RotBase(), R = Body.Radius;
            TrackPoint? posSel = null;
            foreach (var m in sv.Marcas)
            {
                var p = SaveFile.PosicionEn(m.V.Orbit, sim.T, sv.Ut, rot);
                m.P = p;
                // Kepler no frena: una órbita que corta el suelo lo atraviesa, así que se oculta
                m.Oculto = p.Alt < 0;
                m.Dot.Lat = p.Lat; m.Dot.Lon = p.Lon; m.Dot.Hidden = m.Oculto;
                m.Pin.Lat = p.Lat; m.Pin.Lon = p.Lon; m.Pin.R = (R + p.Alt) / R; m.Pin.Hidden = m.Oculto;
                if (m.V == sv.Sel) posSel = p;
            }
            globe.SetOrbitShift(360 * ((sim.T - sv.Ut) / Body.SiderealDay));

            if (sim.Follow && posSel.HasValue)
            {
                var p = posSel.Value;
                // con el foco en la nave, la cámara la acompaña girando a su alrededor
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

        /* ------------------------------------------------------------ partida */

        async Task PickSaveFile()
        {
            using var dlg = new OpenFileDialog { Filter = "Partidas de KSP (*.sfs)|*.sfs;*.loadmeta|Todos|*.*" };
            string saves = @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\saves";
            if (Directory.Exists(saves)) dlg.InitialDirectory = saves;
            if (dlg.ShowDialog(this) == DialogResult.OK) await CargarSave(dlg.FileName);
        }

        /* La partida cargada se copia a la carpeta local del visor: al volver a abrirlo se
           recupera sola, aunque el juego la haya sobrescrito o la carpeta ya no esté. */
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

        /* restoring: se lee la copia guardada al arrancar, que no se vuelve a copiar y
           conserva el instante de la barra de tiempo en que se cerró. */
        async Task CargarSave(string path, bool restoring = false)
        {
            SaveData d;
            string texto;
            UseWaitCursor = true;
            try
            {
                texto = await File.ReadAllTextAsync(path);
                d = await Task.Run(() => SaveFile.Parse(texto));
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
            // las naves de otra partida son otros objetos: los modelos montados ya no valen
            // (con la copia, KSP se busca junto al original)
            loadedSavePath = restoring ? state.SavePath : path;
            vesselModels.Clear();
            ClearModel();
            SetModelStatus(null);

            FiltrarNavesDelCuerpo();
            Vis.Set(tbar, true);
            RenderReloj();
            LayoutOverlays();
            RequestRender();
        }

        /* Las naves de la partida que orbitan el cuerpo que se está viendo; las demás se
           cuentan pero no se dibujan, porque su latitud y longitud son de otro sitio. La
           rotación del cuerpo se mide con ellas; sin ninguna, la del juego. */
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
            // hasta dónde debe dejar alejarse la cámara: la órbita más lejana de la partida
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
                /* Dispersión mediana y no desviación típica: la típica se infla con la
                   única nave rara que se cuele y da una idea falsa de la fiabilidad. */
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
            int box = Theme.S(15), y = r.Y + (r.Height - box) / 2, x = r.X + Theme.S(2);
            var br = new RectangleF(x + 0.5f, y + 0.5f, box - 1, box - 1);
            if (on)
            {
                Theme.FillRound(g, Theme.Accent, Theme.Accent, br, Theme.Sf(3));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pen = new Pen(Color.White, Theme.Sf(2)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                g.DrawLines(pen, new[] { new PointF(x + box * 0.25f, y + box * 0.52f), new PointF(x + box * 0.43f, y + box * 0.7f), new PointF(x + box * 0.76f, y + box * 0.32f) });
            }
            else Theme.FillRound(g, Theme.Bg, hover ? Theme.Accent : Theme.FgDim, br, Theme.Sf(3));
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
            SyncGlobe();
            RequestRender();
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

        /* Pinchar una nave la selecciona y la sigue desde ya; pincharla otra vez la
           suelta. */
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
            RequestRender();
        }

        /* Posición de la nave seleccionada en el marco del globo, en radios de Kerbin. */
        double[] SelPos()
        {
            var p = SaveFile.PosicionEn(sv.Sel.Orbit, sim.T, sv.Ut, RotBase());
            return GlobeView.Sph(p.Lat, p.Lon, (Body.Radius + p.Alt) / Body.Radius);
        }

        /* Seguir a la nave. En el globo el foco de la cámara pasa del centro de Kerbin a
           la nave: arrastrar gira alrededor de ella y la rueda se acerca o se aleja de
           ella. En el mapa plano el mapa se recentra sobre la nave y arrastrar lo
           cancela. */
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

        /* ------------------------------------------------------------ trazas */

        /* El globo tiene un único hueco de traza y lo quieren dos: la órbita dibujada a
           mano y la de la nave pinchada. Manda la última que se pidió. */
        void PushTrack()
        {
            if (!glOk || !surface.MakeCurrent()) return;
            // la nave ya tiene su anillo: de su traza solo se pinta la huella en el suelo
            if (sv.TrackPts != null) globe.SetTrack(sv.TrackPts, space: false);
            else globe.SetTrack(lastTrack);
            RequestRender();
        }

        /* En 2D, «todas las órbitas» son trazas terrestres desde el instante simulado. */
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
            // pinchar una nave sustituye a la órbita manual
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

        /* En 3D las órbitas son anillos cerrados: todas si se ha pedido, y siempre la de
           la nave seleccionada, más opaca y dibujada la última para que quede encima. */
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
