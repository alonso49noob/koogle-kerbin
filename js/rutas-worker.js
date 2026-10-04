/* Rutas por aire, mar y tierra, en un worker para no congelar la página.

   Es la misma búsqueda que la app de escritorio (desktop/src/Core/Rutas.cs): el aire va
   por el gran círculo; el mar y la tierra buscan con A* sobre una rejilla con la altura
   de cada celda. El barco solo pisa celdas bajo el nivel del mar y el rover solo las de
   encima, sin cuestas de más de la pendiente máxima y más despacio cuanto más empinadas.
   Luego el camino se endereza: dos puntos se unen en recta si por ahí no se tarda más.

   Mensajes:
     { tipo: 'malla', W, H, alt: Float32Array, tierra: Uint8Array, hayMar, radio }
     { tipo: 'ruta', id, a: [lat, lon], b: [lat, lon], vAire, vMar, vTierra, pendiente }
   Respuesta a 'ruta': { id, rutas: [aire, mar, tierra] } */

'use strict';

const D2R = Math.PI / 180, R2D = 180 / Math.PI;
const DDX = [1, -1, 0, 0, 1, 1, -1, -1];
const DDY = [0, 0, 1, -1, 1, -1, 1, -1];
const MAX_ACERCAR = 256;
const MAX_ACERCAR_M = 100000;      // más lejos que esto, el agua (o la tierra) no cuenta como «cerca»

let M = null;

function wrapLon(l) { return ((l + 180) % 360 + 360) % 360 - 180; }

function distancia(lat1, lon1, lat2, lon2) {
  const p1 = lat1 * D2R, p2 = lat2 * D2R, dp = (lat2 - lat1) * D2R, dl = (lon2 - lon1) * D2R;
  const h = Math.sin(dp / 2) ** 2 + Math.cos(p1) * Math.cos(p2) * Math.sin(dl / 2) ** 2;
  return 2 * M.radio * Math.asin(Math.min(1, Math.sqrt(h)));
}

/* El punto a una fracción f del gran círculo de p a q. */
function interpolar(p, q, f) {
  const p1 = p[0] * D2R, l1 = p[1] * D2R, p2 = q[0] * D2R, l2 = q[1] * D2R;
  const ax = Math.cos(p1) * Math.cos(l1), ay = Math.cos(p1) * Math.sin(l1), az = Math.sin(p1);
  const bx = Math.cos(p2) * Math.cos(l2), by = Math.cos(p2) * Math.sin(l2), bz = Math.sin(p2);
  const w = Math.acos(Math.max(-1, Math.min(1, ax * bx + ay * by + az * bz)));
  let A, B;
  if (w < 1e-12) { A = 1 - f; B = f; }
  else { A = Math.sin((1 - f) * w) / Math.sin(w); B = Math.sin(f * w) / Math.sin(w); }
  const x = A * ax + B * bx, y = A * ay + B * by, z = A * az + B * bz;
  return [Math.atan2(z, Math.sqrt(x * x + y * y)) * R2D, Math.atan2(y, x) * R2D];
}

/* Puntos cada 0,2° como mucho, para que la línea siga el gran círculo. */
function densificar(pts) {
  const o = [pts[0]];
  for (let k = 1; k < pts.length; k++) {
    const ang = distancia(pts[k - 1][0], pts[k - 1][1], pts[k][0], pts[k][1]) / M.radio * R2D;
    const n = Math.max(1, Math.ceil(ang / 0.2));
    for (let i = 1; i <= n; i++) o.push(i === n ? pts[k] : interpolar(pts[k - 1], pts[k], i / n));
  }
  return o;
}

/* ------------------------------------------------------------ rejilla */

function prepararMalla(d) {
  const { W, H } = d;
  M = {
    W, H, alt: d.alt, tierra: d.tierra, hayMar: d.hayMar, radio: d.radio,
    dy: d.radio * Math.PI / H,
    dx: new Float64Array(H), cosLat: new Float64Array(H), sinLat: new Float64Array(H),
    cosLon: new Float64Array(W), sinLon: new Float64Array(W),
    coste: new Float32Array(W * H), desde: new Uint8Array(W * H), cerrada: new Uint8Array(W * H),
  };
  for (let y = 0; y < H; y++) {
    const p = latDe(y) * D2R;
    M.cosLat[y] = Math.cos(p); M.sinLat[y] = Math.sin(p);
    M.dx[y] = d.radio * M.cosLat[y] * 2 * Math.PI / W;
  }
  for (let x = 0; x < W; x++) {
    const l = lonDe(x) * D2R;
    M.cosLon[x] = Math.cos(l); M.sinLon[x] = Math.sin(l);
  }
  M.zona = zonas();
}

function latDe(y) { return 90 - (y + 0.5) * 180 / M.H; }
function lonDe(x) { return -180 + (x + 0.5) * 360 / M.W; }
function fila(lat) { return Math.max(0, Math.min(M.H - 1, Math.floor((90 - lat) / 180 * M.H))); }
function columna(lon) {
  const x = Math.floor((wrapLon(lon) + 180) / 360 * M.W);
  return ((x % M.W) + M.W) % M.W;
}
function centro(i) { return [latDe(Math.floor(i / M.W)), lonDe(i % M.W)]; }
function distCeldas(i, j) { const p = centro(i), q = centro(j); return distancia(p[0], p[1], q[0], q[1]); }
function esTierra(lat, lon) { return M.tierra[fila(lat) * M.W + columna(lon)] === 1; }

/* Altura interpolada entre los centros de las celdas. */
function altura(lat, lon) {
  const { W, H, alt } = M;
  const fx = (wrapLon(lon) + 180) / 360 * W - 0.5;
  const fy = Math.max(0, Math.min(H - 1, (90 - lat) / 180 * H - 0.5));
  const x0 = Math.floor(fx), y0 = Math.floor(fy), tx = fx - x0, ty = fy - y0;
  const y1 = Math.min(y0 + 1, H - 1);
  const xa = ((x0 % W) + W) % W, xb = (xa + 1) % W;
  const a = alt[y0 * W + xa] * (1 - tx) + alt[y0 * W + xb] * tx;
  const b = alt[y1 * W + xa] * (1 - tx) + alt[y1 * W + xb] * tx;
  return a * (1 - ty) + b * ty;
}

/* Trozos conectados de tierra y de mar: así se sabe al momento si dos puntos se pueden
   unir, sin recorrer un océano entero buscando. */
function zonas() {
  const { W, H, tierra } = M;
  const z = new Int32Array(W * H);
  const pila = new Int32Array(W * H);
  let n = 0;
  for (let s = 0; s < z.length; s++) {
    if (z[s]) continue;
    z[s] = ++n;
    const t = tierra[s];
    let top = 0;
    pila[top++] = s;
    while (top > 0) {
      const i = pila[--top];
      const x = i % W, y = (i / W) | 0;
      for (let k = 0; k < 8; k++) {
        const yy = y + DDY[k];
        if (yy < 0 || yy >= H) continue;
        const j = yy * W + (x + DDX[k] + W) % W;
        if (z[j] || tierra[j] !== t) continue;
        z[j] = n;
        pila[top++] = j;
      }
    }
  }
  return z;
}

/* La celda del medio pedido (y de la zona pedida, si no es 0) más cercana a i. */
function cercana(i, enTierra, enZona) {
  const { W, H, tierra, zona } = M;
  const quiere = enTierra ? 1 : 0;
  const vale = j => tierra[j] === quiere && (!enZona || zona[j] === enZona);
  if (vale(i)) return i;
  const x0 = i % W, y0 = (i / W) | 0;
  let mejor = -1, dMejor = Infinity, hasta = MAX_ACERCAR, ajustado = false;
  for (let r = 1; r <= hasta && r * M.dy <= MAX_ACERCAR_M * 1.5; r++) {
    for (let dyy = -r; dyy <= r; dyy++) {
      const y = y0 + dyy;
      if (y < 0 || y >= H) continue;
      const paso = Math.abs(dyy) === r ? 1 : 2 * r;
      for (let dxx = -r; dxx <= r; dxx += paso) {
        const j = y * W + (x0 + dxx + W * 4) % W;
        if (!vale(j)) continue;
        const d = distCeldas(i, j);
        if (d < dMejor) { dMejor = d; mejor = j; }
      }
    }
    // las celdas se estrechan hacia los polos: se mira un poco más allá del primer hallazgo
    if (mejor >= 0 && !ajustado) { hasta = Math.min(MAX_ACERCAR, r + (r >> 1) + 2); ajustado = true; }
  }
  return dMejor <= MAX_ACERCAR_M ? mejor : -1;
}

/* ------------------------------------------------------------ búsqueda */

/* Montículo binario de (celda, prioridad), sin objetos. */
function Monticulo() {
  let idx = new Int32Array(1 << 16), pri = new Float64Array(1 << 16), n = 0;
  return {
    get size() { return n; },
    push(i, p) {
      if (n === idx.length) {
        const a = new Int32Array(n * 2); a.set(idx); idx = a;
        const b = new Float64Array(n * 2); b.set(pri); pri = b;
      }
      let k = n++;
      while (k > 0) {
        const pa = (k - 1) >> 1;
        if (pri[pa] <= p) break;
        idx[k] = idx[pa]; pri[k] = pri[pa]; k = pa;
      }
      idx[k] = i; pri[k] = p;
    },
    pop() {
      const top = idx[0];
      const i = idx[--n], p = pri[n];
      let k = 0;
      for (;;) {
        let c = 2 * k + 1;
        if (c >= n) break;
        if (c + 1 < n && pri[c + 1] < pri[c]) c++;
        if (pri[c] >= p) break;
        idx[k] = idx[c]; pri[k] = pri[c]; k = c;
      }
      idx[k] = i; pri[k] = p;
      return top;
    },
  };
}

/* El factor de velocidad en una cuesta: entero en llano y un 30 % en la pendiente máxima. */
function factor(pend, max) { return 1 - 0.7 * (pend / max) * (pend / max); }

function aEstrella(s, g, enTierra, v, pendMax) {
  const { W, H, alt, tierra, coste, desde, cerrada, dx, dy, cosLat, sinLat, cosLon, sinLon, radio } = M;
  coste.fill(Infinity);
  desde.fill(255);
  cerrada.fill(0);
  const gx = g % W, gy = (g / W) | 0;
  const gs = sinLat[gy], gc = cosLat[gy], gcl = cosLon[gx], gsl = sinLon[gx];
  const tanMax = Math.tan(pendMax * D2R);
  const quiere = enTierra ? 1 : 0;

  // lo que falta, en segundos: el gran círculo a toda velocidad (nunca sobreestima)
  const h = i => {
    const x = i % W, y = (i / W) | 0;
    const c = sinLat[y] * gs + cosLat[y] * gc * (cosLon[x] * gcl + sinLon[x] * gsl);
    return radio * Math.acos(Math.max(-1, Math.min(1, c))) / v * 0.999;
  };

  const cola = Monticulo();
  coste[s] = 0;
  cola.push(s, h(s));
  while (cola.size > 0) {
    const i = cola.pop();
    if (i === g) break;
    /* Cada celda se abre una sola vez: cerca de los polos la rejilla se aplasta y
       reabrir celdas costaría mucho para ganar casi nada. */
    if (cerrada[i]) continue;
    cerrada[i] = 1;
    const ci = coste[i];
    const x = i % W, y = (i / W) | 0;
    for (let k = 0; k < 8; k++) {
      const yy = y + DDY[k];
      if (yy < 0 || yy >= H) continue;
      const j = yy * W + (x + DDX[k] + W) % W;
      if (tierra[j] !== quiere || cerrada[j]) continue;
      const d = DDY[k] === 0 ? dx[y] : DDX[k] === 0 ? dy : Math.hypot((dx[y] + dx[yy]) / 2, dy);
      let t;
      if (enTierra) {
        const dh = Math.abs(alt[j] - alt[i]);
        if (dh > d * tanMax) continue;
        t = d / (v * factor(Math.atan(dh / d) * R2D, pendMax));
      } else t = d / v;
      const cj = ci + t;
      if (cj >= coste[j]) continue;
      coste[j] = cj;
      desde[j] = k;
      cola.push(j, cj + h(j));
    }
  }
  if (coste[g] === Infinity) return null;

  const camino = [];
  for (let i = g; ;) {
    camino.push(i);
    if (i === s) break;
    const k = desde[i], x = i % W, y = (i / W) | 0;
    i = (y - DDY[k]) * W + (x - DDX[k] + W) % W;
  }
  return camino.reverse();
}

/* Un tramo recto de p a q: si se puede ir por él y cuánto se tarda. */
function tramo(p, q, enTierra, v, pendMax) {
  const d = distancia(p[0], p[1], q[0], q[1]);
  const n = Math.max(1, Math.ceil(d / (M.dy / 2)));
  const paso = d / n;
  let t = 0, sube = 0, baja = 0, pmax = 0, hmax = -Infinity, hPrev = altura(p[0], p[1]);
  for (let i = 1; i <= n; i++) {
    const m = interpolar(p, q, i / n);
    if (esTierra(m[0], m[1]) !== enTierra) return null;
    if (enTierra) {
      const hh = altura(m[0], m[1]), dh = hh - hPrev;
      const pend = paso > 0 ? Math.atan(Math.abs(dh) / paso) * R2D : 0;
      if (pend > pendMax) return null;
      t += paso / (v * factor(pend, pendMax));
      if (dh > 0) sube += dh; else baja -= dh;
      pmax = Math.max(pmax, pend);
      hmax = Math.max(hmax, hh);
      hPrev = hh;
    } else t += paso / v;
  }
  return { t, d, sube, baja, pmax, hmax };
}

/* Une en recta los puntos que se puedan unir sin tardar más: para cada uno, el más lejano,
   a saltos que se doblan y luego a medias. */
function enderezar(pts, enTierra, v, pendMax) {
  if (pts.length < 3) return pts;
  const acum = new Float64Array(pts.length);
  for (let k = 1; k < pts.length; k++) {
    const tr = tramo(pts[k - 1], pts[k], enTierra, v, pendMax);
    acum[k] = acum[k - 1] + (tr ? tr.t : distancia(pts[k - 1][0], pts[k - 1][1], pts[k][0], pts[k][1]) / v);
  }
  const atajo = (i, j) => {
    const tr = tramo(pts[i], pts[j], enTierra, v, pendMax);
    return tr !== null && tr.t <= acum[j] - acum[i] + 1e-6;
  };
  const o = [pts[0]];
  const ult = pts.length - 1;
  let a = 0;
  while (a < ult) {
    let bien = a + 1, paso = 2;
    const tope = Math.min(ult, a + 600);
    while (a + paso <= tope && atajo(a, a + paso)) { bien = a + paso; paso *= 2; }
    let mal = Math.min(tope + 1, a + paso);
    while (mal - bien > 1) {
      const m = (bien + mal) >> 1;
      if (atajo(a, m)) bien = m; else mal = m;
    }
    o.push(pts[bien]);
    a = bien;
  }
  return o;
}

function nueva(medio) {
  return { medio, ok: false, motivo: null, puntos: [], distancia: 0, tiempo: 0, subida: 0, bajada: 0,
           cotaMax: null, pendienteMax: 0, salida: 0, llegada: 0, puertoSalida: null, puertoLlegada: null };
}

function aire(a, b, v) {
  const r = nueva('aire');
  r.ok = true;
  r.puntos = densificar([a, b]);
  r.distancia = distancia(a[0], a[1], b[0], b[1]);
  r.tiempo = r.distancia / v;
  const n = Math.max(1, Math.ceil(r.distancia / (M.dy / 2)));
  let cota = -Infinity;
  for (let i = 0; i <= n; i++) { const p = interpolar(a, b, i / n); cota = Math.max(cota, altura(p[0], p[1])); }
  r.cotaMax = cota;
  return r;
}

function buscar(medio, a, b, v, pendMax) {
  const r = nueva(medio);
  const enTierra = medio === 'tierra';
  if (!enTierra && !M.hayMar) { r.motivo = 'Este cuerpo no tiene mar.'; return r; }
  const { W, tierra, zona } = M;
  const ia = fila(a[0]) * W + columna(a[1]), ib = fila(b[0]) * W + columna(b[1]);
  let sa = cercana(ia, enTierra, 0), sb = cercana(ib, enTierra, 0);
  if (sa < 0 || sb < 0) {
    r.motivo = enTierra ? 'No hay tierra a menos de 100 km del origen o del destino.' : 'No hay mar a menos de 100 km del origen o del destino.';
    return r;
  }
  /* En tierra no se cruza el mar hasta la costa de enfrente: si la tierra más cercana a cada
     punta no es la misma isla o continente, no hay ruta en rover. */
  if (zona[sa] !== zona[sb] && enTierra) {
    r.motivo = 'Origen y destino están en tierras separadas por el mar.';
    return r;
  }
  if (zona[sa] !== zona[sb]) {
    const sb2 = cercana(ib, enTierra, zona[sa]), sa2 = cercana(ia, enTierra, zona[sb]);
    const d1 = sb2 < 0 ? Infinity : distCeldas(ia, sa) + distCeldas(ib, sb2);
    const d2 = sa2 < 0 ? Infinity : distCeldas(ia, sa2) + distCeldas(ib, sb);
    if (d1 === Infinity && d2 === Infinity) {
      r.motivo = enTierra ? 'Origen y destino están en tierras separadas por el mar.'
                          : 'Origen y destino dan a mares que no se tocan.';
      return r;
    }
    if (d1 <= d2) sb = sb2; else sa = sa2;
  }

  const celdas = aEstrella(sa, sb, enTierra, v, pendMax);
  if (!celdas) {
    r.motivo = enTierra ? 'Las cuestas cortan el paso: no hay camino con esa pendiente máxima.'
                        : 'No se encontró camino por mar.';
    return r;
  }
  // las puntas exactas si caen en su propia celda; si no, el agua o la tierra más cercana
  const pts = celdas.map(centro);
  if (sa === ia) pts[0] = a; else { r.puertoSalida = pts[0]; r.salida = distancia(a[0], a[1], pts[0][0], pts[0][1]); }
  const u = pts.length - 1;
  if (sb === ib) pts[u] = b; else { r.puertoLlegada = pts[u]; r.llegada = distancia(b[0], b[1], pts[u][0], pts[u][1]); }

  const recto = enderezar(pts, enTierra, v, pendMax);
  r.puntos = densificar(recto);
  let cota = -Infinity;
  for (let k = 1; k < recto.length; k++) {
    const tr = tramo(recto[k - 1], recto[k], enTierra, v, pendMax);
    const d = distancia(recto[k - 1][0], recto[k - 1][1], recto[k][0], recto[k][1]);
    r.distancia += d;
    // un tramo de celda a celda puede rozar la costa al muestrearlo: cuenta a velocidad llana
    r.tiempo += tr ? tr.t : d / v;
    if (tr) {
      r.subida += tr.sube; r.bajada += tr.baja;
      r.pendienteMax = Math.max(r.pendienteMax, tr.pmax);
      cota = Math.max(cota, tr.hmax);
    }
  }
  if (enTierra && cota > -Infinity) r.cotaMax = cota;
  r.ok = true;
  return r;
}

self.onmessage = e => {
  const d = e.data;
  try {
    if (d.tipo === 'malla') { prepararMalla(d); self.postMessage({ tipo: 'malla-lista' }); return; }
    if (d.tipo === 'ruta') {
      const rutas = [
        aire(d.a, d.b, d.vAire),
        buscar('mar', d.a, d.b, d.vMar, d.pendiente),
        buscar('tierra', d.a, d.b, d.vTierra, d.pendiente),
      ];
      self.postMessage({ tipo: 'ruta', id: d.id, rutas });
    }
  } catch (err) {
    self.postMessage({ tipo: 'error', id: d.id, mensaje: String(err && err.message || err) });
  }
};
