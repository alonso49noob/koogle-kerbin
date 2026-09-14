using System;
using System.Collections.Generic;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Mapa plano equirectangular, el equivalente al Leaflet con EPSG:4326 de la web:
       a zoom z, 180° de latitud ocupan 256·2^z píxeles. El centro y el zoom son
       continuos; el mundo se repite a izquierda y derecha. */
    public sealed class MapView : IDisposable
    {
        public double CenterLat = MapConfig.InitialLat, CenterLon = MapConfig.InitialLon;
        public double Zoom = MapConfig.InitialZoom, TargetZoom = MapConfig.InitialZoom;
        public int W = 1, H = 1;
        public float S = 1;                       // píxeles de pantalla por píxel CSS

        // lo que se pinta, lo fija la ventana principal
        public string BaseKind = "grid";          // grid | image | xyz | none
        public Texture BaseTex;
        public double BaseOffset, BaseOpacity = 1;
        public TileLayer Tiles;
        public Texture BiomeTex;
        public double BiomeOffset, BiomeOpacity;
        public bool Grid = true;
        // la mitad del planeta de noche, con el punto subsolar
        public bool DayNight = true;
        public double SunLon;
        public readonly List<MapLayer> Layers = new();
        public MapDot Hover;
        public int TopLabelOffset = 52;           // bajo la barra superior, en píxeles CSS

        double anchorX, anchorY;
        bool zoomAnim;
        ShaderProgram imgProg;
        uint emptyVao;

        static readonly double[] Steps = { 30, 10, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01 };

        public double Ppd => 256.0 * Math.Pow(2, Zoom) / 180.0 * S;
        public int TileZoom => Math.Clamp((int)Math.Round(Zoom), MapConfig.MinZoom, MapConfig.MaxZoom);

        /* El zoom más alejado con el que los 180° de latitud aún llenan el alto de la
           ventana: más allá se verían franjas vacías por encima del polo norte y por
           debajo del sur. */
        public double MinZoomFit => Math.Min(MapConfig.MaxZoom, Math.Max(MapConfig.MinZoom, Math.Log2(H / (256.0 * S))));
        public bool Animating => zoomAnim;

        public static double StepForZoom(int z)
        {
            double degPerTile = 180 / Math.Pow(2, z);
            double target = degPerTile / 4;
            for (int i = Steps.Length - 1; i >= 0; i--) if (Steps[i] >= target) return Steps[i];
            return Steps[0];
        }

        public static string FmtDeg(double v, double step)
        {
            int dec = step < 0.1 ? 2 : step < 1 ? 1 : 0;
            if (Math.Abs(v) < 0.5 * Math.Pow(10, -dec)) v = 0;          // sin «-0°»
            return Geo.F(v, dec) + "°";
        }

        /* ------------------------------------------------------------ cámara */

        public (double x, double y) Project(double lat, double lon) =>
            (W / 2.0 + (lon - CenterLon) * Ppd, H / 2.0 - (lat - CenterLat) * Ppd);

        /* Proyecta en la copia del mundo más cercana al centro de la vista. */
        public (double x, double y) ProjectNear(double lat, double lon)
        {
            double dl = lon - CenterLon;
            dl -= 360 * Math.Round(dl / 360);
            return (W / 2.0 + dl * Ppd, H / 2.0 - (lat - CenterLat) * Ppd);
        }

        public LatLon Unproject(double x, double y) =>
            new LatLon(CenterLat - (y - H / 2.0) / Ppd, CenterLon + (x - W / 2.0) / Ppd);

        void Clamp()
        {
            double viewH = H / Ppd;
            if (viewH >= 180) CenterLat = 0;
            else CenterLat = Math.Clamp(CenterLat, -90 + viewH / 2, 90 - viewH / 2);
            CenterLon = Geo.WrapLon(CenterLon);
        }

        public void Resize(int w, int h, float s)
        {
            W = Math.Max(1, w); H = Math.Max(1, h); S = s;
            // al agrandar la ventana, el zoom mínimo sube
            double min = MinZoomFit;
            if (TargetZoom < min) TargetZoom = min;
            if (Zoom < min) Zoom = min;
            Clamp();
        }

        public void SetView(double lat, double lon, double zoom)
        {
            Zoom = TargetZoom = Math.Clamp(zoom, MinZoomFit, MapConfig.MaxZoom);
            zoomAnim = false;
            CenterLat = lat; CenterLon = lon;
            Clamp();
        }

        /* Recentra sin tocar el zoom ni cortar su animación (seguir a una nave). */
        public void CenterOn(double lat, double lon)
        {
            CenterLat = lat; CenterLon = lon;
            zoomAnim = false;
            Zoom = TargetZoom;
            Clamp();
        }

        public void Pan(double dx, double dy)
        {
            CenterLon -= dx / Ppd;
            CenterLat += dy / Ppd;
            Clamp();
        }

        /* La rueda va de nivel en nivel, como Leaflet, pero con el paso animado y
           manteniendo quieto el punto bajo el cursor. */
        public void ZoomAt(double delta, double x, double y)
        {
            /* Por niveles enteros, salvo el tope de alejarse, que depende del alto de la
               ventana: al volver a acercarse desde él se retoma el nivel entero. */
            double min = MinZoomFit;
            double next = Math.Round(TargetZoom + delta);
            if (delta > 0 && TargetZoom <= min + 1e-6) next = Math.Floor(min) + Math.Max(1, Math.Round(delta));
            TargetZoom = Math.Clamp(next, min, MapConfig.MaxZoom);
            anchorX = x; anchorY = y;
            zoomAnim = Math.Abs(TargetZoom - Zoom) > 1e-6;
        }

        public bool Animate(double dt)
        {
            if (!zoomAnim) return false;
            var ll = Unproject(anchorX, anchorY);
            double k = 1 - Math.Exp(-dt * 14);
            Zoom += (TargetZoom - Zoom) * k;
            if (Math.Abs(TargetZoom - Zoom) < 0.002) { Zoom = TargetZoom; zoomAnim = false; }
            CenterLon = ll.Lon - (anchorX - W / 2.0) / Ppd;
            CenterLat = ll.Lat + (anchorY - H / 2.0) / Ppd;
            Clamp();
            return true;
        }

        /* Lo que hay bajo el cursor: primero los sitios, que van por encima. */
        public MapDot HitTest(double x, double y)
        {
            for (int li = Layers.Count - 1; li >= 0; li--)
            {
                var layer = Layers[li];
                if (!layer.Visible) continue;
                for (int i = layer.Dots.Count - 1; i >= 0; i--)
                {
                    var d = layer.Dots[i];
                    if (d.Hidden || (d.Tooltip == null && d.Tag == null)) continue;
                    double r = (d.Style == DotStyle.Pin ? 8 : 6) * S;
                    foreach (double px in CopiesX(d.Lat, d.Lon, r))
                    {
                        var (_, py) = Project(d.Lat, 0);
                        if ((px - x) * (px - x) + (py - y) * (py - y) <= r * r) return d;
                    }
                }
            }
            return null;
        }

        IEnumerable<double> CopiesX(double lat, double lon, double margin)
        {
            var (x0, _) = ProjectNear(lat, lon);
            double step = 360 * Ppd;
            for (int k = -2; k <= 2; k++)
            {
                double x = x0 + k * step;
                if (x >= -margin && x <= W + margin) yield return x;
            }
        }

        /* ------------------------------------------------------------ render */

        const string ImgVS = @"#version 330 core
const vec2 P[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
void main() { gl_Position = vec4(P[gl_VertexID], 0.0, 1.0); }";

        /* Cada píxel calcula su lon/lat y lee la textura. La u no se envuelve: con
           REPEAT, las copias del mundo y el desfase salen solos y no hay costura. */
        const string ImgFS = @"#version 330 core
uniform vec2 uView;
uniform vec2 uCenter;
uniform float uPpd, uOff, uOpacity, uCell, uSunLon, uS;
uniform int uMode;
uniform sampler2D uTex;
out vec4 frag;
void main() {
  float px = gl_FragCoord.x;
  float py = uView.y - gl_FragCoord.y;
  float lon = uCenter.x + (px - uView.x * 0.5) / uPpd;
  float lat = uCenter.y - (py - uView.y * 0.5) / uPpd;
  if (lat > 90.0 || lat < -90.0) discard;
  if (uMode == 2) {
    /* Noche: el coseno del ángulo al punto subsolar (en el ecuador). Un margen de unos
       grados a cada lado del terminador hace de crepúsculo. */
    float dl = mod(lon - uSunLon + 540.0, 360.0) - 180.0;
    float mu = cos(radians(lat)) * cos(radians(dl));
    float night = 1.0 - smoothstep(-0.09, 0.07, mu);
    float dusk = exp(-mu * mu / 0.004) * 0.10;
    vec4 c = vec4(vec3(0.0, 0.012, 0.04) * night * 0.66, night * 0.66);
    c = vec4(c.rgb + vec3(1.0, 0.45, 0.15) * dusk, c.a + dusk * 0.2);
    // el Sol: un disco en el punto subsolar
    float d = length(vec2(dl, lat)) * uPpd;
    float rad = 7.0 * uS;
    float disc = 1.0 - smoothstep(rad - 1.0, rad + 0.5, d);
    float ring = (1.0 - smoothstep(rad + 0.5, rad + 2.0 * uS, d)) * (1.0 - disc);
    float glow = exp(-d / (10.0 * uS)) * 0.35 * (1.0 - disc);
    c = mix(c, vec4(0.0, 0.0, 0.0, 0.7), ring);
    c = vec4(c.rgb + vec3(1.0, 0.8, 0.35) * glow, max(c.a, glow));
    c = mix(c, vec4(1.0, 0.86, 0.42, 1.0), disc);
    frag = c;
    return;
  }
  if (uMode == 1) {
    vec3 c = vec3(13.0, 20.0, 29.0) / 255.0;
    float ix = floor((lon + 180.0) / uCell), iy = floor((90.0 - lat) / uCell);
    if (mod(ix + iy, 2.0) < 0.5) c = mix(c, vec3(1.0), 0.012);
    frag = vec4(c * uOpacity, uOpacity);
    return;
  }
  vec4 t = texture(uTex, vec2((lon + uOff + 180.0) / 360.0, (90.0 - lat) / 180.0));
  frag = vec4(t.rgb * t.a, t.a) * uOpacity;
}";

        void EnsureGl()
        {
            if (imgProg != null) return;
            imgProg = new ShaderProgram(ImgVS, ImgFS);
            emptyVao = GL.GenVertexArray();
        }

        void DrawImage(int mode, Texture tex, double off, double opacity)
        {
            if (opacity <= 0) return;
            imgProg.Use();
            imgProg.Vec2("uView", W, H);
            imgProg.Vec2("uCenter", CenterLon, CenterLat);
            imgProg.Float("uPpd", Ppd);
            imgProg.Float("uOff", off);
            imgProg.Float("uOpacity", opacity);
            imgProg.Float("uCell", 180 / Math.Pow(2, TileZoom) / 4);
            imgProg.Int("uMode", mode);
            imgProg.Float("uSunLon", SunLon);
            imgProg.Float("uS", S);
            GL.ActiveTexture(GL.TEXTURE0);
            GL.BindTexture(GL.TEXTURE_2D, tex?.Id ?? 0);
            imgProg.Int("uTex", 0);
            GL.BindVertexArray(emptyVao);
            GL.DrawArrays(GL.TRIANGLES, 0, 3);
            GL.BindVertexArray(0);
        }

        public void Render(Batch2D b, TextCache tc)
        {
            EnsureGl();
            GL.Viewport(0, 0, W, H);
            GL.ClearColor(7 / 255f, 11 / 255f, 17 / 255f, 1);
            GL.Clear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT);
            b.Begin(W, H);

            // mapa base
            switch (BaseKind)
            {
                case "grid":
                    DrawImage(1, null, 0, BaseOpacity);
                    DrawGraticule(b);
                    break;
                case "image":
                    if (BaseTex != null) DrawImage(0, BaseTex, BaseOffset, BaseOpacity);
                    break;
                case "xyz":
                    DrawTiles(b);
                    break;
            }
            // lo pendiente del lote va antes: los biomas se pintan directamente y lo taparían
            b.Flush();

            // biomas encima del relieve, debajo de trazas y marcadores
            if (BiomeTex != null && BiomeOpacity > 0) DrawImage(0, BiomeTex, BiomeOffset, BiomeOpacity);

            // la noche va sobre el terreno y debajo de la retícula, las trazas y los marcadores
            if (DayNight) DrawImage(2, null, 0, 1);

            if (Grid) DrawGraticule(b);

            foreach (var layer in Layers)
            {
                if (!layer.Visible) continue;
                foreach (var ln in layer.Lines) DrawLine(b, ln);
            }
            foreach (var layer in Layers)
            {
                if (!layer.Visible) continue;
                foreach (var d in layer.Dots) if (!d.Hidden) DrawDot(b, d);
            }
            foreach (var layer in Layers)
            {
                if (!layer.Visible) continue;
                foreach (var d in layer.Dots)
                    if (!d.Hidden && d.Label != null)
                        foreach (double x in CopiesX(d.Lat, d.Lon, 300 * S))
                            Chip(b, tc, d.Label, x + 12 * S, Project(d.Lat, 0).y);
            }

            if (Grid) DrawGridLabels(b, tc);

            if (Hover != null && !Hover.Hidden && Hover.Tooltip != null)
            {
                var (hx, hy) = ProjectNear(Hover.Lat, Hover.Lon);
                Chip(b, tc, Hover.Tooltip, hx + 12 * S, hy);
            }

            DrawScale(b, tc);
            DrawAttribution(b, tc);
            b.End();
        }

        (double lonMin, double lonMax, double latMin, double latMax) ViewBounds()
        {
            double hw = W / 2.0 / Ppd, hh = H / 2.0 / Ppd;
            return (CenterLon - hw, CenterLon + hw, CenterLat - hh, CenterLat + hh);
        }

        void DrawTiles(Batch2D b)
        {
            if (Tiles == null) return;
            Tiles.Pump();
            int z = TileZoom;
            int n = 1 << z;
            double deg = 180.0 / n;
            var (lonMin, lonMax, latMin, latMax) = ViewBounds();
            int x0 = (int)Math.Floor((lonMin + 180) / deg), x1 = (int)Math.Floor((lonMax + 180) / deg);
            int y0 = Math.Max(0, (int)Math.Floor((90 - latMax) / deg)), y1 = Math.Min(n - 1, (int)Math.Floor((90 - latMin) / deg));
            var tint = new ColorF(1, 1, 1, (float)BaseOpacity);
            for (int x = x0; x <= x1; x++)
            {
                int xi = ((x % (2 * n)) + 2 * n) % (2 * n);
                double lonW = x * deg - 180;
                for (int y = y0; y <= y1; y++)
                {
                    var tex = Tiles.Get(z, xi, y);
                    if (tex == null) continue;
                    var (sx, sy) = Project(90 - y * deg, lonW);
                    double size = deg * Ppd;
                    b.Image(tex, Math.Floor(sx), Math.Floor(sy), Math.Ceiling(size) + 1, Math.Ceiling(size) + 1, tint);
                }
            }
        }

        static bool IsMajor(double v)
        {
            double a = Math.Abs(v);
            return a < 1e-9 || Math.Abs(a - 90) < 1e-9 || Math.Abs(a - 180) < 1e-9;
        }

        void DrawGraticule(Batch2D b)
        {
            var minor = ColorF.Rgba(120, 160, 200, 0.28f);
            var major = ColorF.Rgba(150, 195, 240, 0.55f);
            double step = StepForZoom(TileZoom);
            double lw = Math.Max(1, Math.Round(S));
            var (lonMin, lonMax, latMin, latMax) = ViewBounds();

            double yTop = Math.Max(0, Project(90, 0).y), yBot = Math.Min(H, Project(-90, 0).y);
            long i0 = (long)Math.Ceiling(lonMin / step), i1 = (long)Math.Floor(lonMax / step);
            if (i1 - i0 < 4000)
                for (long i = i0; i <= i1; i++)
                {
                    double lon = i * step;
                    double x = Math.Round(Project(0, lon).x);
                    b.Rect(x, yTop, lw, yBot - yTop, IsMajor(Geo.WrapLon(lon)) ? major : minor);
                }

            long j0 = (long)Math.Ceiling(Math.Max(-90, latMin) / step), j1 = (long)Math.Floor(Math.Min(90, latMax) / step);
            for (long j = j0; j <= j1; j++)
            {
                double lat = j * step;
                double y = Math.Round(Project(lat, 0).y);
                b.Rect(0, y, W, lw, IsMajor(lat) ? major : minor);
            }
        }

        void DrawGridLabels(Batch2D b, TextCache tc)
        {
            int z = (int)Math.Round(Math.Clamp(Zoom, MapConfig.MinZoom, MapConfig.MaxZoom));
            double step = StepForZoom(z);
            var (lonMin, lonMax, latMin, latMax) = ViewBounds();
            var style = new TextStyle(UI.Theme.MonoFamily, 10 * S, false, unchecked((int)0xD9C8DEF5), false);

            void GridChip(string text, double cx, double cy)
            {
                var t = tc.Get(text, style);
                if (t == null) return;
                b.Rect(cx - 3 * S, cy - 8 * S, t.TextW + 6 * S, 16 * S, ColorF.Rgba(11, 16, 23, 0.78f));
                b.Text(t, cx, cy - t.TextH / 2.0);
            }

            if (lonMax - lonMin < 720)
            {
                long i0 = (long)Math.Ceiling(lonMin / step), i1 = (long)Math.Floor(lonMax / step);
                for (long i = i0; i <= i1 && i1 - i0 < 4000; i++)
                {
                    double lon = i * step;
                    double x = Project(0, lon).x;
                    if (x < 4 * S || x > W - 30 * S) continue;
                    GridChip(FmtDeg(Geo.WrapLon(lon), step), x + 4 * S, TopLabelOffset * S);
                }
            }

            double s0 = Math.Max(-90, latMin), n0 = Math.Min(90, latMax);
            for (long j = (long)Math.Ceiling(s0 / step); j * step <= n0; j++)
            {
                double lat = j * step;
                double y = Project(lat, 0).y;
                if (y < 12 * S || y > H - 12 * S) continue;
                GridChip(FmtDeg(lat, step), 8 * S, y);
            }
        }

        /* Rótulo con fondo, el .km-label de la web. */
        public void Chip(Batch2D b, TextCache tc, string text, double x, double cy)
        {
            var t = tc.Get(text, new TextStyle(UI.Theme.MonoFamily, 10.5f * S, false, unchecked((int)0xFFDBE6F2), false));
            if (t == null) return;
            double padX = 5 * S, padY = 2 * S;
            double w = t.TextW + padX * 2, h = t.TextH + padY * 2;
            double y = Math.Round(cy - h / 2);
            x = Math.Round(x);
            b.Rect(x, y, w, h, ColorF.Rgba(11, 16, 23, 0.85f));
            b.RectOutline(x, y, w, h, Math.Max(1, Math.Round(S)), ColorF.Hex("#263444"));
            b.Text(t, x + padX, y + padY);
        }

        void DrawLine(Batch2D b, MapLine ln)
        {
            var pts = ln.Pts;
            if (pts.Count < 2) return;
            var (lonMin, lonMax, _, _) = ViewBounds();
            int k0 = (int)Math.Ceiling((lonMin - ln.MaxLon) / 360), k1 = (int)Math.Floor((lonMax - ln.MinLon) / 360);
            double width = ln.Width * S, ppd = Ppd;
            double cx = W / 2.0, cy = H / 2.0;
            for (int k = k0; k <= k1; k++)
            {
                double off = k * 360 - CenterLon;
                double px = cx + (pts[0].Lon + off) * ppd, py = cy - (pts[0].Lat - CenterLat) * ppd;
                double fase = 0;
                for (int i = 1; i < pts.Count; i++)
                {
                    double x = cx + (pts[i].Lon + off) * ppd, y = cy - (pts[i].Lat - CenterLat) * ppd;
                    bool fuera = (px < -width && x < -width) || (px > W + width && x > W + width) ||
                                 (py < -width && y < -width) || (py > H + width && y > H + width);
                    if (!fuera)
                    {
                        if (ln.Dashed) b.DashedLine(px, py, x, y, width, ln.Color, 4 * S, 4 * S, ref fase);
                        else b.Line(px, py, x, y, width, ln.Color);
                    }
                    px = x; py = y;
                }
            }
        }

        void DrawDot(Batch2D b, MapDot d)
        {
            double y = Project(d.Lat, 0).y;
            if (y < -20 * S || y > H + 20 * S) return;
            foreach (double x in CopiesX(d.Lat, d.Lon, 20 * S))
            {
                switch (d.Style)
                {
                    case DotStyle.Pin:
                        // .km-pin: 11 px, borde blanco de 2 px y un filo oscuro alrededor
                        b.Circle(x, y, 7.5 * S, ColorF.Rgba(0, 0, 0, 0.25f));
                        b.Circle(x, y, 6.5 * S, ColorF.Rgba(0, 0, 0, 0.6f));
                        b.Circle(x, y, 5.5 * S, ColorF.White);
                        b.Circle(x, y, 3.5 * S, d.Fill);
                        break;
                    case DotStyle.Vessel:
                        b.Circle(x, y, 4.5 * S, ColorF.White);
                        b.Circle(x, y, 3.5 * S, d.Fill);
                        break;
                    case DotStyle.LabelOnly:
                        break;
                    default:
                        b.Circle(x, y, 4.25 * S, ColorF.White);
                        b.Circle(x, y, 2.75 * S, d.Fill);
                        break;
                }
            }
        }

        /* Escala métrica abajo a la izquierda, como L.control.scale: la distancia de
           180 px a la latitud del centro, redondeada a 1-2-3-5. */
        void DrawScale(Batch2D b, TextCache tc)
        {
            double maxW = 180 * S;
            double y = H / 2.0;
            var a = Unproject(0, y);
            var c = Unproject(maxW, y);
            double meters = Geo.Distance(a.Lat, a.Lon, c.Lat, c.Lon);
            if (!(meters > 0)) return;
            double pow10 = Math.Pow(10, Math.Floor(Math.Log10(meters)));
            double d = meters / pow10;
            d = d >= 10 ? 10 : d >= 5 ? 5 : d >= 3 ? 3 : d >= 2 ? 2 : 1;
            double nice = pow10 * d;
            double w = Math.Round(maxW * nice / meters);
            string label = nice < 1000 ? nice.ToString(Geo.Inv) + " m" : (nice / 1000).ToString(Geo.Inv) + " km";

            var t = tc.Get(label, new TextStyle(UI.Theme.MonoFamily, 11 * S, false, unchecked((int)0xFFDBE6F2), false));
            double h = (t?.TextH ?? 14) + 4 * S;
            double x = 10 * S, top = H - 10 * S - h;
            double bw = 2 * S;
            b.Rect(x, top, w, h, ColorF.Rgba(18, 26, 37, 0.9f));
            var line = ColorF.Hex("#8a9bb0");
            b.Rect(x, top, bw, h, line);
            b.Rect(x + w - bw, top, bw, h, line);
            b.Rect(x, top + h - bw, w, bw, line);
            if (t != null) b.Text(t, x + 5 * S, top + 1 * S);
        }

        void DrawAttribution(Batch2D b, TextCache tc)
        {
            var t = tc.Get("Visor no oficial · Kerbal Space Program es de Squad / Private Division",
                           new TextStyle("Segoe UI", 10.5f * S, false, unchecked((int)0xFF8A9BB0), false));
            if (t == null) return;
            double w = t.TextW + 10 * S, h = t.TextH + 2 * S;
            b.Rect(W - w, H - h, w, h, ColorF.Rgba(18, 26, 37, 0.85f));
            b.Text(t, W - w + 5 * S, H - h + 1 * S);
        }

        public void Dispose()
        {
            imgProg?.Dispose();
            GL.DeleteVertexArray(emptyVao);
        }
    }
}
