using System;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;

namespace KerbinMaps.UI
{
    /* Detail height tiles (see TeselaAltura): when getting close to the ground the Parallax map
       is read at full resolution, but only around the camera, with more detail the closer it
       is.

       Level by height above the terrain: below 60 km level 0 (on Kerbin, 460 m per texel; the
       1024 window spans ±235 km, more than the horizon at that height), below 200 km level 1,
       and higher up nothing: the base map is enough. A level is used if it's at least as fine
       as the base map already there: at the same resolution the tile is still better than a PNG
       dumped from the game, whose gray tops out at 145 (56 m per step on Kerbin, so the low
       land on the coast sank under the sea or came out as sand) and isn't smoothed on flat
       ground. */
    public sealed partial class MainForm
    {
        FuenteAltura fuenteAlt;
        string fuenteAltDe;                         // the body of the source (or the one being loaded)
        bool cargandoFuente, haciendoTesela;
        TeselaAltura tesela;

        const int LadoTesela = 1024;

        string sueloEn3D;                           // the body whose ground was already loaded when going down in 3D or in 2D

        /* The 2D map, up close, is painted with the flight ground seen from above (see
           GlobeView.RenderCenital): from this zoom on, about 7 m per pixel. */
        const double ZoomCenital = 9.5;

        bool Cenital2D => !GlobeVisible && glOk && map.Zoom >= ZoomCenital - 0.01;

        bool FondoCenital()
        {
            if (!Cenital2D) return false;
            globe.W = map.W; globe.H = map.H; globe.S = map.S;
            globe.GroundAt ??= AlturaDelSuelo;
            /* With a map's lighting (night is laid on top by the map itself, same as without
               this) and, from zoom 10 to 12, shifting gradually from the flat map's look to the
               flight ground's. */
            double x = Math.Clamp((map.Zoom - 10) / 2, 0, 1);
            globe.RenderCenital(batch, text, map.CenterLat, map.CenterLon, map.Ppd, false, 1 - x * x * (3 - 2 * x));
            return true;
        }

        /* What the 2D map spans vertically, in meters: it stands in for the height above the
           ground when choosing the tile level. */
        double AltoDelMapaM => map.H / map.Ppd * Body.Radius * Math.PI / 180;

        /* Every frame: if the camera asks for another tile, it starts building it. And when
           going down to the ground in the 3D view, whatever dresses the terrain up close
           (textures, clouds, scatters, buildings) is loaded the first time, as when entering
           flight. */
        void ActualizarTesela()
        {
            if (!glOk) return;
            if ((is3D && globe.PlanetaCerca || Cenital2D) && sueloEn3D != Body.Name)
            {
                sueloEn3D = Body.Name;
                CargarSuelo();
            }
            if (!state.DetailTiles || !(GlobeVisible || Cenital2D)) { QuitarTesela(); return; }
            var (lat, lon, agl) = GlobeVisible ? globe.EyeGround() : (map.CenterLat, Geo.WrapLon(map.CenterLon), AltoDelMapaM);
            int nivel = agl < 60000 ? 0 : agl < 200000 ? 1 : -1;
            if (nivel < 0) { QuitarTesela(); return; }         // from far away the source isn't even loaded
            var fuente = FuenteAlturas();
            if (fuente == null) { QuitarTesela(); return; }
            int anchoBase = MapImg("height")?.Width ?? 0;
            if (fuente.AnchoNivel(nivel) < anchoBase) { QuitarTesela(); return; }
            if (tesela != null && tesela.Fuente == fuente && tesela.Nivel == nivel && tesela.Cubre(lat, lon)) return;
            if (haciendoTesela) return;
            haciendoTesela = true;
            Task.Run(() =>
            {
                try { return fuente.Tesela(nivel, lat, lon, LadoTesela); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[tesela] " + ex.Message); return null; }
            }).ContinueWith(t =>
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        haciendoTesela = false;
                        if (t.Result == null || fuente != fuenteAlt) return;
                        PonerTesela(t.Result);
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        void PonerTesela(TeselaAltura t)
        {
            if (!surface.MakeCurrent()) return;
            globe.TeselaTex?.Dispose();
            globe.TeselaTex = Texture.FromR32F(t.Metros, t.Tw, t.Th);
            globe.Tesela = t;
            tesela = t;
            TrasCambioDeSuelo();
        }

        void QuitarTesela()
        {
            if (tesela == null) return;
            tesela = null;
            globe.Tesela = null;
            if (surface.MakeCurrent()) globe.TeselaTex?.Dispose();
            globe.TeselaTex = null;
            TrasCambioDeSuelo();
        }

        /* Whatever was placed on the ground is repositioned: buildings and scatters. */
        void TrasCambioDeSuelo()
        {
            kkFirma = null;
            if (kk != null) AplicarKonstructs();       // which also repositions the vessels
            else ColocarNavesEnSuelo();
            ActualizarCampoScatters();
            RenderVueloInfo();
            RequestRender();
        }

        /* The body's raw height map in the Parallax bundle, or null. Loaded once per body, in
           the background (on Kerbin it's 42 MB). */
        FuenteAltura FuenteAlturas()
        {
            string cuerpo = Body.Name;
            if (fuenteAltDe == cuerpo) return fuenteAlt;
            if (cargandoFuente) return null;
            fuenteAlt = null;
            fuenteAltDe = cuerpo;
            if (parallaxBundle == null || !state.UseParallax || !parallaxCuerpos.Contains(cuerpo)) return null;
            if (!parallaxRanges.TryGetValue(cuerpo, out var rango)) return null;
            cargandoFuente = true;
            string bundle = parallaxBundle;
            bool espejo = state.BodyMapMirror;
            double offset = state.BodyMapOffset;
            Task.Run(() =>
            {
                try { return ParallaxPlanets.CargarAlturasCrudas(bundle, cuerpo); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[tesela] fuente: " + ex.Message); return null; }
            }).ContinueWith(t =>
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        cargandoFuente = false;
                        if (fuenteAltDe != cuerpo || t.Result is not var (datos, w, h, mips)) return;
                        // read from the bundle, the gray uses the full scale (see RangoAltura)
                        fuenteAlt = new FuenteAltura { Datos = datos, Ancho = w, Alto = h, Mips = mips, Espejo = espejo, Offset = offset, Min = rango.Min, Max = rango.Max };
                        RequestRender();
                    }));
                }
                catch (InvalidOperationException) { }
            });
            return null;
        }

        /* The tile's height blended with the base map's (for the CPU). */
        double ConTesela(double lat, double lon, double h)
        {
            var t = tesela;
            if (t != null && t.Altura(lat, lon, out double ht, out double w)) h += (ht - h) * w;
            return h;
        }
    }
}
