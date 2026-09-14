/* Capas de mapa.

   En vez de obligarte a trocear la textura en miles de ficheros, la imagen
   equirectangular se pinta tesela a tesela desde un ImageBitmap en memoria:
   misma fluidez que un tile server, cero pasos de preparación. Si ya tienes
   teselas de verdad, la fuente XYZ también está disponible. */

(function () {
  const TS = 256;
  const EMPTY_PNG = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

  /* Límites geográficos de una tesela en el esquema EPSG:4326 de Leaflet
     (a zoom 0 el mundo ocupa 2x1 teselas). */
  function tileBounds(coords) {
    const n = Math.pow(2, coords.z);
    return {
      lonW: (coords.x / (2 * n)) * 360 - 180,
      lonE: ((coords.x + 1) / (2 * n)) * 360 - 180,
      latN: 90 - (coords.y / n) * 180,
      latS: 90 - ((coords.y + 1) / n) * 180
    };
  }

  function canvasTile(size) {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const c = document.createElement('canvas');
    c.width = size * dpr;
    c.height = size * dpr;
    const ctx = c.getContext('2d');
    ctx.scale(dpr, dpr);
    return { canvas: c, ctx };
  }

  /* ------------------------------------------------------------------
     Capa a partir de una única imagen equirectangular
     ------------------------------------------------------------------ */

  KM.ImageLayer = L.GridLayer.extend({
    /* lonOffset: grados que hay que sumar a la longitud del mapa para dar con
       la columna correcta de la imagen. No todos los mapas que circulan usan el
       mismo meridiano de origen que KSP, y sin esto encajan mal por mucho. */
    options: { tileSize: TS, smooth: true, noWrap: false, lonOffset: 0 },

    initialize(bitmap, options) {
      L.setOptions(this, options);
      this._bmp = bitmap;
    },

    setBitmap(bitmap) {
      this._bmp = bitmap;
      this.redraw();
      return this;
    },

    setLonOffset(deg) {
      this.options.lonOffset = deg || 0;
      this.redraw();
      return this;
    },

    /* Un recorte de la imagen en una franja horizontal de la tesela. */
    _slice(ctx, bmp, sx, sy, sw, sh, dx, dw, pad) {
      if (sw <= 0 || dw <= 0) return;
      if (!pad) {
        ctx.drawImage(bmp, sx, sy, sw, sh, dx, 0, dw, TS);
        return;
      }
      /* Con suavizado, pintar el rectángulo justo deja costuras de 1 px entre
         teselas porque al filtro le faltan los píxeles vecinos. Se pide un
         píxel de más por cada lado y se deja que el canvas lo recorte. */
      const p = 1, kx = dw / sw, ky = TS / sh;
      ctx.drawImage(
        bmp,
        sx - p, sy - p, sw + 2 * p, sh + 2 * p,
        dx - p * kx, -p * ky, (sw + 2 * p) * kx, (sh + 2 * p) * ky
      );
    },

    createTile(coords, done) {
      const { canvas, ctx } = canvasTile(TS);
      /* Leaflet solo poda los niveles de zoom antiguos cuando las teselas nuevas
         se declaran cargadas. Pintamos en síncrono, pero el aviso tiene que
         llegar en otro tick o Leaflet se pierde. */
      setTimeout(() => done(null, canvas), 0);

      const bmp = this._bmp;
      if (!bmp) return canvas;

      const b = tileBounds(coords);
      const iw = bmp.width, ih = bmp.height;
      const off = this.options.lonOffset || 0;

      const sy = ((90 - b.latN) / 180) * ih;
      const sh = ((b.latN - b.latS) / 180) * ih;
      const sw = ((b.lonE - b.lonW) / 360) * iw;
      const sx = ((((b.lonW + off + 180) % 360) + 360) % 360) / 360 * iw;

      /* Al ampliar, un heightmap o un mapa de biomas debe leerse con el color
         exacto del píxel: interpolar inventaría alturas y biomas que no existen. */
      ctx.imageSmoothingEnabled = !!this.options.smooth;
      if (this.options.smooth) ctx.imageSmoothingQuality = 'high';

      if (sw >= iw) {                       // la tesela abarca el mundo entero
        ctx.drawImage(bmp, 0, sy, iw, sh, 0, 0, TS, TS);
        return canvas;
      }

      const over = sx + sw - iw;
      if (over > 0) {
        /* La tesela cae sobre la costura de la imagen: se pinta en dos trozos,
           el final de la textura y luego el principio. */
        const f = (sw - over) / sw;
        this._slice(ctx, bmp, sx, sy, sw - over, sh, 0, TS * f, false);
        this._slice(ctx, bmp, 0, sy, over, sh, TS * f, TS * (1 - f), false);
      } else {
        this._slice(ctx, bmp, sx, sy, sw, sh, 0, TS, !!this.options.smooth);
      }
      return canvas;
    }
  });

  /* ------------------------------------------------------------------
     Retícula lat/lon (sirve de fondo vacío y de rejilla superpuesta)
     ------------------------------------------------------------------ */

  const STEPS = [30, 10, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01];

  function stepForZoom(z) {
    const degPerTile = 180 / Math.pow(2, z);
    const target = degPerTile / 4;
    for (let i = STEPS.length - 1; i >= 0; i--) if (STEPS[i] >= target) return STEPS[i];
    return STEPS[0];
  }

  function fmtDeg(v, step) {
    const dec = step < 0.1 ? 2 : step < 1 ? 1 : 0;
    return v.toFixed(dec) + '°';
  }

  KM.stepForZoom = stepForZoom;
  KM.fmtDeg = fmtDeg;

  KM.GraticuleLayer = L.GridLayer.extend({
    options: {
      tileSize: TS, noWrap: false,
      filled: false,          // fondo opaco -> se usa como mapa base de respaldo
      color: 'rgba(120,160,200,0.28)',
      majorColor: 'rgba(150,195,240,0.55)'
    },

    createTile(coords, done) {
      const { canvas, ctx } = canvasTile(TS);
      setTimeout(() => done(null, canvas), 0);

      const b = tileBounds(coords);
      const step = stepForZoom(coords.z);

      if (this.options.filled) {
        ctx.fillStyle = '#0d141d';
        ctx.fillRect(0, 0, TS, TS);
        /* Damero muy tenue: deja claro que no hay imagen cargada. */
        const cell = TS / 4;
        ctx.fillStyle = 'rgba(255,255,255,0.012)';
        for (let i = 0; i < 4; i++)
          for (let j = 0; j < 4; j++)
            if ((i + j) % 2 === 0) ctx.fillRect(i * cell, j * cell, cell, cell);
      }

      const x = lon => ((lon - b.lonW) / (b.lonE - b.lonW)) * TS;
      const y = lat => ((b.latN - lat) / (b.latN - b.latS)) * TS;

      ctx.lineWidth = 1;

      const isMajor = v => Math.abs(v) < 1e-9 || Math.abs(Math.abs(v) - 90) < 1e-9
                        || Math.abs(Math.abs(v) - 180) < 1e-9;

      // meridianos
      const lonStart = Math.ceil(b.lonW / step) * step;
      for (let lon = lonStart; lon <= b.lonE + 1e-9; lon += step) {
        const px = Math.round(x(lon)) + 0.5;
        ctx.strokeStyle = isMajor(lon) ? this.options.majorColor : this.options.color;
        ctx.beginPath(); ctx.moveTo(px, 0); ctx.lineTo(px, TS); ctx.stroke();
      }

      // paralelos
      const latStart = Math.ceil(b.latS / step) * step;
      for (let lat = latStart; lat <= b.latN + 1e-9; lat += step) {
        const py = Math.round(y(lat)) + 0.5;
        ctx.strokeStyle = isMajor(lat) ? this.options.majorColor : this.options.color;
        ctx.beginPath(); ctx.moveTo(0, py); ctx.lineTo(TS, py); ctx.stroke();
      }

      return canvas;
    }
  });

  /* ------------------------------------------------------------------
     Etiquetas de la retícula

     Van en un lienzo fijo al viewport, no dentro de las teselas: si se pintan
     por tesela, cada línea sale rotulada tantas veces como teselas cruza y el
     mapa se vuelve ilegible. Aquí cada paralelo y cada meridiano se rotula una
     sola vez, pegado al borde, como en un mapa de verdad.
     ------------------------------------------------------------------ */

  KM.GraticuleLabels = L.Layer.extend({
    options: { topOffset: 52, leftOffset: 8 },

    onAdd(map) {
      this._map = map;
      this._canvas = L.DomUtil.create('canvas', 'km-grat-labels', map.getContainer());
      this._ctx = this._canvas.getContext('2d');
      /* Durante la animación de zoom las coordenadas de pantalla todavía no
         corresponden al zoom final, así que se ocultan las etiquetas y se
         vuelven a pintar al terminar. */
      this._hide = () => { this._canvas.style.opacity = '0'; };
      this._show = () => { this._canvas.style.opacity = '1'; this._draw(); };
      map.on('move resize viewreset', this._draw, this);
      map.on('zoomstart', this._hide);
      map.on('zoomend', this._show);
      this._draw();
      return this;
    },

    onRemove(map) {
      map.off('move resize viewreset', this._draw, this);
      map.off('zoomstart', this._hide);
      map.off('zoomend', this._show);
      L.DomUtil.remove(this._canvas);
      return this;
    },

    _draw() {
      const map = this._map;
      if (!map) return;
      const size = map.getSize();
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      const cv = this._canvas, ctx = this._ctx;

      if (cv.width !== size.x * dpr || cv.height !== size.y * dpr) {
        cv.width = size.x * dpr; cv.height = size.y * dpr;
        cv.style.width = size.x + 'px'; cv.style.height = size.y + 'px';
      }
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, size.x, size.y);

      const step = stepForZoom(map.getZoom());
      const b = map.getBounds();
      ctx.font = '10px ui-monospace, Consolas, monospace';
      ctx.textBaseline = 'middle';

      const chip = (text, cx, cy) => {
        const w = ctx.measureText(text).width;
        ctx.fillStyle = 'rgba(11,16,23,0.78)';
        ctx.fillRect(cx - 3, cy - 8, w + 6, 16);
        ctx.fillStyle = 'rgba(200,222,245,0.85)';
        ctx.fillText(text, cx, cy);
      };

      // meridianos, rotulados arriba
      ctx.textAlign = 'left';
      const w0 = b.getWest(), e0 = b.getEast();
      if (e0 - w0 < 720) {                       // evita rotular el mundo entero repetido
        for (let lon = Math.ceil(w0 / step) * step; lon <= e0; lon += step) {
          const x = map.latLngToContainerPoint([0, lon]).x;
          if (x < 4 || x > size.x - 30) continue;
          chip(fmtDeg(KM.geo.wrapLon(lon), step), x + 4, this.options.topOffset);
        }
      }

      // paralelos, rotulados a la izquierda
      const s0 = Math.max(-90, b.getSouth()), n0 = Math.min(90, b.getNorth());
      for (let lat = Math.ceil(s0 / step) * step; lat <= n0; lat += step) {
        const y = map.latLngToContainerPoint([lat, b.getCenter().lng]).y;
        if (y < 12 || y > size.y - 12) continue;
        chip(fmtDeg(lat, step), this.options.leftOffset, y);
      }
    }
  });

  /* ------------------------------------------------------------------
     Fuente XYZ clásica (teselas ya troceadas)
     ------------------------------------------------------------------ */

  KM.xyzLayer = function (url) {
    return L.tileLayer(url, {
      tileSize: TS,
      minZoom: KM.MAP.minZoom,
      maxZoom: KM.MAP.maxZoom,
      errorTileUrl: EMPTY_PNG,
      noWrap: false
    });
  };

  /* Decodifica un Blob a ImageBitmap (o a <img> si el navegador es viejo). */
  KM.decodeImage = async function (blob) {
    if (window.createImageBitmap) {
      try { return await createImageBitmap(blob); } catch (e) { /* sigue al plan B */ }
    }
    return new Promise((resolve, reject) => {
      const url = URL.createObjectURL(blob);
      const img = new Image();
      img.onload = () => { resolve(img); };
      img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('imagen ilegible')); };
      img.src = url;
    });
  };
})();
