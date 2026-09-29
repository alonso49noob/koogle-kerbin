using System;
using System.Collections.Generic;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Ksp;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Vista «Vuelo»: una cámara libre a ras de suelo.

       Se mueve como en un juego: W/S adelante y atrás en la dirección en la que se mira,
       A/D de lado, R/F o espacio y control para subir y bajar, arrastrar para mirar y la
       rueda para el acelerador. Mayúsculas multiplica la velocidad. No hay inercia ni
       choques: solo no deja bajar del suelo. */
    public sealed partial class MainForm
    {
        bool isFree;
        bool restaurandoVista;                    // al arrancar, la cámara vuelve donde se quedó
        readonly HashSet<Keys> teclas = new();
        double ultimoVuelo;

        bool VolandoConTeclas => isFree && teclas.Count > 0;

        void EntrarVuelo(string vista)
        {
            // se entra por donde se estaba mirando: el centro del mapa o del globo
            double lat, lon;
            if (vista == "sky") { lat = state.ObsLat; lon = state.ObsLon; }
            else if (vista == "3d") { var c = globe.Center(); lat = c.Lat; lon = c.Lon; }
            else { lat = map.CenterLat; lon = Geo.WrapLon(map.CenterLon); }

            globe.GroundAt = AlturaDelSuelo;
            /* Al arrancar se vuelve a donde se dejó la cámara; entrando desde otra vista,
               a donde se estaba mirando, que es lo que uno espera al pulsar «Vuelo». */
            if (state.FreeLat is double fl && state.FreeLon is double fo && (restaurandoVista || vista == "sky"))
                globe.SetFree(fl, fo, state.FreeAlt, state.FreeAz);
            else
                globe.SetFree(lat, lon, null, state.FreeAz);
            globe.FreeSpeed = Math.Clamp(state.FreeSpeed, GlobeView.FreeMinSpeed, GlobeView.FreeMaxSpeed);
            globe.FreeRelief = state.FreeRelief;
            globe.Detail = state.FreeDetail;
            globe.Debug = state.FreeDebug;
            globe.EnterFree();
            CargarSuelo();
            teclas.Clear();
            ultimoVuelo = 0;
        }

        /* Altura del terreno bajo un punto, con el mapa de alturas del cuerpo y su
           calibración, interpolada igual que en el shader. Sin mapa, el nivel del mar. */
        double AlturaDelSuelo(double lat, double lon) => Math.Max(0, AlturaCruda(lat, lon));

        /* La misma sin cortar en el nivel del mar: el fondo marino, para los scatters que
           solo salen en la costa o bajo el agua. */
        double AlturaCruda(double lat, double lon)
        {
            var img = MapImg("height");
            var (hmin, hmax) = RangoAltura();
            double h = img == null ? 0 : img.HeightSmooth(lat, Geo.WrapLon(lon), hmin, hmax, (int)HeightOffNow);
            return Aplanado.Aplicar(aplanados, lat, lon, h, Body.Radius);
        }

        /* Un paso de vuelo con las teclas que estén pulsadas. Lo llama el bucle de
           fotogramas con el tiempo transcurrido, así que la velocidad no depende de los
           fotogramas por segundo. */
        void PasoDeVuelo(double now)
        {
            if (!isFree) return;
            if (globe.ClampFree()) { UpdateFreeHud(); RequestRender(); }
            double dt = ultimoVuelo > 0 ? Math.Min(0.1, now - ultimoVuelo) : 0;
            ultimoVuelo = now;
            if (dt <= 0 || teclas.Count == 0) return;

            double fwd = (Tecla(Keys.W) ? 1 : 0) - (Tecla(Keys.S) ? 1 : 0);
            double side = (Tecla(Keys.D) ? 1 : 0) - (Tecla(Keys.A) ? 1 : 0);
            double up = (Tecla(Keys.R) || Tecla(Keys.Space) ? 1 : 0) - (Tecla(Keys.F) || Tecla(Keys.ControlKey) ? 1 : 0);
            double boost = Tecla(Keys.ShiftKey) ? 5 : 1;
            if (globe.MoveFree(dt, fwd, side, up, boost))
            {
                UpdateFreeHud();
                guardarVueloTimer.Stop();
                guardarVueloTimer.Start();
                RequestRender();
            }
        }

        bool Tecla(Keys k) => teclas.Contains(k);

        void VueloKeyDown(KeyEventArgs e)
        {
            if (!isFree || FocusInText()) return;
            switch (e.KeyCode)
            {
                case Keys.W: case Keys.A: case Keys.S: case Keys.D:
                case Keys.R: case Keys.F: case Keys.Space: case Keys.ControlKey: case Keys.ShiftKey:
                    if (teclas.Add(e.KeyCode) && teclas.Count == 1) ultimoVuelo = clock.Elapsed.TotalSeconds;
                    e.Handled = true;
                    RequestRender();
                    break;
                case Keys.Left: globe.FreeLook(-3, 0); UpdateFreeHud(); e.Handled = true; RequestRender(); break;
                case Keys.Right: globe.FreeLook(3, 0); UpdateFreeHud(); e.Handled = true; RequestRender(); break;
                case Keys.Up: globe.FreeLook(0, 3); UpdateFreeHud(); e.Handled = true; RequestRender(); break;
                case Keys.Down: globe.FreeLook(0, -3); UpdateFreeHud(); e.Handled = true; RequestRender(); break;
            }
        }

        void VueloKeyUp(KeyEventArgs e)
        {
            if (teclas.Remove(e.KeyCode) && teclas.Count == 0) ultimoVuelo = 0;
        }

        void GuardarVuelo()
        {
            state.FreeLat = globe.FreeLat;
            state.FreeLon = globe.FreeLon;
            state.FreeAlt = globe.FreeAlt;
            state.FreeAz = globe.FreeAz;
            state.FreeSpeed = globe.FreeSpeed;
            SaveSettings();
        }

        /* La velocidad va de 2 a 20 000 m/s: el deslizador la recorre en escala
           logarítmica, para tener tanto paseo como vuelo rasante rápido. */
        static int VelocidadAPaso(double v) =>
            (int)Math.Round(1 + 99 * (Math.Log(Math.Clamp(v, GlobeView.FreeMinSpeed, GlobeView.FreeMaxSpeed) / GlobeView.FreeMinSpeed)
                                      / Math.Log(GlobeView.FreeMaxSpeed / GlobeView.FreeMinSpeed)));

        static double PasoAVelocidad(int paso) =>
            GlobeView.FreeMinSpeed * Math.Pow(GlobeView.FreeMaxSpeed / GlobeView.FreeMinSpeed, (paso - 1) / 99.0);

        /* Lo que viste el suelo en el vuelo y en el cielo: texturas, nubes y scatters, con
           los ajustes del panel. */
        void CargarSuelo()
        {
            globe.Clouds = state.Clouds;
            globe.CloudAlt = state.CloudAlt;
            globe.DetailVariation = state.TextureVariation;
            globe.Scatters = state.Scatters;
            globe.ScatterDensity = state.ScatterDensity;
            globe.Wind = state.Wind;
            _ = CargarTexturasDeTerreno();
            _ = CargarNubes();
            _ = CargarScatters();
        }

        void RenderVueloInfo()
        {
            if (vueloInfo == null) return;
            var partes = new List<string>();
            partes.Add(globe.HasDetail && terrenoOrigen != null
                ? Lang.F("Texturas de suelo: {0}.", terrenoOrigen)
                : Lang.T("Sin texturas de suelo: no encontré Parallax ni CTTP en tu instalación."));
            if (state.Scatters)
                partes.Add(globe.ScatterField != null
                    ? Lang.F("Scatters de Parallax: {0} tipos, {1} objetos a la vista.", globe.ScatterField.Layers.Count, Geo.F(globe.ScatterVisible, 0))
                    : scattersDe != null && FindGameData() != null && ParallaxScatters.FindDir(FindGameData()) == null
                        ? Lang.T("Sin scatters: Parallax_StockScatterTextures no está instalado.")
                        : Lang.F("Sin scatters de Parallax para {0}.", Body.Current.Label));
            partes.Add(globe.CloudTex != null
                ? Lang.F("Nubes de {0}: cargadas del juego.", Body.Current.Label)
                : Lang.F("Sin mapa de nubes para {0} en tu instalación.", Body.Current.Label));
            if (MapImg("height") == null)
                partes.Add(Lang.F("Sin mapa de alturas de {0}: el terreno sale liso.", Body.Current.Label));
            vueloInfo.SetText(string.Join(Environment.NewLine, partes));
        }

        void UpdateFreeHud()
        {
            if (!isFree) return;
            double agl = globe.FreeAgl;
            var rows = new List<(string, string)>
            {
                ("lat", Geo.FmtLat(globe.FreeLat)),
                ("lon", Geo.FmtLon(globe.FreeLon)),
                ("alt", Geo.F(globe.FreeAlt, 0) + " m"),
                ("sobre el suelo", Geo.F(agl, 0) + " m"),
                ("rumbo", Geo.F(globe.FreeAz, 0) + "°"),
                ("velocidad", Geo.F(globe.FreeSpeed, 0) + " m/s"),
            };
            if (MapImg("biome") != null)
            {
                string hex = MapImg("biome").BiomeHex(globe.FreeLat, globe.FreeLon, BiomeOffNow);
                if (hex != null) rows.Add(("bioma", BiomeName(hex) ?? hex));
            }
            hud.SetRows(rows.ToArray());
            hud.Location = new System.Drawing.Point(mapArea.Width - Theme.S(12) - hud.Width, Theme.S(56));
        }
    }
}
