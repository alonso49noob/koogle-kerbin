/* Proyección y geodesia sobre Kerbin.

   El mapa usa una proyección equirectangular (plate carrée): la misma en la que
   están las texturas de los cuerpos de KSP, así que una imagen 2:1 se pega
   directamente sin reproyectar. Leaflet ya trae esa proyección como EPSG:4326;
   lo único que hay que cambiar es el radio, para que distancias y escala salgan
   en kilómetros de Kerbin y no de la Tierra. */

(function () {
  const R = KM.BODY.radius;
  const D2R = Math.PI / 180, R2D = 180 / Math.PI;

  KM.CRS = L.extend({}, L.CRS.EPSG4326, {
    R: R,
    wrapLng: [-180, 180],
    distance: function (a, b) {
      return KM.geo.distance(a.lat, a.lng, b.lat, b.lng);
    }
  });

  KM.geo = {
    R: R, D2R: D2R, R2D: R2D,

    /* Distancia sobre el gran círculo, en metros. */
    distance(lat1, lon1, lat2, lon2) {
      const p1 = lat1 * D2R, p2 = lat2 * D2R;
      const dp = (lat2 - lat1) * D2R, dl = (lon2 - lon1) * D2R;
      const h = Math.sin(dp / 2) ** 2 + Math.cos(p1) * Math.cos(p2) * Math.sin(dl / 2) ** 2;
      return 2 * R * Math.asin(Math.min(1, Math.sqrt(h)));
    },

    /* Rumbo inicial de A a B, en grados desde el norte. */
    bearing(lat1, lon1, lat2, lon2) {
      const p1 = lat1 * D2R, p2 = lat2 * D2R, dl = (lon2 - lon1) * D2R;
      const y = Math.sin(dl) * Math.cos(p2);
      const x = Math.cos(p1) * Math.sin(p2) - Math.sin(p1) * Math.cos(p2) * Math.cos(dl);
      return (Math.atan2(y, x) * R2D + 360) % 360;
    },

    /* Punto a distancia `dist` (m) y rumbo `brg` (°) desde un origen. */
    destination(lat, lon, brg, dist) {
      const d = dist / R, t = brg * D2R, p1 = lat * D2R, l1 = lon * D2R;
      const p2 = Math.asin(Math.sin(p1) * Math.cos(d) + Math.cos(p1) * Math.sin(d) * Math.cos(t));
      const l2 = l1 + Math.atan2(Math.sin(t) * Math.sin(d) * Math.cos(p1),
                                 Math.cos(d) - Math.sin(p1) * Math.sin(p2));
      return [p2 * R2D, KM.geo.wrapLon(l2 * R2D)];
    },

    /* Interpolación sobre el gran círculo: en equirectangular, la recta entre
       dos puntos NO es el camino más corto, así que hay que trocearla. */
    greatCircle(lat1, lon1, lat2, lon2, steps) {
      const d = KM.geo.distance(lat1, lon1, lat2, lon2) / R;
      const n = steps || Math.max(2, Math.ceil(d * R2D / 2));
      if (d < 1e-9) return [[lat1, lon1], [lat2, lon2]];
      const p1 = lat1 * D2R, l1 = lon1 * D2R, p2 = lat2 * D2R, l2 = lon2 * D2R;
      const out = [];
      for (let i = 0; i <= n; i++) {
        const f = i / n;
        const A = Math.sin((1 - f) * d) / Math.sin(d);
        const B = Math.sin(f * d) / Math.sin(d);
        const x = A * Math.cos(p1) * Math.cos(l1) + B * Math.cos(p2) * Math.cos(l2);
        const y = A * Math.cos(p1) * Math.sin(l1) + B * Math.cos(p2) * Math.sin(l2);
        const z = A * Math.sin(p1) + B * Math.sin(p2);
        out.push([Math.atan2(z, Math.hypot(x, y)) * R2D, Math.atan2(y, x) * R2D]);
      }
      return out;
    },

    /* Círculo de radio constante sobre la superficie (p. ej. el horizonte visible
       desde una altitud dada). Devuelve tramos ya partidos en el antimeridiano. */
    circle(lat, lon, radiusM, steps) {
      const n = steps || 180, pts = [];
      for (let i = 0; i <= n; i++) pts.push(KM.geo.destination(lat, lon, i * 360 / n, radiusM));
      return KM.geo.splitAntimeridian(pts);
    },

    /* Radio sobre la superficie del casquete visible desde una altitud h. */
    horizonRadius(h) {
      return R * Math.acos(R / (R + h));
    },

    wrapLon(lon) {
      let x = (lon + 180) % 360;
      if (x < 0) x += 360;
      return x - 180;
    },

    /* Una polilínea que cruza ±180° se dibujaría como una raya de lado a lado.
       La partimos en tramos y calculamos la latitud del cruce por interpolación. */
    splitAntimeridian(points) {
      const segs = [];
      let cur = [];
      for (let i = 0; i < points.length; i++) {
        const p = points[i];
        if (i > 0) {
          const prev = points[i - 1];
          if (Math.abs(p[1] - prev[1]) > 180) {
            const dir = p[1] > prev[1] ? -180 : 180;          // borde que toca el punto previo
            const dLon = (p[1] - prev[1]) - Math.sign(p[1] - prev[1]) * 360;
            const f = dLon === 0 ? 0.5 : (dir - prev[1]) / dLon;
            const latX = prev[0] + (p[0] - prev[0]) * f;
            cur.push([latX, dir]);
            segs.push(cur);
            cur = [[latX, -dir]];
          }
        }
        cur.push(p);
      }
      if (cur.length > 1) segs.push(cur);
      return segs;
    },

    /* ---------- formato ---------- */

    fmtLat(lat) { return Math.abs(lat).toFixed(4) + '° ' + (lat >= 0 ? 'N' : 'S'); },
    fmtLon(lon) { return Math.abs(lon).toFixed(4) + '° ' + (lon >= 0 ? 'E' : 'W'); },

    fmtDist(m) {
      if (Math.abs(m) < 1000) return m.toFixed(0) + ' m';
      if (Math.abs(m) < 100000) return (m / 1000).toFixed(2) + ' km';
      return (m / 1000).toFixed(1) + ' km';
    },

    fmtAlt(m) {
      return (m >= 0 ? '+' : '') + m.toFixed(0) + ' m';
    },

    /* Segundos -> d/h/m/s con el día solar de Kerbin (6 h). */
    fmtTime(s) {
      const day = KM.BODY.solarDay;
      const d = Math.floor(s / day); s -= d * day;
      const h = Math.floor(s / 3600); s -= h * 3600;
      const m = Math.floor(s / 60); s -= m * 60;
      const p = n => String(n).padStart(2, '0');
      return (d ? d + 'd ' : '') + p(h) + ':' + p(m) + ':' + p(Math.floor(s));
    }
  };
})();
