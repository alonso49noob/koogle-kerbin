using System;
using System.Collections.Generic;

namespace KerbinMaps.Core
{
    /* Nombres de los biomas de Kerbin en el juego, por su color en el mapa de biomas de
       KSP (el mismo que trae el visor, sacado de la wiki).

       Los nombres que el usuario pone a los biomas son suyos y en su idioma; los mods, en
       cambio, hablan de «Grasslands» o «Deserts». Parallax, por ejemplo, dice en qué biomas
       no sale cada scatter. Los colores se identificaron por su área y su latitud: el azul
       que cubre la mitad del planeta es el agua, el blanco de los dos polos los casquetes,
       el gris claro del sur la plataforma de hielo austral, el morado de latitudes altas la
       tundra, etc. */
    public static class StockBiomes
    {
        static readonly Dictionary<string, string> Kerbin = new(StringComparer.OrdinalIgnoreCase)
        {
            ["#3762ab"] = "Water",
            ["#83bc2e"] = "Grasslands",
            ["#5d852a"] = "Highlands",
            ["#faf2b7"] = "Shores",
            ["#a7a7a7"] = "Mountains",
            ["#eabf6f"] = "Deserts",
            ["#974f23"] = "Badlands",
            ["#c78fdf"] = "Tundra",
            ["#ffffff"] = "Ice Caps",
            ["#e4fdff"] = "Northern Ice Shelf",
            ["#d8d8d8"] = "Southern Ice Shelf",
        };

        /* Nombre del juego para ese color en ese cuerpo, o null si no se conoce. */
        public static string Name(string body, string hex) =>
            body == "Kerbin" && hex != null && Kerbin.TryGetValue(hex, out var n) ? n : null;
    }
}
