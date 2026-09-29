using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KerbinMaps.Ksp
{
    /* Texturas de los datos del propio juego (KSP_x64_Data/sharedassets*.assets).

       Los edificios de Kerbal Konstructs reutilizan las texturas del KSC de serie: su .mu
       pide «model_vab_exterior_tile_00» o «ksc_exterior_terrain_asphalt», que no están en
       GameData sino dentro de los ficheros de Unity del juego, y KK las busca por nombre
       entre las que el juego tiene cargadas. Aquí se hace lo mismo: se indexan por nombre
       las Texture2D (clase 28) de esos ficheros y se leen cuando hacen falta, con sus bytes
       en el .resS de al lado.

       Algunos .mu guardan el nombre como lo exportó una herramienta de extracción:
       «nombre-sharedassets9.assets-478.mbm». Ese sufijo dice fichero y objeto exactos. */
    public sealed class StockAssets
    {
        static readonly Dictionary<string, StockAssets> porCarpeta = new(StringComparer.OrdinalIgnoreCase);

        readonly string dir;
        readonly Dictionary<string, (UnityFile F, UnitySerialized S)> ficheros = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (string File, long Id)> porNombre;
        readonly Dictionary<string, UnityFile> recursos = new(StringComparer.OrdinalIgnoreCase);
        readonly object cerrojo = new();

        public const string Prefijo = "stock:";

        StockAssets(string dir) { this.dir = dir; }

        public string Dir => dir;

        /* La de una instalación, a partir de su GameData (KSP_x64_Data va al lado). */
        public static StockAssets For(string gameData)
        {
            if (gameData == null) return null;
            string raiz = Path.GetDirectoryName(Path.GetFullPath(gameData).TrimEnd('\\', '/'));
            string d = Path.Combine(raiz ?? "", "KSP_x64_Data");
            if (!Directory.Exists(d)) d = Path.Combine(raiz ?? "", "KSP_Data");
            if (!Directory.Exists(d)) return null;
            lock (porCarpeta)
            {
                if (!porCarpeta.TryGetValue(d, out var s)) porCarpeta[d] = s = new StockAssets(d);
                return s;
            }
        }

        /* Clave de una textura por el nombre que trae el .mu, o null si el juego no la tiene. */
        public string Find(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)) return null;
            string bare = Path.GetFileNameWithoutExtension(nombre);
            lock (cerrojo)
            {
                // «nombre-sharedassets9.assets-478»: fichero y objeto exactos
                int guion = bare.LastIndexOf('-');
                if (guion > 0 && long.TryParse(bare.Substring(guion + 1), out long id))
                {
                    string resto = bare.Substring(0, guion);
                    int g2 = resto.LastIndexOf('-');
                    if (g2 > 0 && resto.Substring(g2 + 1).EndsWith(".assets", StringComparison.OrdinalIgnoreCase))
                    {
                        string file = resto.Substring(g2 + 1);
                        var s = SerializadoSinCerrojo(file);
                        if (s != null && s.Objetos.TryGetValue(id, out var o) && o.ClassId == 28) return Prefijo + file + ":" + id;
                        bare = resto.Substring(0, g2);
                    }
                }
                Indexar();
                return porNombre.TryGetValue(bare, out var r) ? Prefijo + r.File + ":" + r.Id : null;
            }
        }

        public TextureFile Load(string clave)
        {
            if (!clave.StartsWith(Prefijo, StringComparison.Ordinal)) return null;
            var p = clave.Substring(Prefijo.Length).Split(':');
            if (p.Length != 2 || !long.TryParse(p[1], out long id)) return null;
            lock (cerrojo)
            {
                var s = SerializadoSinCerrojo(p[0]);
                if (s == null) return null;
                var t = s.LeerTextura(id, (ruta, off, size) =>
                {
                    string nombre = Path.GetFileName(ruta.Replace("archive:/", ""));
                    if (!recursos.TryGetValue(nombre, out var rf))
                    {
                        string full = Path.Combine(dir, nombre);
                        recursos[nombre] = rf = File.Exists(full) ? new UnityFile(full) : null;
                    }
                    return rf?.Read(off, size);
                });
                return ParallaxTerrain.Convertir(t, 2048);
            }
        }

        /* Bytes del fichero de recursos (.resS) de al lado, por la ruta que trae el objeto. */
        public byte[] LeerRecurso(string ruta, long off, int size)
        {
            lock (cerrojo)
            {
                string nombre = Path.GetFileName(ruta.Replace("archive:/", ""));
                if (!recursos.TryGetValue(nombre, out var rf))
                {
                    string full = Path.Combine(dir, nombre);
                    recursos[nombre] = rf = File.Exists(full) ? new UnityFile(full) : null;
                }
                return rf?.Read(off, size);
            }
        }

        /* El nombre del fichero de un serializado ya abierto. */
        public string NombreDe(UnitySerialized x)
        {
            lock (cerrojo) return ficheros.FirstOrDefault(kv => kv.Value.S == x).Key;
        }

        public UnitySerialized Serializado(string file)
        {
            lock (cerrojo) return SerializadoSinCerrojo(file);
        }

        UnitySerialized SerializadoSinCerrojo(string file)
        {
            if (ficheros.TryGetValue(file, out var e)) return e.S;
            UnitySerialized s = null;
            UnityFile f = null;
            try
            {
                string full = Path.Combine(dir, file);
                if (File.Exists(full))
                {
                    f = new UnityFile(full);
                    s = new UnitySerialized(f, 0, f.Length);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[stock] " + file + ": " + ex.Message);
                f?.Dispose();
                f = null; s = null;
            }
            ficheros[file] = (f, s);
            return s;
        }

        /* Todas las texturas de los sharedassets, por nombre. Si dos se llaman igual gana la
           más grande, que suele ser la buena (las pequeñas son iconos o versiones lejanas). */
        void Indexar()
        {
            if (porNombre != null) return;
            porNombre = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
            var tam = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "sharedassets*.assets").Select(Path.GetFileName).ToList(); }
            catch { return; }
            foreach (var file in files)
            {
                var s = SerializadoSinCerrojo(file);
                if (s == null) continue;
                foreach (var kv in s.Objetos)
                {
                    if (kv.Value.ClassId != 28) continue;
                    string n;
                    try { n = s.Nombre(kv.Key); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(n)) continue;
                    if (tam.TryGetValue(n, out long previo) && previo >= kv.Value.Size) continue;
                    tam[n] = kv.Value.Size;
                    porNombre[n] = (file, kv.Key);
                }
            }
        }
    }
}
