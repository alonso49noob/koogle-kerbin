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
    /* Maps of the other bodies, taken from a folder with the game's textures.

       The viewer only ships Kerbin maps. If you've dumped KSP's textures (with any asset
       extractor), pointing here to that folder shows each body with its real map, its heights
       and its biomes if there are any. */
    public sealed partial class MainForm
    {
        Dictionary<string, BodyMapSet> bodyMapIndex = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (double Min, double Max)> bodyHeightRanges = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (double Min, double Max)> parallaxRanges = new(StringComparer.OrdinalIgnoreCase);
        string parallaxBundle;                       // Parallax bundle with the bodies' maps
        HashSet<string> parallaxCuerpos = new(StringComparer.OrdinalIgnoreCase);
        string bodyMapsSource;                       // where the current body's come from
        readonly Dictionary<string, ImageData> bodyImages = new();
        readonly Dictionary<string, Texture> bodyTextures = new();
        string bodyMapsLoaded;                       // the body whose images are loaded
        CancellationTokenSource bodyMapsCts;

        RichLabel bodyMapsInfo;

        /* The offset and mirror to apply to those textures (see BodyMaps). */
        double BodyMapOffset => state.BodyMapOffset;

        /* Calibration of the viewed body's height map: which height gray 0 is and which gray
           255 is.

           For the folder's maps Parallax rules, since that's where they come from: its terrain
           range with the top at gray 145. If it isn't there, SCANsat's range is used (the
           save's or its configuration's), which is right for a SCANsat grayscale export but
           only an approximation for a dump. */
        (double Min, double Max) RangoAltura()
        {
            if (OnMapBody) return (state.HMin, state.HMax);
            /* Read from the Parallax bundle, the gray uses the full scale: 0 and 255 are the
               terrain's minimum and maximum (on Kerbin, that puts the KSC at 79 m and the pad
               at 74). The 145 top belongs to the folder's PNG dumps. */
            if (parallaxRanges.TryGetValue(Body.Name, out var pr))
                return bodyMapsSource == "Parallax" ? (pr.Min, pr.Max) : BodyMaps.Calibracion(pr.Min, pr.Max);
            var cobertura = extras?.Cobertura(Body.Name);
            if (cobertura != null && !double.IsNaN(cobertura.MinHeight) && cobertura.MaxHeight > cobertura.MinHeight)
                return (cobertura.MinHeight, cobertura.MaxHeight);
            if (bodyHeightRanges.TryGetValue(Body.Name, out var r)) return r;
            return (state.HMin, state.HMax);
        }

        /* Heights the body's terrain really has (not the gray ramp): it's what the altimetry
           filter uses as its starting band. */
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

        /* Rereads the folder (at startup, when it's chosen or when the solar system changes). */
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

        /* Loads (in the background) the maps of the body being viewed. */
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
            /* First the folder chosen by hand; if it doesn't have this body, Parallax (from the
               player's KSP or from what the installer downloaded), read straight from its
               bundle. */
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
