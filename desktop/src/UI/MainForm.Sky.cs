using System;
using System.Drawing;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Vista del cielo: dónde está el observador y qué opciones tiene. */
    public sealed partial class MainForm
    {
        Section skySection;
        DarkTextBox skyLat, skyLon, skyAlt;
        DarkCheck chkSkyGrid, chkSkyNight;
        DarkButton skyPickBtn;
        bool skyPicking;
        readonly MapLayer observerLayer = new();

        const double KscLat = -0.0972, KscLon = -74.5577;

        void BuildSkySection()
        {
            skySection = AddSection("Vista del cielo", false);
            skySection.Add(Hint("Ponte de pie en cualquier punto de Kerbin y mira hacia arriba: las naves de la partida " +
                                "cruzan el cielo al ritmo de la barra de tiempo. Arrastra para mirar alrededor, la rueda " +
                                "amplía y las flechas también giran la vista."));

            skyLat = Num(Math.Round(state.ObsLat, 4));
            skyLon = Num(Math.Round(state.ObsLon, 4));
            skyAlt = Num(Math.Round(state.ObsAlt));
            skyLat.Committed += (s, e) => ApplyObserverFields();
            skyLon.Committed += (s, e) => ApplyObserverFields();
            skyAlt.Committed += (s, e) => ApplyObserverFields();
            skySection.Add(new Row2(Field("Latitud (°)", skyLat), Field("Longitud (°)", skyLon)));
            skySection.Add(Field("Altitud sobre el mar (m)", skyAlt));

            var ksc = new DarkButton("KSC", small: true) { Tip = "La plataforma de lanzamiento" };
            var centro = new DarkButton("Centro de la vista", small: true);
            skyPickBtn = new DarkButton("Elegir en el mapa", small: true);
            ksc.Click += (s, e) => SetObserver(KscLat, KscLon, GroundAlt(KscLat, KscLon, 70));
            centro.Click += (s, e) =>
            {
                var c = GlobeVisible ? globe.Center() : new LatLon(map.CenterLat, Geo.WrapLon(map.CenterLon));
                SetObserver(c.Lat, c.Lon, null);
            };
            skyPickBtn.Click += (s, e) => ToggleSkyPick();
            skySection.Add(new BtnRow(ksc, centro, skyPickBtn));

            chkSkyGrid = new DarkCheck("Rejilla de altura y acimut", state.SkyGrid);
            chkSkyNight = new DarkCheck("Forzar la noche", state.SkyForceNight);
            chkSkyGrid.CheckedChanged += (s, e) => { state.SkyGrid = globe.SkyGrid = chkSkyGrid.Checked; SaveSettings(); RequestRender(); };
            chkSkyNight.CheckedChanged += (s, e) => { state.SkyForceNight = globe.SkyForceNight = chkSkyNight.Checked; SaveSettings(); RequestRender(); };
            skySection.Add(Checks(chkSkyGrid, chkSkyNight));

            var go = new DarkButton("Mirar el cielo", ButtonVariant.Primary);
            go.Click += (s, e) => SetViewMode("sky");
            skySection.Add(new BtnRow(go));
            skySection.Add(Hint("El Sol va donde lo pone la órbita de Kerbin en el instante de la barra de tiempo (sin " +
                                "partida cargada, el del comienzo del juego): de día el cielo es azul, al atardecer se " +
                                "enciende el horizonte y de noche salen las estrellas. «Forzar la noche» apaga el día para " +
                                "seguir mejor las naves. Las estrellas son decorativas, pero giran con Kerbin como lo harían las de verdad."));

            globe.SkyAz = state.SkyAz;
            globe.SkyEl = state.SkyEl;
            globe.SkyFov = state.SkyFov;
            globe.SkyGrid = state.SkyGrid;
            globe.SkyForceNight = state.SkyForceNight;
            globe.Light = map.DayNight = state.DayNight;
            ApplyObserverToGlobe();
            RenderObserver();
        }

        /* Altitud del suelo si hay mapa de alturas; si no, la que se indique. */
        double GroundAlt(double lat, double lon, double fallback)
        {
            var img = Img("height");
            return img != null ? Math.Max(0, img.Height_(lat, lon, state.HMin, state.HMax, state.LonOffset.Height)) : fallback;
        }

        void ApplyObserverToGlobe()
        {
            globe.ObsLat = state.ObsLat;
            globe.ObsLon = state.ObsLon;
            globe.ObsAlt = state.ObsAlt;
        }

        void SetObserver(double lat, double lon, double? alt)
        {
            state.ObsLat = Math.Clamp(lat, -90, 90);
            state.ObsLon = Geo.WrapLon(lon);
            state.ObsAlt = Math.Max(0, alt ?? GroundAlt(state.ObsLat, state.ObsLon, 0));
            skyLat.SetNumber(Math.Round(state.ObsLat, 4));
            skyLon.SetNumber(Math.Round(state.ObsLon, 4));
            skyAlt.SetNumber(Math.Round(state.ObsAlt));
            ApplyObserverToGlobe();
            RenderObserver();
            SaveSettings();
            RequestRender();
        }

        void ApplyObserverFields() =>
            SetObserver(skyLat.NumberOr(state.ObsLat), skyLon.NumberOr(state.ObsLon), skyAlt.NumberOr(state.ObsAlt));

        void RenderObserver()
        {
            observerLayer.Clear();
            observerLayer.Dots.Add(new MapDot
            {
                Lat = state.ObsLat, Lon = state.ObsLon, Style = DotStyle.Point,
                Fill = ColorF.Hex("#ffb454"), Tooltip = "Observador del cielo"
            });
            SyncGlobe();
        }

        void ToggleSkyPick()
        {
            skyPicking = !skyPicking;
            skyPickBtn.Active = skyPicking;
            if (skyPicking && isSky) SetViewMode("2d");
            surface.Cursor = skyPicking ? System.Windows.Forms.Cursors.Cross : System.Windows.Forms.Cursors.Default;
            if (skyPicking) Flash("Haz clic en el mapa donde quieras ponerte a mirar el cielo. Esc para cancelar.");
        }

        void SkyPick(LatLon ll)
        {
            skyPicking = false;
            skyPickBtn.Active = false;
            surface.Cursor = System.Windows.Forms.Cursors.Default;
            SetObserver(ll.Lat, ll.Lon, null);
            Flash("Observador en " + Geo.FmtLat(state.ObsLat) + " · " + Geo.FmtLon(state.ObsLon) +
                  " (" + Geo.F(state.ObsAlt, 0) + " m). Pulsa «Cielo» arriba para mirar desde ahí.");
        }

        static readonly string[] RumbosHud = { "N", "NE", "E", "SE", "S", "SO", "O", "NO" };

        void SetDayNight(bool on)
        {
            state.DayNight = globe.Light = map.DayNight = on;
            if (chkLight != null) chkLight.Checked = on;
            if (chkNight2D != null) chkNight2D.Checked = on;
            SaveSettings();
            RequestRender();
        }

        /* Hora solar local con el día de Kerbin de 6 h: «3:00» es mediodía. */
        static string FmtSolar(double h)
        {
            int min = (int)Math.Floor(h * 60) % 360;
            return (min / 60) + ":" + (min % 60).ToString("00") + " h";
        }

        static string Signed(double v) => (v >= 0 ? "+" : "−") + Geo.F(Math.Abs(v), 1) + "°";

        void UpdateSkyHud(double az, double el)
        {
            var (sAz, sEl) = globe.SunAltAz();
            hud.SetRows(("acimut", Geo.F(az, 1) + "° " + RumbosHud[(int)Math.Round(az / 45) % 8]),
                        ("altura", Signed(el)),
                        ("campo", Geo.F(globe.SkyFov, 0) + "°"),
                        ("sol", Signed(sEl) + " " + RumbosHud[(int)Math.Round(sAz / 45) % 8]),
                        ("hora solar", FmtSolar(Sun.LocalHours(state.ObsLon, globe.SunLon))));
            hud.Location = new Point(mapArea.Width - Theme.S(12) - hud.Width, Theme.S(56));
            PlaceOrbitInfo();
        }
    }
}
