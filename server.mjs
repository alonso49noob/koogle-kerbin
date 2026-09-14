/* Servidor estático mínimo, sin dependencias.
   Uso:  node server.mjs [puerto]        (por defecto 8080)
   Solo escucha en 127.0.0.1: el sitio no queda expuesto a la red local. */

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.argv[2] || process.env.PORT || 8080);
const HOST = '127.0.0.1';

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.css':  'text/css; charset=utf-8',
  '.js':   'text/javascript; charset=utf-8',
  '.mjs':  'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png':  'image/png',
  '.jpg':  'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif':  'image/gif',
  '.svg':  'image/svg+xml',
  '.ico':  'image/x-icon',
  '.txt':  'text/plain; charset=utf-8',
  '.md':   'text/plain; charset=utf-8'
};

const server = http.createServer((req, res) => {
  let urlPath;
  try {
    urlPath = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
  } catch {
    res.writeHead(400).end('URL mal formada');
    return;
  }
  if (urlPath.endsWith('/')) urlPath += 'index.html';

  const file = path.join(ROOT, urlPath);
  // path.join ya normaliza ".."; esto corta cualquier intento de salir de ROOT
  if (!file.startsWith(ROOT + path.sep) && file !== path.join(ROOT, 'index.html')) {
    res.writeHead(403).end('Prohibido');
    return;
  }

  fs.stat(file, (err, st) => {
    if (err || !st.isFile()) {
      res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
      res.end('404 — no encontrado: ' + urlPath);
      return;
    }
    res.writeHead(200, {
      'content-type': TYPES[path.extname(file).toLowerCase()] || 'application/octet-stream',
      'content-length': st.size,
      'cache-control': 'no-cache'
    });
    fs.createReadStream(file).pipe(res);
  });
});

server.listen(PORT, HOST, () => {
  console.log('\n  Kerbin Maps  ->  http://' + HOST + ':' + PORT + '/');
  // si hay copias viejas del proyecto por ahí, esto dice cuál se está sirviendo
  console.log('  Carpeta: ' + ROOT + '\n');
  console.log('  Ctrl+C para parar.\n');
});

server.on('error', e => {
  if (e.code === 'EADDRINUSE') {
    console.error('\n  El puerto ' + PORT + ' está ocupado. Prueba: node server.mjs 8081\n');
    process.exit(1);
  }
  throw e;
});
