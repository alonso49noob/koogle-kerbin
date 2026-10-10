using System;
using System.IO;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;

namespace KerbinMaps.UI
{
    /* The game's own ground textures for the free camera.

       KSP paints nearby terrain with a few repeating textures (grass, sand, rock, snow) blended
       by slope and height. The same ones are used here: those of the Community Terrain Texture
       Pack, which is what Parallax and friends carry, and if they aren't there, whatever
       fallbacks come with the installation. With none, flight works the same but up close the
       ground looks smooth: the body's map doesn't give more at that distance. */
    public sealed partial class MainForm
    {

        /* Candidates per slot, in order of preference and relative to GameData. */
        static readonly (string Slot, string[] Rutas)[] Detalles =
        {
            ("grass", new[] { @"Parallax_StockScatterTextures\PluginData\grassuv2.dds", @"CTTP\Textures\PluginData\gravel.dds" }),
            ("sand", new[] { @"CTTP\Textures\PluginData\sand.dds", @"CTTP\Textures\PluginData\beach.dds" }),
            ("rock", new[] { @"CTTP\Textures\PluginData\cliff.dds", @"CTTP\Textures\PluginData\rock.dds" }),
            ("snow", new[] { @"CTTP\Textures\PluginData\snow.dds", @"CTTP\Textures\PluginData\ice.dds" }),
        };

        /* The CTTP ones work for every body: loaded once and kept. */
        Texture cttpGrass, cttpSand, cttpRock, cttpSnow;
        bool cttpLeido;

        /* The Parallax ones are per body: they change when the body changes. */
        Texture[] parallaxTex;
        string terrenoDe;
        string terrenoOrigen;                   // for the panel: where the textures come from

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
            if (cuerpo != Body.Name) return;                    // the body was changed in the meantime

            if (!glOk || !surface.MakeCurrent()) return;
            if (parallaxTex != null) { foreach (var t in parallaxTex) t?.Dispose(); parallaxTex = null; }

            if (px != null)
            {
                var d = new System.Collections.Generic.Dictionary<string, TextureFile>
                {
                    ["low"] = px.Low, ["mid"] = px.Mid, ["high"] = px.High, ["steep"] = px.Steep,
                    ["inf"] = px.Influence, ["disp"] = px.Displacement, ["occ"] = px.Occlusion,
                    ["bl"] = px.BumpLow, ["bm"] = px.BumpMid, ["bh"] = px.BumpHigh, ["bs"] = px.BumpSteep,
                };
                // if two slots share a texture, it's uploaded only once
                var subidas = new System.Collections.Generic.Dictionary<TextureFile, Texture>();
                Texture Una(string k) => d[k] == null ? null : subidas.TryGetValue(d[k], out var ya) ? ya : subidas[d[k]] = Subir(d, k);
                globe.DetGrass = Una("low");
                globe.DetSand = Una("mid");
                globe.DetSnow = Una("high");
                globe.DetRock = Una("steep");
                globe.PxInfluence = Una("inf");
                globe.PxDisplacement = Una("disp");
                globe.PxOcclusion = Una("occ");
                globe.PxBump = new[] { Una("bl"), Una("bm"), Una("bh"), Una("bs") };
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
                globe.PxInfluence = globe.PxDisplacement = globe.PxOcclusion = null;
                globe.PxBump = null;
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
            // missing slots are covered with another, so no black holes are left
            cttpGrass ??= cttpSand ?? cttpRock;
            cttpSand ??= cttpGrass ?? cttpRock;
            cttpRock ??= cttpSand ?? cttpGrass;
            cttpSnow ??= cttpRock ?? cttpSand;
        }

        /* The body's cloud map, from whatever cloud mods are installed. It's 16384x8192 and
           weighs 179 MB with its mipmaps: it's read from the 8192-wide level (43 MB on the GPU,
           5 km per texel), and up close the mod's detail texture fills it in. The speed at
           which the layer rotates comes from its clouds.cfg. */
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
            string rutaDet = Path.Combine(gd, "StockVolumetricClouds", "Clouds", "Textures", "PluginData", "detail1.dds");
            string cuerpo = Body.Name;

            var (leida, detalle, velocidad) = ruta == null ? (null, null, (double?)null) : await Task.Run(() =>
            {
                TextureFile t = null, d = null;
                try { t = TextureFile.LoadDdsLevel(ruta, 8192); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[nubes] " + ex.Message); }
                try { if (File.Exists(rutaDet)) d = TextureFile.LoadDdsLevel(rutaDet, 1024); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[nubes] detalle: " + ex.Message); }
                return (t, d, VelocidadNubes(gd, cuerpo));
            });

            if (!glOk || !surface.MakeCurrent()) return;
            globe.CloudTex?.Dispose();
            globe.CloudTex = leida == null ? null : Subir(new System.Collections.Generic.Dictionary<string, TextureFile> { ["n"] = leida }, "n");
            globe.CloudDetailTex?.Dispose();
            globe.CloudDetailTex = detalle == null ? null : Subir(new System.Collections.Generic.Dictionary<string, TextureFile> { ["d"] = detalle }, "d");
            globe.CloudSpeed = velocidad ?? 29.89;
            RenderVueloInfo();
            RequestRender();
        }

        /* The speed of the body's first layer in the cloud mod's clouds.cfg («speed =
           0,29.89,0» with speedMode = LinearSurface: m/s at the surface). */
        static double? VelocidadNubes(string gd, string cuerpo)
        {
            try
            {
                string cfg = Path.Combine(gd, "StockVolumetricClouds", "Clouds", "clouds.cfg");
                if (!File.Exists(cfg)) return null;
                var lineas = File.ReadAllLines(cfg);
                for (int i = 0; i < lineas.Length; i++)
                {
                    var l = lineas[i].Trim();
                    if (!l.StartsWith("body", StringComparison.Ordinal) || !l.EndsWith("= " + cuerpo, StringComparison.OrdinalIgnoreCase)) continue;
                    for (int j = i + 1; j < Math.Min(lineas.Length, i + 12); j++)
                    {
                        var s = lineas[j].Trim();
                        if (!s.StartsWith("speed ", StringComparison.Ordinal) && !s.StartsWith("speed=", StringComparison.Ordinal)) continue;
                        var v = s.Substring(s.IndexOf('=') + 1).Split(',');
                        if (v.Length == 3 && double.TryParse(v[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y))
                            return Math.Abs(y);
                    }
                    return null;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[nubes] cfg: " + ex.Message); }
            return null;
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
