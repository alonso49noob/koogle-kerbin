/* Herramientas de dibujo: regla sobre gran círculo y huella del horizonte. */

(function () {
  KM.tools = {
    map: null,
    layer: null,
    mode: null,          // null | 'measure' | 'footprint'
    _pts: [],
    _preview: null,
    onModeChange: null,

    init(map) {
      this.map = map;
      this.layer = L.layerGroup().addTo(map);
      map.on('click', e => this._onClick(e));
      map.on('mousemove', e => this._onMove(e));
      document.addEventListener('keydown', e => {
        if (e.key === 'Escape') this.setMode(null);
      });
      return this;
    },

    setMode(mode) {
      if (this.mode === 'measure' && mode !== 'measure') this._endMeasure();
      this.mode = this.mode === mode ? null : mode;
      this._pts = [];
      if (this._preview) { this.layer.removeLayer(this._preview); this._preview = null; }
      this.map.getContainer().style.cursor = this.mode ? 'crosshair' : '';
      if (this.onModeChange) this.onModeChange(this.mode);
    },

    clear() {
      this.layer.clearLayers();
      this._pts = [];
      this._preview = null;
    },

    _onClick(e) {
      const lat = e.latlng.lat, lon = KM.geo.wrapLon(e.latlng.lng);
      if (this.mode === 'measure') this._addMeasurePoint(lat, lon);
      else if (this.mode === 'footprint') this._addFootprint(lat, lon);
    },

    _onMove(e) {
      if (this.mode !== 'measure' || this._pts.length === 0) return;
      const last = this._pts[this._pts.length - 1];
      const lat = e.latlng.lat, lon = KM.geo.wrapLon(e.latlng.lng);
      const segs = KM.geo.splitAntimeridian(KM.geo.greatCircle(last[0], last[1], lat, lon));
      if (this._preview) this.layer.removeLayer(this._preview);
      this._preview = L.polyline(segs, {
        color: '#4ea3ff', weight: 1.5, dashArray: '4 4', opacity: .8, interactive: false
      }).addTo(this.layer);
    },

    _addMeasurePoint(lat, lon) {
      this._pts.push([lat, lon]);
      L.circleMarker([lat, lon], {
        radius: 3.5, color: '#fff', weight: 1.5, fillColor: '#4ea3ff', fillOpacity: 1
      }).addTo(this.layer);

      if (this._pts.length < 2) return;

      const a = this._pts[this._pts.length - 2], b = this._pts[this._pts.length - 1];
      const d = KM.geo.distance(a[0], a[1], b[0], b[1]);
      const brg = KM.geo.bearing(a[0], a[1], b[0], b[1]);
      const segs = KM.geo.splitAntimeridian(KM.geo.greatCircle(a[0], a[1], b[0], b[1]));

      L.polyline(segs, { color: '#4ea3ff', weight: 2, opacity: .95, interactive: false })
        .addTo(this.layer);

      let total = 0;
      for (let i = 1; i < this._pts.length; i++) {
        const p = this._pts[i - 1], q = this._pts[i];
        total += KM.geo.distance(p[0], p[1], q[0], q[1]);
      }

      const label = KM.geo.fmtDist(d) + '  ·  ' + brg.toFixed(1) + '°' +
                    (this._pts.length > 2 ? '   (total ' + KM.geo.fmtDist(total) + ')' : '');

      L.marker([b[0], b[1]], { opacity: 0, interactive: false })
        .bindTooltip(label, { className: 'km-label', permanent: true, direction: 'right', offset: [8, 0] })
        .addTo(this.layer);
    },

    _endMeasure() {
      if (this._preview) { this.layer.removeLayer(this._preview); this._preview = null; }
    },

    _addFootprint(lat, lon) {
      const altKm = parseFloat(document.getElementById('fp-alt').value) || 0;
      const h = altKm * 1000;
      const r = KM.geo.horizonRadius(h);
      const segs = KM.geo.circle(lat, lon, r);

      L.polyline(segs, { color: '#7ee787', weight: 1.8, opacity: .9, interactive: false })
        .addTo(this.layer);
      L.circleMarker([lat, lon], {
        radius: 3.5, color: '#fff', weight: 1.5, fillColor: '#7ee787', fillOpacity: 1
      }).addTo(this.layer);

      const halfAngle = (r / KM.BODY.radius) * 180 / Math.PI;
      L.marker([lat, lon], { opacity: 0, interactive: false })
        .bindTooltip(
          'h ' + altKm.toFixed(0) + ' km · horizonte ' + KM.geo.fmtDist(r) +
          ' (' + halfAngle.toFixed(1) + '° de arco)',
          { className: 'km-label', permanent: true, direction: 'right', offset: [8, 0] })
        .addTo(this.layer);
    }
  };
})();
