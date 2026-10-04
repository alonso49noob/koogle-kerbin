/* Rutas: cuánto se tarda de un punto a otro en avión, en barco y en rover, como un
   navegador de coche. La búsqueda va en js/rutas-worker.js; aquí se hace la rejilla con
   los mapas cargados, se eligen los puntos y se pintan las rutas en el mapa y el globo. */

(function () {
  const $ = id => document.getElementById(id);
  const COL = { aire: '#4ea3ff', mar: '#2dd4bf', tierra: '#ffb454' };
  const NOMBRE = { aire: 'Avión', mar: 'Barco', tierra: 'Rover' };
  const AJUSTES = 'kerbinmaps.rutas.v1';
  const MAX_ANCHO = 4096;

  KM.rutas = {
    map: null, ctx: null, layer: null, worker: null,
    a: null, b: null, nombreA: null, nombreB: null,
    rutas: null, calculando: false,
    _malla: null, _mallaLista: null, _id: 0,
    v: { aire: 300, mar: 15, tierra: 20, pendiente: 30 },

    /* ctx: { mapas() -> { height, color, hMin, hMax, offHeight, offColor }, globe(), fecha(t), ahora() } */
    init(map, ctx) {
      this.map = map;
      this.ctx = ctx;
      this.layer = L.layerGroup().addTo(map);
      Object.assign(this.v, KM.store.loadJSON(AJUSTES, {}));
      for (const k of ['aire', 'mar', 'tierra', 'pendiente']) {
        const el = $('rv-' + k);
        el.value = this.v[k];
        el.addEventListener('change', () => {
          const min = k === 'aire' ? 1 : k === 'pendiente' ? 1 : 0.5;
          let x = parseFloat(el.value);
          if (!isFinite(x)) x = this.v[k];
          x = Math.max(min, k === 'pendiente' ? Math.min(80, x) : x);
          this.v[k] = x;
          el.value = x;
          KM.store.saveJSON(AJUSTES, this.v);
          this.calcular();
        });
      }
      for (const [id, origen] of [['ruta-origen', true], ['ruta-destino', false]]) {
        const sel = $(id);
        sel.addEventListener('mousedown', () => this._opciones());   // los marcadores pueden haber cambiado
        sel.addEventListener('focus', () => this._opciones());
        sel.addEventListener('change', () => this._desdeSelect(sel.value, origen));
      }
      $('ruta-elegir').addEventListener('click', () => KM.tools.setMode('route'));
      $('ruta-invertir').addEventListener('click', () => this.invertir());
      $('ruta-quitar').addEventListener('click', () => this.limpiar());
      this._opciones();
      this._info();
    },

    /* ------------------------------------------------------------ puntos */

    hint() { return !this.a || this.b ? 'Haz clic en el origen. Esc para terminar.' : 'Ahora haz clic en el destino.'; },

    click(lat, lon) {
      if (!this.a || this.b) {
        this.a = [lat, lon]; this.nombreA = null;
        this.b = null; this.nombreB = null;
        $('tool-hint').textContent = $('ruta-estado').textContent = this.hint();
        this.calcular();
      } else {
        this.b = [lat, lon]; this.nombreB = null;
        KM.tools.setMode(null);
        this.calcular();
      }
      this._opciones();
    },

    invertir() {
      [this.a, this.b] = [this.b, this.a];
      [this.nombreA, this.nombreB] = [this.nombreB, this.nombreA];
      this._opciones();
      this.calcular();
    },

    limpiar() {
      this._id++;
      this.a = this.b = null;
      this.nombreA = this.nombreB = null;
      this.rutas = null;
      this.calculando = false;
      if (KM.tools.mode === 'route') KM.tools.setMode(null);
      this._opciones();
      this._dibujar();
      this._info();
    },

    _opciones() {
      const marcas = KM.markers.all();
      for (const [id, p, nombre] of [['ruta-origen', this.a, this.nombreA], ['ruta-destino', this.b, this.nombreB]]) {
        const sel = $(id);
        sel.innerHTML = '';
        const add = (v, t) => { const o = document.createElement('option'); o.value = v; o.textContent = t; sel.appendChild(o); };
        add('', '—');
        if (p && !nombre) add('mapa', p[0].toFixed(3) + ', ' + p[1].toFixed(3));
        marcas.forEach(m => add('m:' + m.id, m.name));
        const mk = p && nombre ? marcas.find(m => m.name === nombre && m.lat === p[0] && m.lon === p[1]) : null;
        sel.value = !p ? '' : !nombre ? 'mapa' : mk ? 'm:' + mk.id : '';
      }
    },

    _desdeSelect(v, origen) {
      if (v === 'mapa') return;
      const m = v.startsWith('m:') ? KM.markers.all().find(x => 'm:' + x.id === v) : null;
      const p = m ? [m.lat, m.lon] : null, nombre = m ? m.name : null;
      if (origen) { this.a = p; this.nombreA = nombre; } else { this.b = p; this.nombreB = nombre; }
      this._opciones();
      this.calcular();
    },

    /* ------------------------------------------------------------ rejilla */

    /* La rejilla con los mapas que haya, hecha una vez por combinación de mapas. Sin mapa
       de alturas, la tierra sale del de color (lo que no es azul) y no hay cuestas. */
    _prepararMalla() {
      const m = this.ctx.mapas();
      const clave = [m.height, m.color, m.hMin, m.hMax, m.offHeight, m.offColor];
      if (this._malla && this._malla.every((x, i) => x === clave[i])) return this._mallaLista;
      this._malla = clave;
      if (this.worker) this.worker.terminate();
      this.worker = new Worker('js/rutas-worker.js');
      this.worker.onmessage = e => this._respuesta(e.data);

      const src = m.height || m.color;
      const W = src ? Math.max(512, Math.min(MAX_ANCHO, src.width)) : 2048, H = W / 2;
      const alt = new Float32Array(W * H), tierra = new Uint8Array(W * H);
      let hayMar = false;
      if (!src) tierra.fill(1);
      else {
        const c = document.createElement('canvas');
        c.width = W; c.height = H;
        const g = c.getContext('2d', { willReadFrequently: true });
        g.imageSmoothingEnabled = !!m.height;
        g.drawImage(src, 0, 0, W, H);
        const d = g.getImageData(0, 0, W, H).data;
        // el desfase de la imagen: la longitud L del mapa está en la columna de L + desfase
        const off = Math.round(((m.height ? m.offHeight : m.offColor) || 0) / 360 * W);
        for (let y = 0; y < H; y++)
          for (let x = 0; x < W; x++) {
            const s = (y * W + (((x + off) % W) + W) % W) * 4, i = y * W + x;
            if (m.height) {
              const lum = (0.2126 * d[s] + 0.7152 * d[s + 1] + 0.0722 * d[s + 2]) / 255;
              const h = m.hMin + lum * (m.hMax - m.hMin);
              alt[i] = h;
              tierra[i] = h > 0 ? 1 : 0;
            } else tierra[i] = d[s + 2] - Math.max(d[s], d[s + 1]) < 14 ? 1 : 0;
            if (!tierra[i]) hayMar = true;
          }
      }
      this._mallaLista = new Promise(res => { this._alListo = res; });
      this.worker.postMessage({ tipo: 'malla', W, H, alt, tierra, hayMar, radio: KM.BODY.radius }, [alt.buffer, tierra.buffer]);
      return this._mallaLista;
    },

    async calcular() {
      const id = ++this._id;
      if (!this.a || !this.b) {
        this.rutas = null; this.calculando = false;
        this._dibujar(); this._info();
        return;
      }
      this.calculando = true;
      this.rutas = null;
      this._dibujar();
      this._info();
      try {
        await this._prepararMalla();
      } catch (err) {
        this.calculando = false;
        this._info('No se pudo preparar la rejilla: ' + err.message);
        return;
      }
      if (id !== this._id) return;
      this.worker.postMessage({
        tipo: 'ruta', id, a: this.a, b: this.b,
        vAire: this.v.aire, vMar: this.v.mar, vTierra: this.v.tierra, pendiente: this.v.pendiente
      });
    },

    _respuesta(d) {
      if (d.tipo === 'malla-lista') { this._alListo(); return; }
      if (d.id !== this._id) return;
      this.calculando = false;
      if (d.tipo === 'error') { this._info('No se pudo calcular la ruta: ' + d.mensaje); return; }
      this.rutas = d.rutas;
      this._dibujar();
      this._info();
    },

    /* La más rápida de las que se pueden hacer. */
    rapida() {
      if (!this.rutas) return null;
      return this.rutas.filter(r => r.ok).sort((x, y) => x.tiempo - y.tiempo)[0] || null;
    },

    /* ------------------------------------------------------------ dibujo */

    _dibujar() {
      this.layer.clearLayers();
      const globo = [];
      const rap = this.rapida();
      if (this.rutas) {
        // la más rápida, encima
        const orden = this.rutas.filter(r => r.ok).sort((x, y) => (x === rap) - (y === rap));
        for (const r of orden) {
          const op = r === rap ? 1 : 0.75;
          const segs = KM.geo.splitAntimeridian(r.puntos);
          L.polyline(segs, {
            color: COL[r.medio], weight: r === rap ? 4 : 2.5, opacity: op, interactive: false,
            dashArray: r.medio === 'aire' ? '7 6' : null
          }).addTo(this.layer);
          globo.push({ pts: r.puntos, color: COL[r.medio], alpha: op });
          for (const p of [r.puertoSalida, r.puertoLlegada])
            if (p) L.circleMarker(p, { radius: 3.5, color: '#fff', weight: 1, fillColor: COL[r.medio], fillOpacity: 1 })
              .bindTooltip(r.medio === 'mar' ? 'Al agua' : 'A tierra').addTo(this.layer);
        }
      }
      const punta = (p, color, txt) => {
        if (!p) return;
        L.circleMarker(p, { radius: 5, color: '#fff', weight: 1.5, fillColor: color, fillOpacity: 1, interactive: false }).addTo(this.layer);
        L.marker(p, { opacity: 0, interactive: false })
          .bindTooltip(txt, { className: 'km-label', permanent: true, direction: 'right', offset: [8, 0] })
          .addTo(this.layer);
      };
      punta(this.a, '#7ee787', this.nombreA || 'Origen');
      punta(this.b, '#ff6b6b', this.nombreB || 'Destino');
      const g = this.ctx.globe();
      if (g && g.setRutas) g.setRutas(globo);
    },

    /* Para el globo cuando se crea después de calcular. */
    paraGlobo() {
      const rap = this.rapida();
      return (this.rutas || []).filter(r => r.ok).map(r => ({ pts: r.puntos, color: COL[r.medio], alpha: r === rap ? 1 : 0.75 }));
    },

    _info(error) {
      const el = $('ruta-info');
      const esc = s => String(s).replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
      if (!this.a || !this.b) { el.innerHTML = !this.a ? 'Elige el origen y el destino.' : 'Falta el destino.'; return; }
      const recta = KM.geo.distance(this.a[0], this.a[1], this.b[0], this.b[1]);
      const filas = ['En línea recta <b>' + KM.geo.fmtDist(recta) + '</b>'];
      if (error) filas.push(esc(error));
      else if (this.calculando || !this.rutas) filas.push('Calculando rutas…');
      else {
        const rap = this.rapida();
        const ahora = this.ctx.ahora();
        for (const r of this.rutas) {
          const cab = '<span class="sw" style="background:' + COL[r.medio] + '"></span><b>' + NOMBRE[r.medio] + '</b>';
          if (!r.ok) { filas.push(cab + '  <span class="dim">' + esc(r.motivo || 'Sin ruta.') + '</span>'); continue; }
          filas.push(cab + '  ' + KM.geo.fmtDist(r.distancia) + '  <b>' + KM.geo.fmtTime(r.tiempo) + '</b>' +
                     (r === rap ? '  ★ la más rápida' : ''));
          const det = [];
          if (r.medio === 'aire' && r.cotaMax != null) det.push('lo más alto debajo ' + (r.cotaMax >= 0 ? '+' : '') + r.cotaMax.toFixed(0) + ' m');
          if (r.salida > 0) det.push('sale a ' + KM.geo.fmtDist(r.salida) + ' del origen');
          if (r.llegada > 0) det.push('llega a ' + KM.geo.fmtDist(r.llegada) + ' del destino');
          if (r.medio === 'tierra') {
            det.push('sube ' + r.subida.toFixed(0) + ' m, baja ' + r.bajada.toFixed(0) + ' m');
            det.push('pendiente máx. ' + r.pendienteMax.toFixed(1) + '°');
          }
          if (r.distancia > recta * 1.02) det.push('rodeo +' + ((r.distancia / recta - 1) * 100).toFixed(0) + ' %');
          if (ahora != null) det.push('llegada ' + this.ctx.fecha(ahora + r.tiempo));
          if (det.length) filas.push('<span class="dim">    ' + det.join(' · ') + '</span>');
        }
      }
      el.innerHTML = filas.join('\n');
    }
  };
})();
