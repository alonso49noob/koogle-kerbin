using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;

namespace KerbinMaps.UI
{
    /* Mapas de los demás cuerpos, sacados de una carpeta con las texturas del juego.

       El visor solo trae mapas de Kerbin. Si tienes volcadas las texturas de KSP (con
       cualquier extractor de assets), apuntando aquí a esa carpeta cada cuerpo se ve con
       su mapa de verdad, sus alturas y sus biomas si los hay. */
    public sealed partial class MainForm
    {
        Dictionary<string, BodyMapSet> bodyMapIndex = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (double Min, double Max)> bodyHeightRanges = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (double Min, double Max)> parallaxRanges = new(StringComparer.OrdinalIgnoreCase);
        string parallaxBundle;                       // paquete de Parallax con los mapas de los cuerpos
        HashSet<string> parallaxCuerpos = new(StringComparer.OrdinalIgnoreCase);
        string bodyMapsSource;                       // de dónde salen los del cuerpo actual
        readonly Dictionary<string, ImageData> bodyImages = new();
        readonly Dictionary<string, Texture> bodyTextures = new();
        string bodyMapsLoaded;                       // el cuerpo cuyas imágenes están cargadas
        CancellationTokenSource bodyMapsCts;

        RichLabel bodyMapsInfo;

        /* El desfase y el espejo que hay que aplicar a esas texturas (ver BodyMaps). */
        double BodyMapOffset => state.BodyMapOffset;

        /* Calibración del mapa de alturas del cuerpo que se ve: qué altura es el gris 0 y
           cuál el 255.

           Para los mapas de la carpeta manda Parallax, que es de donde salen: su rango de
           terreno con el tope de gris 145. Si no está, se usa el rango de SCANsat (el de
           la partida o el de su configuración), que es el correcto para un export en
           grises de SCANsat pero solo una aproximación para un volcado. */
        (double Min, double Max) RangoAltura()
        {
            if (OnMapBody) return (state.HMin, state.HMax);
            if (parallaxRanges.TryGetValue(Body.Name, out var pr)) return BodyMaps.Calibracion(pr.Min, pr.Max);
            var cobertura = extras?.Cobertura(Body.Name);
            if (cobertura != null && !double.IsNaN(cobertura.MinHeight) && cobertura.MaxHeight > cobertura.MinHeight)
                return (cobertura.MinHeight, cobertura.MaxHeight);
            if (bodyHeightRanges.TryGetValue(Body.Name, out var r)) return r;
            return (state.HMin, state.HMax);
        }

        /* Alturas que de verdad tiene el terreno del cuerpo (no la rampa de grises): es lo
           que el filtro de altimetría usa como franja de partida. */
        (double Min, double Max) RangoTerreno()
        {
            if (parallaxRanges.TryGetValue(Body.Name, out var pr)) return pr;
            var cobertura = extras?.Cobertura(Body.Name);
            if (!OnMapBody && cobertura != null && !double.IsNaN(cobertura.MinHeight) && cobertura.MaxHeight > cobertura.MinHeight)
                return (cobertura.MinHeight, cobertura.MaxHeight);
            if (!OnMapBody && bodyHeightRanges.TryGetValue(Body.Name, out var r)) return r;
            return (state.HMin, state.HMax);
        }

        double HMinNow => RangoAltura().Min;
        double HMaxNow => RangoAltura().Max;
        double HeightOffNow => OnMapBody ? state.LonOffset.Height : BodyMapOffset;
        double ColorOffNow => OnMapBody ? state.LonOffset.Color : BodyMapOffset;
        double BiomeOffNow => OnMapBody ? state.LonOffset.Biome : BodyMapOffset;

        /* Relee la carpeta (al arrancar, al elegirla o al cambiar de sistema solar). */
        void IndexarMapasDeCuerpos()
        {
            bodyMapIndex = BodyMaps.Index(state.BodyMapsDir);
            string gd = FindGameData();
            parallaxBundle = ParallaxPlanets.FindBundle(gd);
            parallaxCuerpos = ParallaxPlanets.Cuerpos(parallaxBundle);
            if (bodyHeightRanges.Count == 0) bodyHeightRanges = BodyMaps.Ranges(gd);
            if (parallaxRanges.Count == 0) parallaxRanges = BodyMaps.ParallaxRanges(gd);
            RenderBodyMapsInfo();
        }

        void RenderBodyMapsInfo()
        {
            if (bodyMapsInfo == null) return;
            string px = parallaxBundle != null && state.UseParallax
                ? Lang.F("Mapas de Parallax para <b>{0}</b> cuerpos, leídos de su paquete.", parallaxCuerpos.Count)
                : null;
            if (string.IsNullOrEmpty(state.BodyMapsDir))
            {
                bodyMapsInfo.SetText(px ?? Lang.T("Sin carpeta: los demás cuerpos se ven con su color y la retícula."));
                return;
            }
            int ficheros = bodyMapIndex.Values.Sum(s => (s.Color != null ? 1 : 0) + (s.Height != null ? 1 : 0) + (s.Biome != null ? 1 : 0));
            var mio = bodyMapIndex.GetValueOrDefault(Body.Name);
            string detalle = mio == null
                ? Lang.F("{0}: sin mapa en esa carpeta.", Body.Current.Label)
                : Lang.F("{0}: {1}.", Body.Current.Label, string.Join(" + ", new[]
                  {
                      mio.Color != null ? Lang.T("color") : null,
                      mio.Height != null ? Lang.T("altura") : null,
                      mio.Biome != null ? Lang.T("biomas") : null,
                  }.Where(x => x != null)));
            bodyMapsInfo.SetText(Lang.F("<b>{0}</b> mapas para <b>{1}</b> cuerpos.", ficheros, bodyMapIndex.Count) + "\n" + detalle);
        }

        async Task ElegirCarpetaMapas()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = Lang.T("Carpeta con las texturas de los cuerpos (Kerbin_Color.png, Duna_Height.png...)"),
                UseDescriptionForTitle = true,
                SelectedPath = state.BodyMapsDir ?? "",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            state.BodyMapsDir = dlg.SelectedPath;
            SaveSettings();
            IndexarMapasDeCuerpos();
            if (bodyMapIndex.Count == 0)
                Flash(Lang.T("En esa carpeta no hay ningún fichero que empiece por el nombre de un cuerpo."));
            bodyMapsLoaded = null;
            await CargarMapasDelCuerpo();
        }

        void QuitarCarpetaMapas()
        {
            state.BodyMapsDir = null;
            SaveSettings();
            bodyMapIndex.Clear();
            bodyMapsLoaded = null;
            SoltarMapasDeCuerpo();
            RenderBodyMapsInfo();
            ApplyMapTextures();
            SyncGlobe();
            RequestRender();
        }

        void SoltarMapasDeCuerpo()
        {
            if (bodyTextures.Count > 0 && surface.MakeCurrent())
                foreach (var t in bodyTextures.Values) t.Dispose();
            bodyTextures.Clear();
            bodyImages.Clear();
        }

        /* Carga (en segundo plano) los mapas del cuerpo que se está viendo. */
        async Task CargarMapasDelCuerpo()
        {
            if (OnMapBody) { bodyMapsLoaded = null; SoltarMapasDeCuerpo(); return; }
            if (bodyMapsLoaded == Body.Name) return;

            bodyMapsCts?.Cancel();
            var cts = bodyMapsCts = new CancellationTokenSource();
            var token = cts.Token;

            SoltarMapasDeCuerpo();
            bodyMapsLoaded = Body.Name;
            var set = bodyMapIndex.GetValueOrDefault(Body.Name);
            string cuerpo = Body.Name;
            /* Primero la carpeta elegida a mano; si no tiene este cuerpo, Parallax (del KSP
               del jugador o de lo que bajó el instalador), leído directo de su paquete. */
            bool deParallax = (set == null || !set.Any) && state.UseParallax && parallaxBundle != null && parallaxCuerpos.Contains(cuerpo);
            if ((set == null || !set.Any) && !deParallax) { ApplyMapTextures(); SyncGlobe(); RequestRender(); return; }

            var rutas = new List<(string slot, string path)>();
            if (!deParallax)
            {
                if (set.Color != null) rutas.Add(("color", set.Color));
                if (set.Height != null) rutas.Add(("height", set.Height));
                if (set.Biome != null) rutas.Add(("biome", set.Biome));
            }

            List<(string slot, ImageData img)> cargadas;
            try
            {
                cargadas = await Task.Run(() =>
                {
                    var res = new List<(string, ImageData)>();
                    if (deParallax)
                    {
                        try
                        {
                            var (c, h) = ParallaxPlanets.Load(parallaxBundle, cuerpo);
                            if (state.BodyMapMirror) { c?.MirrorX(); h?.MirrorX(); }
                            if (c != null) res.Add(("color", c));
                            if (h != null) res.Add(("height", h));
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[mapas] parallax " + cuerpo + ": " + ex.Message); }
                        return res;
                    }
                    foreach (var (slot, path) in rutas)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var img = ImageData.Decode(File.ReadAllBytes(path));
                            if (state.BodyMapMirror) img.MirrorX();
                            res.Add((slot, img));
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[mapas] " + path + ": " + ex.Message); }
                    }
                    return res;
                }, token);
            }
            catch (OperationCanceledException) { return; }
            bodyMapsSource = deParallax ? "Parallax" : null;

            if (token.IsCancellationRequested || bodyMapsLoaded != Body.Name) return;

            if (glOk && surface.MakeCurrent())
                foreach (var (slot, img) in cargadas)
                {
                    bodyImages[slot] = img;
                    bodyTextures[slot] = Texture.FromRgba(img.Rgba, img.Width, img.Height,
                        slot == "color" ? TexFilter.Mipmap : TexFilter.Nearest, true);
                }

            RenderBodyMapsInfo();
            AjustarFiltroAlCuerpo();
            ApplyMapTextures();
            SyncGlobe();
            UpdateHud(null);
            RequestRender();
        }
    }
}
