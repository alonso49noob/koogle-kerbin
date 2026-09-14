/* Arranque y cableado de la interfaz. */

(function () {
  const $ = id => document.getElementById(id);

  const state = Object.assign({
    baseId: 'grid',
    customUrl: '',
    opacity: 1,
    grid: true,
    landmarks: true,
    hMin: KM.HEIGHT_RANGE.min,
    hMax: KM.HEIGHT_RANGE.max,
    lonOffset: { color: 0, biome: 0, height: 0 },
    presetId: null,
    view3d: false,
    biomeOn: false,
    biomeTouched: false,               // ¿ha elegido el usuario, o sigue el defecto?
    biomeOpacity: KM.BIOME.defaultOpacity,
    center: KM.MAP.initialCenter,
    zoom: KM.MAP.initialZoom,
    bannerDismissed: false
  }, KM.store.loadJSON(KM.STORAGE_KEYS.settings, {}));

  const bitmaps = { color: null, height: null, biome: null };
  const meta = { color: null, height: null, biome: null };

  let map, baseLayer = null, graticule = null, gratLabels = null, orbitLayer = null;
  let biomeLayer = null;
  let calibTarget = null;                    // 'a' | 'b' mientras se calibra la altura
  const calib = { a: null, b: null };        // {lum, alt}

  /* ------------------------------------------------------------------ mapa */

  function initMap() {
    map = L.map('map', {
      crs: KM.CRS,
      center: state.center,
      zoom: state.zoom,
      minZoom: KM.MAP.minZoom,
      maxZoom: KM.MAP.maxZoom,
      zoomControl: false,   // la rueda y el doble clic bastan; los botones tapaban el del menú
      worldCopyJump: true,
      /* Las teselas se pintan en un canvas local, sin red de por medio: aparecen
         ya dibujadas. El fundido de entrada solo añadiría un parpadeo. */
      fadeAnimation: false,
      maxBounds: L.latLngBounds([[-90, -540], [90, 540]]),
      maxBoundsViscosity: 0.6,
      attributionControl: true
    });
    KM.map = map;

    map.attributionControl.setPrefix('');
    map.attributionControl.addAttribution(
      'Visor no oficial · Kerbal Space Program es de Squad / Private Division'
    );

    L.control.scale({ metric: true, imperial: false, maxWidth: 180, position: 'bottomleft' }).addTo(map);

    /* Panel propio para los biomas: por encima de las teselas del mapa base
       (200) pero por debajo de vectores y marcadores (400), para que la traza
       orbital y los pines nunca queden tapados. */
    map.createPane('biome');
    map.getPane('biome').style.zIndex = 250;
    map.getPane('biome').style.pointerEvents = 'none';

    graticule = new KM.GraticuleLayer({ pane: 'overlayPane', filled: false });
    gratLabels = new KM.GraticuleLabels();
    if (state.grid) { graticule.addTo(map); gratLabels.addTo(map); }

    orbitLayer = L.layerGroup().addTo(map);

    let guardarVista = null;
    map.on('moveend zoomend', () => {
      const c = map.getCenter();
      state.center = [c.lat, KM.geo.wrapLon(c.lng)];
      state.zoom = map.getZoom();
      /* Al seguir a una nave el mapa se mueve en cada fotograma: sin esta espera
         se escribiría en localStorage sesenta veces por segundo. */
      clearTimeout(guardarVista);
      guardarVista = setTimeout(saveSettings, 400);
    });

    map.on('mousemove', onHover);
    map.on('dragstart', () => { if (sim.follow) seguir(false); });
    map.on('click', onMapClick);
  }

  /* --------------------------------------------------------------- capa base */

  function buildBase(id) {
    const def = KM.BASES.find(b => b.id === id) || KM.BASES[0];

    if (def.kind === 'placeholder') {
      return new KM.GraticuleLayer({ filled: true });
    }
    if (def.kind === 'image') {
      if (!bitmaps[def.slot]) return null;
      return new KM.ImageLayer(bitmaps[def.slot], {
        // biomas y alturas se leen por color exacto: nada de interpolar
        smooth: def.slot === 'color',
        lonOffset: state.lonOffset[def.slot] || 0,
        minZoom: KM.MAP.minZoom, maxZoom: KM.MAP.maxZoom
      });
    }
    if (def.kind === 'xyz') {
      const url = id === 'custom' ? state.customUrl : def.url;
      if (!url) return null;
      return KM.xyzLayer(url);
    }
    return null;
  }

  function setBase(id, silent) {
    // un id guardado de una versión anterior (p. ej. 'biome', que ya no es base)
    // dejaría el selector en blanco
    if (!KM.BASES.some(b => b.id === id)) id = 'grid';
    let layer = buildBase(id);
    if (!layer) {
      if (!silent) {
        const def = KM.BASES.find(b => b.id === id);
        if (def && def.kind === 'image') {
          flash('No has cargado todavía la imagen de «' + def.label + '».');
        }
      }
      id = 'grid';
      layer = buildBase('grid');
    }
    if (baseLayer) map.removeLayer(baseLayer);
    baseLayer = layer;
    baseLayer.setOpacity(state.opacity);
    baseLayer.addTo(map);
    baseLayer.bringToBack();

    state.baseId = id;
    $('base-select').value = id;
    $('custom-url-wrap').classList.toggle('hidden', id !== 'custom');
    updateBanner();
    saveSettings();
  }

  function updateBanner() {
    const vacio = !bitmaps.color && !bitmaps.biome && !bitmaps.height;
    const show = !state.bannerDismissed && vacio && state.baseId === 'grid';
    $('banner').classList.toggle('hidden', !show);
  }

  /* ------------------------------------------------------------ capa de biomas */

  function syncBiomeLayer() {
    const want = state.biomeOn && !!bitmaps.biome;
    if (biomeLayer) { map.removeLayer(biomeLayer); biomeLayer = null; }
    if (want) {
      biomeLayer = new KM.ImageLayer(bitmaps.biome, {
        pane: 'biome',
        smooth: false,                 // un color promediado no es ningún bioma
        lonOffset: state.lonOffset.biome || 0,
        opacity: state.biomeOpacity,
        minZoom: KM.MAP.minZoom, maxZoom: KM.MAP.maxZoom
      }).addTo(map);
    }
    $('chk-biome').checked = state.biomeOn;
    $('chk-biome').disabled = !bitmaps.biome;
    $('biome-op-wrap').classList.toggle('hidden', !want);
    if (globe) globe.setOptions(globeOptions());
  }

  function scanBiomes() {
    if (!bitmaps.biome) { flash('Carga antes un mapa de biomas.'); return; }
    const res = KM.probe.palette(bitmaps.biome, { minPct: KM.BIOME.minAreaPct });
    if (!res) { flash('No se pudo leer el mapa de biomas.'); return; }

    const named = res.colors.filter(c => KM.biomeNames.get(c.hex)).length;
    let txt = res.colors.length + ' colores · ' + named + ' con nombre\n' +
              'imagen ' + res.native + (res.sampled !== res.native ? ' (leída a ' + res.sampled + ')' : '');
    if (res.dropped.count) {
      txt += '\n' + res.dropped.count + ' colores sueltos ignorados (' +
             res.dropped.pct.toFixed(2) + '% del total): son el dentado de los bordes.';
    }
    $('biome-summary').textContent = txt;

    const ul = $('biome-legend');
    ul.innerHTML = '';
    res.colors.forEach(c => {
      const li = document.createElement('li');

      const sw = document.createElement('span');
      sw.className = 'sw';
      sw.style.background = c.hex;
      sw.title = c.hex;

      const inp = document.createElement('input');
      inp.type = 'text';
      inp.value = KM.biomeNames.get(c.hex) || '';
      inp.placeholder = c.hex;
      inp.addEventListener('change', () => {
        KM.biomeNames.set(c.hex, inp.value.trim());
      });

      const pct = document.createElement('span');
      pct.className = 'pct';
      pct.textContent = c.pct.toFixed(1) + '%';
      pct.title = 'porción de la superficie de Kerbin';

      li.append(sw, inp, pct);
      ul.appendChild(li);
    });
  }

  /* ------------------------------------------------ desfase de longitud ---- */

  function applyOffset(slot, deg) {
    state.lonOffset[slot] = ((deg % 360) + 360) % 360;
    state.offsetTouched = true;         // tu ajuste manda sobre el de maps.json
    saveSettings();
    const inp = document.querySelector('.slot[data-slot=' + slot + '] .slot-off');
    if (inp) inp.value = state.lonOffset[slot];
    if (globe) globe.setOptions(globeOptions());
    if (slot === 'biome') syncBiomeLayer();
    if (state.baseId === slot && baseLayer && baseLayer.setLonOffset) {
      baseLayer.setLonOffset(state.lonOffset[slot]);
    }
  }

  /* Máscara tierra/agua a 1° por celda, en el espacio de la propia imagen. */
  function landMask(bmp, W, H) {
    const c = document.createElement('canvas');
    c.width = W; c.height = H;
    const x = c.getContext('2d', { willReadFrequently: true });
    x.imageSmoothingEnabled = false;
    x.drawImage(bmp, 0, 0, W, H);
    let d;
    try { d = x.getImageData(0, 0, W, H).data; } catch (e) { return null; }
    const m = new Uint8Array(W * H);
    for (let i = 0; i < W * H; i++) {
      const r = d[i * 4], g = d[i * 4 + 1], b = d[i * 4 + 2];
      m[i] = (b > r + 20 && b > g + 10) ? 0 : 1;     // azul dominante = agua
    }
    return m;
  }

  /* Busca el giro en longitud que hace que dos mapas del mismo planeta encajen.
     Compara siluetas de continentes, así que funciona aunque las paletas no
     tengan nada que ver entre sí. */
  function detectOffset(bmpA, bmpB, offB) {
    const W = 360, H = 180;
    const A = landMask(bmpA, W, H), B = landMask(bmpB, W, H);
    if (!A || !B) return null;

    let best = { pct: -1 }, sum = 0;
    for (let s = 0; s < W; s++) {
      let ok = 0, tot = 0;
      for (let y = 20; y < H - 20; y++) {            // los casquetes no distinguen nada
        for (let i = 0; i < W; i++) {
          if (A[y * W + (i + s) % W] === B[y * W + (i + offB) % W]) ok++;
          tot++;
        }
      }
      const pct = ok / tot * 100;
      sum += pct;
      if (pct > best.pct) best = { shift: s, pct };
    }
    return { shift: best.shift, pct: best.pct, media: sum / W };
  }

  function alignColorToBiome() {
    if (!bitmaps.color || !bitmaps.biome) {
      flash('Hacen falta los dos mapas, color y bioma, para poder compararlos.');
      return;
    }
    const r = detectOffset(bitmaps.color, bitmaps.biome, state.lonOffset.biome | 0);
    if (!r) { flash('No se pudieron leer los píxeles de las imágenes.'); return; }

    /* Si el mejor encaje no destaca sobre el promedio, es que no son el mismo
       planeta (o una de las dos no es equirectangular) y no hay nada que girar. */
    if (r.pct < 75 || r.pct - r.media < 15) {
      flash('No encajan a ninguna longitud (mejor coincidencia ' + r.pct.toFixed(0) +
            '%, promedio ' + r.media.toFixed(0) + '%). ¿Seguro que los dos mapas son ' +
            'de Kerbin y equirectangulares 2:1?');
      return;
    }
    applyOffset('color', r.shift);
    flash('Mapa de color girado ' + r.shift + '° en longitud: ahora coincide con el ' +
          'bioma en un ' + r.pct.toFixed(1) + '% (antes del giro, el promedio era ' +
          r.media.toFixed(0) + '%).');
  }

  /* ------------------------------------------------ mapas predeterminados ---- */

  let presets = null;

  async function loadCatalog() {
    try {
      const res = await fetch('data/maps.json', { cache: 'no-cache' });
      if (!res.ok) return null;
      const d = await res.json();
      if (!d || !Array.isArray(d.presets) || !d.presets.length) return null;
      presets = d;
      return d;
    } catch (e) {
      return null;                        // sin catálogo se sigue por convención de nombre
    }
  }

  function renderPresetUI() {
    if (!presets) return;
    const sel = $('preset-select');
    sel.innerHTML = '';
    presets.presets.forEach(p => {
      const o = document.createElement('option');
      o.value = p.id;
      o.textContent = p.nombre || p.id;
      sel.appendChild(o);
    });
    sel.value = state.presetId || presets.predeterminado || presets.presets[0].id;
    $('preset-wrap').classList.remove('hidden');
    describePreset(sel.value);
  }

  function describePreset(id) {
    const p = presets && presets.presets.find(x => x.id === id);
    if (!p) return;
    const lines = [];
    ['color', 'biome', 'height'].forEach(slot => {
      if (!p[slot]) return;
      const off = p[slot].lonOffset;
      lines.push(slot.padEnd(7) + p[slot].file +
                 (off === 'auto' ? '  (giro automático)' : off ? '  (' + off + '°)' : ''));
    });
    if (p.fuente) lines.push('\nfuente: ' + p.fuente);
    $('preset-note').textContent = lines.join('\n');
  }

  /* Carga un preset entero. El bioma va primero a propósito: es la referencia
     contra la que se mide el giro de los mapas marcados como "auto". */
  async function loadPreset(id, silent) {
    const p = presets && presets.presets.find(x => x.id === id);
    if (!p) return false;

    const faltan = [], autos = [];
    for (const slot of ['biome', 'color', 'height']) {
      const spec = p[slot];
      if (!spec || !spec.file) continue;
      try {
        const res = await fetch('data/' + spec.file, { cache: 'no-cache' });
        if (!res.ok) { faltan.push(spec.file); continue; }
        const blob = await res.blob();
        if (!blob.type.startsWith('image/')) { faltan.push(spec.file); continue; }
        const bmp = await KM.decodeImage(blob);
        bitmaps[slot] = bmp;
        meta[slot] = { name: 'data/' + spec.file, w: bmp.width, h: bmp.height, fromDisk: true };
        if (spec.lonOffset === 'auto') autos.push(slot);
        else state.lonOffset[slot] = (((spec.lonOffset | 0) % 360) + 360) % 360;
        /* Si el preset sabe a qué metros corresponden el gris 0 y el 255 —porque
           vienen de los deslizadores Min y Max de SCANsat—, no hay nada que
           calibrar: se aplican y listo. */
        if (slot === 'height' && typeof spec.hMin === 'number' && typeof spec.hMax === 'number') {
          state.hMin = spec.hMin;
          state.hMax = spec.hMax;
          $('h-min').value = spec.hMin;
          $('h-max').value = spec.hMax;
        }
      } catch (e) {
        console.warn('[preset] no se pudo cargar', spec.file, e);
        faltan.push(spec.file);
      }
    }

    // los marcados "auto" se miden ahora que el bioma ya está en memoria
    const medidos = [];
    for (const slot of autos) {
      if (slot === 'biome' || !bitmaps.biome) { state.lonOffset[slot] = 0; continue; }
      const r = detectOffset(bitmaps[slot], bitmaps.biome, state.lonOffset.biome | 0);
      if (r && r.pct >= 75 && r.pct - r.media >= 15) {
        state.lonOffset[slot] = r.shift;
        medidos.push(slot + ' ' + r.shift + '° (' + r.pct.toFixed(1) + '%)');
      } else {
        state.lonOffset[slot] = 0;
        medidos.push(slot + ' sin giro claro, se deja en 0°');
      }
    }

    state.presetId = id;
    if (bitmaps.color) state.baseId = 'color';
    if (bitmaps.biome && !state.biomeTouched) state.biomeOn = true;
    saveSettings();

    renderSlots();
    setBase(state.baseId, true);
    syncBiomeLayer();
    if (bitmaps.biome) scanBiomes();

    if (!silent) {
      const partes = [];
      if (medidos.length) partes.push('Giro medido: ' + medidos.join('; ') + '.');
      if (faltan.length) partes.push('No están en data/: ' + faltan.join(', ') + '.');
      flash(partes.length ? partes.join(' ') : 'Mapa cargado.');
    } else if (faltan.length) {
      console.warn('[preset] faltan ficheros:', faltan.join(', '));
    }
    return !faltan.length;
  }

  /* ------------------------------------------------- naves de una partida ---- */

  const sv = {
    ut: 0, rot: 0, calib: null,
    naves: [],            // solo las que orbitan el cuerpo del visor
    tipos: {},            // tipo -> visible
    sel: null,            // nave con la traza dibujada
    trackPts: null,       // puntos de esa traza
    layer: null, trackLayer: null,
    marcas: [],           // {v, p, marker, oculto} de cada nave visible
    filas: [],            // filas de la lista, para refrescar la altitud
    todas: false,         // dibujar las órbitas de todas las naves
    allLayer: null, lienzo: null,
    sitiosGlobo: [],      // sitios fijos del globo, para no rehacerlos en cada fotograma
    maxR: 1.2             // radio de la órbita más lejana, en radios de Kerbin
  };

  /* El globo tiene un único hueco de traza y lo quieren dos: la órbita que
     dibujas a mano en su panel y la de la nave que pinchas. Manda la última que
     se pidió; cada una borra la otra al dibujarse. Sin esto, tocar un filtro de
     naves se llevaba por delante la órbita manual. */
  function pushTrack() {
    if (!globe) return;
    // la nave ya tiene su anillo: de su traza solo se pinta la huella en el suelo
    if (sv.trackPts) globe.setTrack(sv.trackPts, { space: false });
    else globe.setTrack(lastTrack);
  }

  const COLOR_TIPO = {
    Relay: '#7ee787', Probe: '#4ea3ff', Station: '#ffb454', Base: '#ffb454',
    Ship: '#ff6b6b', Lander: '#ff6b6b', Rover: '#c77dff',
    Debris: '#8a9bb0', SpaceObject: '#6b7f95', EVA: '#ffffff', Flag: '#c77dff'
  };
  const colorDe = t => COLOR_TIPO[t] || '#8a9bb0';

  /* ------------------------------------------------------ reloj de simulación */

  /* Los mismos escalones que la aceleración de tiempo de KSP, en los dos
     sentidos. Un único escalafón con signo permite frenar hacia atrás paso a paso
     hasta pasar a hacia delante, sin saltar de ×100 000 a ×1. */
  const WARPS = [1, 5, 10, 50, 100, 1000, 10000, 100000];
  const ESCALA = WARPS.slice().reverse().map(w => -w).concat(WARPS);

  const sim = { t: 0, warp: 1, running: false, follow: false, last: 0, raf: null, lento: 0, dirty: false };
  KM.sim = sim;          // para depurar desde la consola
  KM.sv = sv;

  const rotBase = () => sv.rot + (parseFloat($('sv-rot').value) || 0);

  function cambiarWarp(paso) {
    let i = ESCALA.indexOf(sim.warp);
    if (i < 0) i = ESCALA.indexOf(1);
    sim.warp = ESCALA[Math.max(0, Math.min(ESCALA.length - 1, i + paso))];
    sim.running = true;             // cambiar la velocidad arranca, como en el juego
    renderReloj();
  }

  function alternarPausa() { sim.running = !sim.running; sim.dirty = true; renderReloj(); }

  function volverAlGuardado() { sim.t = sv.ut; sim.dirty = true; renderReloj(); }

  /* Calendario del juego: días de 6 h y años de 426 días, desde el año 1, día 1. */
  function fechaKerbal(t) {
    const DIA = KM.BODY.solarDay, ANIO = 426 * DIA;
    const y = Math.floor(t / ANIO), d = Math.floor((t - y * ANIO) / DIA);
    const sg = t - y * ANIO - d * DIA;
    const p = n => String(Math.floor(n)).padStart(2, '0');
    return 'Año ' + (y + 1) + ' · día ' + (d + 1) + ' · ' +
           p(sg / 3600) + ':' + p((sg % 3600) / 60) + ':' + p(sg % 60);
  }
  KM.fechaKerbal = fechaKerbal;

  function renderReloj() {
    $('t-play').innerHTML = sim.running ? '&#10074;&#10074;' : '&#9654;';
    $('t-play').title = sim.running ? 'Pausa (espacio)' : 'Continuar (espacio)';
    $('t-warp').textContent = (sim.warp < 0 ? '◀ ×' : '▶ ×') + Math.abs(sim.warp).toLocaleString('es') +
                              (sim.running ? '' : ' · pausa');
    const dt = sim.t - sv.ut;
    $('t-fecha').textContent = fechaKerbal(sim.t) + '  (' + (dt < 0 ? '−' : '+') +
                               KM.geo.fmtTime(Math.abs(dt)) + ' desde el guardado)';
    $('t-follow').classList.toggle('hidden', !sv.sel);
    $('t-follow').classList.toggle('active', sim.follow);
  }

  async function cargarSave(file) {
    let texto;
    try { texto = await file.text(); }
    catch (e) { flash('No se pudo leer el fichero: ' + e.message); return; }

    let d;
    try { d = KM.save.parse(texto); }
    catch (e) { console.error(e); flash('Ese fichero no parece un .sfs de KSP.'); return; }

    if (!d.ut || !d.vessels.length) {
      flash('No encontré ni UT ni naves ahí dentro. ¿Es un persistent.sfs?');
      return;
    }

    /* El visor es de Kerbin: las naves de otros cuerpos se cuentan pero no se
       dibujan, porque sus latitudes y longitudes son de otro sitio. */
    const deAqui = d.vessels.filter(v => v.body === KM.BODY.name && v.orbit);
    const otras = d.vessels.length - deAqui.length;

    sv.ut = d.ut;
    sv.naves = deAqui;
    sv.calib = KM.save.calibrarRotacion(d.vessels.filter(v => v.body === KM.BODY.name), d.ut);
    sv.rot = sv.calib.rot;
    sv.sel = null;
    // hasta dónde debe dejar alejarse la cámara: la órbita más lejana de la partida
    sv.maxR = deAqui.reduce((mx, v) => Math.max(mx, v.orbit.sma * (1 + v.orbit.ecc) / KM.BODY.radius), 1.2);
    Object.assign(sim, { t: d.ut, warp: 1, running: false, follow: false, dirty: true });
    if (globe) globe.setMinDist(1.02);

    sv.tipos = {};
    deAqui.forEach(v => { if (!(v.type in sv.tipos)) sv.tipos[v.type] = v.type !== 'Debris'; });

    $('sv-rot').value = 0;
    renderSaveInfo(file.name, d.vessels.length, otras);
    renderTipos();
    renderNaves();
    $('tbar').classList.remove('hidden');
    renderReloj();
    arrancarBucle();
  }

  function renderSaveInfo(nombre, total, otras) {
    const c = sv.calib;
    const dias = (sv.ut / KM.BODY.solarDay);
    const lineas = [
      nombre,
      'UT ' + sv.ut.toFixed(0) + ' s  (día ' + Math.floor(dias).toLocaleString('es') + ')',
      total + ' naves en la partida · ' + sv.naves.length + ' orbitando ' + KM.BODY.name +
        (otras ? ' · ' + otras + ' en otros cuerpos' : '')
    ];
    if (c && c.n >= 3) {
      /* Se enseña la dispersión mediana y no una desviación típica: la típica
         se infla con la única nave rara que se cuele y da una idea falsa de lo
         poco fiable que es el resultado. */
      lineas.push('Rotación medida con ' + c.n + ' naves: ' + c.rot.toFixed(2) + '°' +
                  '  (dispersión mediana ' + c.mad.toFixed(2) + '°' +
                  (c.descartadas ? ', ' + c.descartadas + ' descartada' + (c.descartadas > 1 ? 's' : '') +
                                   ' por incoherente' + (c.descartadas > 1 ? 's' : '') : '') + ')');
    } else if (c && c.n) {
      lineas.push('Rotación medida con solo ' + c.n + ' nave(s): poco fiable, ' +
                  'ajústala a mano si las trazas no cuadran.');
    } else {
      lineas.push('Ninguna nave con posición y época sincronizadas: no se pudo ' +
                  'medir la rotación. Las longitudes serán arbitrarias hasta que la ajustes.');
    }
    $('sv-info').textContent = lineas.join('\n');
  }

  function renderTipos() {
    const cont = $('sv-tipos');
    cont.innerHTML = '';
    const cuenta = {};
    sv.naves.forEach(v => { cuenta[v.type] = (cuenta[v.type] || 0) + 1; });
    Object.keys(sv.tipos).sort().forEach(t => {
      const lab = document.createElement('label');
      const cb = document.createElement('input');
      cb.type = 'checkbox'; cb.checked = sv.tipos[t];
      cb.addEventListener('change', () => { sv.tipos[t] = cb.checked; renderNaves(); });
      lab.appendChild(cb);
      const sw = document.createElement('span');
      sw.className = 'mk-dot'; sw.style.background = colorDe(t);
      lab.appendChild(sw);
      lab.appendChild(document.createTextNode(' ' + t + ' (' + cuenta[t] + ')'));
      cont.appendChild(lab);
    });
  }

  function navesVisibles() {
    return sv.naves.filter(v => sv.tipos[v.type]);
  }

  function renderNaves() {
    if (!sv.layer) sv.layer = L.layerGroup().addTo(map);
    sv.layer.clearLayers();

    const rot = rotBase();
    sv.marcas = navesVisibles().map(v => {
      const p = KM.save.posicionEn(v.orbit, sim.t, sv.ut, rot);
      const oculto = p.alt < 0;
      const marker = L.circleMarker([p.lat, p.lon], {
        radius: 4, color: '#fff', weight: 1, fillColor: colorDe(v.type),
        opacity: oculto ? 0 : 1, fillOpacity: oculto ? 0 : 1
      }).bindTooltip(v.name, { className: 'km-label', direction: 'right', offset: [8, 0] })
        .on('click', () => seleccionarNave(v))
        .addTo(sv.layer);
      return { v, p, marker, oculto };
    });

    const ul = $('sv-list');
    ul.innerHTML = '';
    sv.filas = [];
    sv.marcas.slice().sort((a, b) => a.v.name.localeCompare(b.v.name)).slice(0, 250).forEach(m => {
      const li = document.createElement('li');
      li.className = 'sv-row' + (sv.sel === m.v ? ' sel' : '');
      const dot = document.createElement('span');
      dot.className = 'mk-dot'; dot.style.background = colorDe(m.v.type);
      const nm = document.createElement('span');
      nm.className = 'mk-name'; nm.textContent = m.v.name;
      const al = document.createElement('span');
      al.className = 'sv-alt'; al.textContent = (m.p.alt / 1000).toFixed(0) + ' km';
      li.append(dot, nm, al);
      li.addEventListener('click', () => seleccionarNave(m.v));
      ul.appendChild(li);
      sv.filas.push({ m, alt: al });
    });

    dibujarTrazaNave();
    dibujarTodas();
    construirAnillos();
    syncGlobe();
  }

  function seleccionarNave(v) {
    sv.sel = (sv.sel === v) ? null : v;
    if (!sv.sel) seguir(false);
    renderNaves();
    if (sv.sel) {
      const p = KM.save.posicionEn(v.orbit, sim.t, sv.ut, rotBase());
      if (is3D && globe) {
        // si la cámara quedara por dentro de la órbita, la nave estaría a su espalda
        const r = (KM.BODY.radius + p.alt) / KM.BODY.radius;
        globe.setCenter(p.lat, p.lon, Math.max(globe.cam.dist, r * 1.15 + 0.1));
      } else {
        map.setView([p.lat, p.lon], Math.max(map.getZoom(), 3), { animate: false });
      }
    }
    renderReloj();
  }

  /* Seguir a la nave: la cámara se pone sobre ella mirando al centro de Kerbin,
     así que la nave queda justo en el centro de la pantalla con el suelo pasando
     por debajo. Arrastrar lo cancela, como en Google Earth. */
  function seguir(on) {
    sim.follow = !!on && !!sv.sel;
    if (globe && !sim.follow) globe.setMinDist(1.02);
    sim.dirty = true;
    if (sv.naves.length) renderReloj();
  }

  /* En 2D, «todas las órbitas» son trazas terrestres desde el instante simulado,
     pintadas en un canvas: con decenas de líneas redibujándose varias veces por
     segundo, el SVG de Leaflet se atraganta. */
  function dibujarTodas() {
    if (!sv.allLayer) {
      sv.lienzo = L.canvas({ padding: 0.3 });
      sv.allLayer = L.layerGroup().addTo(map);
    }
    sv.allLayer.clearLayers();
    if (!sv.todas || is3D) return;          // en 3D ya están los anillos
    const rot = rotBase();
    const n = Math.min(3, parseInt($('sv-orbits').value, 10) || 1);
    for (const m of sv.marcas) {
      if (m.v === sv.sel || m.oculto) continue;
      const t = KM.save.trazaDesde(m.v.orbit, sim.t, sv.ut, rot, n, 96);
      KM.geo.splitAntimeridian(t.puntos.map(p => [p.lat, p.lon])).forEach(seg => {
        L.polyline(seg, { color: colorDe(m.v.type), weight: 1, opacity: 0.45,
                          interactive: false, renderer: sv.lienzo }).addTo(sv.allLayer);
      });
    }
  }

  /* En 3D las órbitas son anillos cerrados: todas si se ha pedido, y siempre la de
     la nave seleccionada, más opaca y dibujada la última para que quede encima. */
  function construirAnillos() {
    if (!globe || !globe.ready) return;
    const rot = rotBase();
    const lista = [];
    for (const m of sv.marcas) {
      const sel = m.v === sv.sel;
      if (!sv.todas && !sel) continue;
      lista.push({ points: KM.save.anillo(m.v.orbit, rot, 180), color: colorDe(m.v.type),
                   alpha: sel ? 0.95 : 0.4, sel });
    }
    lista.sort((a, b) => a.sel - b.sel);
    globe.setOrbits(lista);
    globe.setOrbitShift(360 * ((sim.t - sv.ut) / KM.BODY.siderealDay));
    globe.setScene(sv.maxR);
  }

  function arrancarBucle() {
    if (sim.raf) return;
    /* Si la pestaña pasa a segundo plano el navegador deja de llamar al bucle, y
       al volver no debe saltar horas de golpe: ese hueco se descarta al recuperar
       la visibilidad. El tope no puede ser por fotograma: con una escena pesada a
       3 fotogramas por segundo, cortar cada paso a 0,25 s hacía que un segundo
       real simulase 0,75 s y el ×1 dejaba de ser tiempo real. Queda un tope de 2 s
       solo como red, por si el depurador o una pausa larga paran la página. */
    document.addEventListener('visibilitychange', () => { if (!document.hidden) sim.last = 0; });
    const tick = now => {
      sim.raf = requestAnimationFrame(tick);
      const dt = sim.last ? Math.min(2, (now - sim.last) / 1000) : 0;
      sim.last = now;
      if (!sv.naves.length) return;
      if (sim.running) {
        sim.t += dt * sim.warp;
        if (sim.t < 0) { sim.t = 0; sim.running = false; }
      }
      if (sim.running || sim.dirty) fotograma(now);
    };
    sim.raf = requestAnimationFrame(tick);
  }

  /* Un fotograma de simulación. Lo barato va siempre: mover marcadores, girar
     anillos, seguir a la nave. Lo caro (rehacer trazas, textos de la lista) va
     unas pocas veces por segundo, o en cuanto algo lo pide. */
  function fotograma(now) {
    const rot = rotBase(), R = KM.BODY.radius;
    const navesGlobo = [];
    let posSel = null;

    for (const m of sv.marcas) {
      const p = KM.save.posicionEn(m.v.orbit, sim.t, sv.ut, rot);
      m.p = p;
      // Kepler no frena: una órbita que corta el suelo lo atraviesa, así que se oculta
      const oculto = p.alt < 0;
      if (!is3D) {
        m.marker.setLatLng([p.lat, p.lon]);
        if (m.oculto !== oculto) m.marker.setStyle({ opacity: oculto ? 0 : 1, fillOpacity: oculto ? 0 : 1 });
      }
      m.oculto = oculto;
      navesGlobo.push({ lat: p.lat, lon: p.lon, r: (R + p.alt) / R, name: m.v.name,
                        color: colorDe(m.v.type), kind: 'nave', hidden: oculto });
      if (m.v === sv.sel) posSel = p;
    }

    if (globe && globe.ready) {
      globe.updateMarkers(sv.sitiosGlobo.concat(navesGlobo));
      globe.setOrbitShift(360 * ((sim.t - sv.ut) / KM.BODY.siderealDay));
    }

    if (sim.follow && posSel) {
      if (is3D && globe) {
        globe.setMinDist(((R + posSel.alt) / R) * 1.15 + 0.1);
        globe.setCenter(posSel.lat, posSel.lon);
      } else {
        map.setView([posSel.lat, posSel.lon], map.getZoom(), { animate: false });
      }
    }

    if (sim.dirty || now - sim.lento >= 150) {
      sim.lento = now;
      for (const f of sv.filas) if (f.m.p) f.alt.textContent = (f.m.p.alt / 1000).toFixed(0) + ' km';
      dibujarTrazaNave();
      dibujarTodas();
      renderReloj();
    }
    sim.dirty = false;
  }

  function dibujarTrazaNave() {
    if (!sv.trackLayer) sv.trackLayer = L.layerGroup().addTo(map);
    sv.trackLayer.clearLayers();
    sv.trackPts = null;

    if (!sv.sel) { pushTrack(); return; }

    const rot = rotBase();
    const n = parseInt($('sv-orbits').value, 10) || 2;
    const t = KM.save.trazaDesde(sv.sel.orbit, sim.t, sv.ut, rot, n);
    const col = colorDe(sv.sel.type);

    KM.geo.splitAntimeridian(t.puntos.map(p => [p.lat, p.lon])).forEach(seg => {
      L.polyline(seg, { color: col, weight: 2, opacity: .9, interactive: false }).addTo(sv.trackLayer);
    });

    sv.trackPts = t.puntos;
    // pinchar una nave sustituye a la órbita manual
    lastTrack = null;
    orbitLayer.clearLayers();
    pushTrack();

    const e = sv.sel.orbit;
    const pe = e.sma * (1 - e.ecc) - KM.BODY.radius, ap = e.sma * (1 + e.ecc) - KM.BODY.radius;
    $('orb-out').innerHTML = [
      sv.sel.name,
      'Pe / Ap      <b>' + (pe / 1000).toFixed(0) + ' / ' + (ap / 1000).toFixed(0) + ' km</b>',
      'Inclinación  <b>' + e.inc.toFixed(2) + '°</b>',
      'Excentricid. <b>' + e.ecc.toFixed(4) + '</b>',
      'Periodo      <b>' + KM.geo.fmtTime(t.periodo) + '</b>'
    ].join('\n');
  }

  /* -------------------------------------------------------------- vista 3D ---- */

  let globe = null, is3D = false, lastTrack = null;

  const MARKER_COLORS = {
    ksc: '#4ea3ff', base: '#7ee787', aeropuerto: '#ffb454',
    anomalia: '#c77dff', usuario: '#ff6b6b', otro: '#8a9bb0'
  };

  function globeOptions() {
    return {
      biomeAmt: state.biomeOn && bitmaps.biome ? state.biomeOpacity : 0,
      colorOff: state.lonOffset.color | 0,
      biomeOff: state.lonOffset.biome | 0,
      heightOff: state.lonOffset.height | 0,
      hMin: state.hMin, hMax: state.hMax
    };
  }

  /* Empuja al globo lo que haya cargado ahora mismo. Se llama tras cualquier
     cambio de imagen o de ajuste, para que las dos vistas no se separen. */
  function syncGlobe() {
    if (!globe || !globe.ready) return;
    ['color', 'biome', 'height'].forEach(slot => {
      if (globe._last !== undefined && globe._last[slot] === bitmaps[slot]) return;
      globe.setTexture(slot, bitmaps[slot]);
    });
    globe._last = { color: bitmaps.color, biome: bitmaps.biome, height: bitmaps.height };
    globe.setOptions(globeOptions());
    sv.sitiosGlobo = KM.markers.all().map(m => ({
      lat: m.lat, lon: m.lon, name: m.name, color: MARKER_COLORS[m.cat] || MARKER_COLORS.otro
    }));
    const rot = rotBase();
    const naves = sv.marcas.map(({ v }) => {
      const p = KM.save.posicionEn(v.orbit, sim.t, sv.ut, rot);
      return { lat: p.lat, lon: p.lon, r: (KM.BODY.radius + p.alt) / KM.BODY.radius,
               name: v.name, color: colorDe(v.type), kind: 'nave', hidden: p.alt < 0 };
    });
    globe.setMarkers(sv.sitiosGlobo.concat(naves));
    $('g-relief-wrap').classList.toggle('hidden', !bitmaps.height);
    $('g-relief-hint').classList.toggle('hidden', !!bitmaps.height);
  }

  /* El zoom del mapa plano y la distancia de cámara miden cosas distintas; esto
     las empareja para que al cambiar de vista se siga mirando lo mismo. */
  function zoomToDist(z) { return Math.max(1.05, Math.min(12, 1.05 + 7 / Math.pow(1.9, z))); }
  function distToZoom(d) {
    const z = Math.log(7 / Math.max(0.001, d - 1.05)) / Math.log(1.9);
    return Math.max(KM.MAP.minZoom, Math.min(KM.MAP.maxZoom, Math.round(z)));
  }

  function set3D(on) {
    if (on && !globe) {
      globe = new KM.Globe($('globe'), $('globe-pins'));
      if (!globe.init()) {
        flash(globe.error || 'No se pudo arrancar la vista 3D.');
        globe = null;
        return;
      }
      globe.onHover = p => {
        if (!p) { $('hud-lat').textContent = $('hud-lon').textContent = '—'; return; }
        onHover({ latlng: L.latLng(p.lat, p.lon) });
      };
      globe.onPick = p => { if (p) onMapClick({ latlng: L.latLng(p.lat, p.lon) }); };
      KM.globe = globe;
      globe.onUserDrag = () => { if (sim.follow) seguir(false); };
    }
    if (on && !globe) return;

    is3D = on;
    $('globe').classList.toggle('hidden', !on);
    $('globe-pins').classList.toggle('hidden', !on);
    $('map').classList.toggle('hidden', on);
    $('globe-panel').classList.toggle('hidden', !on);
    $('view-toggle').textContent = on ? 'Ver en 2D' : 'Ver en 3D';
    $('view-toggle').classList.toggle('active', on);

    if (on) {
      const c = map.getCenter();
      globe.setCenter(c.lat, KM.geo.wrapLon(c.lng), zoomToDist(map.getZoom()));
      syncGlobe();
      construirAnillos();
      pushTrack();
      globe.start();
    } else {
      const c = globe.center();
      globe.stop();
      map.setView([c.lat, c.lon], distToZoom(globe.cam.dist), { animate: false });
      map.invalidateSize();
      sim.dirty = true;          // las trazas 2D no se rehacen mientras se está en 3D
    }
    state.view3d = on;
    saveSettings();
  }

  /* --------------------------------------------------- calibración de alturas */

  function applyCalibration() {
    const { a, b } = calib;
    const lines = [];
    if (a) lines.push('A  gris ' + (a.lum * 255).toFixed(0).padStart(3) + '  →  ' + a.alt + ' m');
    if (b) lines.push('B  gris ' + (b.lum * 255).toFixed(0).padStart(3) + '  →  ' + b.alt + ' m');

    if (a && b) {
      if (Math.abs(a.lum - b.lum) < 1 / 255) {
        lines.push('Los dos puntos tienen el mismo gris: elige uno más alto o más bajo.');
      } else {
        const span = (b.alt - a.alt) / (b.lum - a.lum);
        const min = a.alt - a.lum * span;
        const max = min + span;
        state.hMin = Math.round(min);
        state.hMax = Math.round(max);
        $('h-min').value = state.hMin;
        $('h-max').value = state.hMax;
        saveSettings();
        lines.push('→ gris 0 = ' + state.hMin + ' m, gris 255 = ' + state.hMax + ' m');
      }
    }
    $('cal-out').textContent = lines.join('\n');
  }

  /* --------------------------------------------------------- ranuras de imagen */

  async function loadSlot(slot, file) {
    try {
      const bmp = await KM.decodeImage(file);
      bitmaps[slot] = bmp;
      meta[slot] = { name: file.name, w: bmp.width, h: bmp.height, size: file.size };

      /* El desfase pertenece a la imagen anterior, no a esta. Arrastrarlo al mapa
         nuevo lo pinta girado sin motivo aparente, que es de lo más desconcertante:
         se reinicia, y si hay un bioma con el que comparar se mide el de verdad. */
      const previo = state.lonOffset[slot] | 0;
      state.lonOffset[slot] = 0;
      let medido = null;
      if (slot === 'color' && bitmaps.biome) {
        const r = detectOffset(bmp, bitmaps.biome, state.lonOffset.biome | 0);
        if (r && r.pct >= 75 && r.pct - r.media >= 15) {
          state.lonOffset[slot] = r.shift;
          medido = r;
        }
      }
      saveSettings();
      renderSlots();

      /* Que no se pueda guardar no impide usar el mapa en esta sesión. */
      try {
        await KM.store.putImage(slot, file, meta[slot]);
      } catch (e) {
        console.warn('[slots] no se pudo guardar', slot, e);
        flash('El mapa está cargado, pero no se pudo guardar en el navegador: ' +
              'tendrás que volver a soltarlo la próxima vez. Si lo dejas en data/ ' +
              'se carga solo y te ahorras el problema.');
      }

      if (slot === 'height') {
        const sat = KM.probe.saturation(bmp);
        if (sat !== null && sat > 0.15) {
          flash('Ojo: «' + file.name + '» tiene mucho color (saturación ' +
                (sat * 100).toFixed(0) + '%). Parece un mapa de elevación con paleta, no un ' +
                'gris. La sonda traduce luminancia a metros, y en esas paletas el amarillo de ' +
                'media ladera brilla más que el rojo de la cumbre: las cimas saldrían hundidas. ' +
                'Reexpórtalo en escala de grises.');
        }
      }

      const ratio = bmp.width / bmp.height;
      if (Math.abs(ratio - 2) > 0.02) {
        flash('Ojo: «' + file.name + '» es ' + bmp.width + '×' + bmp.height +
              ' (proporción ' + ratio.toFixed(2) + ':1). Una equirectangular debería ser 2:1, ' +
              'o saldrá deformada.');
      }

      if (slot === 'biome') {
        state.biomeOn = true;             // si acabas de cargarlo, querrás verlo
        state.biomeTouched = true;
        syncBiomeLayer();
        scanBiomes();
        saveSettings();
      } else if (slot === 'color' && (state.baseId === 'grid' || state.baseId === 'color')) {
        setBase('color');
      } else if (state.baseId === slot) {
        setBase(slot);
      }
      updateBanner();

      if (medido) {
        flash('Giro medido contra el mapa de biomas: ' + medido.shift + '° (' +
              medido.pct.toFixed(1) + '% de coincidencia).');
      } else if (previo) {
        flash('Desfase de longitud reiniciado a 0°: el de ' + previo + '° era del mapa ' +
              'anterior. Si este no cae donde debe, usa «Alinear color con el bioma» ' +
              'o ajusta los grados a mano.');
      }
    } catch (e) {
      console.error(e);
      flash('No se pudo leer esa imagen: ' + (e.message || e));
    }
  }

  async function clearSlot(slot) {
    const wasDisk = meta[slot] && meta[slot].fromDisk;
    bitmaps[slot] = null;
    meta[slot] = null;
    try { await KM.store.delImage(slot); }
    catch (e) { console.warn('[slots] no se pudo borrar', slot, e); }
    renderSlots();
    if (slot === 'biome') {
      syncBiomeLayer();
      $('biome-legend').innerHTML = '';
      $('biome-summary').textContent = '';
    }
    if (state.baseId === slot) setBase('grid');
    updateBanner();
    if (wasDisk) flash('Quitado de la vista. El fichero sigue en data/: para que no vuelva a ' +
                       'cargarse al recargar la página, muévelo o renómbralo.');
  }

  async function restoreSlots() {
    for (const slot of ['color', 'height', 'biome']) {
      try {
        const rec = await KM.store.getImage(slot);
        if (!rec || !rec.blob) continue;
        bitmaps[slot] = await KM.decodeImage(rec.blob);
        meta[slot] = rec.meta || { name: '(guardado)', w: bitmaps[slot].width, h: bitmaps[slot].height };
      } catch (e) {
        console.warn('[slots] no se pudo restaurar', slot, e);
      }
    }
    renderSlots();
  }

  /* Si dejas los PNG directamente en data/ con uno de estos nombres, se cargan
     solos al abrir la página: no hace falta arrastrarlos cada vez. */
  const DISK_CANDIDATES = {
    color:  ['Kerbin_Color.png', 'kerbin_color.png', 'color.png'],
    height: ['Kerbin_Height.png', 'kerbin_height.png', 'height.png'],
    biome:  ['Kerbin_Biome.png', 'kerbin_biome.png', 'biome.png']
  };

  async function discoverDiskMaps() {
    for (const slot of Object.keys(DISK_CANDIDATES)) {
      if (bitmaps[slot]) continue;                 // lo ya cargado manda
      for (const name of DISK_CANDIDATES[slot]) {
        try {
          const res = await fetch('data/' + name, { cache: 'no-cache' });
          if (!res.ok) continue;
          const blob = await res.blob();
          if (!blob.type.startsWith('image/')) continue;
          const bmp = await KM.decodeImage(blob);
          bitmaps[slot] = bmp;
          meta[slot] = { name: 'data/' + name, w: bmp.width, h: bmp.height, fromDisk: true };
          break;
        } catch (e) { /* no está: se prueba el siguiente nombre */ }
      }
    }
    renderSlots();
  }

  function renderSlots() {
    document.querySelectorAll('.slot').forEach(el => {
      const slot = el.dataset.slot;
      const m = meta[slot];
      el.classList.toggle('filled', !!m);
      el.querySelector('[data-state]').textContent = m ? (m.w + '×' + m.h) : 'vacío';
      el.querySelector('[data-state]').title = m ? m.name : '';
      const off = el.querySelector('.slot-off');
      if (off && document.activeElement !== off) off.value = state.lonOffset[slot] | 0;
    });
    $('hud-alt-row').classList.toggle('hidden', !bitmaps.height);
    $('hud-biome-row').classList.toggle('hidden', !bitmaps.biome);
    syncGlobe();
  }

  /* Adivina la ranura por el nombre del fichero; los exportadores de KSP suelen
     llamarlos Kerbin_Color / Kerbin_Height / Kerbin_Biome. */
  function guessSlot(name) {
    const n = name.toLowerCase();
    if (/height|altura|elev|terrain|_h\b/.test(n)) return 'height';
    if (/biome|bioma/.test(n)) return 'biome';
    return 'color';
  }

  /* -------------------------------------------------------------------- HUD */

  let hoverLatLng = null, hoverQueued = false;

  function onHover(e) {
    hoverLatLng = e.latlng;
    if (hoverQueued) return;
    hoverQueued = true;
    requestAnimationFrame(() => {
      hoverQueued = false;
      if (!hoverLatLng) return;
      const lat = hoverLatLng.lat, lon = KM.geo.wrapLon(hoverLatLng.lng);
      $('hud-lat').textContent = KM.geo.fmtLat(lat);
      $('hud-lon').textContent = KM.geo.fmtLon(lon);

      if (bitmaps.height) {
        const h = KM.probe.height(bitmaps.height, lat, lon,
                                  { min: state.hMin, max: state.hMax }, state.lonOffset.height);
        $('hud-alt').textContent = h === null ? '—' : KM.geo.fmtAlt(h);
      }
      if (bitmaps.biome) {
        const b = KM.probe.biome(bitmaps.biome, lat, lon, state.lonOffset.biome);
        $('hud-biome').textContent = b ? (KM.biomeNames.get(b.hex) || b.hex) : '—';
      }
    });
  }

  function onMapClick(e) {
    if (KM.tools.mode) return;                 // una herramienta activa manda
    const lat = e.latlng.lat, lon = KM.geo.wrapLon(e.latlng.lng);

    if (calibTarget) {
      const c = KM.probe.sample(bitmaps.height, lat, lon, state.lonOffset.height);
      const which = calibTarget;
      calibTarget = null;
      map.getContainer().style.cursor = '';
      $('cal-' + which).classList.remove('active');
      if (!c) { flash('Ahí no hay píxel que leer.'); return; }
      const lum = (0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b) / 255;
      const txt = prompt('Altitud real de ese punto, en metros (gris ' +
                         (lum * 255).toFixed(0) + '):', '0');
      if (txt === null) return;
      const alt = parseFloat(txt);
      if (!isFinite(alt)) { flash('Eso no es un número.'); return; }
      calib[which] = { lum, alt };
      applyCalibration();
      return;
    }

    let html = '<span class="coords">' + KM.geo.fmtLat(lat) + '  ·  ' + KM.geo.fmtLon(lon) + '</span>';
    html += '<br><span class="coords">' + lat.toFixed(6) + ', ' + lon.toFixed(6) + '</span>';

    if (bitmaps.height) {
      const h = KM.probe.height(bitmaps.height, lat, lon,
                                { min: state.hMin, max: state.hMax }, state.lonOffset.height);
      if (h !== null) html += '<br><span class="coords">altura ' + KM.geo.fmtAlt(h) + '</span>';
    }
    if (bitmaps.biome) {
      const b = KM.probe.biome(bitmaps.biome, lat, lon, state.lonOffset.biome);
      if (b) {
        const nm = KM.biomeNames.get(b.hex);
        html += '<br><span class="coords">bioma ' +
                '<span style="display:inline-block;width:9px;height:9px;background:' + b.hex +
                ';border:1px solid #666;vertical-align:-1px"></span> ' +
                (nm || b.hex) + '</span>' +
                '<br><button class="btn btn-sm" data-name-biome="' + b.hex + '">' +
                (nm ? 'Renombrar bioma' : 'Poner nombre al bioma') + '</button>';
      }
    }

    html += '<p style="margin:8px 0 0;display:flex;gap:6px">' +
            '<button class="btn btn-sm" data-copy="' + lat.toFixed(6) + ', ' + lon.toFixed(6) + '">Copiar</button>' +
            '<button class="btn btn-sm" data-addmarker="' + lat.toFixed(6) + ',' + lon.toFixed(6) + '">Marcador aquí</button>' +
            '</p>';

    L.popup({ closeButton: true }).setLatLng(e.latlng).setContent(html).openOn(map);
  }

  /* ------------------------------------------------------------------ buscar */

  function doSearch(q) {
    const box = $('search-results');
    const coord = parseCoords(q);
    let items = [];

    if (coord) {
      items.push({ kind: 'coord', name: 'Ir a ' + KM.geo.fmtLat(coord[0]) + ' · ' + KM.geo.fmtLon(coord[1]),
                   sub: coord[0].toFixed(5) + ', ' + coord[1].toFixed(5), lat: coord[0], lon: coord[1] });
    }
    KM.markers.search(q).forEach(m => items.push({
      kind: 'marker', id: m.id, name: m.name,
      sub: m.lat.toFixed(4) + ', ' + m.lon.toFixed(4)
    }));

    if (!items.length) { box.classList.add('hidden'); return; }

    box.innerHTML = '';
    items.forEach(it => {
      const d = document.createElement('div');
      d.innerHTML = escapeHTML(it.name) + '<small>' + escapeHTML(it.sub) + '</small>';
      d.addEventListener('click', () => {
        if (it.kind === 'coord') map.setView([it.lat, it.lon], Math.max(map.getZoom(), 6));
        else KM.markers.focus(it.id, map);
        box.classList.add('hidden');
        $('search').blur();
      });
      box.appendChild(d);
    });
    box.classList.remove('hidden');
  }

  /* Acepta «-0.0972, -74.5577», «-0.0972 -74.5577» y «0.09 S 74.55 W». */
  function parseCoords(s) {
    const t = s.trim();
    let m = t.match(/^(-?\d+(?:\.\d+)?)\s*[,;\s]\s*(-?\d+(?:\.\d+)?)$/);
    if (m) {
      const lat = parseFloat(m[1]), lon = parseFloat(m[2]);
      if (Math.abs(lat) <= 90 && Math.abs(lon) <= 360) return [lat, KM.geo.wrapLon(lon)];
    }
    m = t.match(/^(\d+(?:\.\d+)?)\s*°?\s*([NSns])\s*[,;\s]\s*(\d+(?:\.\d+)?)\s*°?\s*([EWew])$/);
    if (m) {
      const lat = parseFloat(m[1]) * (/[Ss]/.test(m[2]) ? -1 : 1);
      const lon = parseFloat(m[3]) * (/[Ww]/.test(m[4]) ? -1 : 1);
      if (Math.abs(lat) <= 90) return [lat, KM.geo.wrapLon(lon)];
    }
    return null;
  }

  /* ------------------------------------------------------------------ órbita */

  function drawOrbit() {
    const o = KM.orbit.compute({
      pe: (parseFloat($('orb-pe').value) || 0) * 1000,
      ap: (parseFloat($('orb-ap').value) || 0) * 1000,
      inc: parseFloat($('orb-inc').value) || 0,
      lan: parseFloat($('orb-lan').value) || 0,
      argp: parseFloat($('orb-argp').value) || 0,
      orbits: parseInt($('orb-n').value, 10) || 1
    });

    orbitLayer.clearLayers();
    lastTrack = o.points;
    // dibujar una órbita a mano sustituye a la traza de la nave pinchada
    if (sv.sel) {
      sv.sel = null; sv.trackPts = null;
      if (sv.trackLayer) sv.trackLayer.clearLayers();
      document.querySelectorAll('#sv-list li.sel').forEach(li => li.classList.remove('sel'));
    }
    pushTrack();
    o.segments.forEach(seg => {
      L.polyline(seg, { color: '#c77dff', weight: 2, opacity: .9, interactive: false }).addTo(orbitLayer);
    });

    // periapsis (t = 0) y apoapsis (media órbita después)
    const pePt = o.points[0];
    L.circleMarker([pePt.lat, pePt.lon], {
      radius: 4, color: '#fff', weight: 1.5, fillColor: '#c77dff', fillOpacity: 1
    }).bindTooltip('Pe  ' + KM.geo.fmtDist(o.pe), { className: 'km-label', permanent: true, direction: 'right', offset: [8, 0] })
      .addTo(orbitLayer);

    const rows = [];
    const row = (label, value, tail) =>
      rows.push(label.padEnd(14, ' ') + '<b>' + value + '</b>' + (tail || ''));

    row('Periodo', KM.geo.fmtTime(o.T), '  (' + o.T.toFixed(0) + ' s)');
    row('Semieje a', (o.a / 1000).toFixed(1) + ' km');
    row('Excentricidad', o.e.toFixed(4));
    row('v en Pe / Ap', o.vPe.toFixed(0) + ' / ' + o.vAp.toFixed(0) + ' m/s');
    row('Deriva/vuelta', o.drift.toFixed(2) + '°', ' de longitud');
    row('Lat. máxima', '±' + o.maxLat.toFixed(1) + '°');
    row('Horizonte Pe', KM.geo.fmtDist(o.footprintPe));
    if (o.synchronous) rows.push('<b>Órbita síncrona</b>: la traza se repite sobre sí misma.');
    if (o.suborbital) rows.push('<b>Aviso</b>: el periapsis está bajo el nivel del mar → impacto.');
    else if (o.inAtmosphere) rows.push('<b>Aviso</b>: el periapsis entra en atmósfera (< 70 km) → frenará.');

    $('orb-out').innerHTML = rows.join('\n');
  }

  /* -------------------------------------------------------------- info cuerpo */

  function renderBodyInfo() {
    const B = KM.BODY;
    const g0 = B.mu / (B.radius * B.radius);
    const circ = 2 * Math.PI * B.radius;
    const vRot = circ / B.siderealDay;
    const vEsc = Math.sqrt(2 * B.mu / B.radius);
    const vLow = Math.sqrt(B.mu / (B.radius + 75000));
    const rows = [
      'Radio            <b>' + (B.radius / 1000).toFixed(0) + ' km</b>',
      'Circunf. ecuador <b>' + (circ / 1000).toFixed(1) + ' km</b>',
      'g en superficie  <b>' + g0.toFixed(3) + ' m/s²</b>',
      'Día sidéreo      <b>' + KM.geo.fmtTime(B.siderealDay) + '</b>',
      'Día solar        <b>6 h exactas</b>',
      'v de rotación    <b>' + vRot.toFixed(1) + ' m/s</b> en el ecuador',
      'Atmósfera hasta  <b>' + (B.atmosphere / 1000).toFixed(0) + ' km</b>',
      'Órbita síncrona  <b>' + (B.synchronousAlt / 1000).toFixed(0) + ' km</b> de altitud',
      'v órbita 75 km   <b>' + vLow.toFixed(0) + ' m/s</b>',
      'v de escape      <b>' + vEsc.toFixed(0) + ' m/s</b>',
      'SOI              <b>' + (B.soi / 1000).toFixed(0) + ' km</b>'
    ];
    $('body-info').innerHTML = rows.join('\n');
  }

  /* ------------------------------------------------------------------ varios */

  let flashTimer = null;
  function flash(msg) {
    const b = $('banner');
    b.querySelector('span').innerHTML = escapeHTML(msg);
    b.classList.remove('hidden');
    clearTimeout(flashTimer);
    flashTimer = setTimeout(() => { b.classList.add('hidden'); updateBanner(); }, 7000);
  }

  function escapeHTML(s) {
    return String(s).replace(/[&<>"']/g, c =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  function saveSettings() {
    KM.store.saveJSON(KM.STORAGE_KEYS.settings, state);
  }

  function download(name, text, type) {
    const blob = new Blob([text], { type: type || 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 2000);
  }

  /* ------------------------------------------------------------------- eventos */

  function wireUI() {
    // selector de base
    const sel = $('base-select');
    KM.BASES.forEach(b => {
      const o = document.createElement('option');
      o.value = b.id; o.textContent = b.label;
      sel.appendChild(o);
    });
    sel.value = state.baseId;
    sel.addEventListener('change', () => setBase(sel.value));

    $('custom-url').value = state.customUrl;
    $('custom-url-apply').addEventListener('click', () => {
      state.customUrl = $('custom-url').value.trim();
      saveSettings();
      setBase('custom');
    });

    // retícula y marcadores
    $('chk-grid').checked = state.grid;
    $('chk-grid').addEventListener('change', e => {
      state.grid = e.target.checked;
      if (state.grid) { graticule.addTo(map); gratLabels.addTo(map); }
      else { map.removeLayer(graticule); map.removeLayer(gratLabels); }
      saveSettings();
    });

    $('chk-landmarks').checked = state.landmarks;
    $('chk-landmarks').addEventListener('change', e => {
      state.landmarks = e.target.checked;
      if (state.landmarks) KM.markers.layer.addTo(map); else map.removeLayer(KM.markers.layer);
      saveSettings();
    });

    // opacidad
    const op = $('op-range');
    op.value = Math.round(state.opacity * 100);
    $('op-val').textContent = op.value + '%';
    op.addEventListener('input', () => {
      state.opacity = op.value / 100;
      $('op-val').textContent = op.value + '%';
      if (baseLayer) baseLayer.setOpacity(state.opacity);
      saveSettings();
    });

    // biomas superpuestos
    $('chk-biome').addEventListener('change', e => {
      state.biomeOn = e.target.checked;
      state.biomeTouched = true;
      syncBiomeLayer();
      saveSettings();
    });
    const bop = $('biome-op-range');
    bop.value = Math.round(state.biomeOpacity * 100);
    $('biome-op-val').textContent = bop.value + '%';
    bop.addEventListener('input', () => {
      state.biomeOpacity = bop.value / 100;
      $('biome-op-val').textContent = bop.value + '%';
      if (biomeLayer) biomeLayer.setOpacity(state.biomeOpacity);
      if (globe) globe.setOptions(globeOptions());
      saveSettings();
    });

    $('view-toggle').addEventListener('click', () => set3D(!is3D));

    const svDrop = $('sv-drop');
    svDrop.addEventListener('click', () => $('sv-file').click());
    $('sv-file').addEventListener('change', e => {
      if (e.target.files[0]) cargarSave(e.target.files[0]);
      e.target.value = '';
    });
    ['dragenter', 'dragover'].forEach(ev => svDrop.addEventListener(ev, e => {
      e.preventDefault(); e.stopPropagation(); svDrop.classList.add('over');
    }));
    ['dragleave', 'drop'].forEach(ev => svDrop.addEventListener(ev, e => {
      e.preventDefault(); e.stopPropagation(); svDrop.classList.remove('over');
    }));
    svDrop.addEventListener('drop', e => {
      const f = [...e.dataTransfer.files][0];
      if (f) cargarSave(f);
    });
    $('sv-orbits').addEventListener('input', e => {
      $('sv-orb-val').textContent = e.target.value;
      dibujarTrazaNave();
      dibujarTodas();
    });
    $('sv-rot').addEventListener('change', () => { if (sv.naves.length) renderNaves(); });

    $('sv-all').addEventListener('change', e => {
      sv.todas = e.target.checked;
      dibujarTodas();
      construirAnillos();
    });
    $('t-now').addEventListener('click', volverAlGuardado);
    $('t-back').addEventListener('click', () => cambiarWarp(-1));
    $('t-fwd').addEventListener('click', () => cambiarWarp(+1));
    $('t-play').addEventListener('click', alternarPausa);
    $('t-follow').addEventListener('click', () => seguir(!sim.follow));

    /* Atajos como en KSP: «.» acelera, «,» frena o va hacia atrás, espacio pausa,
       F sigue a la nave. No se interceptan mientras se escribe o con un botón
       enfocado, que ya gestiona el espacio por su cuenta. */
    document.addEventListener('keydown', e => {
      if (!sv.naves.length || e.ctrlKey || e.metaKey || e.altKey) return;
      const tag = (e.target && e.target.tagName) || '';
      if (/^(INPUT|TEXTAREA|SELECT|BUTTON)$/.test(tag) || (e.target && e.target.isContentEditable)) return;
      /* El espacio se reconoce también por e.code: según el navegador, la
         distribución de teclado o lo que genere el evento, puede llegar como key
         ' ', 'Spacebar' o solo con code 'Space'. Para «.», «,» y F se usa solo
         e.key: por posición física, en otras distribuciones serían otra letra. */
      if (e.key === '.') cambiarWarp(+1);
      else if (e.key === ',') cambiarWarp(-1);
      else if (e.key === ' ' || e.key === 'Spacebar' || e.code === 'Space') { e.preventDefault(); alternarPausa(); }
      else if (e.key === 'f' || e.key === 'F') seguir(!sim.follow);
    });
    $('g-light').addEventListener('change', e => globe && globe.setOptions({ light: e.target.checked }));
    $('g-atm').addEventListener('change', e => globe && globe.setOptions({ atmosphere: e.target.checked }));
    const rel = $('g-relief');
    rel.addEventListener('input', () => {
      $('g-relief-val').textContent = '×' + rel.value;
      if (globe) globe.setOptions({ relief: +rel.value });
    });

    $('preset-select').addEventListener('change', e => describePreset(e.target.value));
    $('preset-load').addEventListener('click', () => loadPreset($('preset-select').value));
    $('align-color').addEventListener('click', alignColorToBiome);
    $('biome-scan').addEventListener('click', scanBiomes);
    $('biome-export').addEventListener('click', () =>
      download('kerbin-biomas.json', JSON.stringify(KM.biomeNames.load(), null, 2)));
    $('biome-import').addEventListener('click', () => $('biome-file').click());
    $('biome-file').addEventListener('change', e => {
      const f = e.target.files[0];
      if (!f) return;
      f.text().then(t => {
        try {
          const d = JSON.parse(t);
          let n = 0;
          Object.keys(d).forEach(k => {
            if (typeof d[k] === 'string' && d[k]) { KM.biomeNames.set(k, d[k]); n++; }
          });
          flash('Importados ' + n + ' nombres de bioma.');
          if (bitmaps.biome) scanBiomes();
        } catch (err) { flash('Ese JSON no se pudo leer: ' + err.message); }
      });
      e.target.value = '';
    });

    // rango de alturas y su calibración
    $('h-min').value = state.hMin;
    $('h-max').value = state.hMax;
    $('h-min').addEventListener('change', e => { state.hMin = parseFloat(e.target.value) || 0; saveSettings(); });
    $('h-max').addEventListener('change', e => { state.hMax = parseFloat(e.target.value) || 0; saveSettings(); });

    ['a', 'b'].forEach(which => {
      $('cal-' + which).addEventListener('click', () => {
        if (!bitmaps.height) { flash('Carga antes un mapa de alturas.'); return; }
        if (calibTarget) $('cal-' + calibTarget).classList.remove('active');
        calibTarget = calibTarget === which ? null : which;
        $('cal-' + which).classList.toggle('active', calibTarget === which);
        map.getContainer().style.cursor = calibTarget ? 'crosshair' : '';
      });
    });
    $('cal-reset').addEventListener('click', () => {
      calib.a = calib.b = null;
      calibTarget = null;
      ['a', 'b'].forEach(w => $('cal-' + w).classList.remove('active'));
      map.getContainer().style.cursor = '';
      $('cal-out').textContent = '';
    });

    // ranuras de imagen
    document.querySelectorAll('.slot').forEach(el => {
      const slot = el.dataset.slot;
      const input = el.querySelector('input[type=file]');
      el.querySelector('.slot-load').addEventListener('click', () => input.click());
      input.addEventListener('change', () => {
        if (input.files[0]) loadSlot(slot, input.files[0]);
        input.value = '';
      });
      el.querySelector('.slot-clear').addEventListener('click', () => clearSlot(slot));
      el.querySelector('.slot-off').addEventListener('change', ev =>
        applyOffset(slot, parseFloat(ev.target.value) || 0));
    });

    // arrastrar y soltar
    const dz = $('dropzone');
    const stop = e => { e.preventDefault(); e.stopPropagation(); };
    ['dragenter', 'dragover'].forEach(ev =>
      dz.addEventListener(ev, e => { stop(e); dz.classList.add('over'); }));
    ['dragleave', 'drop'].forEach(ev =>
      dz.addEventListener(ev, e => { stop(e); dz.classList.remove('over'); }));
    dz.addEventListener('drop', e => {
      [...e.dataTransfer.files].forEach(f => {
        if (f.type.startsWith('image/')) loadSlot(guessSlot(f.name), f);
      });
    });
    // evita que soltar fuera de la zona abra la imagen en el navegador
    ['dragover', 'drop'].forEach(ev => window.addEventListener(ev, e => e.preventDefault()));

    // herramientas
    KM.tools.onModeChange = mode => {
      $('tool-measure').classList.toggle('active', mode === 'measure');
      $('tool-footprint').classList.toggle('active', mode === 'footprint');
      $('tool-hint').textContent =
        mode === 'measure'  ? 'Haz clic para encadenar puntos. Esc para terminar.' :
        mode === 'footprint' ? 'Haz clic donde esté el satélite. Esc para terminar.' :
                               'Ninguna herramienta activa.';
    };
    $('tool-measure').addEventListener('click', () => KM.tools.setMode('measure'));
    $('tool-footprint').addEventListener('click', () => KM.tools.setMode('footprint'));
    $('tool-clear').addEventListener('click', () => KM.tools.clear());

    // órbita
    $('orb-draw').addEventListener('click', drawOrbit);
    $('orb-clear').addEventListener('click', () => {
      orbitLayer.clearLayers();
      lastTrack = null;
      pushTrack();
      $('orb-out').innerHTML = '';
    });

    // marcadores
    $('mk-add').addEventListener('click', () => {
      const c = map.getCenter();
      const name = prompt('Nombre del marcador:', 'Punto ' + (KM.markers.user.length + 1));
      if (!name) return;
      KM.markers.add({ name, lat: c.lat, lon: KM.geo.wrapLon(c.lng), cat: 'usuario' });
    });
    $('mk-export').addEventListener('click', () => download('kerbin-marcadores.json', KM.markers.exportJSON()));
    $('mk-import').addEventListener('click', () => $('mk-file').click());
    $('mk-file').addEventListener('change', e => {
      const f = e.target.files[0];
      if (!f) return;
      f.text().then(t => {
        try { flash('Importados ' + KM.markers.importJSON(t) + ' marcadores.'); }
        catch (err) { flash('Ese JSON no se pudo leer: ' + err.message); }
      });
      e.target.value = '';
    });

    // búsqueda
    const search = $('search');
    search.addEventListener('input', () => doSearch(search.value));
    search.addEventListener('keydown', e => {
      if (e.key === 'Enter') {
        const first = $('search-results').querySelector('div');
        if (first) first.click();
      } else if (e.key === 'Escape') {
        $('search-results').classList.add('hidden');
        search.blur();
      }
    });
    document.addEventListener('click', e => {
      if (!e.target.closest('.search-wrap')) $('search-results').classList.add('hidden');
    });

    // barra lateral
    $('sb-toggle').addEventListener('click', () => {
      $('app').classList.toggle('sb-hidden');
      setTimeout(() => map.invalidateSize(), 200);
    });

    $('banner-close').addEventListener('click', () => {
      state.bannerDismissed = true;
      saveSettings();
      $('banner').classList.add('hidden');
    });

    // botones dentro de los popups
    document.addEventListener('click', e => {
      const copy = e.target.closest('[data-copy]');
      if (copy) {
        navigator.clipboard.writeText(copy.dataset.copy)
          .then(() => { copy.textContent = 'Copiado'; })
          .catch(() => { copy.textContent = copy.dataset.copy; });
        return;
      }
      const add = e.target.closest('[data-addmarker]');
      if (add) {
        const [lat, lon] = add.dataset.addmarker.split(',').map(Number);
        const name = prompt('Nombre del marcador:', 'Punto ' + (KM.markers.user.length + 1));
        if (name) { KM.markers.add({ name, lat, lon, cat: 'usuario' }); map.closePopup(); }
        return;
      }
      const bio = e.target.closest('[data-name-biome]');
      if (bio) {
        const hex = bio.dataset.nameBiome;
        const name = prompt('Nombre para el bioma ' + hex + ':', KM.biomeNames.get(hex) || '');
        if (name) { KM.biomeNames.set(hex, name); map.closePopup(); }
      }
    });
  }

  /* -------------------------------------------------------------------- inicio */

  async function main() {
    initMap();
    wireUI();
    renderBodyInfo();
    KM.tools.init(map);

    /* Ninguno de estos pasos puede impedir que el mapa llegue a dibujarse: si
       falla el almacenamiento o falta un fichero, se sigue adelante sin él. */
    const intentar = (etiqueta, p) =>
      Promise.resolve(p).catch(e => console.warn('[inicio] ' + etiqueta + ':', e));

    await intentar('marcadores', KM.markers.init(map));
    if (!state.landmarks && KM.markers.layer) map.removeLayer(KM.markers.layer);
    await intentar('imágenes guardadas', restoreSlots());
    await intentar('catálogo de mapas', loadCatalog().then(renderPresetUI));

    /* Primera visita: se carga el preset marcado como predeterminado. Si ya
       tenías algo guardado en el navegador, se respeta y no se toca nada. */
    const vacio = !bitmaps.color && !bitmaps.biome && !bitmaps.height;
    if (vacio && presets) {
      await intentar('mapa predeterminado',
                     loadPreset(state.presetId || presets.predeterminado, true));
    }
    await intentar('imágenes en data/', discoverDiskMaps());
    if (state.baseId === 'grid' && bitmaps.color) state.baseId = 'color';
    setBase(state.baseId, true);
    // un mapa de biomas encontrado en data/ se muestra solo, salvo que lo hayas apagado
    if (bitmaps.biome && !state.biomeTouched) state.biomeOn = true;
    syncBiomeLayer();
    if (bitmaps.biome) scanBiomes();

    KM.markers.onRender = syncGlobe;
    if (state.view3d) set3D(true);
    window.addEventListener('resize', () => { if (globe) globe._dirty = true; });
  }

  document.addEventListener('DOMContentLoaded', main);
})();
