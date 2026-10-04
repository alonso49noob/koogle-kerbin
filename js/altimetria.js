/* Filtro de altimetría: el terreno que cae dentro de una franja de altura se pinta con
   una paleta tipo SCANsat (azul abajo, verde en las llanuras, amarillo y marrón arriba y
   blanco en las cumbres) y el de fuera se apaga. Lo mismo que la app de escritorio, en el
   mapa plano. Necesita mapa de alturas, con la calibración de «Altura (opcional)». */

(function () {
  const $ = id => document.getElementById(id);
  const AJUSTES = 'kerbinmaps.altimetria.v1';
  const MAX_ANCHO = 4096;
  const PAL = [[0.13, 0.25, 0.55], [0.10, 0.55, 0.62], [0.25, 0.62, 0.29],
               [0.85, 0.79, 0.35], [0.68, 0.36, 0.20], [0.96, 0.96, 0.98]];

  function paleta(t) {
    t = Math.max(0, Math.min(1, t));
    const s = t * 5, i = Math.min(4, Math.floor(s)), f = s - i;
    const a = PAL[i], b = PAL[i + 1];
    return [a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f];
  }

  KM.altimetria = {
    map: null, ctx: null, layer: null, _clave: null, _pct: null,
    s: { on: false, min: null, max: null, opacity: 0.85 },

    /* ctx: { mapas() -> { height, hMin, hMax, offHeight } } */
    init(map, ctx) {
      this.map = map;
      this.ctx = ctx;
      Object.assign(this.s, KM.store.loadJSON(AJUSTES, {}));
      $('alt-on').checked = this.s.on;
      $('alt-op').value = Math.round(this.s.opacity * 100);
      $('alt-op-val').textContent = Math.round(this.s.opacity * 100) + ' %';
      $('alt-on').addEventListener('change', e => { this.s.on = e.target.checked; this._guardar(); this.refrescar(); });
      $('alt-min').addEventListener('change', e => { const v = parseFloat(e.target.value); this.s.min = isFinite(v) ? v : null; this._guardar(); this.refrescar(); });
      $('alt-max').addEventListener('change', e => { const v = parseFloat(e.target.value); this.s.max = isFinite(v) ? v : null; this._guardar(); this.refrescar(); });
      $('alt-op').addEventListener('input', e => {
        this.s.opacity = e.target.value / 100;
        $('alt-op-val').textContent = e.target.value + ' %';
        if (this.layer) this.layer.setOpacity(this.s.opacity);
        this._guardar();
      });
      $('alt-reset').addEventListener('click', () => { this.s.min = this.s.max = null; this._guardar(); this.refrescar(); });
      this.refrescar();
    },

    _guardar() { KM.store.saveJSON(AJUSTES, this.s); },

    rango() {
      const m = this.ctx.mapas();
      let min = this.s.min != null ? this.s.min : m.hMin, max = this.s.max != null ? this.s.max : m.hMax;
      if (max <= min) max = min + 1;
      return { min, max };
    },

    /* Se llama tras cualquier cambio de mapas o de calibración; si nada de lo que
       importa ha cambiado, no hace nada. */
    refrescar() {
      if (!this.ctx) return;
      const m = this.ctx.mapas();
      const { min, max } = this.rango();
      $('alt-min').value = Math.round(min);
      $('alt-max').value = Math.round(max);
      const clave = [this.s.on, m.height, m.hMin, m.hMax, m.offHeight, min, max];
      if (this._clave && this._clave.every((x, i) => x === clave[i])) return;
      this._clave = clave;

      if (this.layer) { this.map.removeLayer(this.layer); this.layer = null; }
      this._pct = null;
      if (this.s.on && m.height) {
        const W = Math.max(256, Math.min(MAX_ANCHO, m.height.width)), H = W / 2;
        const c = document.createElement('canvas');
        c.width = W; c.height = H;
        const g = c.getContext('2d', { willReadFrequently: true });
        g.drawImage(m.height, 0, 0, W, H);
        const img = g.getImageData(0, 0, W, H), d = img.data;
        const span = Math.max(1, max - min);
        let tot = 0, dentro = 0;
        for (let y = 0; y < H; y++) {
          const w = Math.cos((90 - (y + 0.5) * 180 / H) * Math.PI / 180);
          for (let x = 0; x < W; x++) {
            const i = (y * W + x) * 4;
            const lum = (0.2126 * d[i] + 0.7152 * d[i + 1] + 0.0722 * d[i + 2]) / 255;
            const alt = m.hMin + lum * (m.hMax - m.hMin);
            tot += w;
            if (alt < min || alt > max) {
              d[i] = 4; d[i + 1] = 5; d[i + 2] = 9; d[i + 3] = 199;
            } else {
              dentro += w;
              const p = paleta((alt - min) / span);
              d[i] = p[0] * 255; d[i + 1] = p[1] * 255; d[i + 2] = p[2] * 255; d[i + 3] = 255;
            }
          }
        }
        g.putImageData(img, 0, 0);
        this._pct = tot ? dentro / tot * 100 : null;
        this.layer = new KM.ImageLayer(c, {
          smooth: true, lonOffset: m.offHeight || 0, opacity: this.s.opacity,
          minZoom: KM.MAP.minZoom, maxZoom: KM.MAP.maxZoom
        }).addTo(this.map);
        this.layer.setOpacity(this.s.opacity);
      }
      this._info(m, min, max);
    },

    _info(m, min, max) {
      const el = $('alt-info');
      if (!m.height) { el.innerHTML = 'Sin mapa de alturas cargado: el filtro no puede medir nada.'; return; }
      const filas = ['Franja <b>' + Math.round(min) + '</b> a <b>' + Math.round(max) + '</b> m (el mapa va de ' +
                     Math.round(m.hMin) + ' a ' + Math.round(m.hMax) + ' m).'];
      if (this._pct != null) filas.push('Dentro de la franja: <b>' + this._pct.toFixed(1) + ' %</b> de la superficie.');
      el.innerHTML = filas.join('\n');
    }
  };
})();
