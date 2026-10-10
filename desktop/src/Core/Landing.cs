using System;
using System.Collections.Generic;

namespace KerbinMaps.Core
{
    public readonly struct V3
    {
        public readonly double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
        public double Len => Math.Sqrt(X * X + Y * Y + Z * Z);
        public V3 Unit { get { double l = Len; return l <= 0 ? this : this * (1 / l); } }
        public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
    }

    public sealed class LandingPlan
    {
        public bool Ok;
        public string Problema;

        public double BurnUT;              // when to brake
        public double Dv;                  // how much, retrograde, m/s
        public double BurnLat, BurnLon, BurnAlt;
        public double TouchUT;             // when it touches the ground
        public double ImpactLat, ImpactLon;
        public double ErrorM;              // distance to the target
        public double PeriapsisTrasFrenar; // above sea level, can be negative
        public double EntradaUT;           // when it crosses the top of the atmosphere (NaN if there's none)
        public double VelocidadImpacto;    // m/s, without a final braking burn
        public bool ConAire;               // there was a drag model
        public List<TrackPoint> Descenso = new();
    }

    /* Landing from orbit: where and how much to brake to come down on a given point.

       KSP's orbit inside an SOI is exactly Keplerian, so the state comes from the save's
       elements. The braking burn is assumed instantaneous and retrograde (that's what you do in
       the game: point retrograde and burn), and the descent is integrated with RK4: gravity
       always, and drag if the body has air.

       With an atmosphere the result is approximate: drag depends on the vessel's shape and
       mass, and with FAR or Kerbalism installed the game's model isn't this one. Without air
       (Mun, Minmus, Ike, Gilly...) the calculation is exact except for the terrain. */
    public static class Landing
    {
        /* Position and velocity in the body's inertial frame, at time t. */
        public static (V3 R, V3 V) Estado(Elements e, double t)
        {
            double mu = Body.Mu, a = e.Sma, ecc = e.Ecc;
            double n = Math.Sqrt(mu / (a * a * a));
            double M = e.Mna + n * (t - e.Eph);
            double E = ManualOrbit.EccentricAnomaly(M, ecc, 1e-13);
            double nu = 2 * Math.Atan2(Math.Sqrt(1 + ecc) * Math.Sin(E / 2), Math.Sqrt(1 - ecc) * Math.Cos(E / 2));
            double p = a * (1 - ecc * ecc);
            double r = p / (1 + ecc * Math.Cos(nu));
            double sq = Math.Sqrt(mu / p);

            // in the orbital plane (perifocal) and from there to inertial
            var rp = new V3(r * Math.Cos(nu), r * Math.Sin(nu), 0);
            var vp = new V3(-sq * Math.Sin(nu), sq * (ecc + Math.Cos(nu)), 0);
            double w = e.Lpe * Geo.D2R, i = e.Inc * Geo.D2R, om = e.Lan * Geo.D2R;
            return (Girar(rp, w, i, om), Girar(vp, w, i, om));
        }

        static V3 Girar(V3 v, double argp, double inc, double lan)
        {
            // Rz(lan) · Rx(inc) · Rz(argp)
            double c1 = Math.Cos(argp), s1 = Math.Sin(argp);
            double x1 = v.X * c1 - v.Y * s1, y1 = v.X * s1 + v.Y * c1, z1 = v.Z;
            double c2 = Math.Cos(inc), s2 = Math.Sin(inc);
            double x2 = x1, y2 = y1 * c2 - z1 * s2, z2 = y1 * s2 + z1 * c2;
            double c3 = Math.Cos(lan), s3 = Math.Sin(lan);
            return new V3(x2 * c3 - y2 * s3, x2 * s3 + y2 * c3, z2);
        }

        /* From the inertial frame to the rotating ground. */
        public static (double Lat, double Lon) Suelo(V3 r, double t, double ut, double rotUT)
        {
            double rot = rotUT + 360 * ((t - ut) / Body.SiderealDay);
            double lat = Math.Asin(Math.Clamp(r.Z / r.Len, -1, 1)) * Geo.R2D;
            double lon = Geo.WrapLon(Math.Atan2(r.Y, r.X) * Geo.R2D - rot);
            return (lat, lon);
        }

        /* Air density at a height, with an exponential atmosphere. The scale height follows
           from KSP cutting the atmosphere where pressure is already negligible (about fourteen
           scale heights), so H ≈ ceiling / 14 gives the right falloff from one body to another
           without tabulating anything. */
        public static double Densidad(double alt)
        {
            double techo = Body.Atmosphere;
            if (techo <= 0 || alt >= techo || alt < -500) return 0;
            double h = techo / 14.0;
            return Body.Current.AirDensity * 1.225 * Math.Exp(-Math.Max(0, alt) / h);
        }

        /* Acceleration: gravity and, if there's air, drag against the relative wind (the air
           rotates with the body). bc is the ballistic coefficient, mass over Cd·area, in kg/m²:
           the larger it is, the less it brakes. */
        static V3 Acel(V3 r, V3 v, double bc, bool aire)
        {
            double rr = r.Len;
            var g = r * (-Body.Mu / (rr * rr * rr));
            if (!aire || bc <= 0) return g;

            double alt = rr - Body.Radius;
            double rho = Densidad(alt);
            if (rho <= 0) return g;

            // air velocity at that point: the body's rotation around Z
            double w = 2 * Math.PI / Body.SiderealDay;
            var vAire = new V3(-w * r.Y, w * r.X, 0);
            var vRel = v - vAire;
            double s = vRel.Len;
            if (s <= 0) return g;
            return g + vRel.Unit * (-0.5 * rho * s * s / bc);
        }

        /* Integrates from a state until it touches the ground (or until time runs out). */
        public static (List<TrackPoint> Pts, double TouchT, V3 RFin, V3 VFin, double EntradaT) Caer(
            V3 r0, V3 v0, double t0, double ut, double rotUT, double bc, double maxT = 40000, bool grueso = false)
        {
            bool aire = Body.Current.HasAir || Body.Atmosphere > 0;
            var pts = new List<TrackPoint>();
            var r = r0; var v = v0;
            double t = t0, entrada = double.NaN;
            int cadaPunto = 1, contados = 0;

            (V3 R, V3 V) Paso(V3 rr, V3 vv, double h)
            {
                var k1v = Acel(rr, vv, bc, aire); var k1r = vv;
                var k2v = Acel(rr + k1r * (h / 2), vv + k1v * (h / 2), bc, aire); var k2r = vv + k1v * (h / 2);
                var k3v = Acel(rr + k2r * (h / 2), vv + k2v * (h / 2), bc, aire); var k3r = vv + k2v * (h / 2);
                var k4v = Acel(rr + k3r * h, vv + k3v * h, bc, aire); var k4r = vv + k3v * h;
                return (rr + (k1r + k2r * 2 + k3r * 2 + k4r) * (h / 6), vv + (k1v + k2v * 2 + k3v * 2 + k4v) * (h / 6));
            }

            for (int paso = 0; paso < 200000 && t - t0 < maxT; paso++)
            {
                double alt = r.Len - Body.Radius;
                if (aire && double.IsNaN(entrada) && alt <= Body.Atmosphere) entrada = t;

                // short steps near the ground and inside the air, long ones up high; in the
                // search sweep large steps are enough, they only need ranking
                double dt = alt > 200000 ? 20 : alt > 20000 ? 5 : alt > 2000 ? 1 : 0.25;
                if (grueso) dt *= 8;

                if (!grueso && paso % cadaPunto == 0)
                {
                    var (la, lo) = Suelo(r, t, ut, rotUT);
                    pts.Add(new TrackPoint(la, lo, alt, t));
                    // the trace is for drawing: a few hundred points are plenty
                    if (++contados > 600) { cadaPunto *= 2; contados = 0; }
                }
                if (alt <= 0) break;

                var (rN, vN) = Paso(r, v, dt);

                /* If that step goes underground, it's halved until it finds the moment of
                   contact. The halving goes in its own variable: if it were left in dt, the
                   calculation at the top of the loop would raise it again and the descent would
                   never end. */
                if (rN.Len - Body.Radius < 0)
                {
                    double h = dt;
                    for (int i = 0; i < 24 && h > 0.02; i++)
                    {
                        h /= 2;
                        var (rr, vv) = Paso(r, v, h);
                        if (rr.Len - Body.Radius >= 0) { r = rr; v = vv; t += h; }
                    }
                    break;
                }
                r = rN; v = vN; t += dt;
            }

            var (lat, lon) = Suelo(r, t, ut, rotUT);
            pts.Add(new TrackPoint(lat, lon, r.Len - Body.Radius, t));
            return (pts, t, r, v, entrada);
        }

        /* Retrograde Δv that leaves the periapsis at the requested height. Found by bisection:
           braking always lowers it, so the function is monotonic. */
        public static double DvParaPeriapsis(V3 r, V3 v, double periapsisObjetivo)
        {
            double objetivo = Body.Radius + periapsisObjetivo;
            double lo = 0, hi = v.Len * 0.95;
            for (int i = 0; i < 80; i++)
            {
                double mid = (lo + hi) / 2;
                if (Periapsis(r, v - v.Unit * mid) > objetivo) lo = mid; else hi = mid;
            }
            return (lo + hi) / 2;
        }

        static double Periapsis(V3 r, V3 v)
        {
            double mu = Body.Mu, rr = r.Len, vv = v.Len;
            double energia = vv * vv / 2 - mu / rr;
            if (Math.Abs(energia) < 1e-12) return 0;
            double a = -mu / (2 * energia);
            var h = new V3(r.Y * v.Z - r.Z * v.Y, r.Z * v.X - r.X * v.Z, r.X * v.Y - r.Y * v.X);
            double e2 = 1 - h.Dot(h) / (mu * a);
            double e = e2 <= 0 ? 0 : Math.Sqrt(e2);
            return a * (1 - e);
        }

        /* Finds when to brake to come down on the target. First a sweep over the whole orbit
           and then refinement, alternating time and Δv: the time drives where it lands and the
           Δv how much earlier the fall comes. */
        public static LandingPlan Planear(Elements orb, double t0, double ut, double rotUT,
                                          double objLat, double objLon, double periapsisObjetivo, double bc)
        {
            var plan = new LandingPlan();
            if (orb == null) { plan.Problema = "La nave no tiene órbita en este cuerpo."; return plan; }
            if (orb.Ecc >= 1) { plan.Problema = "La órbita no es cerrada."; return plan; }

            double periodo = SaveFile.Periodo(orb);
            double rp = orb.Sma * (1 - orb.Ecc) - Body.Radius;
            if (rp <= 0) { plan.Problema = "La órbita ya corta el suelo: no hace falta frenar."; return plan; }

            double Evaluar(double tb, double dvExtra, out LandingPlan detalle, bool grueso = false)
            {
                var (r, v) = Estado(orb, tb);
                double dv = DvParaPeriapsis(r, v, periapsisObjetivo) + dvExtra;
                if (dv <= 0 || dv >= v.Len) { detalle = null; return double.MaxValue; }
                var (pts, tt, rf, vf, entrada) = Caer(r, v - v.Unit * dv, tb, ut, rotUT, bc, 40000, grueso);
                var (la, lo) = Suelo(rf, tt, ut, rotUT);
                double err = Geo.Distance(la, lo, objLat, objLon);
                var (bla, blo) = Suelo(r, tb, ut, rotUT);
                detalle = new LandingPlan
                {
                    Ok = true, BurnUT = tb, Dv = dv, TouchUT = tt,
                    BurnLat = bla, BurnLon = blo, BurnAlt = r.Len - Body.Radius,
                    ImpactLat = la, ImpactLon = lo, ErrorM = err,
                    PeriapsisTrasFrenar = Periapsis(r, v - v.Unit * dv) - Body.Radius,
                    EntradaUT = entrada,
                    VelocidadImpacto = vf.Len,
                    ConAire = Body.Atmosphere > 0,
                    Descenso = pts,
                };
                return err;
            }

            // coarse sweep over a full revolution
            int n = 240;
            double mejorT = t0, mejorErr = double.MaxValue;
            LandingPlan mejor = null;
            for (int i = 0; i < n; i++)
            {
                double tb = t0 + periodo * i / n;
                double err = Evaluar(tb, 0, out var det, grueso: true);
                if (err < mejorErr) { mejorErr = err; mejorT = tb; mejor = det; }
            }
            if (mejor == null) { plan.Problema = "No se encontró ninguna frenada que llegue al suelo."; return plan; }
            // the sweep was coarse: refinement starts from scratch with the fine calculation
            mejorErr = Evaluar(mejorT, 0, out mejor);

            // refinement: time and then Δv, a couple of times
            double paso = periodo / n;
            double dvExtra = 0;
            for (int ronda = 0; ronda < 3; ronda++)
            {
                double lo = mejorT - paso, hi = mejorT + paso;
                for (int i = 0; i < 40; i++)
                {
                    double m1 = lo + (hi - lo) * 0.382, m2 = lo + (hi - lo) * 0.618;
                    double e1 = Evaluar(m1, dvExtra, out var d1), e2 = Evaluar(m2, dvExtra, out var d2);
                    if (e1 < e2) { hi = m2; if (e1 < mejorErr) { mejorErr = e1; mejor = d1; mejorT = m1; } }
                    else { lo = m1; if (e2 < mejorErr) { mejorErr = e2; mejor = d2; mejorT = m2; } }
                }
                // fine tuning of the Δv: more braking shortens the range
                double dlo = dvExtra - 60, dhi = dvExtra + 60;
                for (int i = 0; i < 30; i++)
                {
                    double m1 = dlo + (dhi - dlo) * 0.382, m2 = dlo + (dhi - dlo) * 0.618;
                    double e1 = Evaluar(mejorT, m1, out var d1), e2 = Evaluar(mejorT, m2, out var d2);
                    if (e1 < e2) { dhi = m2; if (e1 < mejorErr) { mejorErr = e1; mejor = d1; dvExtra = m1; } }
                    else { dlo = m1; if (e2 < mejorErr) { mejorErr = e2; mejor = d2; dvExtra = m2; } }
                }
                paso /= 6;
            }
            return mejor;
        }
    }
}
