using System;
using System.Collections.Generic;
using System.Diagnostics;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    public sealed class GlobePin
    {
        public double Lat, Lon, R = 1;
        public string Name;
        public ColorF Color = ColorF.Hex("#4ea3ff");
        public bool Vessel, Hidden;
        public object Tag;
        internal double Sx, Sy;
        internal bool OnScreen;
    }

    public sealed class OrbitRing
    {
        public IReadOnlyList<TrackPoint> Points;
        public ColorF Color;
        public float Alpha = 0.5f;
    }

    /* Cómo se coloca la cámara: girando alrededor del planeta, alrededor de una nave
       (el foco pasa del centro de Kerbin a la nave) o de pie en la superficie
       mirando al cielo. */
    public enum CamMode { Planet, Focus, Sky }

    /* Vista 3D: Kerbin como esfera texturizada. Mismos shaders que la versión WebGL2,
       pasados a OpenGL 3.3, con la cámara generalizada a los tres modos. */
    public sealed partial class GlobeView : IDisposable
    {
        const double R2D = 180 / Math.PI, D2R = Math.PI / 180;

        // cámara alrededor del planeta
        public double CamLat = 0, CamLon = -74.5, CamDist = 3.2;
        // cámara alrededor de una nave: rumbo y elevación desde los que se la mira
        public double[] FocusTarget = { 0, 0, 1.1 };
        public double FocusAz, FocusEl = 25, FocusDist = 0.25;
        // modelo de la nave enfocada, si se ha podido montar con las piezas de KSP
        public Ksp.AssembledVessel FocusModel;
        public object FocusTag;
        public double FocusMinDist = 0.0003;
        VesselModelRenderer modelRenderer;

        public bool Light = true, Atmosphere = true;

        /* Longitud del punto subsolar (lo fija la ventana con el tiempo de la barra). */
        public double SunLon = -90;
        public double[] SunDir => Sph(0, SunLon, 1);

        /* Cuánto Sol le llega a un punto (en radios): 0 dentro de la sombra del planeta,
           con una penumbra corta del grosor de la atmósfera. */
        public double SunlightAt(double[] p)
        {
            var s = SunDir;
            double along = Dot(p, s);
            if (along >= 0) return 1;
            double perp = Math.Sqrt(Math.Max(0, Dot(p, p) - along * along));
            double k = Math.Clamp((perp - 1) / (Body.Atmosphere / Body.Radius), 0, 1);
            return k * k * (3 - 2 * k);
        }
        public double Relief, BiomeAmt, ColorOff, BiomeOff, HeightOff;
        public double HMin = HeightRange.Min, HMax = HeightRange.Max;
        public Texture ColorTex, BiomeTex, HeightTex;
        public double MinDist = 1.02, MaxDist = 12, SceneR = 1.2;
        public int W = 1, H = 1;
        public float S = 1;
        public readonly List<GlobePin> Pins = new();

        public CamMode Mode { get; private set; } = CamMode.Planet;

        ShaderProgram prog, atmProg, lineProg;
        uint vao, posBuf, uvBuf, idxBuf;
        int count;
        readonly float[] proj = new float[16], view = new float[16];

        sealed class LineBuf { public uint Vao, Buf; public int N; }
        LineBuf trackG, trackS, orbits;
        bool trackSpace;
        readonly List<(int off, int n, ColorF c)> orbitSegs = new();
        double orbitShift;

        // cámara del último fotograma, para el picking
        double[] eyeL = { 0, 0, 3.2 }, fwdL = { 0, 0, -1 }, upL = { 0, 1, 0 };
        double fovL = 45;

        // transición suave entre modos
        struct Cam { public double[] Eye, Target, Up; public double Fov; }
        Cam trans0;
        long transStart;
        bool transActive;
        const double TransDur = 0.6;

        public bool Animating => transActive;

        /* ---------------------------------------------------------------- shaders */

        const string VS = @"#version 330 core
in vec3 aPos;
in vec2 aUv;
uniform mat4 uProj, uView;
uniform sampler2D uHeight;
uniform vec2 uHeightSize;
uniform float uRelief, uHeightOff, uHMin, uHMax, uRadius, uScale;
out vec2 vUv;
out vec3 vNormal;
out vec3 vDir;
const float PI = 3.14159265;

vec3 dirOf(vec2 uv) {
  float lat = (0.5 - uv.y) * PI;
  float lon = (uv.x - 0.5) * 2.0 * PI;
  return vec3(cos(lat) * sin(lon), sin(lat), cos(lat) * cos(lon));
}

float dispAt(vec2 uv) {
  float lum = dot(texture(uHeight, vec2(fract(uv.x + uHeightOff), uv.y)).rgb, vec3(0.2126, 0.7152, 0.0722));
  return ((uHMin + lum * (uHMax - uHMin)) / uRadius) * uRelief;
}

void main() {
  vUv = aUv;
  vDir = aPos;
  float r = uScale;
  if (uRelief > 0.0) {
    r += dispAt(aUv);
    /* La normal sale del terreno desplazado: con la de la esfera, la luz no se
       entera de que hay montañas. */
    vec2 e = 2.0 / uHeightSize;
    vec2 uu = aUv + vec2(e.x, 0.0);
    vec2 vv = aUv + vec2(0.0, e.y);
    vec3 pc = dirOf(aUv) * r;
    vec3 pu = dirOf(uu) * (uScale + dispAt(uu));
    vec3 pv = dirOf(vv) * (uScale + dispAt(vv));
    vec3 n = normalize(cross(pu - pc, pv - pc));
    vNormal = dot(n, aPos) < 0.0 ? -n : n;
  } else {
    vNormal = aPos;
  }
  gl_Position = uProj * uView * vec4(aPos * r, 1.0);
}";

        const string FS = @"#version 330 core
in vec2 vUv;
in vec3 vNormal;
in vec3 vDir;
uniform sampler2D uColor, uBiome;
uniform float uColorOff, uBiomeOff, uBiomeAmt;
uniform int uHasColor, uHasBiome, uLit;
uniform vec3 uLightDir;
out vec4 frag;

/* Con fract() la u se envuelve, pero eso dispara las derivadas en la costura y el
   mipmap elige el nivel más borroso. Pasando las derivadas sin envolver se evita. */
vec3 shifted(sampler2D t, float off) {
  return textureGrad(t, vec2(fract(vUv.x + off), vUv.y), dFdx(vUv), dFdy(vUv)).rgb;
}

void main() {
  vec3 base = vec3(0.10, 0.13, 0.18);
  if (uHasColor != 0) base = shifted(uColor, uColorOff);
  if (uHasBiome != 0 && uBiomeAmt > 0.0) base = mix(base, shifted(uBiome, uBiomeOff), uBiomeAmt);
  if (uLit == 0) { frag = vec4(base, 1.0); return; }
  /* Día y noche. El terminador se decide con la esfera (vDir) y no con la normal del
     relieve, que haría de noche las laderas en sombra en pleno día. Cerca del terminador
     el Sol entra rasante y rojizo; en la cara de noche queda un poco de luz fría para
     adivinar el terreno. */
  float mu = dot(normalize(vDir), uLightDir);
  float day = smoothstep(-0.09, 0.07, mu);
  float direct = max(dot(normalize(vNormal), uLightDir), 0.0) * smoothstep(-0.03, 0.05, mu);
  vec3 sunCol = mix(vec3(1.0, 0.58, 0.34), vec3(1.0), smoothstep(0.02, 0.32, mu));
  vec3 dayLight = sunCol * (0.12 + 0.92 * direct);
  vec3 nightLight = vec3(0.060, 0.075, 0.115);
  frag = vec4(base * mix(nightLight, dayLight, day), 1.0);
}";

        const string AtmFS = @"#version 330 core
in vec3 vNormal;
in vec2 vUv;
uniform vec3 uCamPos, uLightDir;
uniform float uAtmScale;
uniform int uLit;
out vec4 frag;
void main() {
  vec3 n = normalize(vNormal);
  vec3 viewDir = normalize(uCamPos - n * uAtmScale);
  float f = pow(1.0 - abs(dot(n, viewDir)), 3.0);   // el aire se ve de refilón
  vec3 col = vec3(0.36, 0.60, 1.0);
  if (uLit != 0) {
    /* El aire iluminado llega algo más allá del terminador (el Sol aún le da desde
       arriba) y allí se ve anaranjado; en la cara de noche apenas queda un rastro. */
    float mu = dot(n, uLightDir);
    float lit = smoothstep(-0.28, 0.12, mu);
    col = mix(vec3(1.0, 0.46, 0.20), col, smoothstep(-0.06, 0.30, mu));
    f *= mix(0.05, 1.0, lit);
  }
  frag = vec4(col * f, f * 0.9);
}";

        /* Dirección unitaria + radio por separado: el shader puede levantar la línea
           sobre el relieve y girar los anillos sin renormalizar nada. En la vista del
           cielo no hay búfer de profundidad que valga (el suelo está a metros y las
           órbitas a miles de kilómetros), así que el planeta tapa las líneas con un
           corte de rayo por vértice. */
        const string LineVS = @"#version 330 core
in vec3 aDir;
in float aRad;
in vec2 aUv;
uniform mat4 uProj, uView;
uniform sampler2D uHeight;
uniform float uRelief, uHeightOff, uHMin, uHMax, uRadius, uLift, uUseRelief, uLonShift, uPointSize;
uniform int uOcclude;
uniform vec3 uOccEye;
uniform float uOccC;
out float vOcc;
void main() {
  float r = aRad + uLift;
  if (uUseRelief > 0.5 && uRelief > 0.0) {
    float lum = dot(texture(uHeight, vec2(fract(aUv.x + uHeightOff), aUv.y)).rgb, vec3(0.2126, 0.7152, 0.0722));
    r += ((uHMin + lum * (uHMax - uHMin)) / uRadius) * uRelief;
  }
  /* Resta uLonShift a la longitud: lo que ha girado Kerbin desde que se
     construyeron los anillos, un giro alrededor del eje norte (Y). */
  float c = cos(uLonShift), s = sin(uLonShift);
  vec3 d = vec3(aDir.x * c - aDir.z * s, aDir.y, aDir.x * s + aDir.z * c);
  vec3 p = d * r;
  vOcc = 0.0;
  if (uOcclude != 0) {
    vec3 v = p - uOccEye;
    float L = length(v);
    vec3 dir = v / L;
    float b = dot(uOccEye, dir);
    float disc = b * b - uOccC;
    if (disc > 0.0) {
      float t = -b - sqrt(disc);
      if (t > 0.0 && t < L) vOcc = 1.0;
    }
  }
  gl_Position = uProj * uView * vec4(p, 1.0);
  gl_PointSize = uPointSize;
}";

        const string LineFS = @"#version 330 core
in float vOcc;
uniform vec4 uLineColor;
out vec4 frag;
void main() {
  if (vOcc > 0.5) discard;
  frag = uLineColor;
}";

        /* Convenio del visor: u = (lon+180)/360, v = (90-lat)/180. */
        public static double[] Sph(double lat, double lon, double r)
        {
            double a = lat * D2R, b = lon * D2R, ca = Math.Cos(a);
            return new[] { r * ca * Math.Sin(b), r * Math.Sin(a), r * ca * Math.Cos(b) };
        }

        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        static double Len(double[] a) => Math.Sqrt(Dot(a, a));
        static double[] Add(double[] a, double[] b, double k = 1) => new[] { a[0] + b[0] * k, a[1] + b[1] * k, a[2] + b[2] * k };
        static double[] Scale(double[] a, double k) => new[] { a[0] * k, a[1] * k, a[2] * k };
        static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        static double[] Norm(double[] a) { double l = Len(a); return l > 0 ? Scale(a, 1 / l) : new double[] { 0, 1, 0 }; }

        /* Ejes locales en un punto: arriba (radial), este y norte. */
        static void LocalBasis(double[] p, out double[] up, out double[] east, out double[] north)
        {
            up = Norm(p);
            east = Cross(new double[] { 0, 1, 0 }, up);
            east = Len(east) < 1e-9 ? new double[] { 1, 0, 0 } : Norm(east);
            north = Cross(up, east);
        }

        public void Init()
        {
            if (prog != null) return;
            prog = new ShaderProgram(VS, FS, ("aPos", 0), ("aUv", 1));
            atmProg = new ShaderProgram(VS, AtmFS, ("aPos", 0), ("aUv", 1));
            lineProg = new ShaderProgram(LineVS, LineFS, ("aDir", 0), ("aRad", 1), ("aUv", 2));

            const int cols = 192, rows = 96;
            var pos = new float[(cols + 1) * (rows + 1) * 3];
            var uv = new float[(cols + 1) * (rows + 1) * 2];
            var idx = new uint[cols * rows * 6];
            int pi = 0, ui = 0, ii = 0;
            /* La columna de la costura se duplica (u=0 y u=1): si se cosen los vértices,
               la u va de 1 a 0 dentro de un triángulo y sale una franja con toda la
               textura comprimida. */
            for (int y = 0; y <= rows; y++)
            {
                double v = (double)y / rows, lat = 90 - v * 180;
                for (int x = 0; x <= cols; x++)
                {
                    double u = (double)x / cols, lon = -180 + u * 360;
                    var p = Sph(lat, lon, 1);
                    pos[pi++] = (float)p[0]; pos[pi++] = (float)p[1]; pos[pi++] = (float)p[2];
                    uv[ui++] = (float)u; uv[ui++] = (float)v;
                }
            }
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    uint a = (uint)(y * (cols + 1) + x), b = a + cols + 1;
                    idx[ii++] = a; idx[ii++] = b; idx[ii++] = a + 1;
                    idx[ii++] = a + 1; idx[ii++] = b; idx[ii++] = b + 1;
                }
            count = idx.Length;

            vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            posBuf = GL.GenBuffer();
            GL.BindBuffer(GL.ARRAY_BUFFER, posBuf);
            GL.BufferData(GL.ARRAY_BUFFER, pos, pos.Length, GL.STATIC_DRAW);
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, GL.FLOAT, false, 0, 0);
            uvBuf = GL.GenBuffer();
            GL.BindBuffer(GL.ARRAY_BUFFER, uvBuf);
            GL.BufferData(GL.ARRAY_BUFFER, uv, uv.Length, GL.STATIC_DRAW);
            GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 2, GL.FLOAT, false, 0, 0);
            idxBuf = GL.GenBuffer();
            GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, idxBuf);
            GL.BufferData(GL.ELEMENT_ARRAY_BUFFER, idx, idx.Length, GL.STATIC_DRAW);
            GL.BindVertexArray(0);

            trackG = NewLineBuf();
            trackS = NewLineBuf();
            orbits = NewLineBuf();
            InitSky();
        }

        static LineBuf NewLineBuf()
        {
            var lb = new LineBuf { Vao = GL.GenVertexArray(), Buf = GL.GenBuffer() };
            GL.BindVertexArray(lb.Vao);
            GL.BindBuffer(GL.ARRAY_BUFFER, lb.Buf);
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, GL.FLOAT, false, 24, 0);
            GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 1, GL.FLOAT, false, 24, 12);
            GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 2, GL.FLOAT, false, 24, 16);
            GL.BindVertexArray(0);
            return lb;
        }

        static void Fill(LineBuf lb, float[] data, int n)
        {
            GL.BindBuffer(GL.ARRAY_BUFFER, lb.Buf);
            GL.BufferData(GL.ARRAY_BUFFER, data, n * 6, GL.DYNAMIC_DRAW);
            lb.N = n;
        }

        /* Traza orbital: la huella en el suelo y el camino a su altitud real, las dos en
           el marco fijo al cuerpo. */
        public void SetTrack(IReadOnlyList<TrackPoint> points, bool space = true)
        {
            if (points == null || points.Count < 2) { trackG.N = 0; trackS.N = 0; return; }
            int n = points.Count;
            var ground = new float[n * 6];
            var sp = new float[n * 6];
            for (int i = 0; i < n; i++)
            {
                var p = points[i];
                var d = Sph(p.Lat, p.Lon, 1);
                float u = (float)((Geo.WrapLon(p.Lon) + 180) / 360), v = (float)((90 - p.Lat) / 180);
                float rs = (float)((Body.Radius + p.Alt) / Body.Radius);
                int o = i * 6;
                ground[o] = (float)d[0]; ground[o + 1] = (float)d[1]; ground[o + 2] = (float)d[2];
                ground[o + 3] = 1; ground[o + 4] = u; ground[o + 5] = v;
                sp[o] = (float)d[0]; sp[o + 1] = (float)d[1]; sp[o + 2] = (float)d[2];
                sp[o + 3] = rs; sp[o + 4] = u; sp[o + 5] = v;
            }
            Fill(trackG, ground, n);
            Fill(trackS, sp, n);
            trackSpace = space;
        }

        /* Anillos de varias naves. Una órbita kepleriana es una elipse fija en el
           espacio y lo que gira es Kerbin debajo: se construyen una vez y en cada
           fotograma solo se giran en el shader. */
        public void SetOrbits(List<OrbitRing> list)
        {
            orbitSegs.Clear();
            if (list == null || list.Count == 0) { orbits.N = 0; return; }
            int total = 0;
            foreach (var o in list) total += o.Points.Count;
            var data = new float[total * 6];
            int k = 0;
            foreach (var o in list)
            {
                int off = k;
                foreach (var p in o.Points)
                {
                    var d = Sph(p.Lat, p.Lon, 1);
                    int i = k * 6;
                    data[i] = (float)d[0]; data[i + 1] = (float)d[1]; data[i + 2] = (float)d[2];
                    data[i + 3] = (float)((Body.Radius + p.Alt) / Body.Radius);
                    k++;
                }
                orbitSegs.Add((off, o.Points.Count, o.Color.WithAlpha(o.Alpha)));
            }
            Fill(orbits, data, total);
        }

        public void SetOrbitShift(double deg) => orbitShift = (((deg % 360) + 360) % 360) * D2R;
        public double OrbitShiftRad => orbitShift;

        /* Tamaño de la escena: la órbita más lejana. Fija hasta dónde deja alejarse la
           rueda y los planos de recorte. */
        public void SetScene(double maxRadius)
        {
            SceneR = Math.Max(1.2, maxRadius);
            MaxDist = Math.Max(12, SceneR * 3);
            if (CamDist > MaxDist) CamDist = MaxDist;
        }

        public void SetMinDist(double d)
        {
            MinDist = Math.Max(1.02, d);
            if (CamDist < MinDist) CamDist = Math.Min(MinDist, MaxDist);
        }

        /* Latitud y longitud bajo la cámara, sea cual sea el modo. */
        public LatLon Center()
        {
            var e = CurrentCam().Eye;
            double l = Len(e);
            return new LatLon(Math.Asin(Math.Clamp(e[1] / l, -1, 1)) * R2D, Geo.WrapLon(Math.Atan2(e[0], e[2]) * R2D));
        }

        public double EyeDistance => Len(CurrentCam().Eye);

        public void SetCenter(double lat, double lon, double dist = 0)
        {
            CamLat = Math.Clamp(lat, -89.9, 89.9);
            CamLon = lon;
            if (dist > 0) CamDist = Math.Clamp(dist, MinDist, MaxDist);
        }

        /* ------------------------------------------------------------ cámara */

        Cam TargetCam()
        {
            switch (Mode)
            {
                case CamMode.Focus:
                {
                    LocalBasis(FocusTarget, out var up, out var east, out var north);
                    double az = FocusAz * D2R, el = FocusEl * D2R;
                    var heading = Add(Scale(north, Math.Cos(az)), east, Math.Sin(az));
                    var o = Add(Scale(heading, -Math.Cos(el)), up, Math.Sin(el));
                    return new Cam { Eye = Add(FocusTarget, o, FocusDist), Target = FocusTarget, Up = up, Fov = 45 };
                }
                case CamMode.Sky:
                {
                    var eye = ObserverPos();
                    LocalBasis(eye, out var up, out var east, out var north);
                    double az = SkyAz * D2R, el = SkyEl * D2R;
                    var f = Add(Scale(Add(Scale(north, Math.Cos(az)), east, Math.Sin(az)), Math.Cos(el)), up, Math.Sin(el));
                    return new Cam { Eye = eye, Target = Add(eye, f), Up = up, Fov = SkyFov };
                }
                default:
                    return new Cam { Eye = Sph(CamLat, CamLon, CamDist), Target = new double[] { 0, 0, 0 }, Up = new double[] { 0, 1, 0 }, Fov = 45 };
            }
        }

        /* La cámara de este instante: la del modo, o una mezcla si hay transición. El
           ojo se interpola por la esfera (dirección y radio por separado) para que no
           atraviese el planeta al pasar de un lado a otro. */
        Cam CurrentCam()
        {
            var c = TargetCam();
            if (!transActive) return c;
            double t = (Stopwatch.GetTimestamp() - transStart) / (double)Stopwatch.Frequency / TransDur;
            if (t >= 1) { transActive = false; return c; }
            double k = t * t * (3 - 2 * t);

            double r0 = Len(trans0.Eye), r1 = Len(c.Eye);
            var d0 = Scale(trans0.Eye, 1 / r0);
            var d1 = Scale(c.Eye, 1 / r1);
            double ang = Math.Acos(Math.Clamp(Dot(d0, d1), -1, 1));
            double[] dir;
            if (ang < 1e-6) dir = d1;
            else
            {
                double s = Math.Sin(ang);
                dir = Add(Scale(d0, Math.Sin((1 - k) * ang) / s), d1, Math.Sin(k * ang) / s);
            }
            var up = Add(Scale(trans0.Up, 1 - k), c.Up, k);
            return new Cam
            {
                Eye = Scale(dir, r0 + (r1 - r0) * k),
                Target = Add(Scale(trans0.Target, 1 - k), c.Target, k),
                Up = Len(up) < 1e-6 ? c.Up : Norm(up),
                Fov = trans0.Fov + (c.Fov - trans0.Fov) * k
            };
        }

        void BeginTransition()
        {
            trans0 = CurrentCam();
            transStart = Stopwatch.GetTimestamp();
            transActive = true;
        }

        void SetMode(CamMode m)
        {
            if (m == Mode) return;
            BeginTransition();
            Mode = m;
        }

        /* Pasa el foco a un punto (una nave): la cámara conserva el lado desde el que
           se estaba mirando y se acerca girando a su alrededor. */
        public void EnterFocus(double[] target)
        {
            FocusTarget = target;
            if (Mode == CamMode.Focus) return;
            var eye = CurrentCam().Eye;
            var o = Add(eye, target, -1);
            double len = Len(o);
            if (len > 1e-9)
            {
                LocalBasis(target, out var up, out var east, out var north);
                o = Scale(o, 1 / len);
                double el = Math.Asin(Math.Clamp(Dot(o, up), -1, 1));
                var h = Add(Scale(up, Math.Sin(el)), o, -1);          // -cos(el)·rumbo
                FocusAz = Math.Atan2(Dot(h, east), Dot(h, north)) * R2D;
                // bastante de lado y a buena distancia: que se vea la nave contra el horizonte
                FocusEl = Math.Clamp(el * R2D, 10, 40);
                FocusDist = Math.Clamp(len * 0.3, 0.04, 1.0);
            }
            SetMode(CamMode.Focus);
        }

        /* Se acerca de golpe (con transición) a una distancia en la que la nave enfocada
           llena buena parte de la pantalla. */
        public bool ZoomToFocusModel()
        {
            if (Mode != CamMode.Focus || FocusModel == null) return false;
            BeginTransition();
            FocusDist = Math.Max(FocusMinDist, FocusModel.Radius * 3.5 / Body.Radius);
            return true;
        }

        /* Devuelve el foco al centro del planeta, desde donde estaba la cámara. */
        public void ExitFocus()
        {
            if (Mode != CamMode.Focus) return;
            var eye = CurrentCam().Eye;
            double l = Len(eye);
            CamLat = Math.Clamp(Math.Asin(Math.Clamp(eye[1] / l, -1, 1)) * R2D, -89.9, 89.9);
            CamLon = Math.Atan2(eye[0], eye[2]) * R2D;
            CamDist = Math.Clamp(Math.Max(l, 1.3), MinDist, MaxDist);
            SetMode(CamMode.Planet);
        }

        public void EnterSky() => SetMode(CamMode.Sky);

        public void ExitSky(double dist)
        {
            if (Mode != CamMode.Sky) return;
            CamLat = Math.Clamp(ObsLat, -89.9, 89.9);
            CamLon = ObsLon;
            CamDist = Math.Clamp(dist, MinDist, MaxDist);
            SetMode(CamMode.Planet);
        }

        /* ------------------------------------------------------------ interacción */

        double dragX, dragY, dragA, dragB;
        bool dragMoved;
        public bool Dragging { get; private set; }

        public void BeginDrag(double x, double y)
        {
            Dragging = true; dragMoved = false;
            dragX = x; dragY = y;
            switch (Mode)
            {
                case CamMode.Focus: dragA = FocusAz; dragB = FocusEl; break;
                case CamMode.Sky: dragA = SkyAz; dragB = SkyEl; break;
                default: dragA = CamLat; dragB = CamLon; break;
            }
        }

        /* Devuelve true la primera vez que el arrastre pasa de 3 px. */
        public bool Drag(double x, double y)
        {
            if (!Dragging) return false;
            bool first = false;
            double dx = x - dragX, dy = y - dragY;
            if (!dragMoved && Math.Sqrt(dx * dx + dy * dy) > 3 * S) { dragMoved = true; first = true; }
            if (!dragMoved) return false;
            transActive = false;
            switch (Mode)
            {
                case CamMode.Focus:
                    // como en la vista del planeta, lo agarrado acompaña al cursor
                    FocusAz = dragA + dx / S * 0.35;
                    FocusEl = Math.Clamp(dragB + dy / S * 0.35, -80, 85);
                    break;
                case CamMode.Sky:
                {
                    // el cielo agarrado acompaña al cursor
                    double degPerPx = SkyFov / Math.Max(1, H);
                    SkyAz = ((dragA - dx * degPerPx) % 360 + 360) % 360;
                    SkyEl = Math.Clamp(dragB + dy * degPerPx, -89, 89);
                    break;
                }
                default:
                    /* El este cae a la derecha, así que arrastrar a la derecha baja la
                       longitud de la cámara: el terreno agarrado acompaña al cursor. */
                    CamLon = dragB - Arc(dx);
                    CamLat = Math.Clamp(dragA + Arc(dy), -89.9, 89.9);
                    break;
            }
            return first;
        }

        public bool EndDrag()
        {
            bool moved = dragMoved;
            Dragging = false; dragMoved = false;
            return moved;
        }

        /* `notches` positivo acerca, como la rueda de Windows. */
        public void Wheel(double notches)
        {
            switch (Mode)
            {
                case CamMode.Focus:
                    // de cientos de kilómetros a unos metros: cada muesca tiene que avanzar bastante
                    FocusDist = Math.Clamp(FocusDist * Math.Exp(-notches * 0.25), FocusMinDist, MaxDist);
                    break;
                case CamMode.Sky:
                    SkyFov = Math.Clamp(SkyFov * Math.Exp(-notches * 0.1), 3, 110);
                    break;
                default:
                    CamDist = Math.Clamp(CamDist * Math.Exp(-notches * 100 * 0.0012), MinDist, MaxDist);
                    break;
            }
        }

        /* Arco de superficie, en grados, que corresponde a arrastrar `px` píxeles desde
           el centro. Con la cámara a distancia d y el punto a un ángulo α del eje de
           vista, θ = asin(d · sen α) − α: el punto agarrado sigue al cursor. */
        double Arc(double px)
        {
            double h = Math.Max(1, H);
            double d = CamDist;
            int sgn = px < 0 ? -1 : 1;
            double Conv(double a)
            {
                double s = Math.Min(0.95, d * Math.Sin(a));
                return Math.Asin(s) - Math.Asin(s / d);
            }
            double ang = Math.Atan((Math.Abs(px) / (h / 2)) * Math.Tan(22.5 * D2R));
            double aMax = Math.Asin(Math.Min(1, 0.95 / d));
            if (ang <= aMax) return sgn * Conv(ang) * R2D;
            double eps = Math.Max(1e-4, aMax * 0.02);
            double slope = (Conv(aMax) - Conv(aMax - eps)) / eps;
            return sgn * (Conv(aMax) + (ang - aMax) * slope) * R2D;
        }

        /* Dirección del rayo que sale de la cámara por un píxel. */
        double[] RayDir(double px, double py)
        {
            double x = (px / W) * 2 - 1;
            double y = 1 - (py / H) * 2;
            double t = Math.Tan(fovL * D2R / 2), aspect = (double)W / H;
            /* Derecha de pantalla = adelante × arriba. Con (arriba × adelante) el vector
               sale cambiado de signo y el picking queda espejado respecto al dibujo. */
            var fx = Cross(fwdL, upL);
            fx = Len(fx) < 1e-9 ? new double[] { 1, 0, 0 } : Norm(fx);
            var fy = Cross(fx, fwdL);
            return Norm(new[]
            {
                fwdL[0] + fx[0] * x * t * aspect + fy[0] * y * t,
                fwdL[1] + fx[1] * x * t * aspect + fy[1] * y * t,
                fwdL[2] + fx[2] * x * t * aspect + fy[2] * y * t
            });
        }

        /* Rayo desde la cámara por el píxel -> primer corte con la esfera. */
        public LatLon? Pick(double px, double py)
        {
            var d = RayDir(px, py);
            var eye = eyeL;
            double b = 2 * Dot(eye, d);
            double c = Dot(eye, eye) - 1;
            double disc = b * b - 4 * c;
            if (disc < 0) return null;
            double s = (-b - Math.Sqrt(disc)) / 2;
            if (s < 0) return null;
            var q = Add(eye, d, s);
            return new LatLon(Math.Asin(Math.Clamp(q[1], -1, 1)) * R2D, Geo.WrapLon(Math.Atan2(q[0], q[2]) * R2D));
        }

        /* El pin más cercano al cursor, con preferencia por las naves. */
        public GlobePin HitPin(double x, double y)
        {
            GlobePin best = null;
            double bestD = double.MaxValue, r = 10 * S;
            foreach (var p in Pins)
            {
                if (!p.OnScreen || p.Hidden) continue;
                double d = Math.Sqrt((p.Sx - x) * (p.Sx - x) + (p.Sy - y) * (p.Sy - y));
                if (d > r) continue;
                if (p.Vessel) d -= r;                 // a igual distancia, gana la nave
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        /* Proyecta un punto del espacio a pantalla; false si queda detrás de la cámara. */
        public bool ToScreen(double[] p, out double sx, out double sy)
        {
            double ex = view[0] * p[0] + view[4] * p[1] + view[8] * p[2] + view[12];
            double ey = view[1] * p[0] + view[5] * p[1] + view[9] * p[2] + view[13];
            double ez = view[2] * p[0] + view[6] * p[1] + view[10] * p[2] + view[14];
            double cw = -ez;
            sx = sy = 0;
            if (cw <= 0) return false;
            sx = (proj[0] * ex / cw * 0.5 + 0.5) * W;
            sy = (-proj[5] * ey / cw * 0.5 + 0.5) * H;
            return true;
        }

        /* ------------------------------------------------------------------ render */

        void SetupCamera(Cam cam, double near, double far)
        {
            Mat4.Perspective(proj, cam.Fov * D2R, (double)W / H, near, far);
            Mat4.LookAt(view, cam.Eye, cam.Target, cam.Up);
            eyeL = cam.Eye;
            fwdL = Norm(Add(cam.Target, cam.Eye, -1));
            upL = cam.Up;
            fovL = cam.Fov;
        }

        public void Render(Batch2D batch, TextCache tc)
        {
            Init();
            var cam = CurrentCam();
            /* Cerca de la superficie el búfer de profundidad no da para distinguir el
               suelo a metros de una órbita a cientos de kilómetros: ahí se usa el
               trazado de rayos de la vista del cielo, también a mitad de transición. */
            if (Mode == CamMode.Sky || (transActive && Len(cam.Eye) < 1.1))
            {
                RenderSky(batch, tc, cam);
                return;
            }

            GL.Viewport(0, 0, W, H);
            GL.ClearColor(0.027f, 0.043f, 0.067f, 1);
            GL.Clear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT);

            /* Planos de recorte según la distancia: con el cercano fijo y la cámara a
               cientos de radios, la profundidad se queda sin precisión; y al lado de una
               nave hace falta un plano cercano de pocos metros. */
            double h = Math.Max(1e-5, Len(cam.Eye) - 1);
            double toTarget = Len(Add(cam.Target, cam.Eye, -1));
            double near = Math.Max(2e-5, Math.Min(h * 0.5, toTarget * 0.004));
            double far = Len(cam.Eye) + SceneR + 1;
            SetupCamera(cam, near, far);
            var eye = cam.Eye;

            var lightDir = SunDir;
            GL.Enable(GL.DEPTH_TEST);
            GL.Disable(GL.BLEND);
            GL.Enable(GL.CULL_FACE);
            GL.CullFace(GL.BACK);
            GL.DepthMask(true);

            GL.BindVertexArray(vao);
            prog.Use();
            prog.Mat("uProj", proj);
            prog.Mat("uView", view);
            prog.Float("uScale", 1);
            prog.Float("uRadius", Body.Radius);
            prog.Float("uRelief", HeightTex != null ? Relief : 0);
            prog.Float("uHeightOff", HeightOff / 360);
            prog.Float("uHMin", HMin);
            prog.Float("uHMax", HMax);
            prog.Vec2("uHeightSize", HeightTex?.Width ?? 1, HeightTex?.Height ?? 1);
            prog.Float("uColorOff", ColorOff / 360);
            prog.Float("uBiomeOff", BiomeOff / 360);
            prog.Float("uBiomeAmt", BiomeTex != null ? BiomeAmt : 0);
            prog.Int("uHasColor", ColorTex != null ? 1 : 0);
            prog.Int("uHasBiome", BiomeTex != null ? 1 : 0);
            prog.Vec3("uLightDir", lightDir[0], lightDir[1], lightDir[2]);
            prog.Int("uLit", Light ? 1 : 0);
            BindTex(0, ColorTex); prog.Int("uColor", 0);
            BindTex(1, BiomeTex); prog.Int("uBiome", 1);
            BindTex(2, HeightTex); prog.Int("uHeight", 2);
            GL.DrawElements(GL.TRIANGLES, count, GL.UNSIGNED_INT, 0);

            DrawTrack(eye, false);
            DrawOrbits(eye, false);

            if (Atmosphere)
            {
                double scale = (Body.Radius + Body.Atmosphere) / Body.Radius;
                atmProg.Use();
                atmProg.Mat("uProj", proj);
                atmProg.Mat("uView", view);
                atmProg.Float("uScale", scale);
                atmProg.Float("uRelief", 0);
                atmProg.Float("uAtmScale", scale);
                atmProg.Vec3("uCamPos", eye[0], eye[1], eye[2]);
                atmProg.Vec3("uLightDir", lightDir[0], lightDir[1], lightDir[2]);
                atmProg.Int("uLit", Light ? 1 : 0);
                GL.BindVertexArray(vao);
                GL.Enable(GL.BLEND);
                GL.BlendFunc(GL.SRC_ALPHA, GL.ONE);      // aditivo: es luz dispersa
                GL.CullFace(Len(eye) > scale ? GL.FRONT : GL.BACK);  // dentro de la atmósfera, la cara que se ve es la interior
                GL.DepthMask(false);
                GL.DrawElements(GL.TRIANGLES, count, GL.UNSIGNED_INT, 0);
                GL.DepthMask(true);
                GL.CullFace(GL.BACK);
                GL.Disable(GL.BLEND);
            }

            GL.BindVertexArray(0);
            GL.Disable(GL.CULL_FACE);
            GL.Disable(GL.DEPTH_TEST);

            /* La nave enfocada con su modelo, cuando la cámara está lo bastante cerca como
               para que ocupe algo más que un punto. */
            modelPixels = 0;
            if (Mode == CamMode.Focus && FocusModel != null && FocusModel.Items.Count > 0)
            {
                var offset = Scale(Add(FocusTarget, eye, -1), Body.Radius);
                double distM = Len(offset);
                double px = FocusModel.Radius / Math.Max(1e-6, distM) * (H / 2.0) / Math.Tan(cam.Fov * D2R / 2);
                if (px > 1.5)
                {
                    modelRenderer ??= new VesselModelRenderer();
                    modelRenderer.Draw(FocusModel, offset, fwdL, upL, cam.Fov, W, H, lightDir, Light, SunlightAt(FocusTarget));
                    modelPixels = px;
                }
            }

            batch.Begin(W, H);
            PlacePins(batch, tc, eye, vesselsOnly: false);
            batch.End();
        }

        double modelPixels;

        static void BindTex(int unit, Texture t)
        {
            GL.ActiveTexture(GL.TEXTURE0 + (uint)unit);
            GL.BindTexture(GL.TEXTURE_2D, t?.Id ?? 0);
        }

        void LineUniforms(double relief, double[] eye, bool occlude)
        {
            lineProg.Use();
            lineProg.Mat("uProj", proj);
            lineProg.Mat("uView", view);
            lineProg.Float("uRadius", Body.Radius);
            lineProg.Float("uRelief", relief);
            lineProg.Float("uHeightOff", HeightOff / 360);
            lineProg.Float("uHMin", HMin);
            lineProg.Float("uHMax", HMax);
            lineProg.Float("uPointSize", 7 * S);
            lineProg.Int("uOcclude", occlude ? 1 : 0);
            lineProg.Vec3("uOccEye", eye[0], eye[1], eye[2]);
            lineProg.Float("uOccC", Dot(eye, eye) - 1);
            BindTex(2, HeightTex);
            lineProg.Int("uHeight", 2);
        }

        void DrawTrack(double[] eye, bool occlude, bool ground = true)
        {
            if (trackG.N < 2 || (!ground && !trackSpace)) return;
            LineUniforms(HeightTex != null && !occlude ? Relief : 0, eye, occlude);
            GL.Enable(GL.BLEND);
            GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
            GL.Enable(GL.PROGRAM_POINT_SIZE);
            lineProg.Float("uLonShift", 0);

            // camino a su altitud, más tenue (las naves no lo usan: tienen su anillo)
            if (trackSpace)
            {
                GL.BindVertexArray(trackS.Vao);
                lineProg.Float("uLift", 0);
                lineProg.Float("uUseRelief", 0);
                lineProg.Vec4("uLineColor", 0.66, 0.42, 0.92, 0.55);
                GL.DrawArrays(GL.LINE_STRIP, 0, trackS.N);
            }

            /* La huella se levanta un pelo: la esfera es un poliedro inscrito y entre
               vértices queda por debajo de r=1, así que una línea pegada se hunde. Desde
               el suelo no se pinta: ese kilómetro de más la haría cruzar el cielo justo
               por encima del observador. */
            if (ground)
            {
                GL.BindVertexArray(trackG.Vao);
                lineProg.Float("uLift", 0.0016);
                lineProg.Float("uUseRelief", 1);
                lineProg.Vec4("uLineColor", 0.82, 0.55, 1.0, 0.95);
                GL.DrawArrays(GL.LINE_STRIP, 0, trackG.N);

                // periapsis o instante inicial: primer punto de la serie
                lineProg.Vec4("uLineColor", 1, 1, 1, 1);
                GL.DrawArrays(GL.POINTS, 0, 1);
            }

            GL.Disable(GL.PROGRAM_POINT_SIZE);
            GL.Disable(GL.BLEND);
            GL.BindVertexArray(0);
        }

        void DrawOrbits(double[] eye, bool occlude)
        {
            if (orbits.N == 0 || orbitSegs.Count == 0) return;
            LineUniforms(0, eye, occlude);
            lineProg.Float("uUseRelief", 0);
            lineProg.Float("uLift", 0);
            lineProg.Float("uLonShift", orbitShift);
            GL.BindVertexArray(orbits.Vao);
            GL.Enable(GL.BLEND);
            GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
            GL.DepthMask(false);        // translúcidas: que no se tapen unas a otras
            foreach (var g in orbitSegs)
            {
                lineProg.Vec4("uLineColor", g.c.R, g.c.G, g.c.B, g.c.A);
                GL.DrawArrays(GL.LINE_STRIP, g.off, g.n);
            }
            GL.DepthMask(true);
            GL.Disable(GL.BLEND);
            GL.BindVertexArray(0);
        }

        /* ¿Tapa el planeta el segmento cámara -> punto? Para un satélite no basta el
           test del horizonte: puede verse con su vertical tras el limbo. */
        static bool TapadoPorPlaneta(double[] p, double[] eye)
        {
            double dx = p[0] - eye[0], dy = p[1] - eye[1], dz = p[2] - eye[2];
            double L = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (L < 1e-12) return false;
            dx /= L; dy /= L; dz /= L;
            double b = 2 * (eye[0] * dx + eye[1] * dy + eye[2] * dz);
            double c = eye[0] * eye[0] + eye[1] * eye[1] + eye[2] * eye[2] - 1;
            double disc = b * b - 4 * c;
            if (disc < 0) return false;
            double s1 = (-b - Math.Sqrt(disc)) / 2;
            return s1 > 0 && s1 < L;
        }

        void PlacePins(Batch2D b, TextCache tc, double[] eye, bool vesselsOnly)
        {
            double camLen = Len(eye);
            double horizon = 1 / camLen;
            var colocados = new List<(double x, double y)>();
            var labelStyle = new TextStyle("Segoe UI", 11 * S, false, unchecked((int)0xFFEAF2FB), true);

            foreach (var m in Pins)
            {
                m.OnScreen = false;
                if (m.Hidden || (vesselsOnly && !m.Vessel)) continue;
                // con el modelo ya a la vista, el rombo de la nave enfocada sobra
                if (modelPixels > 12 && FocusTag != null && m.Tag == FocusTag) continue;
                double r = m.R > 0 ? m.R : 1;
                var p = Sph(m.Lat, m.Lon, r);
                double visible;
                if (r <= 1.0005)
                {
                    double dot = Dot(p, eye) / (camLen * r);
                    if (dot <= horizon) continue;
                    visible = Math.Min(1, (dot - horizon) / 0.12);   // se desvanecen junto al limbo
                }
                else
                {
                    if (TapadoPorPlaneta(p, eye)) continue;
                    visible = 1;
                }
                if (!ToScreen(p, out double sx, out double sy)) continue;
                if (sx < -40 * S || sy < -40 * S || sx > W + 40 * S || sy > H + 40 * S) continue;
                m.Sx = sx; m.Sy = sy; m.OnScreen = true;
                float a = (float)visible;

                if (m.Vessel)
                {
                    b.Diamond(sx, sy, 6.5 * S, ColorF.Rgba(0, 0, 0, 0.45f * a));
                    b.Diamond(sx, sy, 5 * S, new ColorF(1, 1, 1, a));
                    b.Diamond(sx, sy, 3.2 * S, m.Color.WithAlpha(a));
                }
                else
                {
                    b.Circle(sx, sy, 6.5 * S, ColorF.Rgba(0, 0, 0, 0.4f * a));
                    b.Circle(sx, sy, 4.5 * S, new ColorF(1, 1, 1, a));
                    b.Circle(sx, sy, 3 * S, m.Color.WithAlpha(a));
                }

                /* De lejos varios sitios caen en un puñado de píxeles y los rótulos se
                   pisan. El punto se queda siempre; el nombre, el primero que llega. */
                bool choca = false;
                foreach (var c in colocados)
                    if (Math.Abs(c.x - sx) < 110 * S && Math.Abs(c.y - sy) < 15 * S) { choca = true; break; }
                if (choca) continue;
                colocados.Add((sx, sy));
                b.Text(tc.Get(m.Name, labelStyle), sx + 10 * S, sy - 8 * S, a);
            }
        }

        public void Dispose()
        {
            prog?.Dispose(); atmProg?.Dispose(); lineProg?.Dispose();
            modelRenderer?.Dispose();
            DisposeSky();
            GL.DeleteBuffer(posBuf); GL.DeleteBuffer(uvBuf); GL.DeleteBuffer(idxBuf); GL.DeleteVertexArray(vao);
            foreach (var lb in new[] { trackG, trackS, orbits })
                if (lb != null) { GL.DeleteBuffer(lb.Buf); GL.DeleteVertexArray(lb.Vao); }
        }
    }
}
