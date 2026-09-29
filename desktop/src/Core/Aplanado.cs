using System;
using System.Collections.Generic;

namespace KerbinMaps.Core
{
    /* Una zona del terreno allanada a una altura, como las calcomanías de terreno del juego
       (PQSMod_MapDecal): el KSC está sobre una explanada que el mapa de alturas, de 1,8 km
       por píxel, no recoge; sin ella sus céspedes flotarían o quedarían enterrados.

       Plana hasta R0 y fundida con el terreno de alrededor hasta R1. La dirección va en el
       marco del visor (x = cos lat sin lon, y = sin lat, z = cos lat cos lon). Las mismas
       cuentas en la CPU (cámara, colocación, scatters) y en el shader del suelo. */
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

        /* Distancia en metros (por la cuerda, que en float y a estas distancias es exacta). */
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

        /* Dentro de la parte plana: ahí el juego quita los scatters. */
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
