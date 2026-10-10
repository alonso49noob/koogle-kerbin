using System;
using System.Collections.Generic;

namespace KerbinMaps.Core
{
    /* A patch of terrain flattened to a height, like the game's terrain decals
       (PQSMod_MapDecal): the KSC sits on a leveled area that the height map, at 1.8 km per
       pixel, doesn't capture; without it its lawns would float or be buried.

       Flat up to R0 and blended into the surrounding terrain up to R1. The direction is in the
       viewer's frame (x = cos lat sin lon, y = sin lat, z = cos lat cos lon). The same math
       runs on the CPU (camera, placement, scatters) and in the ground shader. */
    public sealed class Aplanado
    {
        public double[] N;
        public double R0, R1, H;

        public static Aplanado En(double lat, double lon, double r0, double r1, double h)
        {
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
            return new Aplanado
            {
                N = new[] { Math.Cos(la) * Math.Sin(lo), Math.Sin(la), Math.Cos(la) * Math.Cos(lo) },
                R0 = r0, R1 = r1, H = h,
            };
        }

        /* Distance in meters (along the chord, which in float and at these distances is exact). */
        double Dist(double lat, double lon, double radio)
        {
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
            double x = Math.Cos(la) * Math.Sin(lo) - N[0], y = Math.Sin(la) - N[1], z = Math.Cos(la) * Math.Cos(lo) - N[2];
            return Math.Sqrt(x * x + y * y + z * z) * radio;
        }

        public static double Aplicar(IReadOnlyList<Aplanado> lista, double lat, double lon, double h, double radio)
        {
            if (lista == null) return h;
            foreach (var a in lista)
            {
                double d = a.Dist(lat, lon, radio);
                if (d >= a.R1) continue;
                double t = d <= a.R0 ? 1 : 1 - Suave((d - a.R0) / (a.R1 - a.R0));
                h += (a.H - h) * t;
            }
            return h;
        }

        /* Inside the flat part: the game removes scatters there. */
        public static bool Dentro(IReadOnlyList<Aplanado> lista, double lat, double lon, double radio)
        {
            if (lista == null) return false;
            foreach (var a in lista)
                if (a.Dist(lat, lon, radio) < a.R0) return true;
            return false;
        }

        static double Suave(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
    }
}
