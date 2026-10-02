using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Los demás cuerpos en el globo 3D (ver GlobeView.Cuerpos.cs): dónde está cada uno en el
       instante de la barra de tiempo, visto desde el cuerpo que se ve, con su luz y su mapa.

       Las posiciones salen de las órbitas del sistema solar (las mismas que mueven el Sol)
       en el marco inercial, con el norte en Z; se pasan al marco del globo con la misma
       rotación del cuerpo que usa el Sol. En KSP todos los cuerpos giran alrededor del mismo
       eje, así que el mapa de cada uno solo necesita su propio giro en longitud. */
    public sealed partial class MainForm
    {
        DarkCheck chkCuerpos;
        readonly Dictionary<string, Texture> mapasCuerpos = new(StringComparer.OrdinalIgnoreCase);
        string mapasCuerposDe;                    // el paquete del que ya se pidieron

        /* El instante de la barra de tiempo y la rotación del cuerpo que se ve en él (la que
           mueve las naves; sin partida, la del juego al empezar). */
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

        /* Cada fotograma: son unas pocas decenas de cuentas de órbita. */
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
                // hacia la estrella, que está en el origen del marco inercial
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

        /* Del marco inercial (norte en Z) al del globo: latitud y longitud, menos la rotación
           del cuerpo, como hace el Sol (Sun.Subsolar). */
        static double[] AlGlobo(double[] v, double rot, double r)
        {
            double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            double lat = Math.Asin(Math.Clamp(v[2] / l, -1, 1)) * 180 / Math.PI;
            double lon = Math.Atan2(v[1], v[0]) * 180 / Math.PI - rot;
            return GlobeView.Sph(lat, lon, r);
        }

        /* Los mapas de color de los cuerpos que trae el paquete de Parallax, a 1024 (de
           lejos no hace falta más), una vez y en segundo plano. */
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
