using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KerbinMaps.Ksp
{
    /* Textures from the game's own data (KSP_x64_Data/sharedassets*.assets).

       Kerbal Konstructs buildings reuse the stock KSC textures: their .mu asks for
       «model_vab_exterior_tile_00» or «ksc_exterior_terrain_asphalt», which aren't in GameData
       but inside the game's Unity files, and KK looks them up by name among the ones the game
       has loaded. Here we do the same: the Texture2D objects (class 28) in those files are
       indexed by name and read when needed, with their bytes in the .resS next to them.

       Some .mu files store the name as an extraction tool exported it:
       «name-sharedassets9.assets-478.mbm». That suffix gives the exact file and object. */
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

        /* The one for an installation, from its GameData (KSP_x64_Data sits next to it). */
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

        /* Key of a texture by the name the .mu carries, or null if the game doesn't have it. */
        public string Find(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)) return null;
            string bare = Path.GetFileNameWithoutExtension(nombre);
            lock (cerrojo)
            {
                // «name-sharedassets9.assets-478»: exact file and object
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

        /* The texture as it comes (Unity format and bytes), unconverted: for those in formats
           that have to be decompressed elsewhere, like BC7. */
        public UnitySerialized.Textura LoadCruda(string clave)
        {
            if (clave == null || !clave.StartsWith(Prefijo, StringComparison.Ordinal)) return null;
            var p = clave.Substring(Prefijo.Length).Split(':');
            if (p.Length != 2 || !long.TryParse(p[1], out long id)) return null;
            lock (cerrojo)
            {
                var s = SerializadoSinCerrojo(p[0]);
                return s?.LeerTextura(id, (ruta, off, size) => LeerRecursoSinCerrojo(ruta, off, size));
            }
        }

        byte[] LeerRecursoSinCerrojo(string ruta, long off, int size)
        {
            string nombre = Path.GetFileName(ruta.Replace("archive:/", ""));
            if (!recursos.TryGetValue(nombre, out var rf))
            {
                string full = Path.Combine(dir, nombre);
                recursos[nombre] = rf = File.Exists(full) ? new UnityFile(full) : null;
            }
            return rf?.Read(off, size);
        }

        /* The game's sharedassets files, by name. */
        public IEnumerable<string> Ficheros()
        {
            try { return Directory.EnumerateFiles(dir, "sharedassets*.assets").Select(Path.GetFileName).ToList(); }
            catch { return Array.Empty<string>(); }
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

        /* Bytes of the resource file (.resS) next to it, by the path the object carries. */
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

        /* The file name of an already-open serialized file. */
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

        /* All the textures in the sharedassets, by name. If two have the same name the larger
           one wins, which is usually the right one (the small ones are icons or far versions). */
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
