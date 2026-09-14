/* Lectura de una partida de KSP (.sfs) y calibración del marco de rotación.

   El formato es ConfigNode: un nombre en su línea, una llave que abre, pares
   clave = valor, y llave que cierra. Se recorre de una pasada; un persistent.sfs
   de 6 MB son unas 200 000 líneas y se despacha en menos de un segundo.

   Solo interesan dos cosas: el UT de la partida y los nodos VESSEL con su
   subnodo ORBIT. */

(function () {
  const D2R = Math.PI / 180, R2D = 180 / Math.PI;

  /* Campos de VESSEL que se conservan; el resto (piezas, tripulación, recursos)
     se ignora, que es la mayor parte del fichero. */
  const CAMPOS = ['name', 'type', 'sit', 'landed', 'splashed', 'lat', 'lon', 'alt', 'pid'];
  const ORBITA = ['SMA', 'ECC', 'INC', 'LPE', 'LAN', 'MNA', 'EPH', 'REF', 'IDENT'];

  function parse(text) {
    const lines = text.split('\n');
    const stack = [];
    let pend = null;                 // nombre leído, a la espera de su llave
    let ut = null;
    const vessels = [];
    let v = null, enOrbita = false;

    for (let i = 0; i < lines.length; i++) {
      const s = lines[i].trim();
      if (!s) continue;

      if (s === '{') {
        stack.push(pend || '');
        /* Se apunta la profundidad de la nave: dentro de cada VESSEL hay PARTs y
           módulos con sus propios «name», «type» u «ORBIT», y solo valen los que
           cuelgan directamente de la nave. */
        if (pend === 'VESSEL' && !v) { v = { orbit: null, _d: stack.length }; enOrbita = false; }
        else if (v && pend === 'ORBIT' && !v.orbit && stack.length === v._d + 1) {
          v.orbit = {}; enOrbita = true;
        }
        pend = null;
        continue;
      }
      if (s === '}') {
        const fin = stack.pop();
        if (fin === 'ORBIT' && enOrbita && v && stack.length === v._d) enOrbita = false;
        else if (fin === 'VESSEL' && v && stack.length === v._d - 1) { vessels.push(v); v = null; }
        continue;
      }

      const eq = s.indexOf(' = ');
      if (eq < 0) { pend = s; continue; }

      const k = s.slice(0, eq), val = s.slice(eq + 3);
      if (ut === null && k === 'UT') ut = parseFloat(val);
      if (!v) continue;
      if (enOrbita) { if (ORBITA.includes(k)) v.orbit[k] = val; }
      else if (stack.length === v._d && CAMPOS.includes(k) && v[k] === undefined) v[k] = val;
    }

    return { ut, vessels: vessels.map(normalizar).filter(Boolean) };
  }

  function normalizar(v) {
    if (!v.name) return null;
    const num = x => { const f = parseFloat(x); return isFinite(f) ? f : null; };
    const o = v.orbit;
    const out = {
      name: v.name,
      type: v.type || '?',
      sit: v.sit || '?',
      lat: num(v.lat), lon: num(v.lon), alt: num(v.alt),
      body: o && o.IDENT ? String(o.IDENT).split('/').pop() : null,
      orbit: null
    };
    if (o) {
      const e = {
        sma: num(o.SMA), ecc: num(o.ECC), inc: num(o.INC),
        lpe: num(o.LPE), lan: num(o.LAN), mna: num(o.MNA), eph: num(o.EPH)
      };
      /* Las naves posadas llevan una órbita radial degenerada (e≈1, SMA = R/2)
         que no describe ninguna trayectoria: se descarta. */
      if (e.sma !== null && e.ecc !== null && e.ecc < 0.99 && e.sma > 0) out.orbit = e;
    }
    return out;
  }

  /* Dirección unitaria en el marco inercial para unos elementos y una anomalía
     media dada. Norte = Z, comprobado contra 107 naves del save. */
  function dirInercial(e, M) {
    const ecc = e.ecc;
    M = ((M % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
    let E = ecc < 0.8 ? M : Math.PI;
    for (let i = 0; i < 60; i++) {
      const d = (E - ecc * Math.sin(E) - M) / (1 - ecc * Math.cos(E));
      E -= d;
      if (Math.abs(d) < 1e-13) break;
    }
    const nu = 2 * Math.atan2(Math.sqrt(1 + ecc) * Math.sin(E / 2),
                              Math.sqrt(1 - ecc) * Math.cos(E / 2));
    const u = e.lpe * D2R + nu, I = e.inc * D2R, O = e.lan * D2R;
    const r = e.sma * (1 - ecc * Math.cos(E));
    return {
      x: Math.cos(O) * Math.cos(u) - Math.sin(O) * Math.sin(u) * Math.cos(I),
      y: Math.sin(O) * Math.cos(u) + Math.cos(O) * Math.sin(u) * Math.cos(I),
      z: Math.sin(u) * Math.sin(I),
      r
    };
  }

  KM.save = {
    parse,
    dirInercial,

    /* Ángulo de rotación del cuerpo en el UT de la partida.

       No se usa una constante: se mide con las propias naves. Cada VESSEL guarda
       lat/lon correspondientes a la posición en SU época EPH (comprobado: casan
       con mediana 0,199°, frente a 1,218° si se comparasen contra el UT). La
       diferencia entre la longitud inercial que dan sus elementos y la longitud
       del mapa que guarda el save ES el ángulo de rotación en esa época.

       Cada nave da un punto; se propagan todos al UT y se toma la mediana
       circular. Hace falta calibrar en vez de confiar en el periodo tabulado
       porque la sensibilidad es brutal: tras cientos de miles de vueltas, 7e-5 s
       de error en el periodo ya desplaza un grado. */
    calibrarRotacion(vessels, ut, periodo) {
      const T = periodo || KM.BODY.siderealDay;
      const muestras = [];

      for (const v of vessels) {
        if (!v.orbit || v.lat === null || v.lon === null) continue;
        const e = v.orbit;
        if (e.eph === null || e.mna === null) continue;
        const d = dirInercial(e, e.mna);

        /* Solo valen las naves cuya lat/lon está de verdad sincronizada con su
           época. La latitud no depende de la rotación, así que sirve de filtro
           limpio: si no cuadra, ese par lat/lon es de otro instante. */
        const latCalc = Math.asin(Math.max(-1, Math.min(1, d.z))) * R2D;
        if (Math.abs(latCalc - v.lat) > 0.01) continue;

        const lonIner = Math.atan2(d.y, d.x) * R2D;
        const rotEnEph = lonIner - v.lon;
        muestras.push(((rotEnEph + 360 * ((ut - e.eph) / T)) % 360 + 360) % 360);
      }

      if (!muestras.length) return { rot: 0, n: 0, descartadas: 0, mad: null, R: 0 };

      const media = arr => {
        let sx = 0, sy = 0;
        for (const a of arr) { sx += Math.cos(a * D2R); sy += Math.sin(a * D2R); }
        return ((Math.atan2(sy, sx) * R2D) % 360 + 360) % 360;
      };
      const desv = (a, c) => Math.abs(((a - c + 540) % 360) - 180);
      const mediana = arr => { const s = [...arr].sort((x, y) => x - y); return s[Math.floor((s.length - 1) / 2)]; };

      /* La media circular a secas no aguanta una nave cuya lat/lon sea de otro
         instante y se haya colado por el filtro de latitud: con 36 naves, una sola
         desviada 40° arrastra el resultado casi un grado. Se recorta en dos
         pasadas: lo que se aleje más de 5 veces la dispersión mediana (con un
         suelo de 2°, para no tirar naves buenas cuando todas casan al céntimo)
         queda fuera, y se recalcula con el resto. */
      let usadas = muestras, rot = media(muestras);
      for (let pasada = 0; pasada < 2; pasada++) {
        const mad = mediana(usadas.map(a => desv(a, rot)));
        const corte = Math.max(2, 5 * mad);
        const quedan = muestras.filter(a => desv(a, rot) <= corte);
        if (quedan.length < 3) break;
        usadas = quedan;
        rot = media(usadas);
      }

      let sx = 0, sy = 0;
      for (const a of usadas) { sx += Math.cos(a * D2R); sy += Math.sin(a * D2R); }
      return {
        rot,
        n: usadas.length,
        descartadas: muestras.length - usadas.length,
        mad: mediana(usadas.map(a => desv(a, rot))),   // dispersión típica, en grados
        R: Math.hypot(sx / usadas.length, sy / usadas.length)
      };
    },

    /* Posición sobre el suelo (lat, lon, altitud) en un instante dado. */
    posicionEn(e, t, ut, rotUT, periodo) {
      const T = periodo || KM.BODY.siderealDay;
      const n = Math.sqrt(KM.BODY.mu / Math.pow(e.sma, 3));
      const d = dirInercial(e, e.mna + n * (t - e.eph));
      const rot = rotUT + 360 * ((t - ut) / T);
      return {
        lat: Math.asin(Math.max(-1, Math.min(1, d.z))) * R2D,
        lon: KM.geo.wrapLon(Math.atan2(d.y, d.x) * R2D - rot),
        alt: d.r - KM.BODY.radius,
        t
      };
    },

    /* Traza terrestre: N órbitas a partir del UT de la partida. */
    traza(e, ut, rotUT, orbitas, periodo) {
      return this.trazaDesde(e, ut, ut, rotUT, orbitas || 2, null, periodo);
    },

    /* Traza desde un instante cualquiera (el de la simulación). `porVuelta` fija
       la densidad: para dibujar todas las naves a la vez basta con menos puntos. */
    trazaDesde(e, t0, ut, rotUT, orbitas, porVuelta, periodo) {
      const P = 2 * Math.PI / Math.sqrt(KM.BODY.mu / Math.pow(e.sma, 3));
      const N = orbitas || 1;
      const base = porVuelta || Math.max(90, 180 * (1 + 2 * e.ecc));
      const pasos = Math.max(2, Math.round(base * N));
      const pts = [];
      for (let i = 0; i <= pasos; i++) {
        pts.push(this.posicionEn(e, t0 + (i / pasos) * N * P, ut, rotUT, periodo));
      }
      return { puntos: pts, periodo: P };
    },

    /* La órbita como anillo cerrado, en el marco fijo al cuerpo con la rotación
       del instante `ut` (rotUT). Se muestrea en anomalía excéntrica y no en
       tiempo: en tiempo, una órbita excéntrica amontona los puntos en el apoapsis
       y deja el periapsis hecho de tramos rectos. */
    anillo(e, rotUT, puntos) {
      const n = puntos || 180;
      const pts = [];
      for (let i = 0; i <= n; i++) {
        const E = (i / n) * 2 * Math.PI;
        const d = dirInercial(e, E - e.ecc * Math.sin(E));
        pts.push({
          lat: Math.asin(Math.max(-1, Math.min(1, d.z))) * R2D,
          lon: KM.geo.wrapLon(Math.atan2(d.y, d.x) * R2D - rotUT),
          alt: d.r - KM.BODY.radius
        });
      }
      return pts;
    }
  };
})();
