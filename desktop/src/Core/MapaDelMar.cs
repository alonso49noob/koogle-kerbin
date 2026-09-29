using System;

namespace KerbinMaps.Core
{
    /* El color del mar, sin la costa.

       Cerca del suelo manda el relieve: es mar lo que queda bajo el nivel del mar. Pero el
       mapa de color es de 1 km por texel, y sus texeles de costa mezclan el azul con la
       arena y la hierba. El agua que el relieve pone junto a la orilla tomaba ese color y
       salía como un anillo más oscuro y, más allá, un halo arenoso siguiendo toda la costa.

       Aquí se hace un mapa aparte, de medio grado, con solo el mar abierto: la media de los
       texeles que son azules en el mapa de color y están a más de 30 m de profundidad en el
       de alturas. Las celdas sin mar se rellenan con las vecinas, así que junto a la costa
       queda el color del mar de al lado. */
    public static class MapaDelMar
    {
        public const int Ancho = 720, Alto = 360;

        /* RGBA de Ancho×Alto (la fila 0 es el norte y la columna 0 los 180° oeste), o null
           si el cuerpo no tiene mar en esos mapas. `media` es el color medio de todo su mar. */
        public static byte[] Construir(ImageData color, ImageData altura, double hmin, double hmax,
                                       double colorOff, double alturaOff, out float[] media)
        {
            media = null;
            if (color == null || altura == null) return null;
            var suma = new double[Ancho * Alto * 3];
            var n = new int[Ancho * Alto];
            double tr = 0, tg = 0, tb = 0;
            int total = 0;
            for (int y = 0; y < Alto; y++)
                for (int x = 0; x < Ancho; x++)
                    for (int s = 0; s < 4; s++)
                    {
                        double lat = 90 - (y + 0.25 + 0.5 * (s >> 1)) * 180.0 / Alto;
                        double lon = -180 + (x + 0.25 + 0.5 * (s & 1)) * 360.0 / Ancho;
                        if (altura.Height_(lat, lon, hmin, hmax, alturaOff) > -30) continue;
                        var c = color.SampleBilinear(lat, lon, colorOff);
                        if (c.B - Math.Max(c.R, c.G) < 0.06f) continue;       // no es azul: costa o hielo
                        int i = y * Ancho + x;
                        suma[i * 3] += c.R; suma[i * 3 + 1] += c.G; suma[i * 3 + 2] += c.B;
                        n[i]++;
                        tr += c.R; tg += c.G; tb += c.B; total++;
                    }
            if (total < 200) return null;
            media = new[] { (float)(tr / total), (float)(tg / total), (float)(tb / total) };

            var rgb = new float[Ancho * Alto * 3];
            var hay = new bool[Ancho * Alto];
            for (int i = 0; i < n.Length; i++)
                if (n[i] > 0)
                {
                    hay[i] = true;
                    for (int k = 0; k < 3; k++) rgb[i * 3 + k] = (float)(suma[i * 3 + k] / n[i]);
                }

            // las celdas de tierra, con la media de sus vecinas ya rellenas, en varias pasadas
            for (int pasada = 0; pasada < 48; pasada++)
            {
                var nuevas = new System.Collections.Generic.List<(int I, float R, float G, float B)>();
                for (int y = 0; y < Alto; y++)
                    for (int x = 0; x < Ancho; x++)
                    {
                        int i = y * Ancho + x;
                        if (hay[i]) continue;
                        float r = 0, g = 0, b = 0;
                        int m = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = y + dy;
                            if (yy < 0 || yy >= Alto) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int j = yy * Ancho + (x + dx + Ancho) % Ancho;
                                if (!hay[j]) continue;
                                r += rgb[j * 3]; g += rgb[j * 3 + 1]; b += rgb[j * 3 + 2]; m++;
                            }
                        }
                        if (m > 0) nuevas.Add((i, r / m, g / m, b / m));
                    }
                if (nuevas.Count == 0) break;
                foreach (var (i, r, g, b) in nuevas)
                {
                    rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;
                    hay[i] = true;
                }
            }

            var o = new byte[Ancho * Alto * 4];
            for (int i = 0; i < Ancho * Alto; i++)
            {
                for (int k = 0; k < 3; k++)
                    o[i * 4 + k] = (byte)Math.Clamp((int)Math.Round((hay[i] ? rgb[i * 3 + k] : media[k]) * 255), 0, 255);
                o[i * 4 + 3] = 255;
            }
            return o;
        }
    }
}
