using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* The other bodies on the 3D globe (see GlobeView.Cuerpos.cs): where each one is at the
       time on the time bar, seen from the body being viewed, with its light and its map.

       Positions come from the solar system's orbits (the same ones that move the Sun) in the
       inertial frame, with north on Z; they're taken to the globe's frame with the same body
       rotation the Sun uses. In KSP all bodies rotate around the same axis, so each one's map
       only needs its own longitude rotation. */
    public sealed partial class MainForm
    {
        DarkCheck chkCuerpos;
        readonly Dictionary<string, Texture> mapasCuerpos = new(StringComparer.OrdinalIgnoreCase);
        string mapasCuerposDe;                    // the bundle they were already requested from

        /* The time on the time bar and the rotation of the viewed body at that time (the one
           that moves vessels; without a save, the game's at the start). */
        (double Ut, double Rot) InstanteYGiro()
        {
            if (sv.Data == null) return (0, Sun.DefaultRotation(0));
            return (sim.T, RotBase() + 360 * ((sim.T - sv.Ut) / Body.SiderealDay));
        }

        void BuildCuerposControl(Section seccion)
        {
            chkCuerpos = new DarkCheck("Lunas, planetas y el Sol en su sitio", state.VerCuerpos);
            chkCuerpos.CheckedChanged += (s, e) =>
            {
                state.VerCuerpos = chkCuerpos.Checked;
                globe.VerCuerpos = chkCuerpos.Checked;
                SaveSettings();
                RequestRender();
            };
            seccion.Add(Checks(chkCuerpos));
            globe.VerCuerpos = state.VerCuerpos;
        }

        /* Every frame: it's a few dozen orbit calculations. */
        void ActualizarCuerpos()
        {
            globe.Cuerpos.Clear();
            if (!state.VerCuerpos) return;
            var (ut, rot) = InstanteYGiro();
            var yo = Body.Current;
            var pYo = SolarSystem.PositionAt(yo, ut);
            double R = yo.Radius;
            CargarMapasCuerpos();
            foreach (var b in SolarSystem.Bodies)
            {
                if (b == yo) continue;
                var pb = SolarSystem.PositionAt(b, ut);
                var rel = new[] { pb[0] - pYo[0], pb[1] - pYo[1], pb[2] - pYo[2] };
                double dist = Math.Sqrt(rel[0] * rel[0] + rel[1] * rel[1] + rel[2] * rel[2]);
                if (dist <= 0) continue;
                // toward the star, which is at the origin of the inertial frame
                double dEst = Math.Sqrt(pb[0] * pb[0] + pb[1] * pb[1] + pb[2] * pb[2]);
                var luz = b.IsStar || dEst <= 0 ? new double[] { 0, 1, 0 } : AlGlobo(new[] { -pb[0], -pb[1], -pb[2] }, rot, 1);
                double rotB = b.IsStar ? 0 : b.InitialRotation + 360 * ut / b.SiderealDay;
                double giro = (rot - rotB) / 360;
                globe.Cuerpos.Add(new GlobeView.CuerpoEnElCielo
                {
                    Nombre = b.Label,
                    Pos = AlGlobo(rel, rot, dist / R),
                    Radio = b.Radius / R,
                    Luz = luz,
                    Giro = giro - Math.Floor(giro),
                    Tinte = b.Tint,
                    Mapa = mapasCuerpos.GetValueOrDefault(b.Name),
                    Estrella = b.IsStar,
                });
            }
        }

        /* From the inertial frame (north on Z) to the globe's: latitude and longitude, minus
           the body's rotation, as the Sun does (Sun.Subsolar). */
        static double[] AlGlobo(double[] v, double rot, double r)
        {
            double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            double lat = Math.Asin(Math.Clamp(v[2] / l, -1, 1)) * 180 / Math.PI;
            double lon = Math.Atan2(v[1], v[0]) * 180 / Math.PI - rot;
            return GlobeView.Sph(lat, lon, r);
        }

        /* The color maps of the bodies in the Parallax bundle, at 1024 (from afar nothing more
           is needed), once and in the background. */
        void CargarMapasCuerpos()
        {
            string paquete = parallaxBundle;
            if (paquete == null || mapasCuerposDe == paquete || !glOk) return;
            mapasCuerposDe = paquete;
            var nombres = new List<string>(parallaxCuerpos);
            Task.Run(() =>
            {
                var res = new List<(string, ImageData)>();
                foreach (var n in nombres)
                {
                    try
                    {
                        var c = ParallaxPlanets.Load(paquete, n, 1024).Color;
                        if (c != null) res.Add((n, MapasDelJuego.DesdeParallax(c)));
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[cuerpos] " + n + ": " + ex.Message); }
                }
                return res;
            }).ContinueWith(t =>
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (mapasCuerposDe != paquete || !surface.MakeCurrent()) return;
                        foreach (var (n, img) in t.Result)
                        {
                            if (mapasCuerpos.TryGetValue(n, out var vieja)) vieja.Dispose();
                            mapasCuerpos[n] = Texture.FromRgba(img.Rgba, img.Width, img.Height, TexFilter.Mipmap, true);
                        }
                        RequestRender();
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }
    }
}
