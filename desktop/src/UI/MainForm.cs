using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Ventana principal: el panel lateral a la izquierda y la vista (mapa, globo o
       cielo) con sus controles flotantes a la derecha. La lógica va repartida en
       partes: Sidebar, Maps, Vessels, Tools y Sky, igual que estaba en app.js. */
    public sealed partial class MainForm : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        struct NativeMsg { public IntPtr hwnd; public uint msg; public IntPtr w, l; public uint time; public int x, y; }

        [DllImport("user32.dll")] static extern bool PeekMessage(out NativeMsg msg, IntPtr hWnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        readonly string[] startArgs;
        readonly AppState state;

        GlSurface surface;
        Batch2D batch;
        TextCache text;
        readonly MapView map = new();
        readonly GlobeView globe = new();
        bool glOk, is3D, isSky;
        string glError;

        readonly MapLayer allLayer = new(), orbitLayer = new(), trackLayer = new(), toolLayer = new(), vesselLayer = new(), markerLayer = new();

        Panel mapArea;
        ScrollHost sideScroll;
        SidebarPanel sidebar;
        StackPanel sideStack;
        DarkButton sbToggle, btn2D, btn3D, btnSky;
        DarkTextBox search;
        OverlayPanel searchBox;
        DrawList searchList;
        HudPanel hud;
        OverlayPanel banner;
        RichLabel bannerText;
        DarkButton bannerClose;
        readonly Timer flashTimer = new() { Interval = 7000 };
        readonly Timer saveViewTimer = new() { Interval = 400 };
        MapPopup popup;

        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastFrame;
        bool needsFrame;

        bool mouseDown, moved;
        Point downPt, lastPt;
        double wheelAcc;

        string CurrentView => isSky ? "sky" : is3D ? "3d" : "2d";
        bool GlobeVisible => is3D || isSky;

        public MainForm(string[] args)
        {
            startArgs = args ?? Array.Empty<string>();
            state = Store.Load("settings.json", new AppState());
            state.LonOffset ??= new LonOffsets();
            // el idioma se resuelve antes de montar la interfaz: los textos se traducen al crearse
            Lang.Use(Lang.Detect(state.Lang), Store.DataDir);

            Theme.Init(DeviceDpi);
            AutoScaleMode = AutoScaleMode.None;
            Text = "Koogle Kerbin";
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = Theme.UI;
            KeyPreview = true;
            MinimumSize = new Size(Theme.S(900), Theme.S(600));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            StartPosition = FormStartPosition.Manual;
            var area = Screen.PrimaryScreen.WorkingArea;
            var bounds = new Rectangle(state.WinX, state.WinY, state.WinW, state.WinH);
            if (state.WinX == int.MinValue || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
                bounds = new Rectangle(area.X + (area.Width - Math.Min(state.WinW, area.Width)) / 2,
                                       area.Y + (area.Height - Math.Min(state.WinH, area.Height)) / 2,
                                       Math.Min(state.WinW, area.Width), Math.Min(state.WinH, area.Height));
            Bounds = bounds;
            if (state.WinMax) WindowState = FormWindowState.Maximized;

            BuildLayout();
            BuildSidebar();

            flashTimer.Tick += (s, e) => { flashTimer.Stop(); Vis.Set(banner, false); UpdateBanner(); };
            saveViewTimer.Tick += (s, e) => { saveViewTimer.Stop(); SaveView(); };
            Application.Idle += OnIdle;
            Shown += async (s, e) => await StartupAsync();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1;
            try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { }   // barra de título oscura
        }

        /* ------------------------------------------------------------ maqueta */

        void BuildLayout()
        {
            mapArea = new Panel { Dock = DockStyle.Fill, BackColor = Theme.MapBg };
            sidebar = new SidebarPanel { Dock = DockStyle.Left, Width = Theme.S(320), BackColor = Theme.Bg2 };
            sideStack = new StackPanel(0) { BackColor = Theme.Bg2, Padding = new Padding(0, 0, 0, Theme.S(32)) };
            sideScroll = new ScrollHost(sideStack) { Dock = DockStyle.Fill, BackColor = Theme.Bg2 };
            sidebar.Controls.Add(sideScroll);
            sidebar.Controls.Add(new SidebarHeader { Dock = DockStyle.Top });
            Controls.Add(mapArea);
            Controls.Add(sidebar);
            Vis.Set(sidebar, !state.SidebarHidden);

            surface = new GlSurface { Dock = DockStyle.Fill, AllowDrop = true };
            surface.ContextCreated += InitGl;
            surface.RenderFrame += Frame;
            surface.MouseDown += SurfaceMouseDown;
            surface.MouseMove += SurfaceMouseMove;
            surface.MouseUp += SurfaceMouseUp;
            surface.MouseDoubleClick += SurfaceDoubleClick;
            surface.MouseWheel += SurfaceWheel;
            surface.MouseLeave += (s, e) =>
            {
                if (mouseDown) return;
                map.Hover = null;
                // en el cielo el HUD es el del cielo, no el de la posición bajo el cursor
                if (isSky) RefreshSkyHud(); else UpdateHud(null);
                RequestRender();
            };
            surface.Resize += (s, e) => RequestRender();
            surface.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            surface.DragDrop += (s, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f) _ = OpenFiles(f); };
            surface.KeyDown += SurfaceKeyDown;
            mapArea.Controls.Add(surface);

            map.Layers.AddRange(new[] { allLayer, orbitLayer, trackLayer, toolLayer, observerLayer, vesselLayer, markerLayer });
            map.SetView(state.CenterLat, state.CenterLon, state.Zoom);

            // barra superior
            sbToggle = new DarkButton("☰") { IconFont = Theme.Icon, Tip = "Ocultar panel" };
            sbToggle.Click += (s, e) => ToggleSidebar();
            btn2D = new DarkButton("2D") { Tip = "Mapa plano" };
            btn3D = new DarkButton("3D") { Tip = "Globo" };
            btnSky = new DarkButton("Cielo") { Tip = "El cielo visto desde un punto de la superficie" };
            btn2D.Click += (s, e) => SetViewMode("2d");
            btn3D.Click += (s, e) => SetViewMode("3d");
            btnSky.Click += (s, e) => SetViewMode("sky");
            btn2D.Active = true;
            search = new DarkTextBox { Placeholder = "Buscar: KSC, o -0.097, -74.557" };
            search.Edited += (s, e) => DoSearch(search.Text);
            search.Inner.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; PickFirstSearch(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Vis.Set(searchBox, false); surface.Focus(); }
            };
            searchBox = new OverlayPanel { Visible = false };
            searchList = new DrawList(280) { RowHeight = Theme.S(44), RowGap = 0, BackColor = Theme.Bg2 };
            searchBox.Controls.Add(searchList);

            hud = new HudPanel();
            banner = new OverlayPanel { BorderColor = Theme.Warn, Visible = false };
            bannerText = new RichLabel(RichMode.Banner) { HideWhenEmpty = false, BackColor = Theme.Bg2 };
            bannerClose = new DarkButton("Ocultar", ButtonVariant.Ghost, small: true);
            bannerClose.Click += (s, e) =>
            {
                if (!flashTimer.Enabled) { state.BannerDismissed = true; SaveSettings(); }
                flashTimer.Stop();
                Vis.Set(banner, false);
            };
            banner.Controls.Add(bannerText);
            banner.Controls.Add(bannerClose);

            popup = new MapPopup();

            BuildTimeBar();

            foreach (Control c in new Control[] { sbToggle, btn2D, btn3D, btnSky, search, searchBox, hud, orbHud, banner, tbar, popup })
            {
                mapArea.Controls.Add(c);
                c.BringToFront();
            }
            mapArea.Resize += (s, e) => LayoutOverlays();
            UpdateHud(null);
            LayoutOverlays();
        }

        void LayoutOverlays()
        {
            int m = Theme.S(12), h = Theme.S(34), gap = Theme.S(8), seg = Theme.S(3);
            sbToggle.SetBounds(m, m, h, h);
            int x = sbToggle.Right + gap;
            foreach (var b in new[] { btn2D, btn3D, btnSky })
            {
                int w = Math.Max(Theme.S(44), b.PreferredWidth + Theme.S(4));
                b.SetBounds(x, m, w, h);
                x += w + seg;
            }
            int sw = Math.Min(Theme.S(360), (int)(mapArea.Width * 0.5));
            search.SetBounds(x - seg + gap, m, sw, h);
            searchBox.SetBounds(search.Left, search.Bottom + Theme.S(4), sw, searchList.HeightFor(sw) + 2);
            searchList.SetBounds(1, 1, sw - 2, searchBox.Height - 2);
            hud.Location = new Point(mapArea.Width - m - hud.Width, Theme.S(56));
            PlaceOrbitInfo();

            LayoutTimeBar();
            int maxW = Math.Min(Theme.S(620), mapArea.Width - Theme.S(32));
            int btnW = bannerClose.PreferredWidth;
            int textW = Math.Min(bannerText.NaturalWidth() + Theme.S(4), maxW - btnW - Theme.S(40));
            int th = bannerText.HeightFor(textW);
            int bh = Math.Max(th, bannerClose.PreferredHeight) + Theme.S(20);
            int bw = textW + btnW + Theme.S(40);
            banner.SetBounds((mapArea.Width - bw) / 2, mapArea.Height - Theme.S(70) - bh, bw, bh);
            bannerText.SetBounds(Theme.S(14), (bh - th) / 2, textW, th);
            bannerClose.SetBounds(bw - Theme.S(14) - btnW, (bh - bannerClose.PreferredHeight) / 2, btnW, bannerClose.PreferredHeight);
            PlacePopup();
        }

        void ToggleSidebar()
        {
            state.SidebarHidden = !state.SidebarHidden;
            Vis.Set(sidebar, !state.SidebarHidden);
            SaveSettings();
            RequestRender();
        }

        /* ------------------------------------------------------------ bucle */

        void InitGl()
        {
            try
            {
                batch = new Batch2D();
                text = new TextCache();
                globe.Init();
                glOk = true;
            }
            catch (Exception ex)
            {
                glError = ex.Message;
                Debug.WriteLine("[gl] " + ex);
            }
        }

        public void RequestRender()
        {
            needsFrame = true;
            if (surface != null && surface.IsHandleCreated) surface.Invalidate();
        }

        static bool AppIdle => !PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

        bool WantsFrames => needsFrame || map.Animating || globe.Animating || SimWantsFrames;

        void OnIdle(object sender, EventArgs e)
        {
            while (glOk && WantsFrames && AppIdle && WindowState != FormWindowState.Minimized && !IsDisposed)
                Frame();
        }

        /* Un fotograma: avanza la simulación, anima el zoom y pinta. Con la
           sincronía vertical activa, SwapBuffers marca el ritmo de la pantalla. */
        void Frame()
        {
            needsFrame = false;
            if (!glOk || !surface.MakeCurrent()) return;
            double now = clock.Elapsed.TotalSeconds;
            double dt = lastFrame > 0 ? Math.Min(0.1, now - lastFrame) : 0;
            lastFrame = now;

            SimTick(now);
            globe.SunLon = map.SunLon = SunLonNow();
            if (map.Animating) { map.Animate(dt); saveViewTimer.Stop(); saveViewTimer.Start(); }

            int w = Math.Max(1, surface.ClientSize.Width), h = Math.Max(1, surface.ClientSize.Height);
            if (GlobeVisible)
            {
                globe.W = w; globe.H = h; globe.S = Theme.Scale;
                globe.Render(batch, text);
            }
            else
            {
                map.Resize(w, h, Theme.Scale);
                map.Render(batch, text);
            }
            surface.Swap();
            PlacePopup();
        }

        void SaveView()
        {
            if (isSky) { state.SkyAz = globe.SkyAz; state.SkyEl = globe.SkyEl; state.SkyFov = globe.SkyFov; }
            else if (is3D) { var c = globe.Center(); state.CenterLat = c.Lat; state.CenterLon = c.Lon; }
            else { state.CenterLat = map.CenterLat; state.CenterLon = Geo.WrapLon(map.CenterLon); state.Zoom = map.TargetZoom; }
            SaveSettings();
        }

        void SaveSettings() => Store.Save("settings.json", state);

        /* El idioma se aplica al montar la interfaz, así que cambiarlo reinicia el visor. */
        void CambiarIdioma(string code)
        {
            if (string.IsNullOrEmpty(code) || code == Lang.Code) return;
            state.Lang = code;
            SaveSettings();
            if (MessageBox.Show(this, Lang.T("El idioma se aplica al reiniciar el visor. ¿Reiniciar ahora?"),
                    "Koogle Kerbin", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            state.SimT = HasVessels ? sim.T : null;
            SaveView();
            Application.Restart();
            Close();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (WindowState == FormWindowState.Normal) { state.WinX = Left; state.WinY = Top; state.WinW = Width; state.WinH = Height; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState != FormWindowState.Minimized) { SimResetClock(); RequestRender(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            state.WinMax = WindowState == FormWindowState.Maximized;
            if (WindowState == FormWindowState.Normal) { state.WinX = Left; state.WinY = Top; state.WinW = Width; state.WinH = Height; }
            // la partida ya está copiada desde que se cargó; falta en qué instante se dejó
            state.SimT = HasVessels ? sim.T : null;
            SaveView();
            Application.Idle -= OnIdle;
            if (glOk && surface.MakeCurrent())
            {
                DisposeTextures();
                map.Dispose();
                globe.Dispose();
                batch.Dispose();
                text.Dispose();
                glOk = false;
            }
            base.OnFormClosing(e);
        }

        /* ------------------------------------------------------------ entrada */

        bool Picking => ToolMode != null || calibTarget != null || skyPicking;

        void SurfaceMouseDown(object sender, MouseEventArgs e)
        {
            surface.Focus();
            Vis.Set(searchBox, false);
            if (e.Button != MouseButtons.Left) return;
            mouseDown = true; moved = false;
            downPt = lastPt = e.Location;
            if (GlobeVisible) globe.BeginDrag(e.X, e.Y);
        }

        void SurfaceMouseMove(object sender, MouseEventArgs e)
        {
            if (GlobeVisible)
            {
                if (globe.Dragging)
                {
                    /* Arrastrar ya no suelta a la nave seguida: con el foco en ella, el
                       arrastre gira la cámara a su alrededor. */
                    if (globe.Drag(e.X, e.Y)) moved = true;
                    if (moved) { saveViewTimer.Stop(); saveViewTimer.Start(); }
                    RequestRender();
                    return;
                }
                var pin = globe.HitPin(e.X, e.Y);
                surface.Cursor = Picking && is3D ? Cursors.Cross : pin != null ? Cursors.Hand : Cursors.Default;
                if (isSky) { var (az, el) = globe.SkyDirAt(e.X, e.Y); UpdateSkyHud(az, el); }
                else UpdateHud(globe.Pick(e.X, e.Y));
                return;
            }

            if (mouseDown)
            {
                if (!moved && Math.Abs(e.X - downPt.X) + Math.Abs(e.Y - downPt.Y) > Theme.S(3))
                {
                    moved = true;
                    if (Following) Seguir(false);
                    surface.Cursor = Cursors.SizeAll;
                }
                if (moved)
                {
                    map.Pan(e.X - lastPt.X, e.Y - lastPt.Y);
                    lastPt = e.Location;
                    saveViewTimer.Stop(); saveViewTimer.Start();
                    RequestRender();
                }
                return;
            }

            var ll = map.Unproject(e.X, e.Y);
            UpdateHud(ll);
            var hit = map.HitTest(e.X, e.Y);
            if (hit != map.Hover) { map.Hover = hit; RequestRender(); }
            surface.Cursor = Picking ? Cursors.Cross : hit != null ? Cursors.Hand : Cursors.Default;
            if (ToolMode == "measure") ToolMove(new LatLon(ll.Lat, Geo.WrapLon(ll.Lon)));
        }

        void SurfaceMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !mouseDown) return;
            mouseDown = false;
            if (GlobeVisible)
            {
                bool dragged = globe.EndDrag();
                if (!dragged)
                {
                    var pin = globe.HitPin(e.X, e.Y);
                    if (pin?.Tag is Vessel v && !Picking) { popup.Hide(); SeleccionarNave(v); }
                    else if (pin?.Tag is Marker mk && !Picking) ShowMarkerPopup(mk);
                    else
                    {
                        var p = globe.Pick(e.X, e.Y);
                        if (p.HasValue) OnMapClick(p.Value);
                    }
                }
                RequestRender();
                return;
            }
            surface.Cursor = Cursors.Default;
            if (moved) { moved = false; return; }

            var ll = map.Unproject(e.X, e.Y);
            var click = new LatLon(ll.Lat, Geo.WrapLon(ll.Lon));
            if (ToolMode != null) { ToolClick(click); RequestRender(); return; }
            var hit = map.HitTest(e.X, e.Y);
            if (hit?.Tag is Vessel v2 && !Picking) { popup.Hide(); SeleccionarNave(v2); }
            else if (hit?.Tag is Marker mk2 && !Picking) ShowMarkerPopup(mk2);
            else OnMapClick(click);
            RequestRender();
        }

        void SurfaceDoubleClick(object sender, MouseEventArgs e)
        {
            // siguiendo una nave con su modelo, doble clic se acerca a verla
            if (is3D && e.Button == MouseButtons.Left && globe.ZoomToFocusModel()) { RequestRender(); return; }
            if (GlobeVisible || e.Button != MouseButtons.Left || ToolMode != null) return;
            map.ZoomAt(+1, e.X, e.Y);
            RequestRender();
        }

        void SurfaceWheel(object sender, MouseEventArgs e)
        {
            if (GlobeVisible)
            {
                globe.Wheel(e.Delta / 120.0);
                saveViewTimer.Stop(); saveViewTimer.Start();
                RequestRender();
                return;
            }
            wheelAcc += e.Delta;
            while (Math.Abs(wheelAcc) >= 120)
            {
                int step = Math.Sign(wheelAcc);
                map.ZoomAt(step, e.X, e.Y);
                wheelAcc -= step * 120;
            }
            RequestRender();
        }

        void SurfaceKeyDown(object sender, KeyEventArgs e)
        {
            if (isSky)
            {
                double step = Math.Max(1, globe.SkyFov / 20);
                switch (e.KeyCode)
                {
                    case Keys.Left: globe.SkyNudge(-step, 0); break;
                    case Keys.Right: globe.SkyNudge(step, 0); break;
                    case Keys.Up: globe.SkyNudge(0, step); break;
                    case Keys.Down: globe.SkyNudge(0, -step); break;
                    case Keys.Add: case Keys.Oemplus: globe.Wheel(1); break;
                    case Keys.Subtract: case Keys.OemMinus: globe.Wheel(-1); break;
                    default: return;
                }
                e.Handled = true;
                saveViewTimer.Stop(); saveViewTimer.Start();
                RequestRender();
                return;
            }
            if (is3D) return;
            double px = 80 * Theme.Scale;
            switch (e.KeyCode)
            {
                case Keys.Left: map.Pan(-px, 0); break;
                case Keys.Right: map.Pan(px, 0); break;
                case Keys.Up: map.Pan(0, -px); break;
                case Keys.Down: map.Pan(0, px); break;
                case Keys.Add: case Keys.Oemplus: map.ZoomAt(+1, map.W / 2.0, map.H / 2.0); break;
                case Keys.Subtract: case Keys.OemMinus: map.ZoomAt(-1, map.W / 2.0, map.H / 2.0); break;
                default: return;
            }
            e.Handled = true;
            RequestRender();
        }

        static bool FocusInText() => Control.FromHandle(GetFocus()) is TextBoxBase;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !FocusInText())
            {
                if (ToolMode != null) SetToolMode(null);
                else if (skyPicking) ToggleSkyPick();
                else if (!popup.Visible && Following) Seguir(false);
                popup.Hide();
                Vis.Set(searchBox, false);
                RequestRender();
            }
            base.OnKeyDown(e);
        }

        /* Atajos como en KSP: «.» acelera, «,» frena o va hacia atrás, espacio pausa,
           F sigue a la nave. No actúan mientras se escribe en un campo. */
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (FocusInText() || !HasVessels || ModifierKeys.HasFlag(Keys.Control) || ModifierKeys.HasFlag(Keys.Alt)) return;
            switch (e.KeyChar)
            {
                case '.': CambiarWarp(+1); break;
                case ',': CambiarWarp(-1); break;
                case ' ': AlternarPausa(); break;
                case 'f': case 'F': Seguir(!Following); break;
                case 'i': case 'I': ToggleOrbitInfo(); break;
                default: return;
            }
            e.Handled = true;
        }

        async Task OpenFiles(string[] files)
        {
            foreach (var f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".sfs" || ext == ".loadmeta") await CargarSave(f);
                else if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff") await LoadImageFile(GuessSlot(Path.GetFileName(f)), f);
            }
        }

        /* ------------------------------------------------------------ HUD y avisos */

        void UpdateHud(LatLon? p)
        {
            var rows = new System.Collections.Generic.List<(string, string)>
            {
                ("lat", p.HasValue ? Geo.FmtLat(p.Value.Lat) : "—"),
                ("lon", p.HasValue ? Geo.FmtLon(Geo.WrapLon(p.Value.Lon)) : "—")
            };
            if (Img("height") != null)
                rows.Add(("alt", p.HasValue ? Geo.FmtAlt(Img("height").Height_(p.Value.Lat, Geo.WrapLon(p.Value.Lon), state.HMin, state.HMax, state.LonOffset.Height)) : "—"));
            if (Img("biome") != null)
            {
                string hex = p.HasValue ? Img("biome").BiomeHex(p.Value.Lat, Geo.WrapLon(p.Value.Lon), state.LonOffset.Biome) : null;
                rows.Add(("bioma", hex == null ? "—" : BiomeName(hex) ?? hex));
            }
            if (state.DayNight)
                rows.Add(("hora solar", p.HasValue ? FmtSolar(Sun.LocalHours(Geo.WrapLon(p.Value.Lon), globe.SunLon)) : "—"));
            hud.SetRows(rows.ToArray());
            hud.Location = new Point(mapArea.Width - Theme.S(12) - hud.Width, Theme.S(56));
            PlaceOrbitInfo();
        }

        public void Flash(string msg)
        {
            bannerText.SetText(RichLabel.Esc(Lang.T(msg)));
            Vis.Set(banner, true);
            LayoutOverlays();
            banner.BringToFront();
            flashTimer.Stop();
            flashTimer.Start();
        }

        void ShowBannerMessage(string markup)
        {
            bannerText.SetText(markup);
            Vis.Set(banner, true);
            LayoutOverlays();
        }

        void ShowPopup(LatLon anchor, string markup, params (string, Action<DarkButton>)[] actions)
        {
            popup.Open(anchor, markup, actions);
            PlacePopup();
        }

        void PlacePopup()
        {
            if (popup == null || !popup.Visible) return;
            double x, y;
            if (GlobeVisible)
            {
                var p = GlobeView.Sph(popup.AnchorLatLon.Lat, popup.AnchorLatLon.Lon, 1);
                if (!globe.ToScreen(p, out x, out y)) { popup.Left = -10000; return; }
            }
            else (x, y) = map.ProjectNear(popup.AnchorLatLon.Lat, popup.AnchorLatLon.Lon);
            /* Encima del punto; si ahí no cabe (un punto muy al norte, bajo la barra de
               arriba), debajo. Y sin salirse por los lados. */
            int gap = Theme.S(12), top = Theme.S(56), m = Theme.S(8);
            int py = (int)Math.Round(y - popup.Height - gap);
            if (py < top) py = (int)Math.Round(y + gap);
            py = Math.Max(top, Math.Min(py, mapArea.Height - popup.Height - m));
            int px = (int)Math.Round(x - popup.Width / 2.0);
            px = Math.Max(m, Math.Min(px, mapArea.Width - popup.Width - m));
            popup.Location = new Point(px, py);
        }

        /* ------------------------------------------------------------ vistas */

        /* El zoom del mapa plano y la distancia de cámara miden cosas distintas; esto
           las empareja para que al cambiar de vista se siga mirando lo mismo. */
        static double ZoomToDist(double z) => Math.Max(1.05, Math.Min(12, 1.05 + 7 / Math.Pow(1.9, z)));

        static double DistToZoom(double d)
        {
            double z = Math.Log(7 / Math.Max(0.001, d - 1.05)) / Math.Log(1.9);
            return Math.Max(MapConfig.MinZoom, Math.Min(MapConfig.MaxZoom, Math.Round(z)));
        }

        void SetViewMode(string mode)
        {
            if (mode != "2d" && !glOk)
            {
                Flash(glError ?? surface.Error ?? "No se pudo arrancar la vista 3D.");
                return;
            }
            string prev = CurrentView;
            if (mode == prev) return;
            popup.Hide();
            map.Hover = null;
            if (skyPicking && mode == "sky") ToggleSkyPick();

            if (prev == "2d") globe.SetCenter(map.CenterLat, Geo.WrapLon(map.CenterLon), ZoomToDist(map.Zoom));
            if (prev == "sky") SaveView();

            is3D = mode == "3d";
            isSky = mode == "sky";

            switch (mode)
            {
                case "2d":
                    if (prev == "sky") map.SetView(state.ObsLat, state.ObsLon, Math.Max(map.Zoom, 5));
                    else
                    {
                        var c = globe.Center();
                        map.SetView(c.Lat, c.Lon, DistToZoom(globe.EyeDistance));
                    }
                    if (globe.Mode == CamMode.Sky) globe.ExitSky(ZoomToDist(map.Zoom));
                    SimDirty();                 // las trazas 2D no se rehacen mientras se ve el globo
                    break;
                case "3d":
                    if (globe.Mode == CamMode.Sky) globe.ExitSky(Math.Max(1.6, ZoomToDist(map.Zoom)));
                    SyncGlobe();
                    ConstruirAnillos();
                    PushTrack();
                    if (Following && sv.Sel != null) globe.EnterFocus(SelPos());
                    else globe.ExitFocus();
                    break;
                default:
                    ApplyObserverToGlobe();
                    SyncGlobe();
                    ConstruirAnillos();
                    PushTrack();
                    globe.EnterSky();
                    break;
            }

            btn2D.Active = mode == "2d";
            btn3D.Active = mode == "3d";
            btnSky.Active = mode == "sky";
            Vis.Set(globeSection, is3D);
            state.ViewMode = mode;
            state.View3D = is3D;
            SaveSettings();
            if (isSky) UpdateSkyHud(globe.SkyAz, globe.SkyEl);
            else UpdateHud(null);
            LayoutOverlays();
            RequestRender();
        }
    }
}
