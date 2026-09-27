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
        bool terrenoCargado;

        /* Candidatos por ranura, en orden de preferencia y relativos a GameData. */
        static readonly (string Slot, string[] Rutas)[] Detalles =
        {
            ("grass", new[] { @"Parallax_StockScatterTextures\PluginData\grassuv2.dds", @"CTTP\Textures\PluginData\gravel.dds" }),
            ("sand", new[] { @"CTTP\Textures\PluginData\sand.dds", @"CTTP\Textures\PluginData\beach.dds" }),
            ("rock", new[] { @"CTTP\Textures\PluginData\cliff.dds", @"CTTP\Textures\PluginData\rock.dds" }),
            ("snow", new[] { @"CTTP\Textures\PluginData\snow.dds", @"CTTP\Textures\PluginData\ice.dds" }),
        };

        async Task CargarTexturasDeTerreno()
        {
            if (terrenoCargado) return;
            terrenoCargado = true;
            string gd = FindGameData();
            if (gd == null) return;

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
            globe.DetGrass = Subir(leidas, "grass");
            globe.DetSand = Subir(leidas, "sand");
            globe.DetRock = Subir(leidas, "rock");
            globe.DetSnow = Subir(leidas, "snow");
            // las ranuras que falten se cubren con otra, para no dejar huecos negros
            globe.DetGrass ??= globe.DetSand ?? globe.DetRock;
            globe.DetSand ??= globe.DetGrass ?? globe.DetRock;
            globe.DetRock ??= globe.DetSand ?? globe.DetGrass;
            globe.DetSnow ??= globe.DetRock ?? globe.DetSand;
            RenderVueloInfo();
            RequestRender();
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
