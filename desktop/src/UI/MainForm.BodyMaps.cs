using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

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
        readonly Dictionary<string, ImageData> bodyImages = new();
        readonly Dictionary<string, Texture> bodyTextures = new();
        string bodyMapsLoaded;                       // el cuerpo cuyas imágenes están cargadas
        CancellationTokenSource bodyMapsCts;

        RichLabel bodyMapsInfo;

        /* El desfase y el espejo que hay que aplicar a esas texturas (ver BodyMaps). */
        double BodyMapOffset => state.BodyMapOffset;

        /* Rango de alturas del cuerpo que se ve: el de la partida si SCANsat lo trae, el
           de la configuración de SCANsat si está instalado, y si no el de los ajustes. */
        (double Min, double Max) RangoAltura()
        {
            if (OnMapBody) return (state.HMin, state.HMax);
            var cobertura = extras?.Cobertura(Body.Name);
            if (cobertura != null && !double.IsNaN(cobertura.MinHeight) && cobertura.MaxHeight > cobertura.MinHeight)
                return (cobertura.MinHeight, cobertura.MaxHeight);
            if (bodyHeightRanges.TryGetValue(Body.Name, out var r)) return r;
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
            if (bodyHeightRanges.Count == 0) bodyHeightRanges = BodyMaps.Ranges(FindGameData());
            RenderBodyMapsInfo();
        }

        void RenderBodyMapsInfo()
        {
            if (bodyMapsInfo == null) return;
            if (string.IsNullOrEmpty(state.BodyMapsDir))
            {
                bodyMapsInfo.SetText(Lang.T("Sin carpeta: los demás cuerpos se ven con su color y la retícula."));
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
            if (set == null || !set.Any) { ApplyMapTextures(); SyncGlobe(); RequestRender(); return; }

            var rutas = new List<(string slot, string path)>();
            if (set.Color != null) rutas.Add(("color", set.Color));
            if (set.Height != null) rutas.Add(("height", set.Height));
            if (set.Biome != null) rutas.Add(("biome", set.Biome));

            List<(string slot, ImageData img)> cargadas;
            try
            {
                cargadas = await Task.Run(() =>
                {
                    var res = new List<(string, ImageData)>();
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
