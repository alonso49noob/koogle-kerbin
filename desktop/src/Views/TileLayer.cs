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
    /* Classic XYZ source (pre-cut tiles), local or over HTTP, in the EPSG:4326 scheme: at zoom
       z the world is 2^(z+1) × 2^z tiles. They're downloaded and decoded off the UI thread; the
       GPU upload happens in the render. */
    public sealed class TileLayer : IDisposable
    {
        sealed class Tile { public Texture Tex; public bool Failed; public long Used; }

        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public readonly string Template;
        readonly Dictionary<(int z, int x, int y), Tile> cache = new();
        readonly ConcurrentQueue<((int, int, int) key, ImageData img)> ready = new();
        long tick;

        /* Called from another thread when a tile arrives: whoever uses it must switch to the UI
           thread before repainting. */
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
                // tile that doesn't exist: it stays empty, like Leaflet's errorTileUrl
            }
            ready.Enqueue((key, img));
            TileArrived?.Invoke();
        }

        /* Uploads to the GPU whatever has arrived. Only from the thread with the GL context. */
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
