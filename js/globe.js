/* Vista 3D: Kerbin como esfera texturizada.

   WebGL directo, sin motor 3D de por medio: lo que hace falta aquí es una esfera
   con la textura equirectangular pegada, y eso es justo el mapeo UV natural de
   una esfera. Traerse una librería de 600 KB para esto no compensa.

   Requiere WebGL2 por dos motivos concretos: las texturas de biomas no son
   potencia de dos (1800x900), y textureGrad hace falta para que el desfase de
   longitud no deje una costura visible en el meridiano. */

(function () {
  const R2D = 180 / Math.PI, D2R = Math.PI / 180;

  /* ---------------------------------------------------------------- matrices */

  function mat4() { return new Float32Array(16); }

  function identity(m) {
    m.fill(0); m[0] = m[5] = m[10] = m[15] = 1; return m;
  }

  function perspective(out, fovy, aspect, near, far) {
    const f = 1 / Math.tan(fovy / 2), nf = 1 / (near - far);
    out.fill(0);
    out[0] = f / aspect; out[5] = f;
    out[10] = (far + near) * nf; out[11] = -1;
    out[14] = 2 * far * near * nf;
    return out;
  }

  function lookAt(out, eye, center, up) {
    let z0 = eye[0]-center[0], z1 = eye[1]-center[1], z2 = eye[2]-center[2];
    let len = Math.hypot(z0, z1, z2) || 1;
    z0 /= len; z1 /= len; z2 /= len;

    let x0 = up[1]*z2 - up[2]*z1, x1 = up[2]*z0 - up[0]*z2, x2 = up[0]*z1 - up[1]*z0;
    len = Math.hypot(x0, x1, x2);
    if (!len) { x0 = 1; x1 = 0; x2 = 0; } else { x0/=len; x1/=len; x2/=len; }

    const y0 = z1*x2 - z2*x1, y1 = z2*x0 - z0*x2, y2 = z0*x1 - z1*x0;

    out[0]=x0; out[1]=y0; out[2]=z0; out[3]=0;
    out[4]=x1; out[5]=y1; out[6]=z1; out[7]=0;
    out[8]=x2; out[9]=y2; out[10]=z2; out[11]=0;
    out[12]=-(x0*eye[0]+x1*eye[1]+x2*eye[2]);
    out[13]=-(y0*eye[0]+y1*eye[1]+y2*eye[2]);
    out[14]=-(z0*eye[0]+z1*eye[1]+z2*eye[2]);
    out[15]=1;
    return out;
  }

  /* Convenio: mismo que usa el mapa 2D. u = (lon+180)/360, v = (90-lat)/180. */
  function sph(lat, lon, r) {
    const a = lat * D2R, b = lon * D2R;
    const ca = Math.cos(a);
    return [r * ca * Math.sin(b), r * Math.sin(a), r * ca * Math.cos(b)];
  }

  function hexRgb(h) {
    const m = /^#?([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(h || '');
    return m ? [parseInt(m[1], 16) / 255, parseInt(m[2], 16) / 255, parseInt(m[3], 16) / 255]
             : [0.8, 0.8, 0.8];
  }

  /* ------------------------------------------------------------------ malla */

  function buildSphere(cols, rows) {
    const pos = [], uv = [], idx = [];
    /* La columna de la costura se duplica (u=0 y u=1) a propósito: si se cosen
       los vértices, la interpolación de u va 1 -> 0 dentro de un triángulo y
       aparece una franja con toda la textura comprimida. */
    for (let y = 0; y <= rows; y++) {
      const v = y / rows, lat = 90 - v * 180;
      for (let x = 0; x <= cols; x++) {
        const u = x / cols, lon = -180 + u * 360;
        const p = sph(lat, lon, 1);
        pos.push(p[0], p[1], p[2]);
        uv.push(u, v);
      }
    }
    for (let y = 0; y < rows; y++) {
      for (let x = 0; x < cols; x++) {
        const a = y * (cols + 1) + x, b = a + cols + 1;
        idx.push(a, b, a + 1, a + 1, b, b + 1);
      }
    }
    return {
      pos: new Float32Array(pos),
      uv: new Float32Array(uv),
      idx: new Uint32Array(idx)
    };
  }

  /* ----------------------------------------------------------------- shaders */

  const VS = `#version 300 es
  in vec3 aPos;
  in vec2 aUv;
  uniform mat4 uProj, uView;
  uniform sampler2D uHeight;
  uniform vec2 uHeightSize;
  uniform float uRelief, uHeightOff, uHMin, uHMax, uRadius, uScale;
  out vec2 vUv;
  out vec3 vNormal;

  const float PI = 3.14159265;

  /* Dirección unitaria para unas uv equirectangulares. Mismo convenio que el
     resto del visor: u=0 es lon -180, v=0 es lat +90. */
  vec3 dirOf(vec2 uv) {
    float lat = (0.5 - uv.y) * PI;
    float lon = (uv.x - 0.5) * 2.0 * PI;
    return vec3(cos(lat) * sin(lon), sin(lat), cos(lat) * cos(lon));
  }

  float dispAt(vec2 uv) {
    float lum = dot(texture(uHeight, vec2(fract(uv.x + uHeightOff), uv.y)).rgb,
                    vec3(0.2126, 0.7152, 0.0722));
    return ((uHMin + lum * (uHMax - uHMin)) / uRadius) * uRelief;
  }

  void main() {
    vUv = aUv;
    float r = uScale;

    if (uRelief > 0.0) {
      r += dispAt(aUv);

      /* La normal hay que recalcularla a partir del terreno desplazado. Si se
         deja la de la esfera, la luz no se entera de que hay montañas y el
         relieve solo se distingue en la silueta del limbo. Se toman dos vecinos
         a un par de téxeles y se hace el producto vectorial de las tangentes. */
      vec2 e = 2.0 / uHeightSize;
      vec2 uu = aUv + vec2(e.x, 0.0);
      vec2 vv = aUv + vec2(0.0, e.y);
      vec3 pc = dirOf(aUv) * r;
      vec3 pu = dirOf(uu) * (uScale + dispAt(uu));
      vec3 pv = dirOf(vv) * (uScale + dispAt(vv));
      vec3 n = normalize(cross(pu - pc, pv - pc));
      // el producto vectorial puede salir hacia dentro; se fuerza hacia fuera
      vNormal = dot(n, aPos) < 0.0 ? -n : n;
    } else {
      vNormal = aPos;
    }

    gl_Position = uProj * uView * vec4(aPos * r, 1.0);
  }`;

  const FS = `#version 300 es
  precision highp float;
  in vec2 vUv;
  in vec3 vNormal;
  uniform sampler2D uColor, uBiome;
  uniform float uColorOff, uBiomeOff, uBiomeAmt, uAmbient;
  uniform bool uHasColor, uHasBiome;
  uniform vec3 uLightDir;
  out vec4 frag;

  /* El desfase mueve la u fuera de [0,1]; con fract() se envuelve, pero eso
     dispara las derivadas justo en la costura y el mipmap elige el nivel más
     borroso, dejando una línea. Pasando las derivadas sin envolver se evita. */
  vec3 shifted(sampler2D t, float off) {
    return textureGrad(t, vec2(fract(vUv.x + off), vUv.y), dFdx(vUv), dFdy(vUv)).rgb;
  }

  void main() {
    vec3 base = vec3(0.10, 0.13, 0.18);
    if (uHasColor) base = shifted(uColor, uColorOff);
    if (uHasBiome && uBiomeAmt > 0.0) base = mix(base, shifted(uBiome, uBiomeOff), uBiomeAmt);
    float d = max(dot(normalize(vNormal), uLightDir), 0.0);
    frag = vec4(base * (uAmbient + (1.0 - uAmbient) * d), 1.0);
  }`;

  const ATM_FS = `#version 300 es
  precision highp float;
  in vec3 vNormal;
  uniform vec3 uCamPos;
  uniform float uAtmScale;
  out vec4 frag;
  void main() {
    vec3 n = normalize(vNormal);
    vec3 viewDir = normalize(uCamPos - n * uAtmScale);
    /* Fresnel: el aire se ve donde lo miras de refilón, no de frente. */
    float f = pow(1.0 - abs(dot(n, viewDir)), 3.0);
    frag = vec4(vec3(0.36, 0.60, 1.0) * f, f * 0.9);
  }`;

  /* La traza se guarda como dirección unitaria + radio por separado, en vez de
     una posición ya multiplicada: así el shader puede levantarla sobre el relieve
     sin tener que renormalizar nada. */
  const LINE_VS = `#version 300 es
  in vec3 aDir;
  in float aRad;
  in vec2 aUv;
  uniform mat4 uProj, uView;
  uniform sampler2D uHeight;
  uniform float uRelief, uHeightOff, uHMin, uHMax, uRadius, uLift, uUseRelief, uLonShift;
  void main() {
    float r = aRad + uLift;
    if (uUseRelief > 0.5 && uRelief > 0.0) {
      float lum = dot(texture(uHeight, vec2(fract(aUv.x + uHeightOff), aUv.y)).rgb,
                      vec3(0.2126, 0.7152, 0.0722));
      r += ((uHMin + lum * (uHMax - uHMin)) / uRadius) * uRelief;
    }
    /* Resta uLonShift a la longitud: es lo que ha girado Kerbin desde que se
       construyeron los anillos. Con x = cos(lat)·sen(lon), z = cos(lat)·cos(lon),
       restar un ángulo a lon es girar alrededor del eje norte (Y). */
    float c = cos(uLonShift), s = sin(uLonShift);
    vec3 d = vec3(aDir.x * c - aDir.z * s, aDir.y, aDir.x * s + aDir.z * c);
    gl_Position = uProj * uView * vec4(d * r, 1.0);
    gl_PointSize = 7.0;
  }`;

  const LINE_FS = `#version 300 es
  precision highp float;
  uniform vec4 uLineColor;
  out vec4 frag;
  void main() { frag = uLineColor; }`;

  /* ------------------------------------------------------------------- clase */

  KM.Globe = function (canvas, markerLayer) {
    this.canvas = canvas;
    this.markerHost = markerLayer;
    this.gl = null;
    this.ready = false;
    this.error = null;

    this.cam = { lat: 0, lon: -74.5, dist: 3.2 };
    this.opts = {
      light: true, atmosphere: true, relief: 0,
      biomeAmt: 0, colorOff: 0, biomeOff: 0, heightOff: 0,
      hMin: -1000, hMax: 6764
    };
    this.tex = { color: null, biome: null, height: null };
    this._markers = [];
    this._pins = [];
    this.track = null;
    this.orbits = null;
    this.orbitShift = 0;
    this.minDist = 1.02;
    this.maxDist = 12;
    this.sceneR = 1.2;
    this._hSize = [1, 1];
    this._raf = null;
    this._dirty = true;
  };

  KM.Globe.prototype = {

    init() {
      const gl = this.canvas.getContext('webgl2', { antialias: true, alpha: false });
      if (!gl) {
        this.error = 'Este navegador no trae WebGL2, que es lo que necesita la vista 3D.';
        return false;
      }
      this.gl = gl;

      const sphereAttrs = { aPos: 0, aUv: 1 };
      this.prog = this._program(VS, FS, sphereAttrs);
      this.atmProg = this._program(VS, ATM_FS, sphereAttrs);
      this.lineProg = this._program(LINE_VS, LINE_FS, { aDir: 0, aRad: 1, aUv: 2 });
      if (!this.prog || !this.atmProg || !this.lineProg) return false;

      const mesh = buildSphere(192, 96);
      this.count = mesh.idx.length;

      this.vao = gl.createVertexArray();
      gl.bindVertexArray(this.vao);
      this._buffer(gl.ARRAY_BUFFER, mesh.pos);
      gl.enableVertexAttribArray(0);
      gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
      this._buffer(gl.ARRAY_BUFFER, mesh.uv);
      gl.enableVertexAttribArray(1);
      gl.vertexAttribPointer(1, 2, gl.FLOAT, false, 0, 0);
      this._buffer(gl.ELEMENT_ARRAY_BUFFER, mesh.idx);
      gl.bindVertexArray(null);

      this.proj = mat4(); this.view = mat4();
      gl.enable(gl.DEPTH_TEST);
      gl.clearColor(0.027, 0.043, 0.067, 1);

      this._bindInput();
      this.ready = true;
      return true;
    },

    _program(vsSrc, fsSrc, attrs) {
      const gl = this.gl;
      const mk = (type, src) => {
        const s = gl.createShader(type);
        gl.shaderSource(s, src); gl.compileShader(s);
        if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
          this.error = 'Error compilando el shader: ' + gl.getShaderInfoLog(s);
          console.error(this.error, src);
          return null;
        }
        return s;
      };
      const vs = mk(gl.VERTEX_SHADER, vsSrc), fs = mk(gl.FRAGMENT_SHADER, fsSrc);
      if (!vs || !fs) return null;
      const p = gl.createProgram();
      gl.attachShader(p, vs); gl.attachShader(p, fs);
      /* Los índices se fijan a mano en vez de dejar que el enlazador los reparta:
         varios programas comparten el mismo VAO, y si cada uno los numerase a su
         manera, alguno leería basura. */
      Object.keys(attrs || {}).forEach(n => gl.bindAttribLocation(p, attrs[n], n));
      gl.linkProgram(p);
      if (!gl.getProgramParameter(p, gl.LINK_STATUS)) {
        this.error = 'Error enlazando el programa: ' + gl.getProgramInfoLog(p);
        return null;
      }
      return p;
    },

    _buffer(target, data) {
      const gl = this.gl;
      const b = gl.createBuffer();
      gl.bindBuffer(target, b);
      gl.bufferData(target, data, gl.STATIC_DRAW);
      return b;
    },

    setTexture(slot, bitmap) {
      const gl = this.gl;
      if (!gl) return;
      if (this.tex[slot]) { gl.deleteTexture(this.tex[slot]); this.tex[slot] = null; }
      if (!bitmap) { this._dirty = true; return; }

      const t = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, t);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, bitmap);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      if (slot === 'color') {
        gl.generateMipmap(gl.TEXTURE_2D);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      } else {
        // biomas y alturas: color exacto, ni mipmap ni interpolación
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
      }
      this.tex[slot] = t;
      if (slot === 'height') this._hSize = [bitmap.width, bitmap.height];
      this._dirty = true;
    },

    setOptions(o) {
      Object.assign(this.opts, o);
      this._dirty = true;
    },

    /* Traza orbital. Se dibujan dos cosas a partir de los mismos puntos:
       la huella sobre el suelo y el camino a su altitud real.

       Las dos van en el marco fijo al cuerpo, igual que la textura del globo.
       Por eso el camino en altura no se cierra en una elipse: es la trayectoria
       vista desde el planeta que gira, y ese desmadejarse hacia el oeste es
       exactamente la deriva que hace que cada vuelta pase por otro sitio.

       Aquí no hay que partir nada por el antimeridiano: sobre una esfera la
       línea es continua, y ese troceo que en 2D es obligatorio desaparece. */
    setTrack(points, opts) {
      const gl = this.gl;
      if (!gl) return;
      if (!points || points.length < 2) {
        this.track = null;
        this._dirty = true;
        return;
      }

      const n = points.length;
      const ground = new Float32Array(n * 6);
      const space = new Float32Array(n * 6);
      for (let i = 0; i < n; i++) {
        const p = points[i];
        const d = sph(p.lat, p.lon, 1);
        const u = (KM.geo.wrapLon(p.lon) + 180) / 360, v = (90 - p.lat) / 180;
        const rs = (KM.BODY.radius + p.alt) / KM.BODY.radius;
        const o = i * 6;
        ground[o] = d[0]; ground[o+1] = d[1]; ground[o+2] = d[2];
        ground[o+3] = 1;  ground[o+4] = u;    ground[o+5] = v;
        space[o] = d[0];  space[o+1] = d[1];  space[o+2] = d[2];
        space[o+3] = rs;  space[o+4] = u;     space[o+5] = v;
      }

      if (!this.track) {
        this.track = {
          vaoG: gl.createVertexArray(), bufG: gl.createBuffer(),
          vaoS: gl.createVertexArray(), bufS: gl.createBuffer(),
          n: 0
        };
        this._trackVao(this.track.vaoG, this.track.bufG);
        this._trackVao(this.track.vaoS, this.track.bufS);
      }
      gl.bindBuffer(gl.ARRAY_BUFFER, this.track.bufG);
      gl.bufferData(gl.ARRAY_BUFFER, ground, gl.DYNAMIC_DRAW);
      gl.bindBuffer(gl.ARRAY_BUFFER, this.track.bufS);
      gl.bufferData(gl.ARRAY_BUFFER, space, gl.DYNAMIC_DRAW);
      this.track.n = n;
      this.track.space = !(opts && opts.space === false);
      this._dirty = true;
    },

    _trackVao(vao, buf) {
      const gl = this.gl;
      gl.bindVertexArray(vao);
      gl.bindBuffer(gl.ARRAY_BUFFER, buf);
      const stride = 6 * 4;
      gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 3, gl.FLOAT, false, stride, 0);
      gl.enableVertexAttribArray(1); gl.vertexAttribPointer(1, 1, gl.FLOAT, false, stride, 12);
      gl.enableVertexAttribArray(2); gl.vertexAttribPointer(2, 2, gl.FLOAT, false, stride, 16);
      gl.bindVertexArray(null);
    },

    _drawTrack() {
      const gl = this.gl, t = this.track, o = this.opts;
      if (!t || !t.n) return;

      gl.useProgram(this.lineProg);
      const u = n => gl.getUniformLocation(this.lineProg, n);
      gl.uniformMatrix4fv(u('uProj'), false, this.proj);
      gl.uniformMatrix4fv(u('uView'), false, this.view);
      gl.uniform1f(u('uRadius'), KM.BODY.radius);
      gl.uniform1f(u('uRelief'), this.tex.height ? o.relief : 0);
      gl.uniform1f(u('uHeightOff'), (o.heightOff || 0) / 360);
      gl.uniform1f(u('uHMin'), o.hMin);
      gl.uniform1f(u('uHMax'), o.hMax);
      gl.activeTexture(gl.TEXTURE2);
      gl.bindTexture(gl.TEXTURE_2D, this.tex.height);
      gl.uniform1i(u('uHeight'), 2);

      gl.enable(gl.BLEND);
      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

      gl.uniform1f(u('uLonShift'), 0);

      // camino a su altitud, más tenue (las naves no lo usan: tienen su anillo)
      if (t.space) {
        gl.bindVertexArray(t.vaoS);
        gl.uniform1f(u('uLift'), 0);
        gl.uniform1f(u('uUseRelief'), 0);
        gl.uniform4f(u('uLineColor'), 0.66, 0.42, 0.92, 0.55);
        gl.drawArrays(gl.LINE_STRIP, 0, t.n);
      }

      /* La huella se levanta un pelo del suelo: la esfera es un poliedro inscrito
         y entre vértices su superficie queda por debajo de r=1, así que una línea
         pegada a r=1 se hunde a trozos. */
      gl.bindVertexArray(t.vaoG);
      gl.uniform1f(u('uLift'), 0.0016);
      gl.uniform1f(u('uUseRelief'), 1);
      gl.uniform4f(u('uLineColor'), 0.82, 0.55, 1.0, 0.95);
      gl.drawArrays(gl.LINE_STRIP, 0, t.n);

      // periapsis: primer punto de la serie
      gl.uniform4f(u('uLineColor'), 1.0, 1.0, 1.0, 1.0);
      gl.drawArrays(gl.POINTS, 0, 1);

      gl.disable(gl.BLEND);
      gl.bindVertexArray(null);
    },

    /* Anillos de órbita de varias naves a la vez. Una órbita kepleriana es una
       elipse fija en el espacio y lo que se mueve es Kerbin girando debajo: los
       anillos se construyen una sola vez, con la rotación del momento del
       guardado, y en cada fotograma solo se giran en el shader con uLonShift.
       Nada de recalcular geometría a 60 fotogramas por segundo. */
    setOrbits(list) {
      const gl = this.gl;
      if (!gl) return;
      if (!list || !list.length) {
        if (this.orbits) this.orbits.segs = [];
        this._dirty = true;
        return;
      }
      let total = 0;
      list.forEach(o => { total += o.points.length; });
      const data = new Float32Array(total * 6);
      const segs = [];
      let k = 0;
      list.forEach(o => {
        const off = k;
        o.points.forEach(p => {
          const d = sph(p.lat, p.lon, 1), i = k * 6;
          data[i] = d[0]; data[i + 1] = d[1]; data[i + 2] = d[2];
          data[i + 3] = (KM.BODY.radius + p.alt) / KM.BODY.radius;
          data[i + 4] = 0; data[i + 5] = 0;
          k++;
        });
        const c = hexRgb(o.color);
        segs.push({ off, n: o.points.length, rgba: [c[0], c[1], c[2], o.alpha == null ? 0.5 : o.alpha] });
      });
      if (!this.orbits) {
        this.orbits = { vao: gl.createVertexArray(), buf: gl.createBuffer(), segs: [] };
        this._trackVao(this.orbits.vao, this.orbits.buf);
      }
      gl.bindBuffer(gl.ARRAY_BUFFER, this.orbits.buf);
      gl.bufferData(gl.ARRAY_BUFFER, data, gl.DYNAMIC_DRAW);
      this.orbits.segs = segs;
      this._dirty = true;
    },

    setOrbitShift(deg) {
      this.orbitShift = (((deg % 360) + 360) % 360) * D2R;
      this._dirty = true;
    },

    /* Tamaño de la escena: la órbita más lejana. Fija hasta dónde deja alejarse la
       rueda y los planos de recorte; antes estaba clavado en 12 radios y las
       órbitas altas quedaban fuera. */
    setScene(maxRadius) {
      this.sceneR = Math.max(1.2, maxRadius || 1.2);
      this.maxDist = Math.max(12, this.sceneR * 3);
      if (this.cam.dist > this.maxDist) this.cam.dist = this.maxDist;
      this._dirty = true;
    },

    setMinDist(d) {
      this.minDist = Math.max(1.02, d || 1.02);
      if (this.cam.dist < this.minDist) this.cam.dist = Math.min(this.minDist, this.maxDist);
      this._dirty = true;
    },

    _drawOrbits() {
      const gl = this.gl, O = this.orbits;
      if (!O || !O.segs.length) return;
      gl.useProgram(this.lineProg);
      const u = n => gl.getUniformLocation(this.lineProg, n);
      gl.uniformMatrix4fv(u('uProj'), false, this.proj);
      gl.uniformMatrix4fv(u('uView'), false, this.view);
      gl.uniform1f(u('uRelief'), 0);
      gl.uniform1f(u('uUseRelief'), 0);
      gl.uniform1f(u('uLift'), 0);
      gl.uniform1f(u('uLonShift'), this.orbitShift);
      gl.bindVertexArray(O.vao);
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
      gl.depthMask(false);        // translúcidas: que no se tapen unas a otras
      for (const g of O.segs) {
        gl.uniform4f(u('uLineColor'), g.rgba[0], g.rgba[1], g.rgba[2], g.rgba[3]);
        gl.drawArrays(gl.LINE_STRIP, g.off, g.n);
      }
      gl.depthMask(true);
      gl.disable(gl.BLEND);
      gl.bindVertexArray(null);
    },

    /* Mueve marcadores ya creados sin rehacer el DOM. En la simulación esto va en
       cada fotograma, y recrear 70 elementos 60 veces por segundo no tiene sentido. */
    updateMarkers(list) {
      if (!list || list.length !== this._markers.length ||
          list.some((m, i) => m.name !== this._markers[i].name)) {
        this.setMarkers(list);
        return;
      }
      for (let i = 0; i < list.length; i++) {
        const d = this._markers[i], src = list[i];
        d.lat = src.lat; d.lon = src.lon; d.r = src.r; d.hidden = !!src.hidden;
      }
      this._dirty = true;
    },

    setMarkers(list) {
      this._markers = list || [];
      this.markerHost.innerHTML = '';
      this._pins = this._markers.map(m => {
        const el = document.createElement('div');
        el.className = 'globe-pin' + (m.kind === 'nave' ? ' nave' : '');
        el.innerHTML = '<i style="background:' + (m.color || '#4ea3ff') + '"></i><b>' +
                       m.name.replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])) +
                       '</b>';
        this.markerHost.appendChild(el);
        return el;
      });
      this._dirty = true;
    },

    center() { return { lat: this.cam.lat, lon: KM.geo.wrapLon(this.cam.lon) }; },

    setCenter(lat, lon, dist) {
      this.cam.lat = Math.max(-89.9, Math.min(89.9, lat));
      this.cam.lon = lon;
      if (dist) this.cam.dist = Math.max(this.minDist, Math.min(this.maxDist, dist));
      this._dirty = true;
    },

    /* ------------------------------------------------------------ interacción */

    _bindInput() {
      const cv = this.canvas;
      let drag = null;

      cv.addEventListener('pointerdown', e => {
        drag = { x: e.clientX, y: e.clientY, lat: this.cam.lat, lon: this.cam.lon };
        cv.setPointerCapture(e.pointerId);
        cv.style.cursor = 'grabbing';
      });

      cv.addEventListener('pointermove', e => {
        if (drag) {
          if (!drag.movido && Math.hypot(e.clientX - drag.x, e.clientY - drag.y) > 3) {
            drag.movido = true;
            if (this.onUserDrag) this.onUserDrag();      // p. ej. dejar de seguir a una nave
          }
          /* El este cae a la derecha en pantalla, así que arrastrar hacia la
             derecha baja la longitud de la cámara: es lo que hace que el terreno
             agarrado acompañe al cursor. */
          this.cam.lon = drag.lon - this._arc(e.clientX - drag.x);
          this.cam.lat = Math.max(-89.9, Math.min(89.9, drag.lat + this._arc(e.clientY - drag.y)));
          this._dirty = true;
        } else if (this.onHover) {
          this.onHover(this.pick(e));
        }
      });

      const end = e => {
        drag = null;
        cv.style.cursor = 'grab';
        try { cv.releasePointerCapture(e.pointerId); } catch (_) {}
      };
      cv.addEventListener('pointerup', end);
      cv.addEventListener('pointercancel', end);
      cv.addEventListener('pointerleave', () => { if (this.onHover) this.onHover(null); });

      cv.addEventListener('wheel', e => {
        e.preventDefault();
        const f = Math.exp(e.deltaY * 0.0012);
        this.cam.dist = Math.max(this.minDist, Math.min(this.maxDist, this.cam.dist * f));
        this._dirty = true;
      }, { passive: false });

      cv.addEventListener('click', e => {
        if (this.onPick) this.onPick(this.pick(e));
      });
    },

    /* Arco de superficie, en grados, que corresponde a arrastrar `px` píxeles
       desde el centro de la vista.

       No es un factor a ojo: se despeja de la geometría para que el punto que
       agarras siga al cursor. Con la cámara a distancia d del centro y el punto
       en la superficie (radio 1), si α es el ángulo respecto al eje de vista, el
       teorema del seno da el arco desde el punto subcámara:

           θ = asin(d · sen α) − α

       Un ritmo constante de grados por píxel solo vale junto al centro. Según te
       alejas, la esfera se escorza y el mismo píxel abarca cada vez más arco;
       linealizarlo es lo que hacía que se disparase. */
    _arc(px) {
      const h = this.canvas.getBoundingClientRect().height || 1;
      const d = this.cam.dist;
      const sgn = px < 0 ? -1 : 1;

      /* s = d·sen α, topado un poco antes del limbo: justo en él dθ/dα se va a
         infinito y el globo pegaría un salto. */
      const conv = a => {
        const s = Math.min(0.95, d * Math.sin(a));
        return Math.asin(s) - Math.asin(s / d);
      };

      const a = Math.atan((Math.abs(px) / (h / 2)) * Math.tan(22.5 * D2R));
      const aMax = Math.asin(Math.min(1, 0.95 / d));
      if (a <= aMax) return sgn * conv(a) * R2D;

      // pasado el tope se continúa al ritmo que llevaba ahí, sin discontinuidad
      const eps = Math.max(1e-4, aMax * 0.02);
      const slope = (conv(aMax) - conv(aMax - eps)) / eps;
      return sgn * (conv(aMax) + (a - aMax) * slope) * R2D;
    },

    /* Rayo desde la cámara por el píxel -> primer corte con la esfera. */
    pick(ev) {
      const r = this.canvas.getBoundingClientRect();
      const x = ((ev.clientX - r.left) / r.width) * 2 - 1;
      const y = 1 - ((ev.clientY - r.top) / r.height) * 2;

      const eye = sph(this.cam.lat, this.cam.lon, this.cam.dist);
      const aspect = r.width / r.height;
      const t = Math.tan(45 * D2R / 2);

      // base de la cámara
      let fz = [-eye[0], -eye[1], -eye[2]];
      let l = Math.hypot(...fz); fz = fz.map(v => v / l);
      const upW = [0, 1, 0];
      /* Derecha de pantalla = adelante x arriba. Ojo con el orden: lookAt construye
         su eje X como (arriba x atras), y "atras" es el opuesto de "adelante", asi
         que calcularlo aqui como (arriba x adelante) da el vector cambiado de signo
         y deja el picking espejado en X respecto a lo que se dibuja. */
      let fx = [fz[1]*upW[2]-fz[2]*upW[1], fz[2]*upW[0]-fz[0]*upW[2], fz[0]*upW[1]-fz[1]*upW[0]];
      l = Math.hypot(...fx) || 1; fx = fx.map(v => v / l);
      const fy = [fx[1]*fz[2]-fx[2]*fz[1], fx[2]*fz[0]-fx[0]*fz[2], fx[0]*fz[1]-fx[1]*fz[0]];

      const dir = [
        fz[0] + fx[0]*x*t*aspect + fy[0]*y*t,
        fz[1] + fx[1]*x*t*aspect + fy[1]*y*t,
        fz[2] + fx[2]*x*t*aspect + fy[2]*y*t
      ];
      l = Math.hypot(...dir);
      const d = dir.map(v => v / l);

      const b = 2 * (eye[0]*d[0] + eye[1]*d[1] + eye[2]*d[2]);
      const c = eye[0]**2 + eye[1]**2 + eye[2]**2 - 1;
      const disc = b*b - 4*c;
      if (disc < 0) return null;                   // el rayo pasa de largo

      const s = (-b - Math.sqrt(disc)) / 2;
      if (s < 0) return null;
      const p = [eye[0] + d[0]*s, eye[1] + d[1]*s, eye[2] + d[2]*s];
      return {
        lat: Math.asin(Math.max(-1, Math.min(1, p[1]))) * R2D,
        lon: KM.geo.wrapLon(Math.atan2(p[0], p[2]) * R2D)
      };
    },

    /* ------------------------------------------------------------------ bucle */

    start() {
      if (this._raf) return;
      const loop = () => { this._raf = requestAnimationFrame(loop); this.render(); };
      this._raf = requestAnimationFrame(loop);
    },

    stop() {
      if (this._raf) cancelAnimationFrame(this._raf);
      this._raf = null;
    },

    resize() {
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      const w = Math.round(this.canvas.clientWidth * dpr);
      const h = Math.round(this.canvas.clientHeight * dpr);
      if (w && h && (this.canvas.width !== w || this.canvas.height !== h)) {
        this.canvas.width = w; this.canvas.height = h;
        this._dirty = true;
      }
    },

    render() {
      if (!this.ready) return;
      this.resize();
      if (!this._dirty) return;
      this._dirty = false;

      const gl = this.gl, o = this.opts;
      const w = this.canvas.width, h = this.canvas.height;
      if (!w || !h) return;

      gl.viewport(0, 0, w, h);
      gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

      const eye = sph(this.cam.lat, this.cam.lon, this.cam.dist);
      /* Planos de recorte según la distancia. Con el cercano fijo en 0,01 y la
         cámara a cientos de radios, el búfer de profundidad se queda sin precisión
         y las órbitas parpadean contra el planeta. */
      const near = Math.max(0.004, this.cam.dist * 0.004);
      const far = this.cam.dist + this.sceneR + 1;
      perspective(this.proj, 45 * D2R, w / h, near, far);
      lookAt(this.view, eye, [0, 0, 0], [0, 1, 0]);

      // luz fija en el espacio, para que haya terminador como en un planeta real
      const lightDir = sph(12, -40, 1);

      gl.bindVertexArray(this.vao);
      gl.useProgram(this.prog);
      gl.disable(gl.BLEND);
      gl.enable(gl.CULL_FACE);
      gl.cullFace(gl.BACK);
      gl.depthMask(true);

      const u = n => gl.getUniformLocation(this.prog, n);
      gl.uniformMatrix4fv(u('uProj'), false, this.proj);
      gl.uniformMatrix4fv(u('uView'), false, this.view);
      gl.uniform1f(u('uScale'), 1);
      gl.uniform1f(u('uRadius'), KM.BODY.radius);
      gl.uniform1f(u('uRelief'), this.tex.height ? o.relief : 0);
      gl.uniform1f(u('uHeightOff'), (o.heightOff || 0) / 360);
      gl.uniform1f(u('uHMin'), o.hMin);
      gl.uniform1f(u('uHMax'), o.hMax);
      gl.uniform2f(u('uHeightSize'),
                   this.tex.height ? this._hSize[0] : 1, this.tex.height ? this._hSize[1] : 1);
      gl.uniform1f(u('uColorOff'), (o.colorOff || 0) / 360);
      gl.uniform1f(u('uBiomeOff'), (o.biomeOff || 0) / 360);
      gl.uniform1f(u('uBiomeAmt'), this.tex.biome ? o.biomeAmt : 0);
      gl.uniform1i(u('uHasColor'), this.tex.color ? 1 : 0);
      gl.uniform1i(u('uHasBiome'), this.tex.biome ? 1 : 0);
      gl.uniform3f(u('uLightDir'), lightDir[0], lightDir[1], lightDir[2]);
      gl.uniform1f(u('uAmbient'), o.light ? 0.42 : 1.0);

      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, this.tex.color);
      gl.uniform1i(u('uColor'), 0);
      gl.activeTexture(gl.TEXTURE1);
      gl.bindTexture(gl.TEXTURE_2D, this.tex.biome);
      gl.uniform1i(u('uBiome'), 1);
      gl.activeTexture(gl.TEXTURE2);
      gl.bindTexture(gl.TEXTURE_2D, this.tex.height);
      gl.uniform1i(u('uHeight'), 2);

      gl.drawElements(gl.TRIANGLES, this.count, gl.UNSIGNED_INT, 0);

      this._drawTrack();
      this._drawOrbits();

      if (o.atmosphere) {
        const scale = (KM.BODY.radius + KM.BODY.atmosphere) / KM.BODY.radius;
        gl.useProgram(this.atmProg);
        const a = n => gl.getUniformLocation(this.atmProg, n);
        gl.uniformMatrix4fv(a('uProj'), false, this.proj);
        gl.uniformMatrix4fv(a('uView'), false, this.view);
        gl.uniform1f(a('uScale'), scale);
        gl.uniform1f(a('uRelief'), 0);
        gl.uniform1f(a('uAtmScale'), scale);
        gl.uniform3f(a('uCamPos'), eye[0], eye[1], eye[2]);
        gl.bindVertexArray(this.vao);
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.SRC_ALPHA, gl.ONE);      // aditivo: es luz dispersa
        gl.cullFace(gl.FRONT);                   // solo la cara de atrás, o tapa el planeta
        gl.depthMask(false);
        gl.drawElements(gl.TRIANGLES, this.count, gl.UNSIGNED_INT, 0);
        gl.depthMask(true);
        gl.cullFace(gl.BACK);
        gl.disable(gl.BLEND);
      }

      gl.bindVertexArray(null);
      this._placePins(eye);
    },

    /* Los pines son HTML encima del lienzo: nítidos a cualquier zoom y con el
       mismo aspecto que en el mapa 2D. */
    /* ¿Tapa el planeta el segmento cámara -> punto? Para puntos en la superficie
       basta el test del horizonte, pero un satélite a 700 km puede verse aunque
       su vertical esté tras el limbo, y taparse aunque esté delante si queda
       detrás del disco. Se lanza el rayo y se mira si toca la esfera antes. */
    _tapadoPorPlaneta(p, eye) {
      const dx = p[0]-eye[0], dy = p[1]-eye[1], dz = p[2]-eye[2];
      const L = Math.hypot(dx, dy, dz);
      const d = [dx/L, dy/L, dz/L];
      const b = 2 * (eye[0]*d[0] + eye[1]*d[1] + eye[2]*d[2]);
      const c = eye[0]**2 + eye[1]**2 + eye[2]**2 - 1;
      const disc = b*b - 4*c;
      if (disc < 0) return false;
      const s1 = (-b - Math.sqrt(disc)) / 2;
      return s1 > 0 && s1 < L;
    },

    _placePins(eye) {
      const rect = this.canvas.getBoundingClientRect();
      const camLen = Math.hypot(eye[0], eye[1], eye[2]);
      /* Coseno del ángulo a partir del cual un punto queda tras el horizonte. */
      const horizon = 1 / camLen;

      const colocados = [];

      for (let i = 0; i < this._markers.length; i++) {
        const m = this._markers[i], el = this._pins[i];
        if (m.hidden) { el.style.display = 'none'; continue; }
        const r = m.r || 1;
        const p = sph(m.lat, m.lon, r);
        let visible;
        if (r <= 1.0005) {
          const dot = (p[0]*eye[0] + p[1]*eye[1] + p[2]*eye[2]) / (camLen * r);
          if (dot <= horizon) { el.style.display = 'none'; continue; }
          /* Se desvanecen cerca del limbo, donde el terreno se ve de canto. */
          visible = Math.min(1, (dot - horizon) / 0.12);
        } else {
          if (this._tapadoPorPlaneta(p, eye)) { el.style.display = 'none'; continue; }
          visible = 1;
        }

        // proyectar a pantalla
        const v = this.view, pr = this.proj;
        const ex = v[0]*p[0] + v[4]*p[1] + v[8]*p[2] + v[12];
        const ey = v[1]*p[0] + v[5]*p[1] + v[9]*p[2] + v[13];
        const ez = v[2]*p[0] + v[6]*p[1] + v[10]*p[2] + v[14];
        const cw = -ez;
        if (cw <= 0) { el.style.display = 'none'; continue; }
        const sx = (pr[0]*ex / cw * 0.5 + 0.5) * rect.width;
        const sy = (-pr[5]*ey / cw * 0.5 + 0.5) * rect.height;

        el.style.display = '';
        el.style.left = sx + 'px';
        el.style.top = sy + 'px';
        el.style.opacity = visible.toFixed(2);

        /* De lejos, varios sitios caen en un puñado de píxeles y los rótulos se
           pisan hasta ser ilegibles. El punto se mantiene siempre —es la posición,
           que es el dato—, y el nombre se cede al primero que llegó. */
        const label = el.lastElementChild;
        const choca = colocados.some(c => Math.abs(c.x - sx) < 110 && Math.abs(c.y - sy) < 15);
        label.style.display = choca ? 'none' : '';
        if (!choca) colocados.push({ x: sx, y: sy });
      }
    }
  };
})();
