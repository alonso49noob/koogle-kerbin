using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Ksp;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Scatters de Parallax (hierba, arbustos, árboles, cactus, rocas) en el vuelo y en la
       vista del cielo. Se cargan por cuerpo, de la instalación de KSP o de lo que bajó el
       instalador: sus configuraciones, sus modelos .mu y sus texturas, leídas del paquete
       de Unity del mod. Dónde va cada uno lo decide ScatterField con el mismo mapa de
       alturas que dibuja el suelo, para que queden apoyados en él. */
    public sealed partial class MainForm
    {
        string scattersDe;
        object[] campoFirma;                      // con qué mapas se hizo el reparto actual
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
            if (cuerpo != Body.Name || scattersDe != clave) return;          // se cambió de cuerpo mientras tanto
            if (a == null || !glOk || !surface.MakeCurrent()) { RenderVueloInfo(); return; }

            globe.ScatterGpu = ScatterGpu.Upload(a);
            globe.MsaaSamples = surface.Samples;
            CrearCampoScatters();
        }

        /* El reparto trabaja en otro hilo, así que se le da una copia fija de lo que
           necesita (los mapas, su calibración, los nombres de biomas) en vez de dejarle leer
           lo que la ventana puede estar cambiando. Si los mapas cambian, se rehace. */
        void CrearCampoScatters()
        {
            if (globe.ScatterGpu == null) return;
            globe.ScatterField?.Stop();

            var alt = MapImg("height");
            var col = MapImg("color");
            var bio = MapImg("biome");
            var (hmin, hmax) = RangoAltura();
            // enteros, como los recibe el shader (ver SyncGlobe): si no, el suelo y la hierba no coinciden
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
                /* Con los nombres del juego («Grasslands», «Deserts»...), que son los que usan
                   las listas de Parallax; si no se conocen, los que les haya puesto el usuario. */
                Bioma = (la, lo) =>
                {
                    string hex = bio?.BiomeHex(la, Geo.WrapLon(lo), bOff);
                    if (hex == null) return null;
                    return StockBiomes.Name(cuerpo, hex) ?? (nombres.TryGetValue(hex, out var n) && !string.IsNullOrEmpty(n) ? n : null);
                },
            };
            campo.Changed = () =>
            {
                // desde el hilo que genera: se pinta otra vez, sin pasarse de 20 por segundo
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

        /* Se llama al cambiar los mapas: si el reparto se hizo con otros, se rehace. */
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
