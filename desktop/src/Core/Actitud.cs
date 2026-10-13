using System;

namespace KerbinMaps.Core
{
    /* Where a vessel points, in its local horizon frame: east, north and up at its position.

       KSP saves `rot` as the vessel's rotation relative to the body (srfRelRotation), in
       Unity's left-handed frame with Y toward the north pole and X at longitude 0. On rails
       that rotation is kept relative to the surface, which is also how the viewer draws the
       vessel's model, so the navball and the model agree. The nose is the vessel transform's
       up (+Y) and the «top» of the vessel, the navball's up, is its −Z: a plane on the runway
       has it pointing at the sky. */
    public static class Actitud
    {
        /* Rotates a vector by a Unity quaternion (x, y, z, w): v + 2w(q×v) + 2 q×(q×v). */
        static double[] Rotar(double[] q, double x, double y, double z)
        {
            double qx = q[0], qy = q[1], qz = q[2], qw = q[3];
            double cx = qy * z - qz * y, cy = qz * x - qx * z, cz = qx * y - qy * x;
            double ccx = qy * cz - qz * cy, ccy = qz * cx - qx * cz, ccz = qx * cy - qy * cx;
            return new[] { x + 2 * (qw * cx + ccx), y + 2 * (qw * cy + ccy), z + 2 * (qw * cz + ccz) };
        }

        /* Unity body frame (Y north) to the viewer's body-fixed frame (x = cos lat cos lon,
           y = cos lat sin lon, z = sin lat): swapping Y and Z also turns it right-handed. */
        static double[] AGeo(double[] u) => new[] { u[0], u[2], u[1] };

        /* East, north and up at a point, in the body-fixed frame. */
        public static (double[] E, double[] N, double[] U) Ejes(double lat, double lon)
        {
            double la = lat * Geo.D2R, lo = lon * Geo.D2R;
            double cl = Math.Cos(la), sl = Math.Sin(la), co = Math.Cos(lo), so = Math.Sin(lo);
            return (new[] { -so, co, 0.0 }, new[] { -sl * co, -sl * so, cl }, new[] { cl * co, cl * so, sl });
        }

        /* A body-fixed vector in local components (east, north, up). */
        public static double[] ALocal(double[] v, double lat, double lon)
        {
            var (E, N, U) = Ejes(lat, lon);
            return new[] { Dot(v, E), Dot(v, N), Dot(v, U) };
        }

        /* The nose and the top of the vessel in local components (east, north, up). */
        public static (double[] Nariz, double[] Arriba) Local(double[] rot, double lat, double lon)
        {
            var nariz = AGeo(Rotar(rot, 0, 1, 0));
            var arriba = AGeo(Rotar(rot, 0, 0, -1));
            return (ALocal(nariz, lat, lon), ALocal(arriba, lat, lon));
        }

        /* A vector in Unity's body frame (as Koogle Recorder writes velocities) in local
           components (east, north, up). */
        public static double[] UnityALocal(double[] u, double lat, double lon) => ALocal(AGeo(u), lat, lon);

        /* Pitch above the horizon and heading from north, in degrees, of a local vector. */
        public static (double Pitch, double Rumbo) Angulos(double[] local)
        {
            double n = Math.Sqrt(Dot(local, local));
            if (n < 1e-12) return (0, 0);
            double pitch = Math.Asin(Math.Clamp(local[2] / n, -1, 1)) * Geo.R2D;
            double rumbo = (Math.Atan2(local[0], local[1]) * Geo.R2D + 360) % 360;
            return (pitch, rumbo);
        }

        /* An inertial vector (Z north) in the body-fixed frame, with the body rotated `rotDeg`. */
        public static double[] AFijo(double[] v, double rotDeg)
        {
            double r = rotDeg * Geo.D2R, c = Math.Cos(r), s = Math.Sin(r);
            return new[] { v[0] * c + v[1] * s, -v[0] * s + v[1] * c, v[2] };
        }

        public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    }
}
