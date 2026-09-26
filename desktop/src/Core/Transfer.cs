using System;
using System.Collections.Generic;
using System.Linq;

namespace KerbinMaps.Core
{
    /* Una ventana de lanzamiento: cuándo salir, cuánto cuesta y cuándo se llega. */
    public sealed class TransferWindow
    {
        public double DepartUT, ArriveUT, Tof;
        public double DvSalida, DvLlegada, Dv;      // eyección, captura y total
        public double VinfSalida, VinfLlegada;      // velocidad hiperbólica de exceso
        public double C3 => VinfSalida * VinfSalida;
        public double AnguloFase;                   // origen→destino visto desde el centro, en la salida
        public double AnguloEyeccion;               // desde el prógrado del cuerpo de salida
        public bool Directo = true;                 // sin correcciones a medio camino
    }

    public sealed class TransferPlan
    {
        public bool Ok;
        public string Problema;
        public string Central;                      // alrededor de qué se hace la transferencia
        public bool Luna;                           // el destino orbita el cuerpo de salida
        public double ParkAlt, CaptureAlt;
        public List<TransferWindow> Ventanas = new();
    }

    /* Trayectorias entre cuerpos y sus ventanas de lanzamiento.

       Se calcula con cónicas parcheadas, que es justo lo que hace KSP: la nave sale de la
       esfera de influencia del cuerpo de salida con una velocidad de exceso, recorre una
       elipse alrededor del cuerpo central y llega a la del destino. La transferencia se
       resuelve con Lambert entre las dos posiciones y el tiempo de vuelo, así que es
       <b>balística</b>: una sola quemada de salida, sin correcciones a medio camino.

       Lo que sale de aquí es lo mismo que pide el juego para montarla: instante de salida,
       Δv, tiempo de vuelo, ángulo de fase entre los dos cuerpos y ángulo de eyección
       respecto al prógrado. */
    public static class Transfer
    {
        /* Estado de un cuerpo respecto a otro (que tiene que ser un ancestro suyo). */
        public static (V3 R, V3 V) Estado(BodyDef b, BodyDef central, double ut)
        {
            V3 r = default, v = default;
            for (var c = b; c != null && c != central && c.Orbit != null; c = c.Parent)
            {
                var parent = c.Parent;
                if (parent == null || parent.Mu <= 0) break;
                var e = new Elements
                {
                    Sma = c.Orbit.Sma, Ecc = c.Orbit.Ecc, Inc = c.Orbit.Inc,
                    Lan = c.Orbit.Lan, Lpe = c.Orbit.ArgPe, Mna = c.Orbit.Mna, Eph = c.Orbit.Epoch
                };
                var (rr, vv) = EstadoKepler(e, parent.Mu, ut);
                r += rr; v += vv;
            }
            return (r, v);
        }

        static (V3 R, V3 V) EstadoKepler(Elements e, double mu, double t)
        {
            double a = e.Sma, ecc = e.Ecc;
            double n = Math.Sqrt(mu / (a * a * a));
            double E = ManualOrbit.EccentricAnomaly(e.Mna + n * (t - e.Eph), ecc, 1e-13);
            double nu = 2 * Math.Atan2(Math.Sqrt(1 + ecc) * Math.Sin(E / 2), Math.Sqrt(1 - ecc) * Math.Cos(E / 2));
            double p = a * (1 - ecc * ecc), r = p / (1 + ecc * Math.Cos(nu)), sq = Math.Sqrt(mu / p);
            var rp = new V3(r * Math.Cos(nu), r * Math.Sin(nu), 0);
            var vp = new V3(-sq * Math.Sin(nu), sq * (ecc + Math.Cos(nu)), 0);
            double w = e.Lpe * Geo.D2R, i = e.Inc * Geo.D2R, om = e.Lan * Geo.D2R;
            return (Girar(rp, w, i, om), Girar(vp, w, i, om));
        }

        static V3 Girar(V3 v, double argp, double inc, double lan)
        {
            double c1 = Math.Cos(argp), s1 = Math.Sin(argp);
            double x1 = v.X * c1 - v.Y * s1, y1 = v.X * s1 + v.Y * c1, z1 = v.Z;
            double c2 = Math.Cos(inc), s2 = Math.Sin(inc);
            double x2 = x1, y2 = y1 * c2 - z1 * s2, z2 = y1 * s2 + z1 * c2;
            double c3 = Math.Cos(lan), s3 = Math.Sin(lan);
            return new V3(x2 * c3 - y2 * s3, x2 * s3 + y2 * c3, z2);
        }

        static V3 Cruz(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        /* ------------------------------------------------------------------ Lambert */

        static double StumpffC(double z) =>
            z > 1e-6 ? (1 - Math.Cos(Math.Sqrt(z))) / z
            : z < -1e-6 ? (Math.Cosh(Math.Sqrt(-z)) - 1) / -z
            : 0.5 - z / 24 + z * z / 720;

        static double StumpffS(double z)
        {
            if (z > 1e-6) { double s = Math.Sqrt(z); return (s - Math.Sin(s)) / (s * s * s); }
            if (z < -1e-6) { double s = Math.Sqrt(-z); return (Math.Sinh(s) - s) / (s * s * s); }
            return 1.0 / 6 - z / 120 + z * z / 5040;
        }

        /* Problema de Lambert por variables universales (Bate-Mueller-White): dadas dos
           posiciones y el tiempo entre ellas, la órbita que las une. El tiempo crece con z
           de forma monótona, así que basta bisecar. */
        public static (V3 V1, V3 V2)? Lambert(V3 r1, V3 r2, double dt, double mu, bool prograde)
        {
            double R1 = r1.Len, R2 = r2.Len;
            if (R1 <= 0 || R2 <= 0 || dt <= 0) return null;

            double cosDnu = Math.Clamp(r1.Dot(r2) / (R1 * R2), -1, 1);
            double dnu = Math.Acos(cosDnu);
            double cz = Cruz(r1, r2).Z;
            if (prograde ? cz < 0 : cz > 0) dnu = 2 * Math.PI - dnu;

            double A = Math.Sin(dnu) * Math.Sqrt(R1 * R2 / (1 - Math.Cos(dnu)));
            if (Math.Abs(A) < 1e-9 || double.IsNaN(A)) return null;

            double Y(double z)
            {
                double c = StumpffC(z);
                if (c <= 0) return double.NaN;
                return R1 + R2 + A * (z * StumpffS(z) - 1) / Math.Sqrt(c);
            }

            double Tiempo(double z)
            {
                double y = Y(z);
                if (double.IsNaN(y) || y < 0) return double.NaN;
                double c = StumpffC(z), x = Math.Sqrt(y / c);
                return (x * x * x * StumpffS(z) + A * Math.Sqrt(y)) / Math.Sqrt(mu);
            }

            // el rango útil de z va de casi -4π² (hipérbolas) a 4π² (una vuelta entera)
            double lo = -4 * Math.PI * Math.PI + 1e-3, hi = 4 * Math.PI * Math.PI - 1e-3;
            // con A > 0 hay un z mínimo por debajo del cual y < 0
            for (int i = 0; i < 200 && (double.IsNaN(Tiempo(lo)) || Tiempo(lo) > dt); i++) lo += 0.5;
            if (double.IsNaN(Tiempo(lo))) return null;
            if (Tiempo(hi) < dt) return null;

            double z = 0;
            for (int i = 0; i < 200; i++)
            {
                z = (lo + hi) / 2;
                double t = Tiempo(z);
                if (double.IsNaN(t)) { lo = z; continue; }
                if (t < dt) lo = z; else hi = z;
                if (hi - lo < 1e-10) break;
            }

            double yz = Y(z);
            if (double.IsNaN(yz) || yz <= 0) return null;
            double f = 1 - yz / R1, g = A * Math.Sqrt(yz / mu), gp = 1 - yz / R2;
            if (Math.Abs(g) < 1e-12) return null;
            var v1 = (r2 - r1 * f) * (1 / g);
            var v2 = (r2 * gp - r1) * (1 / g);
            return (v1, v2);
        }

        /* ---------------------------------------------------------------- búsqueda */

        /* El cuerpo alrededor del cual se hace la transferencia: el ancestro común. */
        public static BodyDef Comun(BodyDef a, BodyDef b)
        {
            var cadena = new List<BodyDef>();
            for (var c = a; c != null; c = c.Parent) cadena.Add(c);
            for (var c = b; c != null; c = c.Parent)
                if (cadena.Contains(c)) return c;
            return null;
        }

        /* Δv para salir de una órbita circular de aparcamiento con esa velocidad de exceso. */
        public static double DvEyeccion(BodyDef cuerpo, double altParking, double vinf)
        {
            double r = cuerpo.Radius + altParking;
            double vPark = Math.Sqrt(cuerpo.Mu / r);
            return Math.Sqrt(vinf * vinf + 2 * cuerpo.Mu / r) - vPark;
        }

        /* Δv para frenar de la hipérbola de llegada a una órbita circular. */
        public static double DvCaptura(BodyDef cuerpo, double altCaptura, double vinf)
        {
            double r = cuerpo.Radius + altCaptura;
            return Math.Sqrt(vinf * vinf + 2 * cuerpo.Mu / r) - Math.Sqrt(cuerpo.Mu / r);
        }

        public static TransferPlan Buscar(BodyDef origen, BodyDef destino, double ut0, double span,
                                          double parkAlt, double captureAlt, bool capturar, int maxVentanas = 3)
        {
            var plan = new TransferPlan { ParkAlt = parkAlt, CaptureAlt = captureAlt };
            if (origen == null || destino == null || origen == destino)
            {
                plan.Problema = "Elige dos cuerpos distintos.";
                return plan;
            }

            var central = Comun(origen, destino);
            if (central == null) { plan.Problema = "Esos dos cuerpos no comparten ningún cuerpo central."; return plan; }
            plan.Central = central.Label;
            plan.Luna = central == origen;

            // de la Luna al planeta y al revés: el «origen» es la propia órbita de aparcamiento
            BodyDef salida = plan.Luna ? origen : Ascender(origen, central);
            BodyDef llegada = plan.Luna ? destino : Ascender(destino, central);
            if (salida == null || llegada == null) { plan.Problema = "No sé encadenar esas dos órbitas."; return plan; }

            double mu = central.Mu;
            double rSalida = plan.Luna ? origen.Radius + parkAlt : Estado(salida, central, ut0).R.Len;
            double rLlegada = Estado(llegada, central, ut0).R.Len;
            double aHohmann = (rSalida + rLlegada) / 2;
            double tofHohmann = Math.PI * Math.Sqrt(aHohmann * aHohmann * aHohmann / mu);

            int nDep = 220, nTof = 160;
            double tofMin = tofHohmann * 0.35, tofMax = tofHohmann * 2.2;
            var rejilla = new List<TransferWindow>();

            for (int i = 0; i < nDep; i++)
            {
                double t1 = ut0 + span * i / (nDep - 1.0);
                var (r1, v1cuerpo) = plan.Luna ? Aparcamiento(origen, central, parkAlt, t1) : Estado(salida, central, t1);
                for (int j = 0; j < nTof; j++)
                {
                    double tof = tofMin + (tofMax - tofMin) * j / (nTof - 1.0);
                    double t2 = t1 + tof;
                    var (r2, v2cuerpo) = Estado(llegada, central, t2);
                    var sol = Lambert(r1, r2, tof, mu, true);
                    if (sol == null) continue;

                    double vinfOut = (sol.Value.V1 - v1cuerpo).Len;
                    double vinfIn = (sol.Value.V2 - v2cuerpo).Len;
                    if (!double.IsFinite(vinfOut) || !double.IsFinite(vinfIn)) continue;

                    double dvOut = plan.Luna
                        ? Math.Abs((sol.Value.V1 - v1cuerpo).Len)      // ya se está en órbita del central
                        : DvEyeccion(origen, parkAlt, vinfOut);
                    double dvIn = capturar ? DvCaptura(destino, captureAlt, vinfIn) : 0;

                    rejilla.Add(new TransferWindow
                    {
                        DepartUT = t1, ArriveUT = t2, Tof = tof,
                        DvSalida = dvOut, DvLlegada = dvIn, Dv = dvOut + dvIn,
                        VinfSalida = vinfOut, VinfLlegada = vinfIn,
                        AnguloFase = Angulo(r1, r2Origen(llegada, central, t1)),
                        AnguloEyeccion = AnguloEyeccion(v1cuerpo, sol.Value.V1 - v1cuerpo),
                    });
                }
            }

            if (rejilla.Count == 0) { plan.Problema = "No sale ninguna trayectoria directa en ese plazo."; return plan; }

            /* Las mejores, separadas entre sí: si no, salen veinte variantes de la misma
               ventana con minutos de diferencia. */
            double separacion = Math.Max(tofHohmann * 0.5, span / 12);
            foreach (var w in rejilla.OrderBy(w => w.Dv))
            {
                if (plan.Ventanas.Any(v => Math.Abs(v.DepartUT - w.DepartUT) < separacion)) continue;
                plan.Ventanas.Add(w);
                if (plan.Ventanas.Count >= maxVentanas) break;
            }
            plan.Ok = plan.Ventanas.Count > 0;
            if (!plan.Ok) plan.Problema = "No sale ninguna trayectoria directa en ese plazo.";
            return plan;
        }

        /* El ancestro del cuerpo que orbita directamente al central. */
        static BodyDef Ascender(BodyDef b, BodyDef central)
        {
            for (var c = b; c != null; c = c.Parent)
                if (c.Parent == central) return c;
            return null;
        }

        /* Un punto de la órbita de aparcamiento: se toma el que va por delante del cuerpo,
           que es de donde sale una transferencia hacia fuera. */
        static (V3 R, V3 V) Aparcamiento(BodyDef cuerpo, BodyDef central, double alt, double ut)
        {
            double r = cuerpo.Radius + alt;
            double v = Math.Sqrt(cuerpo.Mu / r);
            // la órbita se supone ecuatorial y prógrada, que es lo normal para una transferencia
            double ang = 2 * Math.PI * (ut / (2 * Math.PI * Math.Sqrt(r * r * r / cuerpo.Mu)));
            return (new V3(r * Math.Cos(ang), r * Math.Sin(ang), 0), new V3(-v * Math.Sin(ang), v * Math.Cos(ang), 0));
        }

        static V3 r2Origen(BodyDef destino, BodyDef central, double ut) => Estado(destino, central, ut).R;

        static double Angulo(V3 a, V3 b)
        {
            double c = Math.Clamp(a.Unit.Dot(b.Unit), -1, 1);
            double ang = Math.Acos(c) * Geo.R2D;
            return Cruz(a, b).Z < 0 ? -ang : ang;
        }

        static double AnguloEyeccion(V3 vCuerpo, V3 vinf)
        {
            if (vCuerpo.Len <= 0 || vinf.Len <= 0) return double.NaN;
            return Angulo(vCuerpo, vinf);
        }
    }
}
