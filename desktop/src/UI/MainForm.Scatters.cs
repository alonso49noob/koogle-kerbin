using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Ksp;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Parallax scatters (grass, bushes, trees, cacti, rocks) in flight and sky views. Loaded
       per body, from the KSP installation or from what the installer downloaded: their
       configurations, their .mu models and their textures, read from the mod's Unity bundle.
       Where each one goes is decided by ScatterField with the same height map that draws the
       ground, so they rest on it. */
    public sealed partial class MainForm
    {
        string scattersDe;
        object[] campoFirma;                      // which maps the current distribution was made with
        long ultimoPintadoScatters;

        async Task CargarScatters()
        {
            string clave = Body.Name + "|" + state.Scatters;
            if (scattersDe == clave) return;
            scattersDe = clave;
            QuitarScatters();
            if (!state.Scatters) { RenderVueloInfo(); RequestRender(); return; }
            string gd = FindGameData();
            string cuerpo = Body.Name;
            var a = await Task.Run(() =>
            {
                try { return ParallaxScatters.Load(gd, cuerpo); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[scatters] " + ex.Message); return null; }
            });
            if (cuerpo != Body.Name || scattersDe != clave) return;          // the body was changed in the meantime
            if (a == null || !glOk || !surface.MakeCurrent()) { RenderVueloInfo(); return; }

            globe.ScatterGpu = ScatterGpu.Upload(a);
            globe.MsaaSamples = surface.Samples;
            CrearCampoScatters();
        }

        /* The distribution runs on another thread, so it's given a fixed copy of what it needs
           (the maps, their calibration, the biome names) instead of letting it read what the
           window may be changing. If the maps change, it's redone. */
        void CrearCampoScatters()
        {
            if (globe.ScatterGpu == null) return;
            globe.ScatterField?.Stop();

            var alt = MapImg("height");
            var col = MapImg("color");
            var bio = MapImg("biome");
            var (hmin, hmax) = RangoAltura();
            // integers, as the shader receives them (see SyncGlobe): otherwise the ground and the grass don't line up
            double hOff = (int)HeightOffNow, cOff = (int)ColorOffNow, bOff = (int)BiomeOffNow;
            string cuerpo = Body.Name;
            var nombres = new Dictionary<string, string>(biomeNames, StringComparer.OrdinalIgnoreCase);
            var llanos = aplanados;
            var tes = tesela;
            double radio = Body.Radius;

            var campo = new ScatterField(Body.Radius, globe.ScatterGpu.Defs)
            {
                Altura = (la, lo) =>
                {
                    double h = alt == null ? 0 : alt.HeightSmooth(la, Geo.WrapLon(lo), hmin, hmax, hOff);
                    if (tes != null && tes.Altura(la, lo, out double ht, out double w)) h += (ht - h) * w;
                    return Aplanado.Aplicar(llanos, la, lo, h, radio);
                },
                Excluir = (la, lo) => Aplanado.Dentro(llanos, la, lo, radio),
                Color = (la, lo) => col == null ? (1f, 1f, 1f) : col.SampleBilinear(la, Geo.WrapLon(lo), cOff),
                /* With the game's names («Grasslands», «Deserts»...), which are the ones
                   Parallax's lists use; if they aren't known, the ones the user gave them. */
                Bioma = (la, lo) =>
                {
                    string hex = bio?.BiomeHex(la, Geo.WrapLon(lo), bOff);
                    if (hex == null) return null;
                    return StockBiomes.Name(cuerpo, hex) ?? (nombres.TryGetValue(hex, out var n) && !string.IsNullOrEmpty(n) ? n : null);
                },
            };
            campo.Changed = () =>
            {
                // from the generating thread: paint again, without going over 20 per second
                long ahora = Environment.TickCount64;
                if (ahora - ultimoPintadoScatters < 50) return;
                ultimoPintadoScatters = ahora;
                try { BeginInvoke((Action)(() => { RequestRender(); RenderVueloInfo(); })); } catch (InvalidOperationException) { }
            };
            globe.ScatterField = campo;
            globe.ScatterDensity = state.ScatterDensity;
            campoFirma = FirmaCampo();
            RenderVueloInfo();
            RequestRender();
        }

        object[] FirmaCampo() => new object[]
        {
            Body.Name, MapImg("height"), MapImg("color"), MapImg("biome"), HMinNow, HMaxNow, HeightOffNow, ColorOffNow, BiomeOffNow, aplanados, tesela,
        };

        /* Called when the maps change: if the distribution was made with others, it's redone. */
        void ActualizarCampoScatters()
        {
            if (globe.ScatterGpu == null || campoFirma == null) return;
            if (FirmaCampo().SequenceEqual(campoFirma)) return;
            CrearCampoScatters();
        }

        void QuitarScatters()
        {
            globe.ScatterField?.Stop();
            globe.ScatterField = null;
            campoFirma = null;
            if (globe.ScatterGpu != null && glOk && surface.MakeCurrent())
            {
                globe.ScatterGpu.Dispose();
                globe.DisposeScatterBatches();
            }
            globe.ScatterGpu = null;
        }
    }
}
