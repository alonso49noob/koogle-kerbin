using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Herramientas de dibujo, órbita manual, marcadores, búsqueda, clic en el mapa e
       información del cuerpo. */
    public sealed partial class MainForm
    {
        string toolMode;
        readonly List<LatLon> toolPts = new();
        MapLine toolPreview;
        string ToolMode => toolMode;

        List<Marker> reference = new(), user = new();

        static readonly Dictionary<string, string> MarkerColors = new()
        {
            ["ksc"] = "#4ea3ff", ["base"] = "#7ee787", ["aeropuerto"] = "#ffb454",
            ["anomalia"] = "#c77dff", ["usuario"] = "#ff6b6b", ["otro"] = "#8a9bb0"
        };
        static string MarkerColor(string cat) => cat != null && MarkerColors.TryGetValue(cat, out var c) ? c : MarkerColors["otro"];

        /* ------------------------------------------------------------ herramientas */

        void SetToolMode(string mode)
        {
            if (toolMode == "measure" && mode != "measure") EndMeasure();
            toolMode = toolMode == mode ? null : mode;
            toolPts.Clear();
            RemovePreview();
            toolMeasure.Active = toolMode == "measure";
            toolFootprint.Active = toolMode == "footprint";
            toolHint.SetText(toolMode == "measure" ? "Haz clic para encadenar puntos. Esc para terminar."
                           : toolMode == "footprint" ? "Haz clic donde esté el satélite. Esc para terminar."
                           : "Ninguna herramienta activa.");
            surface.Cursor = toolMode != null ? Cursors.Cross : Cursors.Default;
            RequestRender();
        }

        void RemovePreview()
        {
            if (toolPreview != null) { toolLayer.Lines.Remove(toolPreview); toolPreview = null; }
        }

        void EndMeasure() => RemovePreview();

        void ClearTools()
        {
            toolLayer.Clear();
            toolPts.Clear();
            toolPreview = null;
            RequestRender();
        }

        void ToolMove(LatLon ll)
        {
            if (toolMode != "measure" || toolPts.Count == 0) return;
            var last = toolPts[^1];
            var pts = Geo.GreatCircle(last.Lat, last.Lon, ll.Lat, ll.Lon);
            if (toolPreview == null)
            {
                toolPreview = new MapLine(pts, ColorF.Hex("#4ea3ff", 0.8f), 1.5f, dashed: true);
                toolLayer.Lines.Add(toolPreview);
            }
            else toolPreview.SetPoints(pts);
            RequestRender();
        }

        void ToolClick(LatLon ll)
        {
            if (toolMode == "measure") AddMeasurePoint(ll);
            else if (toolMode == "footprint") AddFootprint(ll);
        }

        void AddMeasurePoint(LatLon p)
        {
            toolPts.Add(p);
            toolLayer.Dots.Add(new MapDot { Lat = p.Lat, Lon = p.Lon, Style = DotStyle.Point, Fill = ColorF.Hex("#4ea3ff") });
            if (toolPts.Count < 2) return;

            var a = toolPts[^2];
            var b = toolPts[^1];
            double d = Geo.Distance(a.Lat, a.Lon, b.Lat, b.Lon);
            double brg = Geo.Bearing(a.Lat, a.Lon, b.Lat, b.Lon);
            toolLayer.Lines.Insert(0, new MapLine(Geo.GreatCircle(a.Lat, a.Lon, b.Lat, b.Lon), ColorF.Hex("#4ea3ff", 0.95f), 2));

            double total = 0;
            for (int i = 1; i < toolPts.Count; i++)
                total += Geo.Distance(toolPts[i - 1].Lat, toolPts[i - 1].Lon, toolPts[i].Lat, toolPts[i].Lon);

            string label = Geo.FmtDist(d) + "  ·  " + Geo.F(brg, 1) + "°" + (toolPts.Count > 2 ? "   (total " + Geo.FmtDist(total) + ")" : "");
            toolLayer.Dots.Add(new MapDot { Lat = b.Lat, Lon = b.Lon, Style = DotStyle.LabelOnly, Label = label });
        }

        void AddFootprint(LatLon p)
        {
            double altKm = fpAlt.NumberOr(0);
            double r = Geo.HorizonRadius(altKm * 1000);
            toolLayer.Lines.Add(new MapLine(Geo.Circle(p.Lat, p.Lon, r), ColorF.Hex("#7ee787", 0.9f), 1.8f));
            toolLayer.Dots.Add(new MapDot { Lat = p.Lat, Lon = p.Lon, Style = DotStyle.Point, Fill = ColorF.Hex("#7ee787") });
            double halfAngle = r / Body.Radius * 180 / Math.PI;
            toolLayer.Dots.Add(new MapDot
            {
                Lat = p.Lat, Lon = p.Lon, Style = DotStyle.LabelOnly,
                Label = "h " + Geo.F(altKm, 0) + " km · horizonte " + Geo.FmtDist(r) + " (" + Geo.F(halfAngle, 1) + "° de arco)"
            });
        }

        /* ------------------------------------------------------------ órbita manual */

        void DrawOrbit()
        {
            var o = ManualOrbit.Compute(orbPe.NumberOr(0) * 1000, orbAp.NumberOr(0) * 1000, orbInc.NumberOr(0),
                                        orbLan.NumberOr(0), orbArgp.NumberOr(0), (int)Math.Truncate(orbN.NumberOr(1)));
            orbitLayer.Clear();
            lastTrack = o.Points;
            // dibujar una órbita a mano sustituye a la traza de la nave pinchada
            if (sv.Sel != null)
            {
                sv.Sel = null;
                sv.TrackPts = null;
                trackLayer.Clear();
                svList.Invalidate();
                ConstruirAnillos();
                if (HasVessels) RenderReloj();
            }
            PushTrack();
            orbitLayer.Lines.Add(MapLine.FromTrack(o.Points, ColorF.Hex("#c77dff", 0.9f), 2));
            var pe = o.Points[0];
            orbitLayer.Dots.Add(new MapDot { Lat = pe.Lat, Lon = pe.Lon, Style = DotStyle.Point, Fill = ColorF.Hex("#c77dff"), Label = "Pe  " + Geo.FmtDist(o.Pe) });

            var rows = new List<string>();
            void Row(string label, string value, string tail = "") => rows.Add(label.PadRight(14) + "<b>" + value + "</b>" + tail);
            Row("Periodo", Geo.FmtTime(o.T), "  (" + Geo.F(o.T, 0) + " s)");
            Row("Semieje a", Geo.F(o.A / 1000, 1) + " km");
            Row("Excentricidad", Geo.F(o.E, 4));
            Row("v en Pe / Ap", Geo.F(o.VPe, 0) + " / " + Geo.F(o.VAp, 0) + " m/s");
            Row("Deriva/vuelta", Geo.F(o.Drift, 2) + "°", " de longitud");
            Row("Lat. máxima", "±" + Geo.F(o.MaxLat, 1) + "°");
            Row("Horizonte Pe", Geo.FmtDist(o.FootprintPe));
            if (o.Synchronous) rows.Add("<b>Órbita síncrona</b>: la traza se repite sobre sí misma.");
            if (o.Suborbital) rows.Add("<b>Aviso</b>: el periapsis está bajo el nivel del mar → impacto.");
            else if (o.InAtmosphere) rows.Add("<b>Aviso</b>: el periapsis entra en atmósfera (&lt; 70 km) → frenará.");
            orbOut.SetText(string.Join("\n", rows));
            RequestRender();
        }

        void ClearOrbit()
        {
            orbitLayer.Clear();
            lastTrack = null;
            PushTrack();
            orbOut.SetText("");
            RequestRender();
        }

        /* ------------------------------------------------------------ marcadores */

        void InitMarkers()
        {
            reference = MapsCatalog.LoadLandmarks(Path.Combine(Store.DataDir, "landmarks.json"));
            user = Store.Load("markers.json", new List<Marker>());
            RenderMarkers();
        }

        IEnumerable<Marker> MarkersAll() => reference.Concat(user);

        void RenderMarkers()
        {
            markerLayer.Clear();
            foreach (var m in MarkersAll())
                markerLayer.Dots.Add(new MapDot { Lat = m.Lat, Lon = m.Lon, Style = DotStyle.Pin, Fill = ColorF.Hex(MarkerColor(m.Cat)), Tooltip = m.Name, Tag = m });
            mkList.SetItems(MarkersAll());
            SyncGlobe();
            RequestRender();
        }

        void SaveMarkers() => Store.Save("markers.json", user);

        void AddMarker(string name, double lat, double lon)
        {
            user.Add(new Marker { Id = "u" + Convert.ToString(DateTime.UtcNow.Ticks / 10000, 16), Name = name, Cat = "usuario", Lat = lat, Lon = lon });
            SaveMarkers();
            RenderMarkers();
        }

        void AddMarkerAtCenter()
        {
            var c = is3D ? globe.Center() : new LatLon(map.CenterLat, Geo.WrapLon(map.CenterLon));
            string name = InputBox.Ask(this, "Nombre del marcador:", "Punto " + (user.Count + 1));
            if (string.IsNullOrWhiteSpace(name)) return;
            AddMarker(name.Trim(), c.Lat, c.Lon);
        }

        void ExportMarkers()
        {
            using var dlg = new SaveFileDialog { FileName = "kerbin-marcadores.json", Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(new { items = user }, Store.Json)); }
            catch (Exception ex) { Flash("No se pudo guardar: " + ex.Message); }
        }

        void ImportMarkers()
        {
            using var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json|Todos|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(dlg.FileName));
                var root = doc.RootElement;
                var items = root.ValueKind == JsonValueKind.Array ? root
                          : root.TryGetProperty("items", out var it) ? it : default;
                int n = 0;
                if (items.ValueKind == JsonValueKind.Array)
                    foreach (var m in items.EnumerateArray())
                    {
                        if (!m.TryGetProperty("lat", out var la) || la.ValueKind != JsonValueKind.Number ||
                            !m.TryGetProperty("lon", out var lo) || lo.ValueKind != JsonValueKind.Number) continue;
                        string S(string k, string def) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString().Length > 0 ? v.GetString() : def;
                        user.Add(new Marker
                        {
                            Id = S("id", "i" + Convert.ToString(DateTime.UtcNow.Ticks / 10000 + n, 16)),
                            Name = S("name", "Sin nombre"), Cat = S("cat", "anomalia"),
                            Lat = la.GetDouble(), Lon = lo.GetDouble(),
                            Desc = S("desc", ""), Confianza = S("confianza", "")
                        });
                        n++;
                    }
                SaveMarkers();
                RenderMarkers();
                Flash("Importados " + n + " marcadores.");
            }
            catch (Exception ex) { Flash("Ese JSON no se pudo leer: " + ex.Message); }
        }

        void DrawMarkerRow(Graphics g, Rectangle r, object item, bool hover)
        {
            var m = (Marker)item;
            int x = r.X + Theme.S(7);
            DrawDot(g, Theme.Hex(MarkerColor(m.Cat)), x, r.Y + r.Height / 2, Theme.S(8));
            x += Theme.S(15);
            bool isUser = user.Contains(m);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, m.Name, Theme.UISmall, new Rectangle(x, r.Y, r.Right - x - Theme.S(26), r.Height), Theme.Fg, flags | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (isUser && hover)
                TextRenderer.DrawText(g, "×", Theme.Make("Segoe UI", 15), new Rectangle(r.Right - Theme.S(24), r.Y, Theme.S(20), r.Height), Theme.Danger, flags | TextFormatFlags.HorizontalCenter);
        }

        void MarkerRowClick(object item, Point pt, Rectangle rect)
        {
            var m = (Marker)item;
            if (user.Contains(m) && pt.X > rect.Width - Theme.S(26))
            {
                user.Remove(m);
                SaveMarkers();
                RenderMarkers();
                return;
            }
            FocusMarker(m);
        }

        void FocusMarker(Marker m)
        {
            if (is3D) globe.SetCenter(m.Lat, m.Lon);
            else map.SetView(m.Lat, m.Lon, Math.Max(map.Zoom, 6));
            ShowMarkerPopup(m);
            RequestRender();
        }

        void ShowMarkerPopup(Marker m)
        {
            string h = "<b>" + RichLabel.Esc(m.Name) + "</b>\n<m>" + Geo.FmtLat(m.Lat) + "  ·  " + Geo.FmtLon(m.Lon) + "</m>";
            if (!string.IsNullOrEmpty(m.Confianza)) h += "\n<m>confianza: " + RichLabel.Esc(m.Confianza) + "</m>";
            if (!string.IsNullOrEmpty(m.Desc)) h += "\n" + RichLabel.Esc(m.Desc);
            string coords = Geo.F(m.Lat, 6) + ", " + Geo.F(m.Lon, 6);
            ShowPopup(new LatLon(m.Lat, m.Lon), h, ("Copiar coordenadas", b => CopyText(coords, b)));
        }

        void CopyText(string text, DarkButton b)
        {
            try { Clipboard.SetText(text); b.Text = "Copiado"; }
            catch { b.Text = text; }
            popup.Relayout();
            PlacePopup();
        }

        /* ------------------------------------------------------------ clic en el mapa */

        void OnMapClick(LatLon ll)
        {
            if (toolMode != null) return;                 // una herramienta activa manda
            double lat = ll.Lat, lon = Geo.WrapLon(ll.Lon);
            if (skyPicking) { SkyPick(new LatLon(lat, lon)); return; }
            if (calibTarget != null) { CalibPick(new LatLon(lat, lon)); return; }

            string h = "<m>" + Geo.FmtLat(lat) + "  ·  " + Geo.FmtLon(lon) + "</m>\n<m>" + Geo.F(lat, 6) + ", " + Geo.F(lon, 6) + "</m>";
            var actions = new List<(string, Action<DarkButton>)>();
            if (Img("height") != null)
                h += "\n<m>altura " + Geo.FmtAlt(Img("height").Height_(lat, lon, state.HMin, state.HMax, state.LonOffset.Height)) + "</m>";
            if (Img("biome") != null)
            {
                string hex = Img("biome").BiomeHex(lat, lon, state.LonOffset.Biome);
                if (hex != null)
                {
                    string nm = BiomeName(hex);
                    h += "\n<m>bioma </m><sw=" + hex + "><m>" + RichLabel.Esc(nm ?? hex) + "</m>";
                    actions.Add((nm != null ? "Renombrar bioma" : "Poner nombre al bioma", b => RenameBiome(hex)));
                }
            }
            string coords = Geo.F(lat, 6) + ", " + Geo.F(lon, 6);
            actions.Add(("Copiar", b => CopyText(coords, b)));
            if (!isSky)
                actions.Add(("Ver el cielo desde aquí", b =>
                {
                    popup.Hide();
                    SetObserver(lat, lon, null);
                    SetViewMode("sky");
                }));
            actions.Add(("Marcador aquí", b =>
            {
                string name = InputBox.Ask(this, "Nombre del marcador:", "Punto " + (user.Count + 1));
                if (string.IsNullOrWhiteSpace(name)) return;
                AddMarker(name.Trim(), lat, lon);
                popup.Hide();
            }));
            ShowPopup(new LatLon(lat, lon), h, actions.ToArray());
        }

        /* ------------------------------------------------------------ búsqueda */

        sealed class SearchItem
        {
            public string Name, Sub;
            public double Lat, Lon;
            public Marker Marker;
        }

        void DoSearch(string q)
        {
            var items = new List<SearchItem>();
            var coord = Geo.ParseCoords(q);
            if (coord.HasValue)
                items.Add(new SearchItem
                {
                    Name = "Ir a " + Geo.FmtLat(coord.Value.Lat) + " · " + Geo.FmtLon(coord.Value.Lon),
                    Sub = Geo.F(coord.Value.Lat, 5) + ", " + Geo.F(coord.Value.Lon, 5),
                    Lat = coord.Value.Lat, Lon = coord.Value.Lon
                });
            string s = (q ?? "").Trim().ToLowerInvariant();
            if (s.Length > 0)
                foreach (var m in MarkersAll().Where(m => (m.Name ?? "").ToLowerInvariant().Contains(s)).Take(8))
                    items.Add(new SearchItem { Name = m.Name, Sub = Geo.F(m.Lat, 4) + ", " + Geo.F(m.Lon, 4), Marker = m, Lat = m.Lat, Lon = m.Lon });

            if (items.Count == 0) { Vis.Set(searchBox, false); return; }
            searchList.SetItems(items);
            LayoutOverlays();
            Vis.Set(searchBox, true);
            searchBox.BringToFront();
        }

        void PickFirstSearch()
        {
            if (Vis.Shown(searchBox) && searchList.Items.Count > 0) SearchRowClick(searchList.Items[0]);
        }

        void SearchRowClick(object item)
        {
            var it = (SearchItem)item;
            if (it.Marker != null) FocusMarker(it.Marker);
            else if (is3D) globe.SetCenter(it.Lat, it.Lon);
            else map.SetView(it.Lat, it.Lon, Math.Max(map.Zoom, 6));
            Vis.Set(searchBox, false);
            surface.Focus();
            RequestRender();
        }

        void DrawSearchRow(Graphics g, Rectangle r, object item, bool hover)
        {
            var it = (SearchItem)item;
            if (hover) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r);
            var c = hover ? Theme.OnAccent : Theme.Fg;
            int x = r.X + Theme.S(12);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, it.Name, Theme.UI, new Rectangle(x, r.Y + Theme.S(7), r.Width - Theme.S(24), Theme.UI.Height), c, flags);
            TextRenderer.DrawText(g, it.Sub, Theme.MonoSmall, new Rectangle(x, r.Y + Theme.S(8) + Theme.UI.Height, r.Width - Theme.S(24), Theme.MonoSmall.Height),
                Color.FromArgb(166, c), flags);
        }

        /* ------------------------------------------------------------ cuerpo */

        void RenderBodyInfo()
        {
            double g0 = Body.Mu / (Body.Radius * Body.Radius);
            double circ = 2 * Math.PI * Body.Radius;
            double vRot = circ / Body.SiderealDay;
            double vEsc = Math.Sqrt(2 * Body.Mu / Body.Radius);
            double vLow = Math.Sqrt(Body.Mu / (Body.Radius + 75000));
            bodyInfo.SetText(string.Join("\n",
                "Radio            <b>" + Geo.F(Body.Radius / 1000, 0) + " km</b>",
                "Circunf. ecuador <b>" + Geo.F(circ / 1000, 1) + " km</b>",
                "g en superficie  <b>" + Geo.F(g0, 3) + " m/s²</b>",
                "Día sidéreo      <b>" + Geo.FmtTime(Body.SiderealDay) + "</b>",
                "Día solar        <b>6 h exactas</b>",
                "v de rotación    <b>" + Geo.F(vRot, 1) + " m/s</b> en el ecuador",
                "Atmósfera hasta  <b>" + Geo.F(Body.Atmosphere / 1000, 0) + " km</b>",
                "Órbita síncrona  <b>" + Geo.F(Body.SynchronousAlt / 1000, 0) + " km</b> de altitud",
                "v órbita 75 km   <b>" + Geo.F(vLow, 0) + " m/s</b>",
                "v de escape      <b>" + Geo.F(vEsc, 0) + " m/s</b>",
                "SOI              <b>" + Geo.F(Body.Soi / 1000, 0) + " km</b>"));
        }
    }
}
