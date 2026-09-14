/* Sonda de píxel: lee altura y bioma bajo el cursor.

   En vez de volcar la imagen entera a un ImageData (un mapa de 8192x4096 son
   128 MB de RGBA), se dibuja en un canvas de 1x1 solo el píxel consultado. Es
   exacto y no ocupa memoria. */

(function () {
  const px = document.createElement('canvas');
  px.width = 1; px.height = 1;
  const pctx = px.getContext('2d', { willReadFrequently: true });
  pctx.imageSmoothingEnabled = false;

  function samplePixel(bmp, lat, lon, lonOffset) {
    if (!bmp) return null;
    const iw = bmp.width, ih = bmp.height;
    let sx = Math.floor(((KM.geo.wrapLon(lon + (lonOffset || 0)) + 180) / 360) * iw);
    let sy = Math.floor(((90 - lat) / 180) * ih);
    sx = Math.max(0, Math.min(iw - 1, sx));
    sy = Math.max(0, Math.min(ih - 1, sy));
    pctx.clearRect(0, 0, 1, 1);
    try {
      pctx.drawImage(bmp, sx, sy, 1, 1, 0, 0, 1, 1);
      const d = pctx.getImageData(0, 0, 1, 1).data;
      return { r: d[0], g: d[1], b: d[2], a: d[3] };
    } catch (e) {
      return null;
    }
  }

  KM.probe = {
    /* Altura en metros a partir del gris del heightmap.
       Se usa luminancia por si el PNG trae un leve tinte de color. */
    height(bmp, lat, lon, range, lonOffset) {
      const c = samplePixel(bmp, lat, lon, lonOffset);
      if (!c) return null;
      const lum = (0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b) / 255;
      return range.min + lum * (range.max - range.min);
    },

    /* Color crudo del mapa de biomas. Los nombres los pone el usuario:
       la paleta depende de la versión del juego y de cómo exportes el mapa. */
    biome(bmp, lat, lon, lonOffset) {
      const c = samplePixel(bmp, lat, lon, lonOffset);
      if (!c || c.a === 0) return null;
      const hex = '#' + [c.r, c.g, c.b].map(v => v.toString(16).padStart(2, '0')).join('');
      return { hex, rgb: c };
    },

    /* Lista todos los colores del mapa de biomas con la superficie real que
       ocupa cada uno.

       Dos detalles que importan: se dibuja sin interpolar, porque un color
       promediado no es ningún bioma; y cada fila de píxeles pesa cos(lat),
       porque en equirectangular una fila cerca del polo representa mucha menos
       superficie que una del ecuador. Sin eso, los casquetes salen enormes. */
    palette(bmp, opts) {
      const o = opts || {};
      const maxW = o.maxW || 1024;
      const minPct = o.minPct !== undefined ? o.minPct : 0.02;

      const scale = Math.min(1, maxW / bmp.width);
      const w = Math.max(1, Math.round(bmp.width * scale));
      const h = Math.max(1, Math.round(bmp.height * scale));

      const c = document.createElement('canvas');
      c.width = w; c.height = h;
      const ctx = c.getContext('2d', { willReadFrequently: true });
      ctx.imageSmoothingEnabled = false;
      ctx.drawImage(bmp, 0, 0, w, h);

      let data;
      try { data = ctx.getImageData(0, 0, w, h).data; }
      catch (e) { return null; }

      const acc = new Map();
      let total = 0;
      for (let y = 0; y < h; y++) {
        const lat = 90 - ((y + 0.5) / h) * 180;
        const wgt = Math.cos(lat * Math.PI / 180);
        for (let x = 0; x < w; x++) {
          const i = (y * w + x) * 4;
          if (data[i + 3] === 0) continue;
          const key = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
          acc.set(key, (acc.get(key) || 0) + wgt);
          total += wgt;
        }
      }
      if (!total) return null;

      const all = [...acc.entries()]
        .map(([key, v]) => ({
          hex: '#' + key.toString(16).padStart(6, '0'),
          pct: (v / total) * 100
        }))
        .sort((a, b) => b.pct - a.pct);

      const colors = all.filter(c2 => c2.pct >= minPct);
      const dropped = all.filter(c2 => c2.pct < minPct);

      return {
        colors,
        sampled: w + '×' + h,
        native: bmp.width + '×' + bmp.height,
        dropped: { count: dropped.length, pct: dropped.reduce((s, d) => s + d.pct, 0) }
      };
    },

    /* Saturación media de la imagen. Sirve para pillar el error de cargar como
       heightmap una figura coloreada por paleta: un gris de verdad da ~0, y una
       paleta azul-verde-amarillo-rojo se va muy por encima. Importa porque la
       sonda traduce luminancia a metros, y en esas paletas la luminancia no
       crece con la altitud: el amarillo de media ladera brilla más que el rojo
       de la cumbre, así que las cimas saldrían hundidas. */
    saturation(bmp, maxW) {
      const w = Math.max(1, Math.min(maxW || 256, bmp.width));
      const h = Math.max(1, Math.round(w * bmp.height / bmp.width));
      const c = document.createElement('canvas');
      c.width = w; c.height = h;
      const ctx = c.getContext('2d', { willReadFrequently: true });
      ctx.drawImage(bmp, 0, 0, w, h);
      let d;
      try { d = ctx.getImageData(0, 0, w, h).data; } catch (e) { return null; }
      let sum = 0, n = 0;
      for (let i = 0; i < w * h; i++) {
        const r = d[i*4], g = d[i*4+1], b = d[i*4+2];
        if (d[i*4+3] === 0) continue;
        const mx = Math.max(r, g, b), mn = Math.min(r, g, b);
        if (mx > 0) { sum += (mx - mn) / mx; n++; }
      }
      return n ? sum / n : null;
    },

    sample: samplePixel
  };

  /* Diccionario color -> nombre de bioma, editable y persistente. */
  KM.biomeNames = {
    key: 'kerbinmaps.biomes.v1',
    map: null,
    load() {
      if (!this.map) this.map = KM.store.loadJSON(this.key, {});
      return this.map;
    },
    get(hex) { return this.load()[hex.toLowerCase()] || null; },
    set(hex, name) {
      this.load()[hex.toLowerCase()] = name;
      KM.store.saveJSON(this.key, this.map);
    }
  };
})();
