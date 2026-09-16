using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Imágenes del mapa: ranuras, capa base, biomas, catálogo de mapas, desfases de
       longitud y calibración de alturas. */
    public sealed partial class MainForm
    {
        static readonly string[] Slots = { "color", "biome", "height" };
        static readonly Dictionary<string, (string label, string kind, string slot, string url)> Bases = new()
        {
            ["grid"] = ("Retícula (sin imagen)", "placeholder", null, null),
            ["color"] = ("Color / satélite (tu PNG)", "image", "color", null),
            ["height"] = ("Altura (tu PNG)", "image", "height", null),
            ["tiles"] = ("Teselas locales ./tiles", "xyz", null, "tiles/{z}/{x}/{y}.png"),
            ["custom"] = ("Plantilla XYZ propia…", "xyz", null, "")
        };

        readonly Dictionary<string, ImageData> images = new();
        readonly Dictionary<string, SlotMeta> metas = new();
        readonly Dictionary<string, Texture> textures = new();
        readonly Dictionary<string, string> biomeNames = Store.Load("biomes.json", new Dictionary<string, string>());
        MapsCatalog catalog;
        TileLayer tileLayer;
        string calibTarget;
        (double lum, double alt)? calibA, calibB;

        ImageData Img(string slot) => images.GetValueOrDefault(slot);
        bool NoImages => Slots.All(s => Img(s) == null);

        string BiomeName(string hex) =>
            hex != null && biomeNames.TryGetValue(hex.ToLowerInvariant(), out var n) && !string.IsNullOrEmpty(n) ? n : null;

        /* ------------------------------------------------------------ texturas */

        void SetImage(string slot, ImageData img)
        {
            images[slot] = img;
            if (textures.TryGetValue(slot, out var old) && surface.MakeCurrent()) old.Dispose();
            textures.Remove(slot);
            if (img == null || !glOk || !surface.MakeCurrent()) return;
            // biomas y alturas se leen por color exacto: nada de interpolar
            textures[slot] = Texture.FromRgba(img.Rgba, img.Width, img.Height, slot == "color" ? TexFilter.Mipmap : TexFilter.Nearest, true);
        }

        void DisposeTextures()
        {
            foreach (var t in textures.Values) t.Dispose();
            textures.Clear();
            tileLayer?.Dispose();
            tileLayer = null;
        }

        /* Empuja al mapa 2D lo que haya cargado y los ajustes de capas. */
        void ApplyMapTextures()
        {
            var def = Bases.GetValueOrDefault(state.BaseId);
            map.BaseOpacity = state.Opacity;
            switch (def.kind)
            {
                case "image":
                    map.BaseKind = OnMapBody ? "image" : "grid";
                    map.BaseTex = MapTex(def.slot);
                    map.BaseOffset = state.LonOffset.Get(def.slot);
                    break;
                case "xyz":
                    map.BaseKind = OnMapBody ? "xyz" : "grid";
                    string url = state.BaseId == "custom" ? state.CustomUrl : def.url;
                    if (tileLayer == null || tileLayer.Template != url)
                    {
                        if (surface.MakeCurrent()) tileLayer?.Dispose();
                        tileLayer = new TileLayer(url);
                        tileLayer.TileArrived = () => { try { BeginInvoke(new Action(RequestRender)); } catch { } };
                    }
                    map.Tiles = tileLayer;
                    break;
                default:
                    map.BaseKind = "grid";
                    break;
            }
            bool biomeOn = state.BiomeOn && MapImg("biome") != null;
            map.BiomeTex = biomeOn ? textures.GetValueOrDefault("biome") : null;
            map.BiomeOffset = state.LonOffset.Biome;
            map.BiomeOpacity = biomeOn ? state.BiomeOpacity : 0;
            map.Grid = state.Grid;
            markerLayer.Visible = state.Landmarks && OnMapBody;
            RequestRender();
        }

        /* Lo mismo para el globo. Se llama tras cualquier cambio de imagen o de ajuste,
           para que las dos vistas no se separen. */
        void SyncGlobe()
        {
            globe.ColorTex = MapTex("color");
            globe.BiomeTex = MapTex("biome");
            globe.HeightTex = MapTex("height");
            globe.BiomeAmt = state.BiomeOn && MapImg("biome") != null ? state.BiomeOpacity : 0;
            globe.ColorOff = (int)state.LonOffset.Color;
            globe.BiomeOff = (int)state.LonOffset.Biome;
            globe.HeightOff = (int)state.LonOffset.Height;
            globe.HMin = state.HMin;
            globe.HMax = state.HMax;
            globe.Pins.Clear();
            if (OnMapBody)
                foreach (var m in MarkersAll())
                    globe.Pins.Add(new GlobePin { Lat = m.Lat, Lon = m.Lon, Name = m.Name, Color = ColorF.Hex(MarkerColor(m.Cat)), Tag = m });
            globe.Pins.Add(new GlobePin { Lat = state.ObsLat, Lon = state.ObsLon, Name = Lang.T("Observador"), Color = ColorF.Hex("#ffb454") });
            globe.Pins.AddRange(VesselPins());
            bool hasHeight = MapImg("height") != null;
            Vis.Set(reliefWrap, hasHeight);
            Vis.Set(reliefHint, !hasHeight);
            RequestRender();
        }

        /* ------------------------------------------------------------ capa base */

        void SetBase(string id, bool silent = false)
        {
            if (id == null || !Bases.ContainsKey(id)) id = "grid";
            var def = Bases[id];
            bool ok = def.kind switch
            {
                "image" => Img(def.slot) != null,
                "xyz" => !string.IsNullOrEmpty(id == "custom" ? state.CustomUrl : def.url),
                _ => true
            };
            if (!ok)
            {
                if (!silent && def.kind == "image") Flash("No has cargado todavía la imagen de «" + def.label + "».");
                id = "grid";
            }
            state.BaseId = id;
            baseCombo.SelectedId = id;
            Vis.Set(customUrlWrap, id == "custom");
            ApplyMapTextures();
            UpdateBanner();
            SaveSettings();
        }

        void ApplyCustomUrl()
        {
            state.CustomUrl = customUrl.Text.Trim();
            SaveSettings();
            SetBase("custom");
        }

        void UpdateBanner()
        {
            if (flashTimer.Enabled) return;
            bool show = !state.BannerDismissed && NoImages && state.BaseId == "grid";
            if (show)
                ShowBannerMessage("<b>Sin imágenes del mapa.</b> Estás viendo la retícula de referencia. Suelta un PNG " +
                                  "equirectangular de Kerbin en «Datos del mapa» para verlo de verdad.");
            else Vis.Set(banner, false);
        }

        void SetOpacity(double v)
        {
            state.Opacity = v;
            opHeader.Value = (int)Math.Round(v * 100) + "%";
            ApplyMapTextures();
            SaveSettings();
        }

        void SetBiomeOn(bool on, bool touched)
        {
            state.BiomeOn = on;
            if (touched) state.BiomeTouched = true;
            SyncBiomeLayer();
            SaveSettings();
        }

        void SyncBiomeLayer()
        {
            bool want = state.BiomeOn && Img("biome") != null;
            chkBiome.SetSilently(state.BiomeOn);
            chkBiome.Enabled = Img("biome") != null;
            Vis.Set(biomeOpWrap, want);
            ApplyMapTextures();
            SyncGlobe();
        }

        void SetBiomeOpacity(double v)
        {
            state.BiomeOpacity = v;
            biomeOpHeader.Value = (int)Math.Round(v * 100) + "%";
            ApplyMapTextures();
            SyncGlobe();
            SaveSettings();
        }

        void SetGrid(bool on) { state.Grid = on; ApplyMapTextures(); SaveSettings(); }
        void SetLandmarks(bool on) { state.Landmarks = on; ApplyMapTextures(); SaveSettings(); }

        /* ------------------------------------------------------------ biomas */

        void ScanBiomes()
        {
            var img = Img("biome");
            if (img == null) { Flash("Carga antes un mapa de biomas."); return; }
            var res = img.Palette(1024, BiomeConfig.MinAreaPct);
            if (res == null) { Flash("No se pudo leer el mapa de biomas."); return; }
            int named = res.Colors.Count(c => BiomeName(c.Hex) != null);
            string txt = Lang.F("{0} colores · {1} con nombre", res.Colors.Count, named) + "\n" +
                         Lang.F("imagen {0}", res.Native) + (res.Sampled != res.Native ? Lang.F(" (leída a {0})", res.Sampled) : "");
            if (res.DroppedCount > 0)
                txt += "\n" + Lang.F("{0} colores sueltos ignorados ({1}% del total): son el dentado de los bordes.",
                    res.DroppedCount, Geo.F(res.DroppedPct, 2));
            biomeSummary.SetText(RichLabel.Esc(txt));
            biomeLegend.SetItems(res.Colors);
        }

        void DrawBiomeRow(Graphics g, Rectangle r, object item, bool hover)
        {
            var e = (PaletteEntry)item;
            int sw = Theme.S(14), x = r.X + Theme.S(4);
            var swr = new RectangleF(x, r.Y + (r.Height - sw) / 2f, sw, sw);
            Theme.FillRound(g, Theme.Hex(e.Hex), Color.FromArgb(56, 255, 255, 255), swr, Theme.Sf(3));
            x += sw + Theme.S(7);
            string name = BiomeName(e.Hex);
            string pct = Geo.F(e.Pct, 1) + "%";
            int pw = Theme.S(44);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, name ?? e.Hex, name != null ? Theme.UISmall : Theme.MonoSmall,
                new Rectangle(x, r.Y, r.Right - x - pw - Theme.S(6), r.Height), name != null ? Theme.Fg : Color.FromArgb(140, Theme.FgDim), flags);
            TextRenderer.DrawText(g, pct, Theme.MonoSmall, new Rectangle(r.Right - pw - Theme.S(4), r.Y, pw, r.Height), Theme.FgDim, flags | TextFormatFlags.Right);
        }

        void SetBiomeName(string hex, string name)
        {
            biomeNames[hex.ToLowerInvariant()] = name;
            Store.Save("biomes.json", biomeNames);
        }

        void RenameBiome(string hex)
        {
            string name = InputBox.Ask(this, "Nombre para el bioma " + hex + ":", BiomeName(hex) ?? "");
            if (name == null) return;
            SetBiomeName(hex, name.Trim());
            if (Img("biome") != null) ScanBiomes();
            popup.Hide();
        }

        void ExportBiomes()
        {
            using var dlg = new SaveFileDialog { FileName = "kerbin-biomas.json", Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(biomeNames, Store.Json)); }
            catch (Exception ex) { Flash("No se pudo guardar: " + ex.Message); }
        }

        void ImportBiomes()
        {
            using var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json|Todos|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(dlg.FileName));
                int n = 0;
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString().Length > 0)
                    {
                        biomeNames[p.Name.ToLowerInvariant()] = p.Value.GetString();
                        n++;
                    }
                Store.Save("biomes.json", biomeNames);
                Flash("Importados " + n + " nombres de bioma.");
                if (Img("biome") != null) ScanBiomes();
            }
            catch (Exception ex) { Flash("Ese JSON no se pudo leer: " + ex.Message); }
        }

        /* ------------------------------------------------------------ desfases */

        void ApplyOffset(string slot, double deg)
        {
            double v = ((deg % 360) + 360) % 360;
            state.LonOffset.Set(slot, v);
            state.OffsetTouched = true;            // tu ajuste manda sobre el de maps.json
            SaveSettings();
            slotRows[slot].Offset.SetNumber(v);
            ApplyMapTextures();
            SyncGlobe();
        }

        /* Busca el giro que hace encajar el color con el bioma comparando la silueta de
           los continentes. Si el mejor encaje no destaca sobre el promedio, no son el
           mismo planeta (o una no es equirectangular) y no hay nada que girar. */
        void AlignColorToBiome()
        {
            if (Img("color") == null || Img("biome") == null)
            {
                Flash("Hacen falta los dos mapas, color y bioma, para poder compararlos.");
                return;
            }
            var r = ImageData.DetectOffset(Img("color"), Img("biome"), (int)state.LonOffset.Biome);
            if (!ImageData.IsClearMatch(r))
            {
                Flash("No encajan a ninguna longitud (mejor coincidencia " + Geo.F(r.Pct, 0) + "%, promedio " + Geo.F(r.Media, 0) +
                      "%). ¿Seguro que los dos mapas son de Kerbin y equirectangulares 2:1?");
                return;
            }
            ApplyOffset("color", r.Shift);
            Flash("Mapa de color girado " + r.Shift + "° en longitud: ahora coincide con el bioma en un " + Geo.F(r.Pct, 1) +
                  "% (antes del giro, el promedio era " + Geo.F(r.Media, 0) + "%).");
        }

        /* ------------------------------------------------------------ catálogo */

        void LoadCatalog()
        {
            catalog = MapsCatalog.Load(Path.Combine(Store.DataDir, "maps.json"));
            if (catalog == null) return;
            presetCombo.SetItems(catalog.Presets.Select(p => (p.Id, p.Nombre ?? p.Id)));
            string sel = state.PresetId ?? catalog.Predeterminado ?? catalog.Presets[0].Id;
            presetCombo.SelectedId = catalog.Find(sel) != null ? sel : catalog.Presets[0].Id;
            Vis.Set(presetWrap, true);
            DescribePreset(presetCombo.SelectedId);
        }

        void DescribePreset(string id)
        {
            var p = catalog?.Find(id);
            if (p == null) return;
            var lines = new List<string>();
            foreach (var slot in Slots)
            {
                var spec = p.Get(slot);
                if (spec == null) continue;
                lines.Add(slot.PadRight(7) + spec.File + (spec.Auto ? Lang.T("  (giro automático)") : spec.LonOffset != 0 ? "  (" + spec.LonOffset + "°)" : ""));
            }
            if (p.Fuente != null) lines.Add("\n" + Lang.T("fuente: ") + p.Fuente);
            presetNote.SetText(RichLabel.Esc(string.Join("\n", lines)));
        }

        static Task<ImageData> DecodeFile(string path) => Task.Run(() => ImageData.Decode(File.ReadAllBytes(path)));

        /* Carga un preset entero. El bioma va primero a propósito: es la referencia
           contra la que se mide el giro de los mapas marcados como "auto". */
        async Task<bool> LoadPresetAsync(string id, bool silent)
        {
            var p = catalog?.Find(id);
            if (p == null) return false;
            var faltan = new List<string>();
            var autos = new List<string>();
            UseWaitCursor = true;
            try
            {
                foreach (var slot in new[] { "biome", "color", "height" })
                {
                    var spec = p.Get(slot);
                    if (spec?.File == null) continue;
                    string path = Path.Combine(Store.DataDir, spec.File);
                    try
                    {
                        if (!File.Exists(path)) { faltan.Add(spec.File); continue; }
                        var img = await DecodeFile(path);
                        SetImage(slot, img);
                        metas[slot] = new SlotMeta { Name = "data/" + spec.File, W = img.Width, H = img.Height, FromDisk = true };
                        if (spec.Auto) autos.Add(slot);
                        else state.LonOffset.Set(slot, ((spec.LonOffset % 360) + 360) % 360);
                        /* Si el preset sabe a qué metros corresponden el gris 0 y el 255 (los
                           deslizadores de SCANsat), no hay nada que calibrar. */
                        if (slot == "height" && spec.HMin.HasValue && spec.HMax.HasValue)
                        {
                            state.HMin = spec.HMin.Value; state.HMax = spec.HMax.Value;
                            hMinBox.SetNumber(state.HMin); hMaxBox.SetNumber(state.HMax);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[preset] " + spec.File + ": " + ex.Message);
                        faltan.Add(spec.File);
                    }
                }

                var medidos = new List<string>();
                foreach (var slot in autos)
                {
                    if (slot == "biome" || Img("biome") == null) { state.LonOffset.Set(slot, 0); continue; }
                    var r = ImageData.DetectOffset(Img(slot), Img("biome"), (int)state.LonOffset.Biome);
                    if (ImageData.IsClearMatch(r))
                    {
                        state.LonOffset.Set(slot, r.Shift);
                        medidos.Add(slot + " " + r.Shift + "° (" + Geo.F(r.Pct, 1) + "%)");
                    }
                    else
                    {
                        state.LonOffset.Set(slot, 0);
                        medidos.Add(slot + " sin giro claro, se deja en 0°");
                    }
                }

                state.PresetId = id;
                if (Img("color") != null) state.BaseId = "color";
                if (Img("biome") != null && !state.BiomeTouched) state.BiomeOn = true;
                SaveSettings();
                RenderSlots();
                SetBase(state.BaseId, true);
                SyncBiomeLayer();
                if (Img("biome") != null) ScanBiomes();

                if (!silent)
                {
                    var partes = new List<string>();
                    if (medidos.Count > 0) partes.Add("Giro medido: " + string.Join("; ", medidos) + ".");
                    if (faltan.Count > 0) partes.Add("No están en data/: " + string.Join(", ", faltan) + ".");
                    Flash(partes.Count > 0 ? string.Join(" ", partes) : "Mapa cargado.");
                }
                return faltan.Count == 0;
            }
            finally { UseWaitCursor = false; }
        }

        /* ------------------------------------------------------------ ranuras */

        const string ImageFilter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Todos|*.*";

        void PickImageFiles()
        {
            using var dlg = new OpenFileDialog { Filter = ImageFilter, Multiselect = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) _ = OpenFiles(dlg.FileNames);
        }

        async Task PickSlotFile(string slot)
        {
            using var dlg = new OpenFileDialog { Filter = ImageFilter };
            if (dlg.ShowDialog(this) == DialogResult.OK) await LoadImageFile(slot, dlg.FileName);
        }

        /* Adivina la ranura por el nombre del fichero; los exportadores de KSP suelen
           llamarlos Kerbin_Color / Kerbin_Height / Kerbin_Biome. */
        static string GuessSlot(string name)
        {
            string n = name.ToLowerInvariant();
            if (Regex.IsMatch(n, @"height|altura|elev|terrain|_h\b")) return "height";
            if (Regex.IsMatch(n, @"biome|bioma")) return "biome";
            return "color";
        }

        async Task LoadImageFile(string slot, string path)
        {
            byte[] bytes;
            ImageData img;
            string file = Path.GetFileName(path);
            UseWaitCursor = true;
            try
            {
                bytes = await File.ReadAllBytesAsync(path);
                img = await Task.Run(() => ImageData.Decode(bytes));
            }
            catch (Exception ex)
            {
                Flash("No se pudo leer esa imagen: " + ex.Message);
                return;
            }
            finally { UseWaitCursor = false; }

            SetImage(slot, img);
            metas[slot] = new SlotMeta { Name = file, W = img.Width, H = img.Height, Size = bytes.Length, Ext = Path.GetExtension(path) };

            /* El desfase pertenece a la imagen anterior, no a esta: se reinicia, y si hay
               un bioma con el que comparar se mide el de verdad. */
            int previo = (int)state.LonOffset.Get(slot);
            state.LonOffset.Set(slot, 0);
            OffsetMatch? medido = null;
            if (slot == "color" && Img("biome") != null)
            {
                var r = ImageData.DetectOffset(img, Img("biome"), (int)state.LonOffset.Biome);
                if (ImageData.IsClearMatch(r)) { state.LonOffset.Set(slot, r.Shift); medido = r; }
            }
            SaveSettings();
            RenderSlots();

            bool guardado = true;
            try { Store.PutImage(slot, bytes, metas[slot]); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[slots] " + ex.Message);
                guardado = false;
            }

            if (slot == "height")
            {
                double? sat = img.Saturation();
                if (sat > 0.15)
                    Flash("Ojo: «" + file + "» tiene mucho color (saturación " + Geo.F(sat.Value * 100, 0) + "%). Parece un mapa de " +
                          "elevación con paleta, no un gris. La sonda traduce luminancia a metros, y en esas paletas el amarillo de " +
                          "media ladera brilla más que el rojo de la cumbre: las cimas saldrían hundidas. Reexpórtalo en escala de grises.");
            }
            double ratio = (double)img.Width / img.Height;
            if (Math.Abs(ratio - 2) > 0.02)
                Flash("Ojo: «" + file + "» es " + img.Width + "×" + img.Height + " (proporción " + Geo.F(ratio, 2) +
                      ":1). Una equirectangular debería ser 2:1, o saldrá deformada.");

            if (slot == "biome")
            {
                state.BiomeOn = true;             // si acabas de cargarlo, querrás verlo
                state.BiomeTouched = true;
                SyncBiomeLayer();
                ScanBiomes();
                SaveSettings();
            }
            else if (slot == "color" && (state.BaseId == "grid" || state.BaseId == "color")) SetBase("color");
            else if (state.BaseId == slot) SetBase(slot);
            UpdateBanner();

            if (!guardado)
                Flash("El mapa está cargado, pero no se pudo guardar: tendrás que volver a cargarlo la próxima vez. " +
                      "Si lo dejas en data/ se carga solo y te ahorras el problema.");
            else if (medido.HasValue)
                Flash("Giro medido contra el mapa de biomas: " + medido.Value.Shift + "° (" + Geo.F(medido.Value.Pct, 1) + "% de coincidencia).");
            else if (previo != 0)
                Flash("Desfase de longitud reiniciado a 0°: el de " + previo + "° era del mapa anterior. Si este no cae donde " +
                      "debe, usa «Alinear color con el bioma» o ajusta los grados a mano.");
        }

        void ClearSlot(string slot)
        {
            bool wasDisk = metas.GetValueOrDefault(slot)?.FromDisk == true;
            SetImage(slot, null);
            metas.Remove(slot);
            try { Store.DelImage(slot); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[slots] " + ex.Message); }
            RenderSlots();
            if (slot == "biome")
            {
                SyncBiomeLayer();
                biomeLegend.SetItems(Array.Empty<object>());
                biomeSummary.SetText("");
            }
            if (state.BaseId == slot) SetBase("grid");
            ApplyMapTextures();
            UpdateBanner();
            if (wasDisk)
                Flash("Quitado de la vista. El fichero sigue en data/: para que no vuelva a cargarse al abrir el visor, muévelo o renómbralo.");
        }

        async Task RestoreSlots()
        {
            foreach (var slot in new[] { "color", "height", "biome" })
            {
                try
                {
                    var rec = Store.GetImage(slot);
                    if (rec == null) continue;
                    var img = await Task.Run(() => ImageData.Decode(rec.Value.bytes));
                    SetImage(slot, img);
                    metas[slot] = rec.Value.meta ?? new SlotMeta { Name = "(guardado)", W = img.Width, H = img.Height };
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[slots] no se pudo restaurar " + slot + ": " + ex.Message);
                }
            }
            RenderSlots();
        }

        /* Si dejas los PNG en data/ con uno de estos nombres, se cargan solos. */
        static readonly Dictionary<string, string[]> DiskCandidates = new()
        {
            ["color"] = new[] { "Kerbin_Color.png", "kerbin_color.png", "color.png" },
            ["height"] = new[] { "Kerbin_Height.png", "kerbin_height.png", "height.png" },
            ["biome"] = new[] { "Kerbin_Biome.png", "kerbin_biome.png", "biome.png" }
        };

        async Task DiscoverDiskMaps()
        {
            foreach (var (slot, names) in DiskCandidates)
            {
                if (Img(slot) != null) continue;                 // lo ya cargado manda
                foreach (var name in names)
                {
                    string path = Path.Combine(Store.DataDir, name);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var img = await DecodeFile(path);
                        SetImage(slot, img);
                        metas[slot] = new SlotMeta { Name = "data/" + name, W = img.Width, H = img.Height, FromDisk = true };
                        break;
                    }
                    catch { /* ilegible: se prueba el siguiente nombre */ }
                }
            }
            RenderSlots();
        }

        void RenderSlots()
        {
            foreach (var slot in Slots)
            {
                var m = metas.GetValueOrDefault(slot);
                var row = slotRows[slot];
                row.SetState(m != null, m != null ? m.W + "×" + m.H : "vacío", m?.Name);
                if (!row.Offset.Inner.Focused) row.Offset.SetNumber((int)state.LonOffset.Get(slot));
            }
            UpdateHud(null);
            ApplyMapTextures();
            SyncGlobe();
        }

        /* ------------------------------------------------------------ calibración */

        void CalibToggle(string which)
        {
            if (Img("height") == null) { Flash("Carga antes un mapa de alturas."); return; }
            calibTarget = calibTarget == which ? null : which;
            calA.Active = calibTarget == "a";
            calB.Active = calibTarget == "b";
            surface.Cursor = calibTarget != null ? Cursors.Cross : Cursors.Default;
        }

        void CalibReset()
        {
            calibA = calibB = null;
            calibTarget = null;
            calA.Active = calB.Active = false;
            surface.Cursor = Cursors.Default;
            calOut.SetText("");
        }

        void CalibPick(LatLon ll)
        {
            string which = calibTarget;
            calibTarget = null;
            calA.Active = calB.Active = false;
            surface.Cursor = Cursors.Default;
            var img = Img("height");
            if (img == null) { Flash("Ahí no hay píxel que leer."); return; }
            img.Sample(ll.Lat, ll.Lon, state.LonOffset.Height, out var r, out var g, out var b, out _);
            double lum = ImageData.Luminance(r, g, b);
            string txt = InputBox.Ask(this, "Altitud real de ese punto, en metros (gris " + Geo.F(lum * 255, 0) + "):", "0");
            if (txt == null) return;
            if (!double.TryParse(txt.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, Geo.Inv, out double alt) || !double.IsFinite(alt))
            {
                Flash("Eso no es un número.");
                return;
            }
            if (which == "a") calibA = (lum, alt); else calibB = (lum, alt);
            ApplyCalibration();
        }

        void ApplyCalibration()
        {
            var lines = new List<string>();
            if (calibA.HasValue) lines.Add("A  gris " + Geo.F(calibA.Value.lum * 255, 0).PadLeft(3) + "  →  " + calibA.Value.alt.ToString(Geo.Inv) + " m");
            if (calibB.HasValue) lines.Add("B  gris " + Geo.F(calibB.Value.lum * 255, 0).PadLeft(3) + "  →  " + calibB.Value.alt.ToString(Geo.Inv) + " m");
            if (calibA.HasValue && calibB.HasValue)
            {
                var (la, aa) = calibA.Value;
                var (lb, ab) = calibB.Value;
                if (Math.Abs(la - lb) < 1.0 / 255)
                    lines.Add("Los dos puntos tienen el mismo gris: elige uno más alto o más bajo.");
                else
                {
                    double span = (ab - aa) / (lb - la);
                    double min = aa - la * span;
                    state.HMin = Math.Round(min);
                    state.HMax = Math.Round(min + span);
                    hMinBox.SetNumber(state.HMin);
                    hMaxBox.SetNumber(state.HMax);
                    SaveSettings();
                    SyncGlobe();
                    lines.Add("→ gris 0 = " + state.HMin + " m, gris 255 = " + state.HMax + " m");
                }
            }
            calOut.SetText(RichLabel.Esc(string.Join("\n", lines)));
        }

        /* ------------------------------------------------------------ arranque */

        /* Ninguno de estos pasos puede impedir que el mapa llegue a dibujarse: si falta
           un fichero o falla el almacenamiento, se sigue sin él. */
        async Task StartupAsync()
        {
            if (!glOk) Flash(surface.Error ?? glError ?? "No se pudo arrancar OpenGL.");
            try { InitMarkers(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[inicio] marcadores: " + ex.Message); }
            try { await RestoreSlots(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[inicio] imágenes: " + ex.Message); }
            try { LoadCatalog(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[inicio] catálogo: " + ex.Message); }

            /* Primera vez: se carga el preset predeterminado. Si ya tenías algo guardado,
               se respeta y no se toca nada. */
            if (NoImages && catalog != null)
                try { await LoadPresetAsync(state.PresetId ?? catalog.Predeterminado, true); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[inicio] preset: " + ex.Message); }
            try { await DiscoverDiskMaps(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[inicio] data/: " + ex.Message); }

            if (state.BaseId == "grid" && Img("color") != null) state.BaseId = "color";
            SetBase(state.BaseId, true);
            if (Img("biome") != null && !state.BiomeTouched) state.BiomeOn = true;
            SyncBiomeLayer();
            if (Img("biome") != null) ScanBiomes();
            SetViewMode(state.ViewMode ?? (state.View3D ? "3d" : "2d"));

            // el sistema solar de la instalación de KSP, antes de la partida: sus naves pueden orbitar cuerpos de un pack
            try { await CargarSistemaSolar(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[inicio] sistema solar: " + ex.Message); }

            // la partida de la última vez, salvo que se abra otra desde la línea de órdenes
            bool abreSfs = false;
            foreach (var a in startArgs)
                if (File.Exists(a) && Path.GetExtension(a).ToLowerInvariant() is ".sfs" or ".loadmeta") abreSfs = true;
            if (!abreSfs && File.Exists(Store.SaveCopyPath))
                try { await CargarSave(Store.SaveCopyPath, restoring: true); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[inicio] partida: " + ex.Message); }

            foreach (var a in startArgs)
                if (File.Exists(a)) await OpenFiles(new[] { a });
            RequestRender();
        }
    }
}
