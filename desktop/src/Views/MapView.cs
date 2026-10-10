using System;
using System.Collections.Generic;
using System.Drawing;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Flat equirectangular map, the equivalent of the web's Leaflet with EPSG:4326: at zoom z,
       180° of latitude take 256·2^z pixels. Center and zoom are continuous; the world repeats
       left and right. */
    public sealed class MapView : IDisposable
    {
        public double CenterLat = MapConfig.InitialLat, CenterLon = MapConfig.InitialLon;
        public double Zoom = MapConfig.InitialZoom, TargetZoom = MapConfig.InitialZoom;
        public int W = 1, H = 1;
        public float S = 1;                       // screen pixels per CSS pixel

        // what gets painted, set by the main window
        public string BaseKind = "grid";          // grid | image | xyz | none
        public Texture BaseTex;
        public double BaseOffset, BaseOpacity = 1;
        public TileLayer Tiles;
        public Texture BiomeTex;
        public double BiomeOffset, BiomeOpacity;
        public Texture ScanTex;                   // SCANsat coverage: what's unscanned, covered
        public double ScanOpacity;
        public Texture AltTex;                    // altimetry filter over the height map
        public double AltOffset, AltOpacity, AltHMin, AltHMax, AltMin, AltMax;
        public bool Grid = true;
        public float[] Tint = { 0.45f, 0.45f, 0.45f };   // the body's color, for the background without an image
        // the night half of the planet, with the subsolar point
        public bool DayNight = true;
        public double SunLat, SunLon;
        public readonly List<MapLayer> Layers = new();
        /* The political map (see FaccionesGlsl): the grid with its colors and, for clipping at
           the coast, the height map and the color map with their offsets. */
        public Texture FacTex;
        public float[] FacColores;
        public double FacRelleno = 0.45;
        public bool FacConMar;
        public Texture FacAltura, FacColor;
        public double FacAlturaOff, FacColorOff, FacHMin, FacHMax;
        public readonly List<MapEtiqueta> Etiquetas = new();
        public MapDot Hover;
        public int TopLabelOffset = 12;           // top margin of the labels, in CSS pixels
        /* Up close, the ground is painted by someone else (the flight renderer in top-down
           view, with the relief, the game textures and the buildings): if it does so, it
           returns true and the base map fades out on top (see FondoDesde). */
        public Func<bool> Fondo;
        /* Between these zooms the flat map fades over that ground, instead of switching at
           once. */
        public double FondoDesde = 9.5, FondoHasta = 10.5;

        double anchorX, anchorY;
        bool zoomAnim;
        ShaderProgram imgProg, facProg;
        uint emptyVao;

        static readonly double[] Steps = { 30, 10, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01, 0.005, 0.002, 0.001 };

        public double Ppd => 256.0 * Math.Pow(2, Zoom) / 180.0 * S;
        public int TileZoom => Math.Clamp((int)Math.Round(Zoom), MapConfig.MinZoom, MapConfig.MaxZoom);
        int GridZoom => Math.Clamp((int)Math.Round(Zoom), MapConfig.MinZoom, MapConfig.MaxZoomVista);

        /* The farthest zoom at which the 180° of latitude still fill the window's height:
           beyond it, empty strips would show above the north pole and below the south. */
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
            int dec = step < 0.01 ? 3 : step < 0.1 ? 2 : step < 1 ? 1 : 0;
            if (Math.Abs(v) < 0.5 * Math.Pow(10, -dec)) v = 0;          // no «-0°»
            return Geo.F(v, dec) + "°";
        }

        /* ------------------------------------------------------------ camera */

        public (double x, double y) Project(double lat, double lon) =>
            (W / 2.0 + (lon - CenterLon) * Ppd, H / 2.0 - (lat - CenterLat) * Ppd);

        /* Projects onto the copy of the world closest to the center of the view. */
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
            // when the window grows, the minimum zoom rises
            double min = MinZoomFit;
            if (TargetZoom < min) TargetZoom = min;
            if (Zoom < min) Zoom = min;
            Clamp();
        }

        public void SetView(double lat, double lon, double zoom)
        {
            Zoom = TargetZoom = Math.Clamp(zoom, MinZoomFit, MapConfig.MaxZoomVista);
            zoomAnim = false;
            CenterLat = lat; CenterLon = lon;
            Clamp();
        }

        /* Recenters without touching the zoom or cutting its animation (following a vessel). */
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

        /* The wheel goes level by level, like Leaflet, but with an animated step and keeping
           the point under the cursor still. */
        public void ZoomAt(double delta, double x, double y)
        {
            /* By whole levels, except the zoom-out limit, which depends on the window height:
               zooming back in from it picks the whole level up again. */
            double min = MinZoomFit;
            double next = Math.Round(TargetZoom + delta);
            if (delta > 0 && TargetZoom <= min + 1e-6) next = Math.Floor(min) + Math.Max(1, Math.Round(delta));
            TargetZoom = Math.Clamp(next, min, MapConfig.MaxZoomVista);
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

        /* What's under the cursor: sites first, since they're on top. */
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

        /* Each pixel computes its lon/lat and reads the texture. u isn't wrapped: with REPEAT,
           the copies of the world and the offset come out on their own and there's no seam. */
        const string ImgFS = @"#version 330 core
uniform vec2 uView;
uniform vec2 uCenter;
uniform float uPpd, uOff, uOpacity, uCell, uSunLat, uSunLon, uS;
uniform int uMode;
uniform float uHMin, uHMax, uAltMin, uAltMax;
uniform vec3 uTint;
uniform sampler2D uTex;
out vec4 frag;

/* Altimetry palette, in SCANsat's style: blue at the bottom, green on the plains, yellow and
   brown higher up and white on the peaks. */
vec3 altPalette(float t) {
  t = clamp(t, 0.0, 1.0);
  vec3 c0 = vec3(0.13, 0.25, 0.55), c1 = vec3(0.10, 0.55, 0.62), c2 = vec3(0.25, 0.62, 0.29);
  vec3 c3 = vec3(0.85, 0.79, 0.35), c4 = vec3(0.68, 0.36, 0.20), c5 = vec3(0.96, 0.96, 0.98);
  float s = t * 5.0;
  if (s < 1.0) return mix(c0, c1, s);
  if (s < 2.0) return mix(c1, c2, s - 1.0);
  if (s < 3.0) return mix(c2, c3, s - 2.0);
  if (s < 4.0) return mix(c3, c4, s - 3.0);
  return mix(c4, c5, s - 4.0);
}

void main() {
  float px = gl_FragCoord.x;
  float py = uView.y - gl_FragCoord.y;
  float lon = uCenter.x + (px - uView.x * 0.5) / uPpd;
  float lat = uCenter.y - (py - uView.y * 0.5) / uPpd;
  if (lat > 90.0 || lat < -90.0) discard;
  if (uMode == 2) {
    /* Night: the cosine of the angle to the subsolar point (at the equator). A margin of a few
       degrees on each side of the terminator acts as twilight. */
    float dl = mod(lon - uSunLon + 540.0, 360.0) - 180.0;
    float mu = sin(radians(lat)) * sin(radians(uSunLat)) + cos(radians(lat)) * cos(radians(uSunLat)) * cos(radians(dl));
    float night = 1.0 - smoothstep(-0.09, 0.07, mu);
    float dusk = exp(-mu * mu / 0.004) * 0.10;
    vec4 c = vec4(vec3(0.0, 0.012, 0.04) * night * 0.66, night * 0.66);
    c = vec4(c.rgb + vec3(1.0, 0.45, 0.15) * dusk, c.a + dusk * 0.2);
    // the Sun: a disc at the subsolar point
    float d = length(vec2(dl, lat - uSunLat)) * uPpd;
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
    // without an image, the background gets a touch of the body's color: Duna looks reddish and
    // Jool greenish even without a map
    vec3 c = mix(vec3(13.0, 20.0, 29.0) / 255.0, uTint * 0.42, 0.85);
    float ix = floor((lon + 180.0) / uCell), iy = floor((90.0 - lat) / uCell);
    if (mod(ix + iy, 2.0) < 0.5) c = mix(c, vec3(1.0), 0.012);
    frag = vec4(c * uOpacity, uOpacity);
    return;
  }
  vec4 t = texture(uTex, vec2((lon + uOff + 180.0) / 360.0, (90.0 - lat) / 180.0));
  if (uMode == 3) {
    // altimetry filter: the height map's gray goes to meters with the calibration
    float lum = dot(t.rgb, vec3(0.2126, 0.7152, 0.0722));
    float alt = uHMin + lum * (uHMax - uHMin);
    if (alt < uAltMin || alt > uAltMax) { frag = vec4(vec3(0.015, 0.02, 0.035) * 0.78, 0.78); return; }
    frag = vec4(altPalette((alt - uAltMin) / max(1.0, uAltMax - uAltMin)) * uOpacity, uOpacity);
    return;
  }
  frag = vec4(t.rgb * t.a, t.a) * uOpacity;
}";

        /* The political map over the terrain. The coast is clipped with the height map, which is
           the one seen up close; from afar, with the color map if it's the one acting as base map
           (the two may not match perfectly and a couple of color texels are tens of pixels). */
        const string FacFS = @"#version 330 core
uniform vec2 uView, uCenter;
uniform float uPpd;
uniform sampler2D uAltTex, uColTex;
uniform vec2 uAltSize;
uniform float uAltOff, uColOff, uHMin, uHMax, uMezcla;
uniform int uMascara;           // 1: with heights; 2: with color; 3: both
" + FaccionesGlsl.Codigo + @"
out vec4 frag;

float gris(ivec2 p) {
  ivec2 sz = ivec2(uAltSize);
  p.x = (p.x % sz.x + sz.x) % sz.x;
  p.y = clamp(p.y, 0, sz.y - 1);
  return dot(texelFetch(uAltTex, p, 0).rgb, vec3(0.2126, 0.7152, 0.0722));
}

// the same interpolation as the nearby ground (grisSuave), so the coast matches
float alturaEn(vec2 uv) {
  vec2 t = uv * uAltSize - 0.5;
  ivec2 i = ivec2(floor(t));
  vec2 f = t - vec2(i);
  f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
  float a = mix(gris(i), gris(i + ivec2(1, 0)), f.x);
  float b = mix(gris(i + ivec2(0, 1)), gris(i + ivec2(1, 1)), f.x);
  return uHMin + mix(a, b, f.y) * (uHMax - uHMin);
}

void main() {
  float px = gl_FragCoord.x;
  float py = uView.y - gl_FragCoord.y;
  float lon = uCenter.x + (px - uView.x * 0.5) / uPpd;
  float lat = uCenter.y - (py - uView.y * 0.5) / uPpd;
  vec2 uv = vec2((lon + 180.0) / 360.0, clamp((90.0 - lat) / 180.0, 0.0, 1.0));
  float h = (uMascara & 1) != 0 ? alturaEn(vec2(fract(uv.x + uAltOff / 360.0), uv.y)) : 1.0;
  float tH = smoothstep(-0.5, 0.5, h / max(fwidth(h), 0.01));
  float tC = 1.0;
  if ((uMascara & 2) != 0) {
    vec3 c = texture(uColTex, vec2(fract(uv.x + uColOff / 360.0), uv.y)).rgb;
    tC = 1.0 - smoothstep(0.03, 0.08, c.b - max(c.r, c.g));
  }
  float tierra = (uMascara & 3) == 3 ? mix(tC, tH, uMezcla) : (uMascara & 1) != 0 ? tH : tC;
  if (lat > 90.0 || lat < -90.0) discard;
  vec4 f = faccionEn(uv, uFacSize.x / (360.0 * uPpd)) * tierra;
  if (f.a < 0.002) discard;
  frag = f;
}";

        void DrawFacciones(double plano)
        {
            if (FacTex == null || FacColores == null) return;
            facProg ??= new ShaderProgram(ImgVS, FacFS);
            facProg.Use();
            facProg.Vec2("uView", W, H);
            facProg.Vec2("uCenter", CenterLon, CenterLat);
            facProg.Float("uPpd", Ppd);
            int mascara = 0;
            if (FacConMar && FacAltura != null) mascara |= 1;
            if (FacConMar && FacColor != null) mascara |= 2;
            // the color one only if it's the visible base map, and only from afar
            bool colorDeBase = BaseKind == "image" && BaseTex == FacColor;
            if (!colorDeBase && (mascara & 1) != 0) mascara = 1;
            facProg.Int("uMascara", mascara);
            facProg.Float("uMezcla", colorDeBase ? 1 - plano : 1);
            facProg.Vec2("uAltSize", FacAltura?.Width ?? 1, FacAltura?.Height ?? 1);
            facProg.Float("uAltOff", FacAlturaOff);
            facProg.Float("uColOff", FacColorOff);
            facProg.Float("uHMin", FacHMin);
            facProg.Float("uHMax", FacHMax);
            GL.ActiveTexture(GL.TEXTURE0);
            GL.BindTexture(GL.TEXTURE_2D, FacAltura?.Id ?? 0);
            facProg.Int("uAltTex", 0);
            GL.ActiveTexture(GL.TEXTURE0 + 1);
            GL.BindTexture(GL.TEXTURE_2D, FacColor?.Id ?? 0);
            facProg.Int("uColTex", 1);
            FaccionesGlsl.Uniformes(facProg, FacTex, FacColores, FacRelleno, 2.2 * S, 2);
            GL.Enable(GL.BLEND);
            GL.BlendFunc(GL.ONE, GL.ONE_MINUS_SRC_ALPHA);
            GL.BindVertexArray(emptyVao);
            GL.DrawArrays(GL.TRIANGLES, 0, 3);
            GL.BindVertexArray(0);
            GL.ActiveTexture(GL.TEXTURE0);
        }

        static readonly float[] TamanosEtiqueta = { 11, 13, 15, 18, 22, 27, 32 };

        /* Each territory's name, centered on its deepest point, with a size according to how
           much it takes on screen (in steps, so as not to rasterize a text for every zoom). */
        void DrawEtiquetas(Batch2D b, TextCache tc)
        {
            double pxPorM = Ppd / (Body.Radius * Geo.D2R);
            foreach (var e in Etiquetas)
            {
                double r = e.RadioM * pxPorM;
                if (r < 16 * S || string.IsNullOrEmpty(e.Texto)) continue;
                double want = Math.Min(r * 0.42, 32 * S) / S;
                float size = TamanosEtiqueta[0];
                foreach (float t in TamanosEtiqueta) if (t <= want) size = t;
                var t2 = tc.Get(e.Texto, new TextStyle("Segoe UI", size * S, true, ArgbClaro(e.Color), true));
                if (t2 == null || t2.TextW > r * 3.2) continue;
                double y = Project(e.Lat, 0).y;
                if (y < -20 * S || y > H + 20 * S) continue;
                foreach (double x in CopiesX(e.Lat, e.Lon, t2.TextW))
                    b.Text(t2, Math.Round(x - t2.TextW / 2.0), Math.Round(y - t2.TextH / 2.0));
            }
        }

        /* A faction's color lightened toward white, for writing on top of it. */
        public static int ArgbClaro(ColorF c)
        {
            int Canal(float v) => Math.Clamp((int)(255 * (v * 0.45 + 0.55)), 0, 255);
            return unchecked((int)0xFF000000) | (Canal(c.R) << 16) | (Canal(c.G) << 8) | Canal(c.B);
        }

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
            imgProg.Float("uHMin", AltHMin);
            imgProg.Float("uHMax", AltHMax);
            imgProg.Float("uAltMin", AltMin);
            imgProg.Float("uAltMax", AltMax);
            imgProg.Vec3("uTint", Tint[0], Tint[1], Tint[2]);
            imgProg.Float("uSunLat", SunLat);
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
            var fondoMapa = UI.Theme.MapBg;
            GL.ClearColor(fondoMapa.R / 255f, fondoMapa.G / 255f, fondoMapa.B / 255f, 1);
            GL.Clear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT);
            bool fondo = Fondo?.Invoke() == true;
            GL.Viewport(0, 0, W, H);
            b.Begin(W, H);

            // base map: without nearby ground, whole; with it, fading out on top
            double plano = 1;
            if (fondo)
            {
                double x = Math.Clamp((Zoom - FondoDesde) / Math.Max(FondoHasta - FondoDesde, 1e-6), 0, 1);
                plano = 1 - x * x * (3 - 2 * x);
            }
            if (plano > 0.002)
            switch (BaseKind)
            {
                case "grid":
                    DrawImage(1, null, 0, BaseOpacity);
                    DrawGraticule(b);
                    break;
                case "image":
                    if (BaseTex != null) DrawImage(0, BaseTex, BaseOffset, BaseOpacity * plano);
                    break;
                case "xyz":
                    DrawTiles(b);
                    break;
            }
            // whatever is pending in the batch goes first: biomes are painted directly and would cover it
            b.Flush();

            // biomes over the relief, under tracks and markers
            if (BiomeTex != null && BiomeOpacity > 0) DrawImage(0, BiomeTex, BiomeOffset, BiomeOpacity);

            // the altimetry filter covers the terrain, so it goes before the coverage
            if (AltTex != null && AltOpacity > 0) DrawImage(3, AltTex, AltOffset, AltOpacity);

            // what the save hasn't scanned, covered: it goes over the terrain and the biomes
            if (ScanTex != null && ScanOpacity > 0) DrawImage(0, ScanTex, 0, ScanOpacity);

            // night goes over the terrain and under the grid, the tracks and the markers
            // night, the same with or without the nearby ground (which is lit like a map)
            if (DayNight) DrawImage(2, null, 0, 1);

            // territories, on top of the night: a political map always has to be readable
            DrawFacciones(plano);

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

            DrawEtiquetas(b, tc);

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
            double step = StepForZoom(GridZoom);
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
            int z = GridZoom;
            double step = StepForZoom(z);
            var (lonMin, lonMax, latMin, latMax) = ViewBounds();
            var style = new TextStyle(UI.Theme.MonoFamily, 10 * S, false, Color.FromArgb(217, UI.Theme.Fg).ToArgb(), false);

            void GridChip(string text, double cx, double cy)
            {
                var t = tc.Get(text, style);
                if (t == null) return;
                b.Rect(cx - 3 * S, cy - 8 * S, t.TextW + 6 * S, 16 * S, Tema(UI.Theme.Bg2, 0.8f));
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

        /* Labels over the map use the interface theme's colors. */
        static ColorF Tema(Color c, float a) => ColorF.Rgba(c.R, c.G, c.B, a);

        /* Label with a background, the web's .km-label. */
        public void Chip(Batch2D b, TextCache tc, string text, double x, double cy)
        {
            var t = tc.Get(text, new TextStyle(UI.Theme.MonoFamily, 10.5f * S, false, UI.Theme.Fg.ToArgb(), false));
            if (t == null) return;
            double padX = 5 * S, padY = 2 * S;
            double w = t.TextW + padX * 2, h = t.TextH + padY * 2;
            double y = Math.Round(cy - h / 2);
            x = Math.Round(x);
            b.Rect(x, y, w, h, Tema(UI.Theme.Bg2, 0.9f));
            b.RectOutline(x, y, w, h, Math.Max(1, Math.Round(S)), Tema(UI.Theme.Line, 1));
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
                        // .km-pin: 11 px, 2 px white border and a dark edge around it
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

        /* Metric scale at the bottom left, like L.control.scale: the distance of 180 px at the
           center's latitude, rounded to 1-2-3-5. */
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

            var t = tc.Get(label, new TextStyle(UI.Theme.MonoFamily, 11 * S, false, UI.Theme.Fg.ToArgb(), false));
            double h = (t?.TextH ?? 14) + 4 * S;
            double x = 10 * S, top = H - 10 * S - h;
            double bw = 2 * S;
            b.Rect(x, top, w, h, Tema(UI.Theme.Bg2, 0.9f));
            var line = Tema(UI.Theme.FgDim, 1);
            b.Rect(x, top, bw, h, line);
            b.Rect(x + w - bw, top, bw, h, line);
            b.Rect(x, top + h - bw, w, bw, line);
            if (t != null) b.Text(t, x + 5 * S, top + 1 * S);
        }

        void DrawAttribution(Batch2D b, TextCache tc)
        {
            var t = tc.Get(Lang.T("Visor no oficial · Kerbal Space Program es de Squad / Private Division"),
                           new TextStyle(UI.Theme.UIFamily, 10.5f * S, false, UI.Theme.FgDim.ToArgb(), false));
            if (t == null) return;
            double w = t.TextW + 10 * S, h = t.TextH + 2 * S;
            b.Rect(W - w, H - h, w, h, Tema(UI.Theme.Bg2, 0.85f));
            b.Text(t, W - w + 5 * S, H - h + 1 * S);
        }

        public void Dispose()
        {
            imgProg?.Dispose();
            facProg?.Dispose();
            GL.DeleteVertexArray(emptyVao);
        }
    }
}
