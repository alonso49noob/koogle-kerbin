using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KerbinMaps.Ksp
{
    /* Texturas de superficie de Parallax para un cuerpo, con cómo las mezcla el mod.

       Parallax pinta el suelo con cuatro texturas por cuerpo: baja, media y alta según la
       altitud, y una de pendiente para lo empinado. Cuándo pasa de una a otra (en metros)
       y cómo de brusco es el paso a la de pendiente lo dice Terrain.cfg, y las texturas van
       dentro de un paquete de Unity que se lee con UnityBundle. */
    public sealed class ParallaxTerrain
    {
        public string Body;
        public TextureFile Low, Mid, High, Steep;
        /* Lo que da variedad al suelo en Parallax, en mosaico como las otras cuatro y con un
           canal por textura (r baja, g media, b alta, a pendiente):
           - influencia: cuánto manda la textura frente al color del planeta;
           - desplazamiento: el relieve fino, con el que se decide la mezcla entre texturas
             (la hierba asoma entre las piedras en vez de fundirse con ellas);
           - oclusión: las sombras de ese relieve. */
        public TextureFile Influence, Displacement, Occlusion;
        /* Mapas de normales de cada textura: el relieve fino que da la luz al suelo. */
        public TextureFile BumpLow, BumpMid, BumpHigh, BumpSteep;
        public double LowMidStart, LowMidEnd, MidHighStart, MidHighEnd;
        public double SteepPower = 8, SteepContrast = 4, SteepMidpoint = 0.7;
        public double Tiling = 0.03;                 // repeticiones por metro

        public double MetrosPorRepeticion => Tiling > 0 ? 1 / Tiling : 30;

        /* Busca la configuración y el paquete de Parallax en GameData. Devuelve null si no
           está instalado o si ese cuerpo no tiene texturas. */
        public static ParallaxTerrain Load(string gameData, string body, int maxAncho = 2048)
        {
            // en el KSP del jugador o en lo que haya bajado el instalador
            string dir = ParallaxPlanets.GameDatas(gameData)
                .Select(gd => Path.Combine(gd, "Parallax_StockTerrainTextures"))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "Terrain.cfg")));
            if (dir == null) return null;
            string cfg = Path.Combine(dir, "Terrain.cfg");

            ConfigNode props = null;
            foreach (var pt in ConfigNode.ParseFile(cfg).Children("ParallaxTerrain"))
                foreach (var b in pt.Children("Body"))
                    if (string.Equals(b.Get("name"), body, StringComparison.OrdinalIgnoreCase))
                        props = b.Children("ShaderProperties").FirstOrDefault();
            if (props == null) return null;

            var t = new ParallaxTerrain
            {
                Body = body,
                LowMidStart = Num(props, "_LowMidBlendStart", 0),
                LowMidEnd = Num(props, "_LowMidBlendEnd", 1),
                MidHighStart = Num(props, "_MidHighBlendStart", 1e6),
                MidHighEnd = Num(props, "_MidHighBlendEnd", 1e6 + 1),
                SteepPower = Num(props, "_SteepPower", 8),
                SteepContrast = Num(props, "_SteepContrast", 4),
                SteepMidpoint = Num(props, "_SteepMidpoint", 0.7),
                Tiling = Num(props, "_Tiling", 0.03),
            };

            string paquete = Directory.GetFiles(dir, "*.unity3d").FirstOrDefault();
            if (paquete == null) return null;
            using var ub = new UnityBundle(paquete);
            var cab = ub.Nodos.First(n => !n.Path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                                       && !n.Path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));
            var sf = new UnitySerialized(ub, cab.Offset, cab.Size);

            (long, long)? Recurso(string p)
            {
                string nombre = p.Substring(p.LastIndexOf('/') + 1);
                var n = ub.Nodos.FirstOrDefault(x => x.Path.Equals(nombre, StringComparison.OrdinalIgnoreCase));
                return n.Path == null ? null : (n.Offset, n.Size);
            }

            // la misma textura puede servir para varias ranuras (la Mun usa mid00 en las tres)
            var leidas = new Dictionary<string, TextureFile>(StringComparer.OrdinalIgnoreCase);
            TextureFile Leer(string clave)
            {
                string ruta = props.Get(clave);
                if (string.IsNullOrEmpty(ruta)) return null;
                if (leidas.TryGetValue(ruta, out var ya)) return ya;
                var id = sf.Buscar(ruta);
                if (id == null) return leidas[ruta] = null;
                try { return leidas[ruta] = Convertir(sf.LeerTextura(id.Value, Recurso), maxAncho); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[parallax] " + ruta + ": " + ex.Message);
                    return leidas[ruta] = null;
                }
            }

            t.Low = Leer("_MainTexLow");
            t.Mid = Leer("_MainTexMid");
            t.High = Leer("_MainTexHigh");
            t.Steep = Leer("_MainTexSteep");
            t.Influence = Leer("_InfluenceMap");
            t.Displacement = Leer("_DisplacementMap");
            t.Occlusion = Leer("_OcclusionMap");
            t.BumpLow = Leer("_BumpMapLow");
            t.BumpMid = Leer("_BumpMapMid");
            t.BumpHigh = Leer("_BumpMapHigh");
            t.BumpSteep = Leer("_BumpMapSteep");
            if (t.Low == null && t.Mid == null && t.High == null && t.Steep == null) return null;
            // las que falten se cubren con otra, para no dejar ranuras vacías
            t.Mid ??= t.Low ?? t.High ?? t.Steep;
            t.Low ??= t.Mid; t.High ??= t.Mid; t.Steep ??= t.Mid;
            return t;
        }

        /* De textura de Unity a una lista de niveles lista para la GPU. DXT1 y DXT5, que es
           lo que usa Parallax, se suben tal cual (también las de crunch, que se deshacen
           hasta sus bloques DXT); los niveles más anchos que `maxAncho` se
           saltan para no llenar la memoria de vídeo con texturas de 4096 que de cerca no se
           notan. Lo demás (algún mapa en R8 o RGBA32) se descomprime a RGBA. */
        internal static TextureFile Convertir(UnitySerialized.Textura u, int maxAncho)
        {
            int block;
            uint fmt;
            switch (u.Formato)
            {
                case 10: block = 8; fmt = TextureFile.DXT1; break;       // DXT1
                case 12: block = 16; fmt = TextureFile.DXT5; break;      // DXT5
                case 28: case 29:                                         // DXT1 y DXT5 en crunch
                {
                    // en el juego son máscaras de 4096 para un par de km: con 1024 sobra
                    var c = Crunch.Decodificar(u.Datos, Math.Min(maxAncho, 1024));
                    if (c.Niveles.Count == 0) throw new InvalidDataException("sin niveles utilizables");
                    var ct = new TextureFile { CompressedFormat = c.Dxt5 ? TextureFile.DXT5 : TextureFile.DXT1, HasAlpha = c.Dxt5,
                                               Width = c.Niveles[0].W, Height = c.Niveles[0].H };
                    foreach (var n in c.Niveles) ct.Levels.Add(n.Bloques);
                    return ct;
                }
                default:
                {
                    var img = ParallaxPlanets.Decodificar(u, maxAncho) ?? throw new InvalidDataException("sin niveles utilizables");
                    var rgba = new TextureFile { Width = img.Width, Height = img.Height, HasAlpha = true };
                    rgba.Levels.Add(img.Rgba);
                    return rgba;
                }
            }
            var tf = new TextureFile { CompressedFormat = fmt, HasAlpha = u.Formato == 12 };
            int w = u.Ancho, h = u.Alto, off = 0;
            for (int i = 0; i < u.Mips && off < u.Datos.Length; i++)
            {
                int size = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block;
                if (off + size > u.Datos.Length) break;
                if (w <= maxAncho)
                {
                    if (tf.Levels.Count == 0) { tf.Width = w; tf.Height = h; }
                    var lvl = new byte[size];
                    Buffer.BlockCopy(u.Datos, off, lvl, 0, size);
                    tf.Levels.Add(lvl);
                }
                off += size;
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            if (tf.Levels.Count == 0) throw new InvalidDataException("sin niveles utilizables");
            return tf;
        }

        static double Num(ConfigNode n, string k, double def) =>
            double.TryParse(n.Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
    }
}
