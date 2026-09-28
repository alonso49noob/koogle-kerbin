using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KerbinMaps.Ksp
{
    /* Edificios de Kerbal Konstructs: los modelos (nodos STATIC con su .mu), los centros de
       grupo (KK_GroupCenter) y las instancias (nodos Instances), leídos de los .cfg de
       GameData tal como los deja KK, y colocados con sus mismas cuentas.

       Cómo coloca KK cada cosa (GroupCenter.cs y StaticInstance.cs en su código):
       - Un centro de grupo va a su latitud y longitud, a RadiusOffset metros sobre el
         terreno (o sobre el nivel del mar con SeaLevelAsReference). Su giro es
         LookRotation(vertical) · Euler(0, 0, Heading) · Euler(-90, -90, -90); en los
         antiguos, sin Heading, el de PQSCity: de «arriba» a la vertical y RotationAngle
         grados alrededor de «arriba».
       - Una instancia es hija de su grupo: localPosition = RelativePosition,
         localEulerAngles = Orientation, localScale = ModelScale.
       - Las antiguas, sin RelativePosition, van por su cuenta: RadialPosition, a
         RadiusOffset sobre el nivel del mar (o sobre el terreno si IsRelativeToTerrain = 2),
         con el giro de PQSCity.
       - Los grupos «KSC_Builtin» y compañía son los sitios del juego (PQSCity de Kerbin):
         no están en ningún .cfg y aquí van con los valores del juego sin mods.

       Todo se calcula en el marco del cuerpo de KSP (Unity, mano izquierda, Y hacia el polo
       norte, x = cos lat cos lon, z = cos lat sin lon) y en metros. Para pasar al del visor
       basta cambiar X por Z, como con las naves. */
    public sealed class KkModel
    {
        public string Name, Title, Category, Author;
        public string Mesh;                       // ruta completa del .mu, o null
        public string CfgPath;
        // módulos AdvancedTextures: a qué objetos del modelo («Any»: todos) les cambia la textura
        public readonly List<(HashSet<string> Transforms, string MainTex)> TexSwaps = new();
        public bool HasMesh => Mesh != null && File.Exists(Mesh);

        /* El modelo montado, con las texturas que le cambia KK. `_MainTex` es una ruta de
           GameData sin extensión o «BUILTIN:/nombre», una textura del propio juego. */
        public AssembledVessel Build(string gameData, StockAssets stock)
        {
            var a = VesselAssembler.BuildModel(Mesh, stock);
            foreach (var (tr, tex) in TexSwaps)
            {
                string ruta = tex.StartsWith("BUILTIN:", StringComparison.OrdinalIgnoreCase)
                    ? stock?.Find(tex.Substring(tex.IndexOf(':') + 1).TrimStart('/'))
                    : TextureFile.Find(Path.Combine(gameData, tex.Replace('/', Path.DirectorySeparatorChar)));
                if (ruta == null) continue;
                foreach (var it in a.Items)
                    if (tr == null || tr.Contains(it.Node)) it.TexturePath = ruta;
            }
            return a;
        }
    }

    public sealed class KkGroup
    {
        public string Name, Body, CfgPath;
        public double Lat, Lon, RadiusOffset, Heading = 361, RotationAngle, Scale = 1;
        public bool Nuevo, Cambiado;              // creado o tocado en el editor, sin guardar
        public double[] Up = { 0, 1, 0 };
        public bool SeaLevel, Builtin, Implicit;
        public ConfigNode Node;

        // calculado por Place
        public double Alt;                        // m sobre el nivel del mar
        public double[] M;                        // 4x4 por columnas, en el marco del cuerpo de KSP
        public int Count;

        public string Key => Body + "_" + Name;
    }

    public sealed class KkInstance
    {
        public string Model, Body, Group = "Ungrouped", Uuid, CfgPath, LaunchSite;
        public int FileIndex = -1;                // qué nodo Instances es dentro de su fichero
        public bool Nuevo, Cambiado, Borrado;     // estado en el editor, sin guardar
        public double[] Rel = { 0, 0, 0 }, Euler = { 0, 0, 0 };
        public double Scale = 1, Visibility = 25000;
        // formato antiguo
        public double[] Radial = { 0, 0, 0 }, OrientUp = { 0, 1, 0 };
        public double RadiusOffset, RotationAngle;
        public int HeightRef;
        public ConfigNode Node;

        // calculado por Place
        public KkGroup GroupRef;
        public KkModel ModelRef;
        public double[] M;                        // modelo → marco del cuerpo de KSP, en metros
        public double Lat, Lon, Alt;
        public bool Placed;

        public bool Legacy => Rel[0] == 0 && Rel[1] == 0 && Rel[2] == 0;
        public string Label => ModelRef?.Title is string t && t.Length > 0 ? t : Model;
    }

    public sealed class KkDatabase
    {
        public readonly Dictionary<string, KkModel> Models = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, KkGroup> Groups = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<KkInstance> Instances = new();
        public int Files;
        public string GameData;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /* Los sitios del juego que KK usa como centros de grupo (BuiltinCenters.cs), con los
           valores de los PQSCity de Kerbin: posición radial, giro final y altura sobre el mar. */
        static readonly (string Name, double[] Radial, double Angle, double Offset)[] Builtins =
        {
            ("KSC", new double[] { 157000, -1000, -570000 }, -15, 42.7000007629395),
            ("KSC2", new double[] { -468960.406, 211164.703, -310261.688 }, -117.65, 201),
            ("Pyramids", new double[] { -468635.094, -68111.1016, -370297.094 }, 0, 98),
            ("IslandAirfield", new double[] { 186253.594, -16135.6797, -570176.813 }, 150, 28),
        };
        // los PQSCity2 del DLC Making History: latitud, longitud, giro y altura
        static readonly (string Name, double Lat, double Lon, double Angle, double Alt)[] Builtins2 =
        {
            ("Desert_Airfield", -6.51999963320189, -144.039999478851, -125, 822.840140053537),
            ("Woomerang_Launch_Site", 45.2899990947616, 136.1100029881, 135, 734.788969624788),
        };

        public static KkDatabase Load(string gameData, string homeWorld)
        {
            var db = new KkDatabase { GameData = gameData };
            foreach (var b in Builtins)
            {
                var n = Norm(b.Radial);
                db.Groups[homeWorld + "_" + b.Name + "_Builtin"] = new KkGroup
                {
                    Name = b.Name + "_Builtin", Body = homeWorld, Builtin = true, SeaLevel = true,
                    Lat = Math.Asin(n[1]) * 180 / Math.PI, Lon = Math.Atan2(n[2], n[0]) * 180 / Math.PI,
                    RotationAngle = b.Angle, RadiusOffset = b.Offset,
                };
            }
            if (Directory.Exists(Path.Combine(gameData, "SquadExpansion", "MakingHistory")))
                foreach (var b in Builtins2)
                    db.Groups[homeWorld + "_" + b.Name + "_Builtin"] = new KkGroup
                    {
                        Name = b.Name + "_Builtin", Body = homeWorld, Builtin = true, SeaLevel = true,
                        Lat = b.Lat, Lon = b.Lon, RotationAngle = b.Angle, RadiusOffset = b.Alt,
                    };

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(gameData, "*.cfg", SearchOption.AllDirectories).ToList(); }
            catch { return db; }
            var pendientes = new List<(ConfigNode Node, string Path)>();
            foreach (var path in files)
            {
                string text;
                try { text = File.ReadAllText(path); }
                catch { continue; }
                // la mayoría de los .cfg no tienen nada de KK: se descartan sin analizarlos
                if (!text.Contains("STATIC", StringComparison.Ordinal) && !text.Contains("KK_GroupCenter", StringComparison.Ordinal)) continue;
                ConfigNode root;
                try { root = ConfigNode.Parse(text.Split('\n')); }
                catch { continue; }
                bool alguno = false;
                foreach (var n in root.Nodes)
                {
                    // «@STATIC», «+STATIC:NEEDS[...]»... son parches de ModuleManager, no definiciones
                    if (n.Name == "STATIC") { pendientes.Add((n, path)); alguno = true; }
                    else if (n.Name == "KK_GroupCenter") { db.AddGroup(n, path); alguno = true; }
                }
                if (alguno) db.Files++;
            }
            // primero todos los modelos, porque una instancia puede ir en otro fichero
            foreach (var (n, path) in pendientes)
                if (n.Get("mesh") != null) db.AddModel(n, path);
            var porFichero = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (n, path) in pendientes)
            {
                string modelo = n.Get("pointername") is string pn && pn.Length > 0 && !pn.Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? pn : n.Get("name");
                if (string.IsNullOrEmpty(modelo))
                {
                    porFichero[path] = porFichero.GetValueOrDefault(path) + n.Children("Instances").Count();
                    continue;
                }
                foreach (var inst in n.Children("Instances"))
                {
                    var i = ParseInstance(inst, modelo, path);
                    i.FileIndex = porFichero.GetValueOrDefault(path);
                    porFichero[path] = i.FileIndex + 1;
                    db.Instances.Add(i);
                }
            }
            foreach (var i in db.Instances)
                i.ModelRef = db.Models.GetValueOrDefault(i.Model);
            return db;
        }

        void AddModel(ConfigNode n, string path)
        {
            string name = n.Get("name");
            if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(path);
            string mesh = n.Get("mesh");
            if (mesh.Contains('.')) mesh = mesh.Substring(0, mesh.LastIndexOf('.'));
            Models[name] = new KkModel
            {
                Name = name,
                Title = n.Get("title"),
                Category = n.Get("category"),
                Author = n.Get("author"),
                Mesh = Path.Combine(Path.GetDirectoryName(path), mesh.Replace('/', Path.DirectorySeparatorChar) + ".mu"),
                CfgPath = path,
            };
            foreach (var mod in n.Children("MODULE"))
            {
                if (mod.Get("name") != "AdvancedTextures" || string.IsNullOrWhiteSpace(mod.Get("_MainTex"))) continue;
                string tr = mod.Get("transforms") ?? "Any";
                var set = tr.Trim().Equals("Any", StringComparison.OrdinalIgnoreCase) ? null
                    : new HashSet<string>(tr.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                Models[name].TexSwaps.Add((set, mod.Get("_MainTex").Trim()));
            }
        }

        void AddGroup(ConfigNode n, string path)
        {
            var g = new KkGroup
            {
                Name = n.Get("Group") ?? "",
                Body = n.Get("CelestialBody") ?? "Kerbin",
                CfgPath = path,
                Node = n,
                RadiusOffset = D(n.Get("RadiusOffset"), 0),
                Heading = D(n.Get("Heading"), 361),
                RotationAngle = D(n.Get("RotationAngle"), 0),
                Scale = D(n.Get("ModelScale"), 1),
                SeaLevel = Bool(n.Get("SeaLevelAsReference")),
                Up = V(n.Get("Orientation")) ?? new double[] { 0, 1, 0 },
            };
            double lat = D(n.Get("RefLatitude"), 361), lon = D(n.Get("RefLongitude"), 361);
            if ((lat == 361 || lon == 361) && V(n.Get("RadialPosition")) is double[] rp && Len(rp) > 0)
            {
                var u = Norm(rp);
                lat = Math.Asin(u[1]) * 180 / Math.PI;
                lon = Math.Atan2(u[2], u[0]) * 180 / Math.PI;
            }
            if (lat == 361 || lon == 361) return;
            g.Lat = lat; g.Lon = lon;
            Groups[g.Key] = g;
        }

        static KkInstance ParseInstance(ConfigNode n, string modelo, string path)
        {
            var i = new KkInstance
            {
                Model = modelo,
                Body = n.Get("CelestialBody") ?? "Kerbin",
                Group = n.Get("Group") is string gr && gr.Length > 0 ? gr : "Ungrouped",
                Uuid = n.Get("UUID"),
                CfgPath = path,
                Node = n,
                Rel = V(n.Get("RelativePosition")) ?? new double[3],
                Euler = V(n.Get("Orientation")) ?? new double[3],
                Scale = D(n.Get("ModelScale"), 1),
                Visibility = D(n.Get("VisibilityRange"), 25000),
                Radial = V(n.Get("RadialPosition")) ?? new double[3],
                RadiusOffset = D(n.Get("RadiusOffset"), 0),
                RotationAngle = D(n.Get("RotationAngle"), 0),
                HeightRef = (int)D(n.Get("IsRelativeToTerrain"), 0),
            };
            // en el formato antiguo «Orientation» es el vector «arriba» del modelo
            if (i.Legacy) i.OrientUp = Len(i.Euler) > 0 ? (double[])i.Euler.Clone() : new double[] { 0, 1, 0 };
            if (n.Children("LaunchSite").FirstOrDefault() is ConfigNode ls) i.LaunchSite = ls.Get("LaunchSiteName");
            i.LaunchSite ??= n.Get("LaunchSiteName");
            return i;
        }

        /* Coloca los grupos y las instancias de un cuerpo. `ground` da la altura del
           terreno (m) en una latitud y longitud: la del mapa de alturas que pinta el visor,
           para que los edificios queden sobre el mismo suelo que se ve. */
        public void Place(string body, double radius, Func<double, double, double> ground)
        {
            foreach (var g in Groups.Values.Where(g => g.Body == body && g.Implicit).ToList()) Groups.Remove(g.Key);
            foreach (var g in Groups.Values)
            {
                if (g.Body != body) continue;
                PlaceGroup(g, radius, ground);
                g.Count = 0;
            }
            foreach (var i in Instances)
            {
                i.Placed = false;
                if (i.Body != body || i.Borrado) continue;
                double[] m;
                if (i.Legacy)
                {
                    if (Len(i.Radial) == 0) continue;
                    var n = Norm(i.Radial);
                    double lat = Math.Asin(n[1]) * 180 / Math.PI, lon = Math.Atan2(n[2], n[0]) * 180 / Math.PI;
                    double alt = i.RadiusOffset + (i.HeightRef == 2 ? ground(lat, lon) : 0);
                    m = Mat.Mul(Mat.Translate(Scale(n, radius + alt)),
                        Mat.Mul(Mat.Rotate(Mat.QMul(FromTo(i.OrientUp, n), AngleAxis(i.RotationAngle, new double[] { 0, 1, 0 }))),
                                Mat.Scale(i.Scale, i.Scale, i.Scale)));
                    i.GroupRef = Groups.GetValueOrDefault(body + "_" + i.Group);
                }
                else
                {
                    if (!Groups.TryGetValue(body + "_" + i.Group, out var g))
                    {
                        // KK crea el centro donde diga RadialPosition, si lo hay
                        if (Len(i.Radial) == 0) continue;
                        var n = Norm(i.Radial);
                        g = new KkGroup
                        {
                            Name = i.Group, Body = body, Implicit = true,
                            Lat = Math.Asin(n[1]) * 180 / Math.PI, Lon = Math.Atan2(n[2], n[0]) * 180 / Math.PI,
                        };
                        Groups[g.Key] = g;
                        PlaceGroup(g, radius, ground);
                    }
                    i.GroupRef = g;
                    m = Mat.Mul(g.M, Mat.Mul(Mat.Translate(i.Rel),
                        Mat.Mul(Mat.Rotate(Mat.Euler(i.Euler)), Mat.Scale(i.Scale, i.Scale, i.Scale))));
                }
                i.M = m;
                double x = m[12], y = m[13], z = m[14];
                double r = Math.Sqrt(x * x + y * y + z * z);
                i.Lat = Math.Asin(y / r) * 180 / Math.PI;
                i.Lon = Math.Atan2(z, x) * 180 / Math.PI;
                i.Alt = r - radius;
                i.Placed = true;
                if (i.GroupRef != null) i.GroupRef.Count++;
            }
        }

        public static void PlaceGroup(KkGroup g, double radius, Func<double, double, double> ground)
        {
            var n = NVec(g.Lat, g.Lon);
            g.Alt = g.SeaLevel ? g.RadiusOffset : ground(g.Lat, g.Lon) + g.RadiusOffset;
            double[] rot;
            if (g.Heading < 361)
                rot = Mat.Mul(LookRotation(n), Mat.Mul(Mat.Rotate(Mat.Euler(new[] { 0, 0, g.Heading })), Mat.Rotate(Mat.Euler(new double[] { -90, -90, -90 }))));
            else
                rot = Mat.Rotate(Mat.QMul(FromTo(g.Up, n), AngleAxis(g.RotationAngle, g.Up)));
            g.M = Mat.Mul(Mat.Translate(Scale(n, radius + g.Alt)), Mat.Mul(rot, Mat.Scale(g.Scale, g.Scale, g.Scale)));
        }

        /* ------------------------------------------------ edición */

        /* Este, norte y vertical de un punto, en el marco de KSP. */
        public static (double[] E, double[] N, double[] U) Enu(double lat, double lon)
        {
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
            return (new[] { -Math.Sin(lo), 0, Math.Cos(lo) },
                    new[] { -Math.Sin(la) * Math.Cos(lo), Math.Cos(la), -Math.Sin(la) * Math.Sin(lo) },
                    NVec(lat, lon));
        }

        /* Mueve una instancia unos metros hacia el este, el norte y arriba (en su sitio).
           Lo que cambia es su posición dentro del grupo. */
        public static bool Move(KkInstance i, double dE, double dN, double dU)
        {
            var g = i.GroupRef;
            if (g?.M == null || i.Legacy || !i.Placed) return false;
            var (e, n, u) = Enu(i.Lat, i.Lon);
            var d = new double[3];
            for (int k = 0; k < 3; k++) d[k] = e[k] * dE + n[k] * dN + u[k] * dU;
            var local = ToLocal(g, d);
            for (int k = 0; k < 3; k++) i.Rel[k] += local[k];
            i.Cambiado = true;
            return true;
        }

        /* Gira alrededor de la vertical del grupo (su eje Y), que es lo que hace el ángulo Y
           de Orientation. Positivo: en el sentido de las agujas del reloj visto desde arriba. */
        public static void Rotate(KkInstance i, double deg)
        {
            i.Euler[1] = ((i.Euler[1] + deg) % 360 + 360) % 360;
            i.Cambiado = true;
        }

        /* Rumbo al que mira el modelo (su eje Z), en grados desde el norte. */
        public static double Heading(KkInstance i)
        {
            if (i.M == null) return 0;
            var f = new[] { i.M[8], i.M[9], i.M[10] };
            var (e, n, _) = Enu(i.Lat, i.Lon);
            double h = Math.Atan2(f[0] * e[0] + f[1] * e[1] + f[2] * e[2], f[0] * n[0] + f[1] * n[1] + f[2] * n[2]) * 180 / Math.PI;
            return (h + 360) % 360;
        }

        /* Un vector del marco de KSP al de dentro del grupo (sin traslación). */
        static double[] ToLocal(KkGroup g, double[] d)
        {
            // las columnas de la matriz del grupo son sus ejes, con su escala
            var r = new double[3];
            for (int c = 0; c < 3; c++)
            {
                double ax = g.M[c * 4], ay = g.M[c * 4 + 1], az = g.M[c * 4 + 2];
                double l2 = ax * ax + ay * ay + az * az;
                r[c] = (d[0] * ax + d[1] * ay + d[2] * az) / l2;
            }
            return r;
        }

        /* Un edificio nuevo en un punto: va al grupo más cercano (a menos de 25 km, como
           los de KK) o a uno nuevo, mirando al rumbo dado. */
        public KkInstance Add(KkModel model, string body, double radius, double lat, double lon, double alt, double heading,
                              Func<double, double, double> ground, string grupoNuevo)
        {
            var pos = Scale(NVec(lat, lon), radius + alt);
            KkGroup g = null;
            double mejor = 25000;
            foreach (var c in Groups.Values)
            {
                if (c.Body != body || c.M == null || c.Implicit) continue;
                double dx = c.M[12] - pos[0], dy = c.M[13] - pos[1], dz = c.M[14] - pos[2];
                double dd = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dd < mejor) { mejor = dd; g = c; }
            }
            if (g == null)
            {
                string nombre = grupoNuevo;
                for (int k = 1; Groups.ContainsKey(body + "_" + nombre); k++) nombre = grupoNuevo + "_" + k;
                g = new KkGroup { Name = nombre, Body = body, Lat = lat, Lon = lon, Heading = 0, Nuevo = true, Cambiado = true };
                Groups[g.Key] = g;
                PlaceGroup(g, radius, ground);
            }
            var d = new[] { pos[0] - g.M[12], pos[1] - g.M[13], pos[2] - g.M[14] };
            var i = new KkInstance
            {
                Model = model.Name, ModelRef = model, Body = body, Group = g.Name, GroupRef = g,
                Uuid = Guid.NewGuid().ToString(), Rel = ToLocal(g, d), Nuevo = true, Cambiado = true,
            };
            Instances.Add(i);
            PlaceOne(i, radius);
            // que mire al rumbo pedido
            Rotate(i, heading - Heading(i));
            PlaceOne(i, radius);
            g.Count++;
            return i;
        }

        /* Copia de una instancia unos metros al este, para moverla después. */
        public KkInstance Duplicate(KkInstance src, double radius)
        {
            var i = new KkInstance
            {
                Model = src.Model, ModelRef = src.ModelRef, Body = src.Body, Group = src.Group, GroupRef = src.GroupRef,
                Uuid = Guid.NewGuid().ToString(), Rel = (double[])src.Rel.Clone(), Euler = (double[])src.Euler.Clone(),
                Scale = src.Scale, Visibility = src.Visibility, Nuevo = true, Cambiado = true,
            };
            Instances.Add(i);
            PlaceOne(i, radius);
            Move(i, 20, 0, 0);
            PlaceOne(i, radius);
            if (i.GroupRef != null) i.GroupRef.Count++;
            return i;
        }

        /* Recoloca una sola instancia con su grupo ya colocado (tras editarla). */
        public void PlaceOne(KkInstance i, double radius)
        {
            var g = i.GroupRef;
            if (g?.M == null || i.Legacy) return;
            var m = Mat.Mul(g.M, Mat.Mul(Mat.Translate(i.Rel),
                Mat.Mul(Mat.Rotate(Mat.Euler(i.Euler)), Mat.Scale(i.Scale, i.Scale, i.Scale))));
            i.M = m;
            double x = m[12], y = m[13], z = m[14];
            double r = Math.Sqrt(x * x + y * y + z * z);
            i.Lat = Math.Asin(y / r) * 180 / Math.PI;
            i.Lon = Math.Atan2(z, x) * 180 / Math.PI;
            i.Alt = r - radius;
            i.Placed = true;
        }

        /* ------------------------------------------------ cuentas de Unity */

        public static double[] NVec(double lat, double lon)
        {
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
            return new[] { Math.Cos(la) * Math.Cos(lo), Math.Sin(la), Math.Cos(la) * Math.Sin(lo) };
        }

        // Quaternion.LookRotation(f) con «arriba» el eje Y: columnas derecha, arriba, delante
        static double[] LookRotation(double[] f)
        {
            var r = Norm(Cross(new double[] { 0, 1, 0 }, f));
            var u = Cross(f, r);
            return new double[] { r[0], r[1], r[2], 0, u[0], u[1], u[2], 0, f[0], f[1], f[2], 0, 0, 0, 0, 1 };
        }

        static double[] AngleAxis(double deg, double[] axis)
        {
            var a = Norm(axis);
            double h = deg * Math.PI / 360, s = Math.Sin(h);
            return new[] { a[0] * s, a[1] * s, a[2] * s, Math.Cos(h) };
        }

        // Quaternion.FromToRotation: el giro más corto que lleva «a» a «b»
        static double[] FromTo(double[] a, double[] b)
        {
            a = Norm(a); b = Norm(b);
            double d = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
            if (d < -0.999999)
            {
                var ax = Cross(new double[] { 1, 0, 0 }, a);
                if (Len(ax) < 1e-6) ax = Cross(new double[] { 0, 1, 0 }, a);
                return AngleAxis(180, ax);
            }
            var c = Cross(a, b);
            var q = new[] { c[0], c[1], c[2], 1 + d };
            double l = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            return new[] { q[0] / l, q[1] / l, q[2] / l, q[3] / l };
        }

        static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        static double Len(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        static double[] Norm(double[] v) { double l = Len(v); return l < 1e-12 ? new double[] { 0, 1, 0 } : new[] { v[0] / l, v[1] / l, v[2] / l }; }
        static double[] Scale(double[] v, double k) => new[] { v[0] * k, v[1] * k, v[2] * k };

        /* ------------------------------------------------ lectura de valores */

        public static double D(string s, double def) =>
            double.TryParse(s?.Trim(), NumberStyles.Float, Inv, out double v) && double.IsFinite(v) ? v : def;

        static bool Bool(string s) => s != null && s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        public static double[] V(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var p = s.Split(',');
            if (p.Length < 3) return null;
            var v = new double[3];
            for (int k = 0; k < 3; k++) v[k] = D(p[k], double.NaN);
            return v.All(double.IsFinite) ? v : null;
        }
    }
}
