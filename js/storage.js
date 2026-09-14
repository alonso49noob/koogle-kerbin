/* Persistencia local.

   Las imágenes del mapa pueden pesar decenas de MB, así que van a IndexedDB
   como Blob (localStorage no aguanta eso). Los ajustes y los marcadores, que son
   cuatro bytes, van a localStorage. Nada sale del navegador.

   Todo lleva plazo máximo y falla en silencio a propósito: guardar cosas es una
   comodidad, no un requisito. IndexedDB puede quedarse colgada sin devolver ni
   éxito ni error (otra pestaña bloqueando una migración, navegación privada,
   permisos de datos de sitio restringidos), y si el arranque la esperase sin
   límite el visor no llegaría a dibujarse nunca. */

(function () {
  const DB_NAME = 'kerbinmaps';
  const STORE = 'images';
  const TIMEOUT = 4000;
  let dbPromise = null;

  function withTimeout(promise, ms, what) {
    let timer;
    return Promise.race([
      promise.finally(() => clearTimeout(timer)),
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(what + ': sin respuesta en ' + ms + ' ms')), ms);
      })
    ]);
  }

  function open() {
    if (dbPromise) return dbPromise;

    dbPromise = withTimeout(new Promise((resolve, reject) => {
      let req;
      try { req = indexedDB.open(DB_NAME, 1); }
      catch (e) { reject(e); return; }

      req.onupgradeneeded = () => {
        const db = req.result;
        if (!db.objectStoreNames.contains(STORE)) db.createObjectStore(STORE);
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error || new Error('no se pudo abrir IndexedDB'));
      req.onblocked = () => reject(new Error('IndexedDB bloqueada por otra pestaña'));
    }), TIMEOUT, 'abrir IndexedDB');

    dbPromise.catch(e => {
      KM.store.available = false;
      console.warn('[storage] sin almacenamiento de imágenes:', e.message,
                   '— el visor funciona igual, pero los mapas que cargues no se ' +
                   'recordarán al recargar.');
    });

    return dbPromise;
  }

  function tx(mode, fn) {
    return withTimeout(
      open().then(db => new Promise((resolve, reject) => {
        const t = db.transaction(STORE, mode);
        const req = fn(t.objectStore(STORE));
        t.onerror = () => reject(t.error);
        t.onabort = () => reject(t.error || new Error('transacción abortada'));
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
      })),
      TIMEOUT, 'operación en IndexedDB'
    );
  }

  KM.store = {
    available: true,

    putImage(slot, blob, meta) {
      return tx('readwrite', s => s.put({ blob, meta, at: Date.now() }, slot));
    },
    getImage(slot) {
      return tx('readonly', s => s.get(slot));
    },
    delImage(slot) {
      return tx('readwrite', s => s.delete(slot));
    },

    loadJSON(key, fallback) {
      try {
        const raw = localStorage.getItem(key);
        return raw ? JSON.parse(raw) : fallback;
      } catch (e) {
        console.warn('[storage] no se pudo leer', key, e);
        return fallback;
      }
    },
    saveJSON(key, value) {
      try {
        localStorage.setItem(key, JSON.stringify(value));
      } catch (e) {
        console.warn('[storage] no se pudo guardar', key, e);
      }
    }
  };
})();
