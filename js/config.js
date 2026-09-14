/* Parámetros del cuerpo y configuración global.
   Valores de Kerbin stock (KSP 1.x). Todo en unidades SI salvo donde se indique. */

window.KM = window.KM || {};

KM.BODY = {
  name: 'Kerbin',
  radius: 600000,            // m
  mu: 3.5316000e12,          // m^3/s^2  (GM)
  siderealDay: 21549.425,    // s  (5 h 59 m 9,425 s)
  solarDay: 21600,           // s  (6 h exactas)
  atmosphere: 70000,         // m
  soi: 84159286,             // m
  // Altitud de una órbita síncrona (keoestacionaria), derivada de mu y siderealDay
  get synchronousAlt() {
    const a = Math.cbrt(this.mu * this.siderealDay * this.siderealDay / (4 * Math.PI * Math.PI));
    return a - this.radius;
  }
};

/* Rango con el que se traduce el gris de un heightmap a metros: gris 0 -> min,
   gris 255 -> max.

   Ojo: el terreno de Kerbin stock es PROCEDURAL (lo genera el PQS con ruido),
   no hay ninguna textura de alturas dentro del juego que extraer. Estos valores
   son solo un punto de partida plausible; sin calibrar contra dos altitudes
   reales, las cifras que salgan no significan nada. Ver README. */
KM.HEIGHT_RANGE = { min: -1000, max: 6764 };

KM.MAP = {
  minZoom: 0,
  maxZoom: 9,
  initialZoom: 2,
  initialCenter: [0, -74.5],
  tileSize: 256
};

/* Fuentes de mapa base disponibles en el selector.
   Los biomas no están aquí: van como capa superpuesta, que es donde sirven. */
KM.BASES = [
  { id: 'grid',    label: 'Retícula (sin imagen)',      kind: 'placeholder' },
  { id: 'color',   label: 'Color / satélite (tu PNG)',  kind: 'image', slot: 'color' },
  { id: 'height',  label: 'Altura (tu PNG)',            kind: 'image', slot: 'height' },
  { id: 'tiles',   label: 'Teselas locales ./tiles',    kind: 'xyz', url: 'tiles/{z}/{x}/{y}.png' },
  { id: 'custom',  label: 'Plantilla XYZ propia…',      kind: 'xyz', url: '' }
];

/* Los mapas de biomas de SCANsat salen a 720x360: un píxel es medio grado,
   unos 5 km en el ecuador. Basta de sobra, porque los biomas son regiones
   planas, pero hay que pintarlos SIN interpolar o los bordes inventan colores
   intermedios que no corresponden a ningún bioma. */
KM.BIOME = {
  defaultOpacity: 0.65,
  /* Al listar la leyenda se ignoran los colores que ocupen menos de esto:
     son el dentado de los bordes, no biomas de verdad. */
  minAreaPct: 0.02
};

KM.STORAGE_KEYS = {
  settings: 'kerbinmaps.settings.v1',
  markers:  'kerbinmaps.markers.v1'
};
