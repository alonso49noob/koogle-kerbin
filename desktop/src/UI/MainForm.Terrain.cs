using System;
using System.IO;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;

namespace KerbinMaps.UI
{
    /* Texturas de suelo del propio juego para la cámara libre.

       KSP pinta el terreno de cerca con unas cuantas texturas que se repiten (hierba,
       arena, roca, nieve) mezcladas por pendiente y altura. Aquí se usan las mismas: las
       del Community Terrain Texture Pack, que es lo que llevan Parallax y compañía, y si
       no están, las de repuesto que vengan en la instalación. Sin ninguna, el vuelo
       funciona igual pero de cerca el suelo queda liso: el mapa del cuerpo no da más de
       sí a esa distancia. */
    public sealed partial class MainForm
    {

        /* Candidatos por ranura, en orden de preferencia y relativos a GameData. */
        static readonly (string Slot, string[] Rutas)[] Detalles =
        {
            ("grass", new[] { @"Parallax_StockScatterTextures\PluginData\grassuv2.dds", @"CTTP\Textures\PluginData\gravel.dds" }),
            ("sand", new[] { @"CTTP\Textures\PluginData\sand.dds", @"CTTP\Textures\PluginData\beach.dds" }),
            ("rock", new[] { @"CTTP\Textures\PluginData\cliff.dds", @"CTTP\Textures\PluginData\rock.dds" }),
            ("snow", new[] { @"CTTP\Textures\PluginData\snow.dds", @"CTTP\Textures\PluginData\ice.dds" }),
        };

        /* Las del CTTP sirven para todos los cuerpos: se cargan una vez y se guardan. */
        Texture cttpGrass, cttpSand, cttpRock, cttpSnow;
        bool cttpLeido;

        /* Las de Parallax son de cada cuerpo: se cambian al cambiar de cuerpo. */
        Texture[] parallaxTex;
        string terrenoDe;
        string terrenoOrigen;                   // para el panel: de dónde salen las texturas

        async Task CargarTexturasDeTerreno()
        {
            if (terrenoDe == Body.Name + "|" + state.UseParallax) return;
            terrenoDe = Body.Name + "|" + state.UseParallax;
            string gd = FindGameData();
            if (gd == null) return;
            string cuerpo = Body.Name;

            ParallaxTerrain px = null;
            if (state.UseParallax)
                px = await Task.Run(() =>
                {
                    try { return ParallaxTerrain.Load(gd, cuerpo); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[parallax] " + ex.Message); return null; }
                });
            if (cuerpo != Body.Name) return;                    // se cambió de cuerpo mientras tanto

            if (!glOk || !surface.MakeCurrent()) return;
            if (parallaxTex != null) { foreach (var t in parallaxTex) t?.Dispose(); parallaxTex = null; }

            if (px != null)
            {
                var d = new System.Collections.Generic.Dictionary<string, TextureFile>
                    { ["low"] = px.Low, ["mid"] = px.Mid, ["high"] = px.High, ["steep"] = px.Steep };
                // si dos ranuras comparten textura, se sube una sola vez
                var subidas = new System.Collections.Generic.Dictionary<TextureFile, Texture>();
                Texture Una(string k) => d[k] == null ? null : subidas.TryGetValue(d[k], out var ya) ? ya : subidas[d[k]] = Subir(d, k);
                globe.DetGrass = Una("low");
                globe.DetSand = Una("mid");
                globe.DetSnow = Una("high");
                globe.DetRock = Una("steep");
                parallaxTex = new System.Collections.Generic.List<Texture>(subidas.Values).ToArray();
                globe.DetailParallax = true;
                globe.DetailTile = px.MetrosPorRepeticion;
                globe.PxLowMid = (px.LowMidStart, px.LowMidEnd);
                globe.PxMidHigh = (px.MidHighStart, px.MidHighEnd);
                globe.PxSteep = (px.SteepPower, px.SteepContrast, px.SteepMidpoint);
                terrenoOrigen = Lang.F("Parallax ({0})", Body.Current.Label);
            }
            else
            {
                await CargarCttp(gd);
                if (!surface.MakeCurrent()) return;
                globe.DetGrass = cttpGrass; globe.DetSand = cttpSand; globe.DetRock = cttpRock; globe.DetSnow = cttpSnow;
                globe.DetailParallax = false;
                globe.DetailTile = 22;
                terrenoOrigen = globe.HasDetail ? "CTTP" : null;
            }
            RenderVueloInfo();
            RequestRender();
        }

        async Task CargarCttp(string gd)
        {
            if (cttpLeido) return;
            cttpLeido = true;
            var leidas = await Task.Run(() =>
            {
                var res = new System.Collections.Generic.Dictionary<string, TextureFile>();
                foreach (var (slot, rutas) in Detalles)
                    foreach (var r in rutas)
                    {
                        string p = Path.Combine(gd, r);
                        if (!File.Exists(p)) continue;
                        try { res[slot] = TextureFile.Load(p); break; }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[terreno] " + r + ": " + ex.Message); }
                    }
                return res;
            });
            if (leidas.Count == 0 || !glOk || !surface.MakeCurrent()) return;
            cttpGrass = Subir(leidas, "grass");
            cttpSand = Subir(leidas, "sand");
            cttpRock = Subir(leidas, "rock");
            cttpSnow = Subir(leidas, "snow");
            // las ranuras que falten se cubren con otra, para no dejar huecos negros
            cttpGrass ??= cttpSand ?? cttpRock;
            cttpSand ??= cttpGrass ?? cttpRock;
            cttpRock ??= cttpSand ?? cttpGrass;
            cttpSnow ??= cttpRock ?? cttpSand;
        }

        /* Mapa de nubes del cuerpo, de los mods de nubes que haya instalados. Es de
           16384x8192 y pesa 179 MB con sus mipmaps: se lee solo un nivel de 2048 de
           ancho, que para pintarlas sobra y se carga al instante. */
        string nubesDe;

        async Task CargarNubes()
        {
            if (nubesDe == Body.Name) return;
            nubesDe = Body.Name;
            string gd = FindGameData();
            if (gd == null) return;

            string[] candidatos =
            {
                Path.Combine(gd, "StockVolumetricClouds", "Clouds", "Textures", "PluginData", Body.Name, "base", "scaled.dds"),
                Path.Combine(gd, "StockVolumetricClouds", "Clouds", "Textures", "PluginData", Body.Name, "scaled.dds"),
                Path.Combine(gd, "BoulderCo", "Clouds", "Textures", Body.Name.ToLowerInvariant() + "1.dds"),
            };
            string ruta = Array.Find(candidatos, File.Exists);

            var leida = ruta == null ? null : await Task.Run(() =>
            {
                try { return TextureFile.LoadDdsLevel(ruta, 2048); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[nubes] " + ex.Message); return null; }
            });

            if (!glOk || !surface.MakeCurrent()) return;
            globe.CloudTex?.Dispose();
            globe.CloudTex = leida == null ? null : Subir(new System.Collections.Generic.Dictionary<string, TextureFile> { ["n"] = leida }, "n");
            RenderVueloInfo();
            RequestRender();
        }

        static Texture Subir(System.Collections.Generic.Dictionary<string, TextureFile> leidas, string slot)
        {
            if (!leidas.TryGetValue(slot, out var tf) || tf == null || tf.Levels.Count == 0) return null;
            try
            {
                return tf.CompressedFormat != 0
                    ? Texture.FromCompressed(tf.CompressedFormat, tf.Width, tf.Height, tf.Levels)
                    : Texture.FromRgba(tf.Levels[0], tf.Width, tf.Height, TexFilter.Mipmap, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[terreno] subir " + slot + ": " + ex.Message);
                return null;
            }
        }
    }
}
