using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Fuente XYZ clásica (teselas ya troceadas), local o por HTTP, en el esquema
       EPSG:4326: a zoom z el mundo son 2^(z+1) × 2^z teselas. Se descargan y
       decodifican fuera del hilo de la interfaz; la subida a la GPU va en el render. */
    public sealed class TileLayer : IDisposable
    {
        sealed class Tile { public Texture Tex; public bool Failed; public long Used; }

        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public readonly string Template;
        readonly Dictionary<(int z, int x, int y), Tile> cache = new();
        readonly ConcurrentQueue<((int, int, int) key, ImageData img)> ready = new();
        long tick;

        /* Se llama desde otro hilo cuando llega una tesela: quien lo use debe pasar
           al hilo de la interfaz antes de repintar. */
        public Action TileArrived;

        public TileLayer(string template) { Template = template; }

        public Texture Get(int z, int x, int y)
        {
            var key = (z, x, y);
            if (!cache.TryGetValue(key, out var t))
            {
                t = new Tile();
                cache[key] = t;
                _ = Load(key);
            }
            t.Used = ++tick;
            return t.Tex;
        }

        async Task Load((int z, int x, int y) key)
        {
            ImageData img = null;
            try
            {
                string url = Template.Replace("{z}", key.z.ToString()).Replace("{x}", key.x.ToString())
                                     .Replace("{y}", key.y.ToString()).Replace("{s}", "a");
                byte[] bytes;
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                else
                {
                    string root = Path.GetDirectoryName(Store.DataDir) ?? AppContext.BaseDirectory;
                    string path = Path.IsPathRooted(url) ? url : Path.Combine(root, url.Replace('/', Path.DirectorySeparatorChar));
                    bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                }
                img = await Task.Run(() => ImageData.Decode(bytes)).ConfigureAwait(false);
            }
            catch
            {
                // tesela que no existe: se queda vacía, como el errorTileUrl de Leaflet
            }
            ready.Enqueue((key, img));
            TileArrived?.Invoke();
        }

        /* Sube a la GPU lo que haya llegado. Solo desde el hilo con el contexto GL. */
        public void Pump()
        {
            while (ready.TryDequeue(out var r))
            {
                if (!cache.TryGetValue(r.key, out var t)) continue;
                if (r.img == null) t.Failed = true;
                else t.Tex = Texture.FromRgba(r.img.Rgba, r.img.Width, r.img.Height, TexFilter.Linear, false);
            }
            if (cache.Count > 600)
            {
                foreach (var kv in cache.Where(k => k.Value.Tex != null || k.Value.Failed)
                                        .OrderBy(k => k.Value.Used).Take(cache.Count - 400).ToList())
                {
                    kv.Value.Tex?.Dispose();
                    cache.Remove(kv.Key);
                }
            }
        }

        public void Dispose()
        {
            foreach (var t in cache.Values) t.Tex?.Dispose();
            cache.Clear();
        }
    }
}
