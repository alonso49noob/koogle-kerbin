using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KerbinMaps.Ksp
{
    public sealed class MuMesh
    {
        public int VertCount;
        public float[] Verts, Normals, Uvs;
        public readonly List<int[]> Submeshes = new();
        /* Malla con esqueleto: 32 bytes por vértice con cuatro índices de hueso y cuatro
           pesos, y una pose de enlace (16 valores) por hueso. */
        public byte[] BoneWeightsRaw;
        public float[] BindPoses;
    }

    public sealed class MuNode
    {
        public string Name;
        public float[] Pos = new float[3];
        public float[] Rot = { 0, 0, 0, 1 };          // x, y, z, w como en Unity
        public float[] Scale = { 1, 1, 1 };
        public MuMesh Mesh;
        public int[] Materials;
        public string[] Bones;                        // SkinnedMeshRenderer: los huesos, por nombre
        public string Tag;                            // etiqueta de Unity («Icon_Only»: solo para el icono)
        public readonly List<MuNode> Children = new();
        public List<MuClip> Clips;                    // si el objeto lleva un componente Animation
    }

    public struct MuKey
    {
        public float Time, Value, InTan, OutTan;
    }

    public sealed class MuCurve
    {
        public string Path, Property;
        public int Type;
        public MuKey[] Keys;

        /* Curva de animación de Unity: tramos de Hermite con las tangentes de cada clave;
           una tangente infinita es un escalón. */
        public float Eval(float t)
        {
            var k = Keys;
            if (t <= k[0].Time) return k[0].Value;
            if (t >= k[^1].Time) return k[^1].Value;
            int i = 0;
            while (i < k.Length - 2 && t >= k[i + 1].Time) i++;
            float dt = k[i + 1].Time - k[i].Time;
            if (dt <= 0) return k[i + 1].Value;
            float s = (t - k[i].Time) / dt;
            float m0 = k[i].OutTan * dt, m1 = k[i + 1].InTan * dt;
            if (float.IsInfinity(m0) || float.IsInfinity(m1) || float.IsNaN(m0) || float.IsNaN(m1)) return k[i].Value;
            float s2 = s * s, s3 = s2 * s;
            return (2 * s3 - 3 * s2 + 1) * k[i].Value + (s3 - 2 * s2 + s) * m0 + (-2 * s3 + 3 * s2) * k[i + 1].Value + (s3 - s2) * m1;
        }
    }

    public sealed class MuClip
    {
        public string Name;
        public int WrapMode;
        public readonly List<MuCurve> Curves = new();

        public float Length
        {
            get
            {
                float len = 0;
                foreach (var c in Curves) if (c.Keys.Length > 0) len = Math.Max(len, c.Keys[^1].Time);
                return len;
            }
        }
    }

    public sealed class MuMaterial
    {
        public string Name, Shader;
        public int MainTex = -1;
        public float[] TexScale = { 1, 1 }, TexOffset = { 0, 0 };
        public float[] Color = { 1, 1, 1, 1 };
        public bool Cutout, Transparent;
    }

    public sealed class MuTextureRef
    {
        public string Name;
        public int Type;
    }

    /* Modelo .mu de KSP: la jerarquía de objetos de Unity con sus mallas, materiales y
       texturas, en el formato binario que documenta io_object_mu. Todo lo que no se
       pinta (colisionadores, animaciones, luces, partículas) se lee igualmente, porque
       el formato no guarda tamaños y hay que recorrerlo para llegar a lo siguiente.
       Las coordenadas se dejan tal cual, en el marco de mano izquierda de Unity. */
    public sealed class MuFile
    {
        public const int Magic = 76543;

        public int Version;
        public string Name;
        public MuNode Root;
        public readonly List<MuMaterial> Materials = new();
        public readonly List<MuTextureRef> Textures = new();

        BinaryReader br;
        long length;

        public static MuFile Load(string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs, Encoding.UTF8);
            var mu = new MuFile { br = br, length = fs.Length };
            if (br.ReadInt32() != Magic) throw new InvalidDataException("no es un modelo .mu");
            mu.Version = br.ReadInt32();
            if (mu.Version < 0 || mu.Version > 6) throw new InvalidDataException("versión de .mu no soportada: " + mu.Version);
            mu.Name = mu.S();
            mu.Root = mu.ReadObject();
            mu.br = null;
            return mu;
        }

        int I() => br.ReadInt32();
        float F() => br.ReadSingle();
        byte B() => br.ReadByte();
        void Skip(long n) => br.BaseStream.Seek(n, SeekOrigin.Current);

        string S()
        {
            int len = 0, shift = 0;
            while (true)
            {
                byte b = br.ReadByte();
                len |= (b & 127) << shift;
                if (b < 128) break;
                shift += 7;
            }
            return Encoding.UTF8.GetString(br.ReadBytes(len));
        }

        float[] V3() => new[] { F(), F(), F() };

        MuNode ReadObject()
        {
            var node = new MuNode { Name = S(), Pos = V3(), Rot = new[] { F(), F(), F(), F() }, Scale = V3() };
            while (br.BaseStream.Position + 4 <= length)
            {
                int t = I();
                switch (t)
                {
                    case 0: node.Children.Add(ReadObject()); break;
                    case 1: return node;
                    case 24: node.Tag = S(); I(); break;                                // etiqueta y capa
                    case 3: B(); ReadMesh(); break;                                     // colisionador de malla
                    case 25: B(); B(); ReadMesh(); break;
                    case 4: Skip(4 + 12); break;                                        // esfera
                    case 26: Skip(1 + 4 + 12); break;
                    case 5: Skip(4 + 4 + 4 + 12); break;                                // cápsula
                    case 27: Skip(1 + 4 + 4 + 4 + 12); break;
                    case 6: Skip(12 + 12); break;                                       // caja
                    case 28: Skip(1 + 12 + 12); break;
                    case 29: Skip(4 + 4 + 4 + 12 + 3 * 4 + 5 * 4 * 2); break;           // rueda
                    case 7: node.Mesh = ReadMesh(); break;
                    case 8:
                        if (Version > 0) { B(); B(); }
                        node.Materials = Ints(I());
                        break;
                    case 9:
                    {
                        var mats = Ints(I());
                        Skip(12 + 12 + 4 + 1);
                        int bones = I();
                        node.Bones = new string[bones];
                        for (int i = 0; i < bones; i++) node.Bones[i] = S();
                        node.Mesh = ReadMesh();
                        node.Materials = mats;
                        break;
                    }
                    case 2: node.Clips = ReadAnimation(); break;
                    case 30: Skip(4 + 16 + 4 + 1 + 4 + 4 + 4 + 4); break;              // cámara
                    case 31: SkipParticles(); break;
                    case 23: Skip(4 + 4 + 4 + 16 + 4 + (Version > 1 ? 4 : 0)); break;  // luz
                    case 10:
                    {
                        int n = I();
                        for (int i = 0; i < n; i++) Materials.Add(ReadMaterial());
                        break;
                    }
                    case 12:
                    {
                        int n = I();
                        for (int i = 0; i < n; i++) Textures.Add(new MuTextureRef { Name = S(), Type = I() });
                        break;
                    }
                    default:
                        throw new InvalidDataException("entrada .mu desconocida: " + t + " en " + (br.BaseStream.Position - 4));
                }
            }
            return node;
        }

        int[] Ints(int n)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = I();
            return a;
        }

        MuMesh ReadMesh()
        {
            if (I() != 13) throw new InvalidDataException("malla .mu sin cabecera");
            var m = new MuMesh { VertCount = I() };
            I();                                                                        // número de submallas
            int nv = m.VertCount;
            while (true)
            {
                int t = I();
                switch (t)
                {
                    case 22: return m;
                    case 14: m.Verts = Floats(nv * 3); break;
                    case 15: m.Uvs = Floats(nv * 2); break;
                    case 16: Skip(nv * 8L); break;
                    case 17: m.Normals = Floats(nv * 3); break;
                    case 18: Skip(nv * 16L); break;
                    case 20: m.BoneWeightsRaw = br.ReadBytes(nv * 32); break;
                    case 21: m.BindPoses = Floats(I() * 16); break;
                    case 19: m.Submeshes.Add(Ints(I())); break;
                    case 32: Skip(nv * 4L); break;
                    default: throw new InvalidDataException("bloque de malla .mu desconocido: " + t);
                }
            }
        }

        float[] Floats(int n)
        {
            var bytes = br.ReadBytes(n * 4);
            var a = new float[n];
            Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length);
            return a;
        }

        /* Animaciones del objeto: las de desplegar paneles, antenas o patas. Cada curva
           mueve una componente (m_LocalPosition.x, m_LocalRotation.w...) de un objeto
           indicado por su ruta relativa al que lleva la animación. */
        List<MuClip> ReadAnimation()
        {
            var list = new List<MuClip>();
            int clips = I();
            for (int c = 0; c < clips; c++)
            {
                var clip = new MuClip { Name = S() };
                Skip(12 + 12);
                clip.WrapMode = I();
                int curves = I();
                for (int k = 0; k < curves; k++)
                {
                    var curve = new MuCurve { Path = S(), Property = S() };
                    int type = I();
                    I(); int w1 = I();
                    int keys = type == 8 ? w1 : I();
                    curve.Type = type;
                    curve.Keys = new MuKey[keys];
                    for (int i = 0; i < keys; i++)
                    {
                        curve.Keys[i] = new MuKey { Time = F(), Value = F(), InTan = F(), OutTan = F() };
                        I();                                            // modo de tangente
                    }
                    if (keys > 0) clip.Curves.Add(curve);
                }
                list.Add(clip);
            }
            S(); B();                                                   // clip por defecto, reproducción automática
            return list;
        }

        void SkipParticles()
        {
            Skip(1 + 4 + 12 + 8 + 4 + 16 + 1 + 8 + 8 + 8 + 12 + 12 + 12 + 4 + 4 + 4 + 1 + 1 + 5 * 16 +
                 12 + 12 + 4 + 12 + 12 + 4 + 1 + 1 + 4 + 4 + 4 + 4 + 12 + 4);
        }

        MuMaterial ReadMaterial()
        {
            var mat = new MuMaterial { Name = S() };
            if (Version >= 4)
            {
                mat.Shader = S();
                int props = I();
                for (int i = 0; i < props; i++)
                {
                    string prop = S();
                    int type = I();
                    switch (type)
                    {
                        case 0:
                        case 1:
                        {
                            var v = new[] { F(), F(), F(), F() };
                            if (prop == "_Color" && type == 0) mat.Color = v;
                            break;
                        }
                        case 2:
                        case 3: F(); break;
                        case 4:
                        {
                            int idx = I();
                            var scale = new[] { F(), F() };
                            var off = new[] { F(), F() };
                            if (prop == "_MainTex") { mat.MainTex = idx; mat.TexScale = scale; mat.TexOffset = off; }
                            break;
                        }
                        default: throw new InvalidDataException("propiedad de material desconocida: " + type);
                    }
                }
                string sh = mat.Shader ?? "";
                mat.Cutout = sh.Contains("Cutoff") || sh.Contains("Cutout");
                mat.Transparent = !mat.Cutout && (sh.Contains("Alpha") || sh.Contains("Transparent") || sh.Contains("Particles"));
                return mat;
            }

            int st = I();
            mat.Shader = "legacy" + st;
            void MainTex() { mat.MainTex = I(); mat.TexScale = new[] { F(), F() }; mat.TexOffset = new[] { F(), F() }; }
            void OtherTex() => Skip(4 + 16);
            switch (st)
            {
                case 1: MainTex(); break;                                               // diffuse
                case 2: MainTex(); Skip(16 + 4); break;                                 // specular
                case 3: MainTex(); OtherTex(); break;                                   // bumped
                case 4: MainTex(); OtherTex(); Skip(16 + 4); break;                     // bumped specular
                case 5: MainTex(); OtherTex(); Skip(16); break;                         // emissive
                case 6: MainTex(); Skip(16 + 4); OtherTex(); Skip(16); break;           // emissive specular
                case 7: MainTex(); OtherTex(); Skip(16 + 4); OtherTex(); Skip(16); break;
                case 8: MainTex(); Skip(4); mat.Cutout = true; break;                   // alpha cutoff
                case 9: MainTex(); OtherTex(); Skip(4); mat.Cutout = true; break;
                case 10: MainTex(); mat.Transparent = true; break;                      // alpha
                case 11: MainTex(); Skip(4 + 16 + 4); mat.Transparent = true; break;
                case 12: MainTex(); mat.Color = new[] { F(), F(), F(), F() }; mat.Transparent = true; break;
                case 13: MainTex(); mat.Color = new[] { F(), F(), F(), F() }; break;    // unlit
                case 14:
                case 15: MainTex(); Skip(16 + 4); mat.Transparent = true; break;        // partículas
                default: throw new InvalidDataException("tipo de material .mu desconocido: " + st);
            }
            return mat;
        }
    }
}
