using System;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;

namespace KerbinMaps.UI
{
    /* Teselas de alturas de detalle (ver TeselaAltura): al acercarse al suelo se lee el
       mapa de Parallax a resolución completa, pero solo alrededor de la cámara, con más
       detalle cuanto más cerca.

       Nivel por altura sobre el terreno: por debajo de 60 km el nivel 0 (en Kerbin, 460 m
       por texel; la ventana de 1024 abarca ±235 km, más que el horizonte a esa altura),
       por debajo de 200 km el nivel 1, y más arriba nada: el mapa base basta. Un nivel se
       usa si es al menos tan fino como el mapa base que ya hay: a la misma resolución la
       tesela sigue siendo mejor que un PNG volcado del juego, que tiene el gris con tope en
       145 (56 m por escalón en Kerbin, así que la tierra baja de la costa se hundía bajo el
       mar o salía como arena) y sin suavizar en lo llano. */
    public sealed partial class MainForm
    {
        FuenteAltura fuenteAlt;
        string fuenteAltDe;                         // el cuerpo de la fuente (o que se está cargando)
        bool cargandoFuente, haciendoTesela;
        TeselaAltura tesela;

        const int LadoTesela = 1024;

        string sueloEn3D;                           // el cuerpo cuyo suelo ya se cargó al bajar en 3D o en el 2D

        /* El mapa 2D, de cerca, se pinta con el suelo del vuelo visto desde arriba (ver
           GlobeView.RenderCenital): a partir de este zoom, unos 7 m por píxel. */
        const double ZoomCenital = 9.5;

        bool Cenital2D => !GlobeVisible && glOk && map.Zoom >= ZoomCenital - 0.01;

        bool FondoCenital()
        {
            if (!Cenital2D) return false;
            globe.W = map.W; globe.H = map.H; globe.S = map.S;
            globe.GroundAt ??= AlturaDelSuelo;
            /* Con la luz de un mapa (la noche la pone el propio mapa encima, igual que sin
               esto) y, del zoom 10 al 12, pasando poco a poco del aspecto del mapa plano al
               del suelo del vuelo. */
            double x = Math.Clamp((map.Zoom - 10) / 2, 0, 1);
            globe.RenderCenital(batch, text, map.CenterLat, map.CenterLon, map.Ppd, false, 1 - x * x * (3 - 2 * x));
            return true;
        }

        /* Lo que abarca en vertical el mapa 2D, en metros: hace de altura sobre el suelo
           para elegir el nivel de la tesela. */
        double AltoDelMapaM => map.H / map.Ppd * Body.Radius * Math.PI / 180;

        /* Cada fotograma: si la cámara pide otra tesela, se pone a hacer. Y al bajar en la
           vista 3D hasta el suelo, se carga lo que viste el terreno de cerca (texturas,
           nubes, scatters, edificios) la primera vez, como al entrar en el vuelo. */
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
            if (nivel < 0) { QuitarTesela(); return; }         // desde lejos ni se carga la fuente
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

        /* Lo que se colocó sobre el suelo se recoloca: edificios y scatters. */
        void TrasCambioDeSuelo()
        {
            kkFirma = null;
            if (kk != null) AplicarKonstructs();
            ActualizarCampoScatters();
            RenderVueloInfo();
            RequestRender();
        }

        /* El mapa de alturas crudo del cuerpo en el paquete de Parallax, o null. Se carga
           una vez por cuerpo, en segundo plano (en Kerbin son 42 MB). */
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
                        // leídos del paquete, el gris usa toda la escala (ver RangoAltura)
                        fuenteAlt = new FuenteAltura { Datos = datos, Ancho = w, Alto = h, Mips = mips, Espejo = espejo, Offset = offset, Min = rango.Min, Max = rango.Max };
                        RequestRender();
                    }));
                }
                catch (InvalidOperationException) { }
            });
            return null;
        }

        /* La altura de la tesela mezclada con la del mapa base (para la CPU). */
        double ConTesela(double lat, double lon, double h)
        {
            var t = tesela;
            if (t != null && t.Altura(lat, lon, out double ht, out double w)) h += (ht - h) * w;
            return h;
        }
    }
}
