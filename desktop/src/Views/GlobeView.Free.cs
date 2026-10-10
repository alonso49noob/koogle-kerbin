using System;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    /* Free camera skimming the ground: you fly over the terrain like in a plane, but without a
       vessel, without inertia and without being able to crash.

       It's drawn with the same ray tracing as the sky view, with the relief added: each ray
       steps through the height map until it crosses the surface. A mesh wouldn't do here — at
       ground level it would need tessellation and levels of detail — and the tracing gives
       exact horizons and mountain silhouettes. */
    public sealed partial class GlobeView
    {
        public double FreeLat = -0.0972, FreeLon = -74.5577, FreeAlt = 120;   // m above sea level
        public double FreeAz = 90, FreeEl = -5;
        public double FreeSpeed = 120;                                        // m/s
        public bool FreeRelief = true;

        /* The ground's detail textures, the game's own: grass, sand, rock and snow. They're
           blended by slope and by the map color, and only show up close: at a kilometer the map
           already rules. */
        public Texture DetGrass, DetSand, DetRock, DetSnow;
        public double DetailTile = 22;            // meters per repeat
        public double DetailAmount = 0.85;
        public bool Detail = true;
        public int Debug;                         // 0 normal; 1 gray, 2 meters, 3 distance

        /* Clouds: the game's map (EVE / Stock Volumetric Clouds) on a layer at its height.
           Without the mod installed there's no texture and they aren't painted. */
        public Texture CloudTex;
        public System.Collections.Generic.IReadOnlyList<Aplanado> Aplanados = Array.Empty<Aplanado>();
        public TeselaAltura Tesela;               // detail heights under the camera
        public Texture TeselaTex;   // leveled areas (the KSC)
        public bool Clouds = true;
        public bool GlobeClouds = true;           // the cloud layer in the 3D view too
        public double CloudAlt = 5200;            // m above sea level
        public double CloudAmount = 1.0;
        public double CloudOff;                   // longitude rotation of the cloud map

        public bool HasDetail => DetGrass != null || DetSand != null || DetRock != null || DetSnow != null;

        /* With Parallax the four slots are low, mid, high and slope (in that order: DetGrass,
           DetSand, DetSnow and DetRock) and they're blended with its parameters. */
        public bool DetailParallax;
        public (double a, double b) PxLowMid = (0, 1), PxMidHigh = (1e6, 1e6 + 1);
        public (double power, double contrast, double mid) PxSteep = (8, 4, 0.7);
        public Texture PxInfluence, PxDisplacement, PxOcclusion;
        public Texture[] PxBump;                  // normals for low, mid, high and slope

        /* Texture variation: keeps the tiling from looking repeated (see the details in the shader). */
        public bool DetailVariation = true;

        /* Terrain height under a point, in meters above sea level. The window sets it with the
           body's height map; without a map, everything is zero. */
        public Func<double, double, double> GroundAt;
        /* The sea color from the color map, for water the relief puts where the map (1 km per
           texel) still paints land. With a fixed one, a ring darker than the nearby sea showed
           up along the whole coast. */
        public float[] SeaColor = { 0.07f, 0.2f, 0.36f };
        public Texture SeaTex;                    // the same, by area (see MapaDelMar)

        public const double FreeMinSpeed = 2, FreeMaxSpeed = 20000;

        public double FreeGround => GroundAt?.Invoke(FreeLat, FreeLon) ?? 0;

        /* Height above the ground, which is what matters when flying. */
        public double FreeAgl => FreeAlt - FreeGround;

        public void EnterFree() => SetMode(CamMode.Free);

        public void ExitFree()
        {
            if (Mode != CamMode.Free) return;
            SetMode(CamMode.Planet);
        }

        /* Places the camera and leaves it looking at the horizon. */
        public void SetFree(double lat, double lon, double? alt = null, double? az = null)
        {
            FreeLat = Math.Clamp(lat, -89.9, 89.9);
            FreeLon = Geo.WrapLon(lon);
            if (az is double a) FreeAz = ((a % 360) + 360) % 360;
            double suelo = FreeGround;
            FreeAlt = alt ?? (suelo + 150);
            if (FreeAlt < suelo + 2) FreeAlt = suelo + 2;
        }

        public void FreeLook(double daz, double del)
        {
            FreeAz = ((FreeAz + daz) % 360 + 360) % 360;
            FreeEl = Math.Clamp(FreeEl + del, -89, 89);
        }

        /* One flight step. `fwd`, `side` and `up` go from -1 to 1; `dt` in seconds.

           Forward follows where you look (like a plane), sideways goes along the horizon and up
           is the local vertical. When moving, latitude and longitude are recomputed by geodesy
           on the sphere, so crossing the poles or the antimeridian doesn't break anything. */
        public bool MoveFree(double dt, double fwd, double side, double up, double boost = 1)
        {
            if (Mode != CamMode.Free) return false;
            if (fwd == 0 && side == 0 && up == 0) return false;

            double v = FreeSpeed * boost * dt;                    // meters in this step
            double el = FreeEl * D2R, az = FreeAz * D2R;

            // components in the local frame: north, east and vertical
            double norte = fwd * Math.Cos(el) * Math.Cos(az) + side * Math.Cos(az + Math.PI / 2);
            double este = fwd * Math.Cos(el) * Math.Sin(az) + side * Math.Sin(az + Math.PI / 2);
            double vert = fwd * Math.Sin(el) + up;

            double dist = Math.Sqrt(norte * norte + este * este) * v;
            if (dist > 0)
            {
                double rumbo = Math.Atan2(este, norte) * R2D;
                var p = Geo.Destination(FreeLat, FreeLon, rumbo, dist);
                FreeLat = Math.Clamp(p.Lat, -89.9, 89.9);
                FreeLon = p.Lon;
            }
            FreeAlt += vert * v;

            // the ground isn't crossed: two meters above it, like someone walking
            double suelo = FreeGround;
            if (FreeAlt < suelo + 2) FreeAlt = suelo + 2;
            if (FreeAlt > Body.Radius * 2) FreeAlt = Body.Radius * 2;
            return true;
        }

        /* If the ground has risen under the camera (the height map arrives after it was placed,
           or its calibration changed), the camera is lifted on top. Inside the terrain every
           ray hits on the way out and the screen turns flat gray. */
        public bool ClampFree()
        {
            if (Mode != CamMode.Free) return false;
            double suelo = FreeGround;
            if (FreeAlt >= suelo + 2) return false;
            FreeAlt = suelo + 2;
            return true;
        }

        public void FreeSpeedStep(int pasos)
        {
            FreeSpeed = Math.Clamp(FreeSpeed * Math.Pow(1.3, pasos), FreeMinSpeed, FreeMaxSpeed);
        }

        /* Camera position in body radii. */
        public double[] FreePos() => Sph(FreeLat, FreeLon, 1 + FreeAlt / Body.Radius);
    }
}
