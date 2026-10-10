using System;
using System.Collections.Generic;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* The political map on the globe: territories on the ground (from orbit and during the
       descent), their names, and what the tool draws while painting (the brush circle and the
       half-drawn polygon). Nothing is painted in flight and sky views: it's a map, not the
       landscape. */
    public sealed partial class GlobeView
    {
        public Texture FacTex;
        public float[] FacColores;
        public double FacRelleno = 0.45;
        public bool FacConMar;                    // clip at the coast (bodies with sea)
        public readonly List<MapEtiqueta> FacEtiquetas = new();

        /* Tool strokes, in lat/lon: painted on screen on top of everything. */
        public readonly List<(IReadOnlyList<LatLon> Pts, ColorF Color, bool Cerrado)> Trazos = new();
        public readonly List<(LatLon P, ColorF Color)> TrazoPuntos = new();

        bool FacVisibles => FacTex != null && FacColores != null;
        static readonly float[] TamanosEtiqueta = { 11, 13, 15, 18, 22, 26, 30 };

        void FacUniforms(ShaderProgram p, int unidad, bool on)
        {
            FaccionesGlsl.Uniformes(p, on && FacVisibles ? FacTex : null, FacColores, FacRelleno, 2.2 * S, unidad);
            p.Int("uFacConMar", FacConMar ? 1 : 0);
        }

        /* Ground height in radii for placing something on top of it, when close. */
        double RadioSuelo(double lat, double lon, bool cerca) =>
            cerca ? 1 + Math.Max(0, GroundAt?.Invoke(lat, lon) ?? 0) / Body.Radius : 1;

        bool VeoPunto(double[] p, double[] eye, bool cerca)
        {
            if (cerca)
            {
                // hidden if the line of sight goes below sea level (with a 100 m margin)
                double k = 1 / (1 - 100 / Body.Radius);
                return !TapadoPorPlaneta(Scale(p, k), Scale(eye, k));
            }
            double camLen = Len(eye);
            return Dot(p, eye) / (camLen * Len(p)) > 1 / camLen;
        }

        /* What's under the cursor on the terrain. From afar the sphere is enough; up close, the
           ray walks the relief (with the camera tilted, a slope is kilometers nearer than where
           the ray hits the sphere). */
        public LatLon? PickSuelo(double px, double py)
        {
            bool cerca = Mode != CamMode.Planet || Len(eyeL) < RadioCerca;
            if (!cerca || GroundAt == null) return Pick(px, py);
            var d = RayDir(px, py);
            var eye = eyeL;
            double R = Body.Radius;
            double Encima(double t, out double lat, out double lon)
            {
                var p = Add(eye, d, t);
                double l = Len(p);
                lat = Math.Asin(Math.Clamp(p[1] / l, -1, 1)) * R2D;
                lon = Math.Atan2(p[0], p[2]) * R2D;
                return (l - 1) * R - Math.Max(0, GroundAt(lat, lon));
            }
            double t0 = 0, t = 0;
            for (int i = 0; i < 700; i++)
            {
                double e = Encima(t, out _, out _);
                if (e < 0)
                {
                    double a = t0, b = t;
                    for (int k = 0; k < 30; k++)
                    {
                        double m = 0.5 * (a + b);
                        if (Encima(m, out _, out _) < 0) b = m; else a = m;
                    }
                    Encima(b, out double la, out double lo);
                    return new LatLon(la, Geo.WrapLon(lo));
                }
                t0 = t;
                t += Math.Max(e / R * 0.5, Math.Max(t * 0.002, 2 / R));
                if (t > 0.8) break;
            }
            return Pick(px, py);
        }

        /* The territory names, at whatever size they have on screen. */
        void DrawFacEtiquetas(Batch2D b, TextCache tc, double[] eye, double fov)
        {
            if (!FacVisibles || FacEtiquetas.Count == 0) return;
            bool cerca = Len(eye) < RadioCerca;
            double focal = H / 2.0 / Math.Tan(fov * D2R / 2);
            foreach (var e in FacEtiquetas)
            {
                if (string.IsNullOrEmpty(e.Texto)) continue;
                var p = Sph(e.Lat, e.Lon, RadioSuelo(e.Lat, e.Lon, cerca));
                if (!VeoPunto(p, eye, cerca)) continue;
                double dist = Len(Add(p, eye, -1)) * Body.Radius;
                double r = e.RadioM / Math.Max(dist, 1) * focal;
                if (r < 16 * S) continue;
                if (!ToScreen(p, out double sx, out double sy)) continue;
                if (sx < -200 * S || sy < -50 * S || sx > W + 200 * S || sy > H + 50 * S) continue;
                double want = Math.Min(r * 0.42, 30 * S) / S;
                float size = TamanosEtiqueta[0];
                foreach (float t in TamanosEtiqueta) if (t <= want) size = t;
                var tt = tc.Get(e.Texto, new TextStyle("Segoe UI", size * S, true, MapView.ArgbClaro(e.Color), true));
                if (tt == null || tt.TextW > r * 3.2) continue;
                b.Text(tt, Math.Round(sx - tt.TextW / 2.0), Math.Round(sy - tt.TextH / 2.0));
            }
        }

        /* The brush circle and the half-drawn polygon, projected to the screen. */
        void DrawTrazos(Batch2D b, double[] eye)
        {
            if (Trazos.Count == 0 && TrazoPuntos.Count == 0) return;
            bool cerca = Mode != CamMode.Planet || Len(eye) < RadioCerca;
            foreach (var (pts, color, cerrado) in Trazos)
            {
                int n = pts.Count;
                if (n < 2) continue;
                bool ant = false;
                double ax = 0, ay = 0;
                for (int i = 0; i <= (cerrado ? n : n - 1); i++)
                {
                    var ll = pts[i % n];
                    var p = Sph(ll.Lat, ll.Lon, RadioSuelo(ll.Lat, ll.Lon, cerca) + 2 / Body.Radius);
                    bool ok = VeoPunto(p, eye, cerca) && ToScreen(p, out double x, out double y) && Math.Abs(x) < 1e5 && Math.Abs(y) < 1e5;
                    if (!ok) { ant = false; continue; }
                    ToScreen(p, out double sx, out double sy);
                    if (ant)
                    {
                        b.Line(ax, ay, sx, sy, 3 * S, ColorF.Rgba(0, 0, 0, 0.45f));
                        b.Line(ax, ay, sx, sy, 1.6 * S, color);
                    }
                    ax = sx; ay = sy; ant = true;
                }
            }
            foreach (var (ll, color) in TrazoPuntos)
            {
                var p = Sph(ll.Lat, ll.Lon, RadioSuelo(ll.Lat, ll.Lon, cerca) + 2 / Body.Radius);
                if (!VeoPunto(p, eye, cerca) || !ToScreen(p, out double sx, out double sy)) continue;
                b.Circle(sx, sy, 5 * S, ColorF.Rgba(0, 0, 0, 0.5f));
                b.Circle(sx, sy, 3.5 * S, color);
            }
        }
    }
}
