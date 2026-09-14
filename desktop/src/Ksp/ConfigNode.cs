using System;
using System.Collections.Generic;
using System.IO;

namespace KerbinMaps.Ksp
{
    /* Formato ConfigNode de KSP (los .cfg y la caché de ModuleManager): nombres,
       llaves y pares clave = valor. Se aceptan llaves en la misma línea que el nombre
       y comentarios con //. */
    public sealed class ConfigNode
    {
        public string Name;
        public readonly List<KeyValuePair<string, string>> Values = new();
        public readonly List<ConfigNode> Nodes = new();

        public ConfigNode(string name) { Name = name; }

        public string Get(string key)
        {
            foreach (var kv in Values) if (kv.Key == key) return kv.Value;
            return null;
        }

        public IEnumerable<string> GetAll(string key)
        {
            foreach (var kv in Values) if (kv.Key == key) yield return kv.Value;
        }

        public IEnumerable<ConfigNode> Children(string name)
        {
            foreach (var n in Nodes) if (n.Name == name) yield return n;
        }

        public static ConfigNode ParseFile(string path) => Parse(File.ReadLines(path));

        public static ConfigNode Parse(IEnumerable<string> lines)
        {
            var root = new ConfigNode("root");
            var stack = new Stack<ConfigNode>();
            stack.Push(root);
            string pend = null;

            foreach (var raw in lines)
            {
                string line = raw;
                int c = line.IndexOf("//", StringComparison.Ordinal);
                if (c >= 0) line = line.Substring(0, c);

                int i = 0;
                while (i < line.Length)
                {
                    int b = line.IndexOfAny(new[] { '{', '}' }, i);
                    string seg = (b < 0 ? line.Substring(i) : line.Substring(i, b - i)).Trim();
                    if (seg.Length > 0)
                    {
                        int eq = seg.IndexOf('=');
                        if (eq >= 0)
                        {
                            stack.Peek().Values.Add(new KeyValuePair<string, string>(seg.Substring(0, eq).Trim(), seg.Substring(eq + 1).Trim()));
                            pend = null;
                        }
                        else pend = seg;
                    }
                    if (b < 0) break;
                    if (line[b] == '{')
                    {
                        var n = new ConfigNode(pend ?? "");
                        stack.Peek().Nodes.Add(n);
                        stack.Push(n);
                    }
                    else if (stack.Count > 1) stack.Pop();
                    pend = null;
                    i = b + 1;
                }
            }
            return root;
        }
    }
}
