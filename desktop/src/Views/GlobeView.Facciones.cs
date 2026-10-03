using System;
using System.Collections.Generic;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* El mapa político en el globo: los territorios sobre el suelo (desde órbita y en la bajada),
       sus nombres, y lo que dibuja la herramienta mientras se pinta (el círculo del pincel y el
       polígono a medias). En el vuelo y en el cielo no se pinta nada: es un mapa, no el paisaje. */
    public sealed partial class GlobeView
    {
        public Texture FacTex;
        public float[] FacColores;
        public double FacRelleno = 0.45;
        public bool FacConMar;                    // recortar por la costa (cuerpos con mar)
        public readonly List<MapEtiqueta> FacEtiquetas = new();

        /* Trazos de la herramienta, en lat/lon: se pintan en pantalla por encima de todo. */
        public readonly List<(IReadOnlyList<LatLon> Pts, ColorF Color, bool Cerrado)> Trazos = new();
        public readonly List<(LatLon P, ColorF Color)> TrazoPuntos = new();

        bool FacVisibles => FacTex != null && FacColores != null;
        static readonly float[] TamanosEtiqueta = { 11, 13, 15, 18, 22, 26, 30 };

        void FacUniforms(ShaderProgram p, int unidad, bool on)
        {
            FaccionesGlsl.Uniformes(p, on && FacVisibles ? FacTex : null, FacColores, FacRelleno, 2.2 * S, unidad);
            p.Int("uFacConMar", FacConMar ? 1 : 0);
        }

        /* Altura del suelo en radios para colocar algo encima de él, cuando se está cerca. */
        double RadioSuelo(double lat, double lon, bool cerca) =>
            cerca ? 1 + Math.Max(0, GroundAt?.Invoke(lat, lon) ?? 0) / Body.Radius : 1;

        bool VeoPunto(double[] p, double[] eye, bool cerca)
        {
            if (cerca)
            {
                // tapado si la visual pasa por debajo del nivel del mar (con un margen de 100 m)
                double k = 1 / (1 - 100 / Body.Radius);
                return !TapadoPorPlaneta(Scale(p, k), Scale(eye, k));
            }
            double camLen = Len(eye);
            return Dot(p, eye) / (camLen * Len(p)) > 1 / camLen;
        }

        /* Lo que hay bajo el cursor sobre el terreno. Desde lejos basta la esfera; de cerca, el
           rayo recorre el relieve (con la cámara inclinada, una ladera está kilómetros más acá de
           donde el rayo corta la esfera). */
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

        /* Los nombres de los territorios, con el tamaño que tengan en pantalla. */
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

        /* El círculo del pincel y el polígono a medias, proyectados a pantalla. */
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
