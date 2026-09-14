/* Marcadores: los de referencia (data/landmarks.json) y los tuyos (localStorage). */

(function () {
  const COLORS = {
    ksc: '#4ea3ff', base: '#7ee787', aeropuerto: '#ffb454',
    anomalia: '#c77dff', usuario: '#ff6b6b', otro: '#8a9bb0'
  };

  function icon(cat) {
    return L.divIcon({
      className: '',
      html: '<div class="km-pin" style="background:' + (COLORS[cat] || COLORS.otro) + '"></div>',
      iconSize: [11, 11], iconAnchor: [5.5, 5.5]
    });
  }

  function popupHTML(m) {
    const lat = KM.geo.fmtLat(m.lat), lon = KM.geo.fmtLon(m.lon);
    let h = '<b>' + escapeHTML(m.name) + '</b><br>';
    h += '<span class="coords">' + lat + '  ·  ' + lon + '</span>';
    if (m.confianza) h += '<br><span class="coords">confianza: ' + m.confianza + '</span>';
    if (m.desc) h += '<p style="margin:8px 0 0;font-size:12.5px">' + escapeHTML(m.desc) + '</p>';
    h += '<p style="margin:8px 0 0"><button class="btn btn-sm" data-copy="' +
         m.lat.toFixed(6) + ', ' + m.lon.toFixed(6) + '">Copiar coordenadas</button></p>';
    return h;
  }

  function escapeHTML(s) {
    return String(s).replace(/[&<>"']/g, c =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  KM.markers = {
    layer: null,
    reference: [],
    user: [],
    _byId: new Map(),

    init(map) {
      this.layer = L.layerGroup().addTo(map);
      this.user = KM.store.loadJSON(KM.STORAGE_KEYS.markers, []);
      return fetch('data/landmarks.json')
        .then(r => r.ok ? r.json() : { items: [] })
        .then(d => { this.reference = d.items || []; })
        .catch(() => { this.reference = []; })
        .then(() => this.render());
    },

    all() {
      return this.reference.concat(this.user);
    },

    render() {
      this.layer.clearLayers();
      this._byId.clear();
      this.all().forEach(m => {
        const mk = L.marker([m.lat, m.lon], { icon: icon(m.cat), title: m.name })
          .bindPopup(popupHTML(m))
          .bindTooltip(m.name, { className: 'km-label', direction: 'right', offset: [8, 0] });
        mk.addTo(this.layer);
        this._byId.set(m.id, mk);
      });
      this._renderList();
      if (this.onRender) this.onRender();
    },

    add(m) {
      m.id = m.id || 'u' + Date.now().toString(36);
      m.cat = m.cat || 'usuario';
      this.user.push(m);
      this.save();
      this.render();
      return m;
    },

    remove(id) {
      this.user = this.user.filter(m => m.id !== id);
      this.save();
      this.render();
    },

    save() {
      KM.store.saveJSON(KM.STORAGE_KEYS.markers, this.user);
    },

    focus(id, map) {
      const m = this.all().find(x => x.id === id);
      if (!m) return;
      map.setView([m.lat, m.lon], Math.max(map.getZoom(), 6));
      const mk = this._byId.get(id);
      if (mk) mk.openPopup();
    },

    search(q) {
      const s = q.trim().toLowerCase();
      if (!s) return [];
      return this.all().filter(m => m.name.toLowerCase().includes(s)).slice(0, 8);
    },

    importJSON(text) {
      const d = JSON.parse(text);
      const items = Array.isArray(d) ? d : (d.items || []);
      let n = 0;
      items.forEach(m => {
        if (typeof m.lat !== 'number' || typeof m.lon !== 'number') return;
        this.user.push({
          id: m.id || 'i' + (Date.now() + n).toString(36),
          name: m.name || 'Sin nombre',
          cat: m.cat || 'anomalia',
          lat: m.lat, lon: m.lon,
          desc: m.desc || '', confianza: m.confianza || ''
        });
        n++;
      });
      this.save();
      this.render();
      return n;
    },

    exportJSON() {
      return JSON.stringify({ items: this.user }, null, 2);
    },

    _renderList() {
      const ul = document.getElementById('mk-list');
      if (!ul) return;
      ul.innerHTML = '';
      this.all().forEach(m => {
        const li = document.createElement('li');
        const isUser = this.user.includes(m);
        li.innerHTML =
          '<span class="mk-dot" style="background:' + (COLORS[m.cat] || COLORS.otro) + '"></span>' +
          '<span class="mk-name">' + escapeHTML(m.name) + '</span>' +
          (isUser ? '<button class="mk-del" title="Borrar">&times;</button>' : '');
        li.addEventListener('click', ev => {
          if (ev.target.classList.contains('mk-del')) {
            ev.stopPropagation();
            KM.markers.remove(m.id);
          } else {
            KM.markers.focus(m.id, KM.map);
          }
        });
        ul.appendChild(li);
      });
    }
  };
})();
