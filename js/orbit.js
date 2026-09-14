/* Traza terrestre de una órbita alrededor de Kerbin.

   KSP usa cónicas parcheadas: dentro de la SOI de Kerbin la órbita es una
   elipse kepleriana exacta, sin achatamiento ni J2 que la perturben. Así que
   basta propagar Kepler y restar la rotación del planeta para pasar del marco
   inercial al fijo al cuerpo. */

(function () {
  const B = KM.BODY;
  const D2R = Math.PI / 180, R2D = 180 / Math.PI;

  /* Ecuación de Kepler M = E - e·sen E, por Newton-Raphson. */
  function eccentricAnomaly(M, e) {
    M = ((M % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
    let E = e < 0.8 ? M : Math.PI;
    for (let i = 0; i < 60; i++) {
      const f = E - e * Math.sin(E) - M;
      const fp = 1 - e * Math.cos(E);
      const d = f / fp;
      E -= d;
      if (Math.abs(d) < 1e-12) break;
    }
    return E;
  }

  KM.orbit = {
    /* pe/ap en metros sobre el nivel del mar; ángulos en grados. */
    compute(opts) {
      let pe = opts.pe, ap = opts.ap;
      if (ap < pe) { const t = ap; ap = pe; pe = t; }

      const rp = B.radius + pe, ra = B.radius + ap;
      const a = (rp + ra) / 2;
      const e = (ra - rp) / (ra + rp);
      const n = Math.sqrt(B.mu / (a * a * a));     // movimiento medio, rad/s
      const T = 2 * Math.PI / n;                   // periodo orbital, s
      const wb = 2 * Math.PI / B.siderealDay;      // rotación de Kerbin, rad/s

      const inc = opts.inc * D2R, lan = opts.lan * D2R, argp = opts.argp * D2R;
      const orbits = Math.max(1, Math.min(60, opts.orbits | 0 || 1));

      /* Más muestras en órbitas excéntricas: cerca del periapsis la traza
         avanza muy rápido y con pocos puntos se ve angulosa. */
      const perOrbit = Math.round(180 * (1 + 2 * e));
      const steps = orbits * perOrbit;
      const pts = [];

      for (let i = 0; i <= steps; i++) {
        const t = (i / steps) * orbits * T;
        const E = eccentricAnomaly(n * t, e);
        const nu = 2 * Math.atan2(Math.sqrt(1 + e) * Math.sin(E / 2),
                                  Math.sqrt(1 - e) * Math.cos(E / 2));
        const r = a * (1 - e * Math.cos(E));
        const u = argp + nu;                        // argumento de latitud

        const X = r * (Math.cos(lan) * Math.cos(u) - Math.sin(lan) * Math.sin(u) * Math.cos(inc));
        const Y = r * (Math.sin(lan) * Math.cos(u) + Math.cos(lan) * Math.sin(u) * Math.cos(inc));
        const Z = r * (Math.sin(u) * Math.sin(inc));

        const lat = Math.asin(Math.max(-1, Math.min(1, Z / r))) * R2D;
        const lon = KM.geo.wrapLon((Math.atan2(Y, X) - wb * t) * R2D);

        pts.push({ lat, lon, alt: r - B.radius, t });
      }

      const vPe = Math.sqrt(B.mu * (2 / rp - 1 / a));
      const vAp = Math.sqrt(B.mu * (2 / ra - 1 / a));
      const drift = -(T / B.siderealDay) * 360;     // desplazamiento de la traza por vuelta

      return {
        points: pts,
        segments: KM.geo.splitAntimeridian(pts.map(p => [p.lat, p.lon])),
        a, e, T, vPe, vAp, drift,
        pe, ap,
        maxLat: Math.min(90, Math.abs(opts.inc) <= 90 ? Math.abs(opts.inc) : 180 - Math.abs(opts.inc)),
        footprintPe: KM.geo.horizonRadius(pe),
        footprintAp: KM.geo.horizonRadius(ap),
        synchronous: Math.abs(T - B.siderealDay) / B.siderealDay < 0.005,
        suborbital: pe < 0,
        inAtmosphere: pe < B.atmosphere
      };
    }
  };
})();
