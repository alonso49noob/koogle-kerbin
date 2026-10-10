using System;
using System.Collections.Generic;

namespace KerbinMaps.Core
{
    /* Names of Kerbin's biomes in the game, by their color in KSP's biome map (the same one the
       viewer ships, taken from the wiki).

       The names the user gives biomes are theirs and in their language; mods, on the other
       hand, talk about «Grasslands» or «Deserts». Parallax, for example, says which biomes each
       scatter doesn't appear in. The colors were identified by their area and latitude: the
       blue covering half the planet is the water, the white at both poles the ice caps, the
       light gray in the south the southern ice shelf, the purple at high latitudes the tundra,
       etc. */
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

        /* The game's name for that color on that body, or null if unknown. */
        public static string Name(string body, string hex) =>
            body == "Kerbin" && hex != null && Kerbin.TryGetValue(hex, out var n) ? n : null;
    }
}
