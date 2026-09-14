/* Genera imágenes equirectangulares de prueba (2:1) para comprobar que el
   visor se alinea donde debe antes de pelearte con el export real del juego.

   Uso:
     node tools/make-test-map.mjs            mapa de color 2048x1024
     node tools/make-test-map.mjs 4096       mapa de color del ancho que digas
     node tools/make-test-map.mjs --biome    mapa de biomas 720x360, como SCANsat
     node tools/make-test-map.mjs --height   heightmap gris 2048x1024, verificable

   El de color lleva un damero de 30°, el ecuador y el meridiano 0 marcados, y
   una cruz sobre las coordenadas del KSC: si al cargarlo el marcador del KSC
   cae justo sobre la cruz, la proyección está bien.

   El de biomas lleva franjas de colores planos, para probar la leyenda y los
   porcentajes de superficie. */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { encodePNG } from './png.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const KSC = { lat: -0.0972, lon: -74.5577 };

const args = process.argv.slice(2);
const wantBiome = args.includes('--biome');
const wantHeight = args.includes('--height');

function buffer(W, H) {
  const px = Buffer.alloc(W * H * 3);
  return {
    px,
    set(x, y, r, g, b) {
      if (x < 0 || y < 0 || x >= W || y >= H) return;
      const i = (y * W + x) * 3;
      px[i] = r; px[i + 1] = g; px[i + 2] = b;
    }
  };
}

function write(name, W, H, px) {
  const png = encodePNG(W, H, px);
  const out = path.join(ROOT, 'data', name);
  fs.mkdirSync(path.dirname(out), { recursive: true });
  fs.writeFileSync(out, png);
  console.log('Escrito ' + out + '  (' + W + 'x' + H + ', ' + (png.length / 1024).toFixed(0) + ' KB)');
}

/* ---------------------------------------------------------------- altura ---- */

/* Rampa lineal en longitud: el gris de un punto es exactamente
   round(255 * (lon+180)/360), así que la altitud que debe devolver la sonda se
   puede calcular a mano y comparar. Un terreno de aspecto realista quedaría más
   bonito pero no serviría para comprobar nada. */
if (wantHeight) {
  const W = Number(args.find(a => /^\d+$/.test(a)) || 2048), H = W / 2;
  const b = buffer(W, H);
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const g = Math.round(((x + 0.5) / W) * 255);
      b.set(x, y, g, g, g);
    }
  }
  write('test-height-ramp.png', W, H, b.px);
  console.log('Rampa lineal en longitud: gris = round(255*(lon+180)/360).');
  console.log('Con gris 0 = -1000 m y gris 255 = 6764 m, en lon 0 debe dar ~2882 m.');
  process.exit(0);
}

/* ---------------------------------------------------------------- biomas ---- */

if (wantBiome) {
  const W = 720, H = 360;                   // lo que saca SCANsat
  const b = buffer(W, H);

  /* Colores planos por bandas de latitud, más un par de manchas, para que haya
     varios biomas con superficies muy distintas que contrastar en la leyenda. */
  const bands = [
    { upTo:  -60, rgb: [235, 240, 248] },   // casquete sur
    { upTo:  -35, rgb: [120, 150, 110] },
    { upTo:  -10, rgb: [ 90, 130,  70] },
    { upTo:   10, rgb: [ 60, 100,  55] },   // franja ecuatorial
    { upTo:   35, rgb: [170, 155, 100] },
    { upTo:   60, rgb: [110, 140, 105] },
    { upTo:   91, rgb: [235, 240, 248] }    // casquete norte, mismo color que el sur
  ];

  for (let y = 0; y < H; y++) {
    const lat = 90 - ((y + 0.5) / H) * 180;
    const band = bands.find(bd => lat < bd.upTo) || bands[bands.length - 1];
    for (let x = 0; x < W; x++) {
      const lon = ((x + 0.5) / W) * 360 - 180;
      let [r, g, bl] = band.rgb;
      // océano en la mitad oeste, para que no sea solo franjas
      if (lon < -20 && Math.abs(lat) < 62) [r, g, bl] = [40, 70, 120];
      // una mancha pequeña alrededor del KSC
      if (Math.hypot(lat - KSC.lat, lon - KSC.lon) < 6) [r, g, bl] = [200, 90, 60];
      b.set(x, y, r, g, bl);
    }
  }

  write('test-biome-720x360.png', W, H, b.px);
  process.exit(0);
}

/* ----------------------------------------------------------------- color ---- */

const W = Number(args.find(a => /^\d+$/.test(a)) || 2048);
const H = W / 2;
const b = buffer(W, H);

for (let y = 0; y < H; y++) {
  const lat = 90 - (y / H) * 180;
  for (let x = 0; x < W; x++) {
    const lon = (x / W) * 360 - 180;
    const cell = (Math.floor((lon + 180) / 30) + Math.floor((90 - lat) / 30)) % 2;
    // claro hacia el polo norte, oscuro hacia el sur: así se ve al vuelo si la
    // imagen está del revés en vertical
    const shade = 0.35 + 0.5 * ((90 - lat) / 180);
    const base = cell ? 70 : 110;
    b.set(x, y, Math.round(base * shade), Math.round(base * shade * 1.25), Math.round(base * shade * 1.6));
  }
}

const xOf = lon => Math.round(((lon + 180) / 360) * W);
const yOf = lat => Math.round(((90 - lat) / 180) * H);

// retícula cada 30°
for (let lon = -180; lon <= 180; lon += 30) {
  const x = Math.min(W - 1, xOf(lon));
  for (let y = 0; y < H; y++) b.set(x, y, 90, 110, 140);
}
for (let lat = -90; lat <= 90; lat += 30) {
  const y = Math.min(H - 1, yOf(lat));
  for (let x = 0; x < W; x++) b.set(x, y, 90, 110, 140);
}

// ecuador en verde, meridiano 0 en rojo
for (let x = 0; x < W; x++) { const y = yOf(0); b.set(x, y - 1, 40, 220, 90); b.set(x, y, 40, 220, 90); }
for (let y = 0; y < H; y++) { const x = xOf(0); b.set(x - 1, y, 230, 60, 60); b.set(x, y, 230, 60, 60); }

// cruz sobre el KSC
const kx = xOf(KSC.lon), ky = yOf(KSC.lat);
for (let d = -14; d <= 14; d++) {
  b.set(kx + d, ky, 255, 220, 0); b.set(kx + d, ky + 1, 255, 220, 0);
  b.set(kx, ky + d, 255, 220, 0); b.set(kx + 1, ky + d, 255, 220, 0);
}

write('test-equirectangular.png', W, H, b.px);
