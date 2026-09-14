# Koogle Kerbin

Visor de Kerbin (KSP stock) en dos versiones:

- **[Aplicación de escritorio para Windows](desktop/README.md)** (`desktop/`, C# y
  OpenGL): mapa plano, globo 3D, vista del cielo, día y noche, y las naves de una
  partida con sus modelos de verdad. El instalador se publicará en las *releases*.
- **Versión web** (el resto de este repositorio), que se describe a continuación.

Kerbal Space Program es de Squad y Take-Two Interactive. Este es un proyecto de
aficionados sin relación con ellos.

---

## Versión web: Kerbin Maps

Visor de superficie de Kerbin (KSP stock) para ejecutar en local, en la línea de
Kerbal Maps: un mapa deslizante con retícula, biomas superpuestos, marcadores,
regla y trazas terrestres de órbitas.

Todo corre en tu máquina. No hay servicios externos, ni telemetría, ni CDN: la
única dependencia es Leaflet, que está copiada en `vendor/leaflet/`.

---

## Arrancar

```bash
node server.mjs
```

Y abre <http://127.0.0.1:8080>. Para otro puerto: `node server.mjs 8099`.

Si no tienes Node, cualquier servidor estático vale; por ejemplo el de Python:

```bash
python -m http.server 8080 --bind 127.0.0.1
```

El servidor solo escucha en `127.0.0.1`, así que no queda expuesto a la red local.

Vale cualquier servidor estático; lo que **no** funciona es abrir `index.html`
con doble clic (`file://`), porque el navegador bloquea el `fetch` de
`data/landmarks.json` y la lectura de píxeles del canvas.

---

## Los mapas

En `data/` hay dos imágenes de Kerbin para que puedas probar el visor nada más
arrancarlo, sin tener que exportar nada:

| Fichero | Qué es | Desfase | Origen |
|---|---|---|---|
| `Kerbin_Color_HD.jpg` | color 4096×2048 | 0° | aportado, origen original desconocido |
| `Kerbin_Color.png` | color 2048×1024 | **90°** | wiki de la comunidad de KSP |
| `Kerbin_Biome.png` | biomas 1800×900 | 0° | wiki de la comunidad de KSP |
| `Kerbin_Height.png` | alturas 2048×1024 | 0° | export de SCANsat (gris −1500…6500 m) |

**No son mías ni del proyecto.** Derivan de las texturas del juego, propiedad de
Squad / Private Division, y la wiki no declara licencia. Están ahí solo para que
pruebes el visor en tu máquina, dando por hecho que tienes el juego. **No las
redistribuyas.** Si te sobran, borra los dos ficheros y `data/maps.json`: el
visor arranca igual y te recibe con la retícula de referencia.

La procedencia y los ajustes de cada una están escritos en `data/maps.json`.

### Mapas predeterminados

`data/maps.json` es un catálogo: cada entrada dice qué fichero va en cada ranura
y con qué desfase de longitud. El marcado como `predeterminado` se carga solo la
primera vez que abres el visor; los demás están en el desplegable **Mapa
predeterminado** del panel «Datos del mapa», a un clic.

Añadir el tuyo es meter una entrada más:

```json
{
  "id": "mio",
  "nombre": "Mi export de KittopiaTech",
  "color": { "file": "Kerbin_Color_HD.png", "lonOffset": "auto" },
  "biome": { "file": "Kerbin_Biome.png",    "lonOffset": 0 }
}
```

`lonOffset` admite un número de grados o `"auto"`. Con `"auto"`, al cargar el
preset el visor mide el giro solo comparando la silueta de los continentes contra
el mapa de biomas, y te dice cuánto ha girado y con qué confianza. Si el fichero
no está en `data/`, te lo dice por su nombre y deja lo que ya tuvieras cargado.

Si ya tienes algo guardado en el navegador, el preset predeterminado no se
carga: manda lo tuyo.

### Cómo meter tus mapas

Dos vías, la que te resulte más cómoda:

1. **Arrastrar y soltar** el PNG sobre el panel «Datos del mapa». Se guarda en
   IndexedDB del navegador y sigue ahí la próxima vez que abras la página.
2. **Dejar el fichero en `data/`** con uno de estos nombres y recargar:
   `Kerbin_Color.png`, `Kerbin_Biome.png`, `Kerbin_Height.png`. Se cargan solos.

El requisito es que la imagen sea **equirectangular y de proporción 2:1**, con
longitud −180° a la izquierda y +180° a la derecha, y latitud +90° arriba. Es el
formato en el que KSP guarda sus texturas de cuerpos, así que normalmente no hay
que reproyectar nada. Si cargas algo que no sea 2:1 el visor te avisa.

La resolución da igual: 720×360 y 8192×4096 funcionan las dos.

### Cuando el mapa no cae donde debe

No todos los mapas que circulan usan el mismo meridiano de origen que KSP. El de
color que viene incluido, sin ir más lejos, está girado 90°: tal cual se descarga,
el KSC cae en mitad de un desierto en vez de en su costa.

Cada ranura tiene una casilla de grados que gira el mapa en longitud. Y si tienes
cargados color y bioma a la vez, el botón **«Alinear color con el bioma»** calcula
el giro solo: compara la silueta de los continentes de los dos mapas y se queda con
el ángulo que mejor encaja. Funciona aunque las paletas no se parezcan en nada,
porque compara formas de costa, no colores. Si el mejor encaje no destaca sobre el
promedio, en vez de girar a ciegas te avisa de que esos dos mapas no casan.

Ojo con una trampa: **el desfase es de la imagen, no de la ranura.** Si cargas un
mapa nuevo encima de otro, el visor reinicia el desfase a 0 en vez de arrastrar el
del anterior, y si hay un mapa de biomas cargado aprovecha para medir el del nuevo
ahí mismo y decírtelo. Heredar el giro del mapa anterior pinta el nuevo torcido sin
motivo aparente, que es de lo más desconcertante.

Los dos mapas de color incluidos son un buen ejemplo de que esto no es teórico: el
de 4096 está alineado (máximo en 0°, 95,9%; los ángulos vecinos bajan) y el de la
wiki necesita 90° (94,7% girando 90°, entre 44% y 58% con cualquier otro ángulo).

### Biomas — la vía práctica

**El mapa de biomas de SCANsat (720×360) vale tal cual.** Es 2:1, y aunque un
píxel sea medio grado (unos 5 km en el ecuador), los biomas son regiones planas,
así que no se pierde nada. El visor lo pinta **sin interpolar**: un color
promediado entre dos biomas no es ningún bioma, y además rompería la sonda.

Los biomas van como **capa superpuesta**, con su propia opacidad, encima del
mapa de color. Es como se miran de verdad: qué bioma cae sobre qué terreno.

Al cargarlo, el panel «Biomas» lee la leyenda entera: cada color con el
porcentaje de superficie de Kerbin que ocupa. El porcentaje está ponderado por
`cos(lat)`, porque en equirectangular una fila de píxeles junto al polo
representa muchísima menos superficie que una del ecuador; sin esa corrección
los casquetes saldrían tres veces más grandes de lo que son.

Los nombres los pones tú, en la propia leyenda. La paleta cambia entre versiones
y exportadores, así que el visor no adivina ninguno. Se guardan en el navegador
y puedes exportarlos a JSON para reutilizarlos.

### Altura

**El terreno de Kerbin stock es procedural.** Lo genera el PQS con ruido en
tiempo de ejecución; no existe ninguna textura de alturas dentro de los archivos
del juego que puedas extraer. Por eso la altura está en un panel aparte marcado
como opcional. Hay dos formas de conseguir un heightmap de verdad.

#### La buena: SCANsat en escala de grises

Es la única vía que da datos actuales, a resolución decente y con escala
conocida. SCANsat ya guarda la altimetría; solo hay que hacer que la pinte de
forma que se pueda leer como número:

1. Escanea Kerbin con un instrumento de altimetría (el SAR da más resolución).
2. Abre el **big map** y ponlo en **Altimetry**.
3. En el desplegable de proyección elige **Rectangular**. Las otras
   (KavrayskiyVII, Polar) no son equirectangulares y no encajarán.
4. Abre la ventana de **color management** (el icono de paleta) y en la pestaña
   de terreno:
   - elige la paleta en **escala de grises**;
   - déjala en **gradiente suave**, no en *discrete*, o saldrán escalones;
   - **desactiva Clamp**. Si está activo, todo lo que quede por debajo del corte
     se pinta con los dos primeros colores de la paleta y el gris deja de ser
     proporcional a la altura, que es justo lo que necesitas que sea.
5. **Apunta los valores de los deslizadores Min y Max.** Son los cortes que usa
   SCANsat para convertir altura en color, así que son exactamente los dos
   números que pide el visor.
6. Exporta con el icono de cámara.

En el visor: carga el PNG en la ranura **Altura** y escribe en el panel
`gris 0 = Min` y `gris 255 = Max`. Sin calibrar nada, porque la escala ya la
sabes. Mejor aún: apúntalos en `data/maps.json` dentro del preset y se aplican
solos al cargar.

```json
"height": { "file": "Kerbin_Height.png", "lonOffset": 0, "hMin": -1000, "hMax": 6800 }
```

El heightmap incluido sale de SCANsat con `TrueGreyScale`. Su rango no está
estimado: `GameData/SCANsat/Resources/SCANcolors.cfg` guarda por cuerpo
`minHeightRange` y `maxHeightRange`, y para Kerbin son −1500 y 6500. Mirar ahí es
más fiable que apuntar los deslizadores a ojo.

Comprobado contra datos independientes: el KSC lee 37 m (su altitud real ronda los
70, y cada píxel cubre 1,8 km), la alineación sale 0° con 96,6% de coincidencia
contra el mapa de biomas, y el océano da entre −1090 y −935 m, o sea batimetría de
verdad y no un plano.

**Aviso sobre los polos.** El 14,7% de la imagen es gris 48, que con ese rango es
exactamente 0 m, y el 98,4% de esos píxeles está por encima de |lat| 60°. Es zona
sin escanear rellenada a 0 m, no terreno. Por debajo de esa latitud el mapa está
prácticamente completo.

**No vale el mapa de altimetría en color.** Es el mismo dato, pero la sonda
traduce luminancia a metros, y en esas paletas la luminancia no crece con la
altitud: el amarillo de media ladera brilla más que el rojo de la cumbre, así que
las cimas saldrían hundidas. El visor mide la saturación de lo que cargas en la
ranura de Altura y te avisa si le has metido una paleta en vez de un gris.

Si algún día no sabes de dónde salió un gris, la calibración de dos puntos del
panel sigue ahí: pulsa «Punto A», haz clic en un sitio del que sepas la altitud
real, escríbela, repite con B en otro de altitud bien distinta y el rango se
ajusta solo. Funciona también si el mapa está invertido (oscuro = alto).

#### La otra: volcar el PQS

KittopiaTech o el editor en juego de Kopernicus pueden muestrear el PQS y sacar
un heightmap. Da más resolución que SCANsat y no depende de haber escaneado
nada, pero el gris no viene con escala: ahí sí toca calibrar a dos puntos.

#### La que no vale: la figura de la wiki

En la wiki hay un `Kerbin heightmap.jpg`. No es un gris, es una figura coloreada
por bandas con leyenda y ejes dibujados encima, de KSP 0.18.2.

Se puede decodificar —`tools/decode-banded-map.html` lo hace, y clasifica el
96,8% de los píxeles contra las 13 bandas de su leyenda—, pero el resultado
**no es fiable donde más te importa**:

- Son **12 escalones**, no una superficie. Los cortes están en −1000, −500, 0,
  100, 200, 500, 1000, 1500, 2000, 2500 y 3000 m.
- La geografía global sí sigue valiendo: coincide un **94,6%** con el mapa de
  biomas actual, sin girar. Los continentes de 2012 son los de hoy.
- Pero **la costa del KSC no coincide**. En esa figura el KSC cae casi un grado
  mar adentro, unos 90 km. Baikerbanur, Woomerang y el Dessert Site, que están
  tierra adentro, sí salen bien; los puntos costeros, no.

Sirve para «¿esta región es tierra alta o baja?». No sirve para la altitud de un
sitio concreto, y menos junto al mar. Por eso **no viene precargado**: tener el
visor diciendo «alt +217 m» en el KSC con esos datos sería peor que no decir nada.

La herramienta se queda porque funciona con cualquier figura por bandas que
tenga leyenda, no solo con esa.

### Color

Cualquier mapa equirectangular de Kerbin que tengas o generes sirve tal cual.
SCANsat también puede exportar uno, y la función de exportar mapas de
KittopiaTech / el editor en juego de Kopernicus produce color de un tirón. No te
doy la ruta exacta de menús de cada mod porque cambia entre versiones; mira la
documentación del que uses.

### Comprobar que encaja

Genera las imágenes de prueba:

```bash
node tools/make-test-map.mjs
```

```bash
node tools/make-test-map.mjs --biome
```

La primera escribe `data/test-equirectangular.png` (2048×1024) con el ecuador en
verde, el meridiano 0 en rojo y una cruz amarilla sobre las coordenadas del KSC:
si el marcador azul del KSC cae justo sobre la cruz, la proyección está bien.

La segunda escribe `data/test-biome-720x360.png`, con colores planos, para
probar la leyenda y los porcentajes con la misma resolución que da SCANsat.

Y para la cadena de alturas:

```bash
node tools/make-test-map.mjs --height
```

Escribe `data/test-height-ramp.png`, un gris con una rampa lineal en longitud:
`gris = round(255·(lon+180)/360)`. Como la altitud que debe salir en cada punto se
calcula a mano, sirve para comprobar que la sonda y el rango gris→metros están
bien antes de meter un export de verdad. Un terreno de aspecto realista quedaría
más bonito pero no permitiría verificar nada.

---

## Qué trae

**Mapa base.** Retícula de referencia, tu imagen de color, tu mapa de alturas,
teselas locales en `tiles/`, o una plantilla XYZ propia. Con control de opacidad
y desfase de longitud por capa, con detección automática del giro.

**Mapas predeterminados.** Catálogo en `data/maps.json` con el desfase de cada
uno ya resuelto (o medido al vuelo). Cambiar de mapa es un clic.

**Vista 3D.** El botón «Ver en 3D» de la barra superior pasa a un globo: mismas
texturas, mismos biomas superpuestos, mismos marcadores y la traza orbital, con
su huella en el suelo y su camino a la altitud real. Arrastra para girar, rueda
para acercarte. Al volver a 2D conserva el punto que estabas mirando.

**Biomas superpuestos.** Capa encima del relieve con su propia opacidad, pintada
sin interpolar, y leyenda con el reparto de superficie. Ver arriba.

**Retícula.** Paralelos y meridianos con paso adaptativo al zoom, rotulados una
sola vez en el borde.

**Coordenadas.** Latitud y longitud bajo el cursor, en el mismo convenio que usa
KSP (N positivo, E positivo). Al hacer clic, un globo con las coordenadas en
decimal, listas para copiar, y un botón para dejar ahí un marcador.

**Sonda.** Bajo el cursor, el bioma (por su color, con el nombre que le hayas
puesto) y, si tienes heightmap calibrado, la altitud.

**Regla.** Encadena puntos y mide sobre el gran círculo, con el rumbo inicial de
cada tramo y el total acumulado. Importante: en una proyección equirectangular la
recta entre dos puntos **no** es el camino más corto, y la regla lo tiene en
cuenta.

**Huella / horizonte.** Dada una altitud, dibuja hasta dónde llega la línea de
visión desde ahí. Sirve para colocar relés de comunicación o planear cobertura
de escaneo.

**Traza terrestre.** Introduce periapsis, apoapsis, inclinación, LAN y argumento
del periapsis, y dibuja por dónde pasa la nave sobre el suelo, teniendo en cuenta
la rotación de Kerbin. Devuelve periodo, semieje, excentricidad, velocidad en
periapsis y apoapsis, deriva de longitud por vuelta y latitud máxima alcanzada.
Avisa si el periapsis entra en atmósfera o bajo el nivel del mar, y detecta las
órbitas síncronas.

**Naves de una partida.** Arrastras un `persistent.sfs` y aparecen tus naves en
órbita de Kerbin, en 2D y en 3D (en el globo, a su altitud real). Una barra de
tiempo las mueve hacia delante y hacia atrás con los escalones de aceleración de
KSP, con pausa. Puedes dibujar las órbitas de todas a la vez y hacer que la cámara
siga a una. Ver más abajo.

**Marcadores.** Vienen seis puntos de referencia (KSC, pista, Island Airfield,
Baikerbanur, Woomerang, Dessert). Puedes añadir los tuyos, exportarlos a JSON e
importarlos.

**Búsqueda.** Por nombre de marcador o por coordenadas: `-0.0972, -74.5577` y
`0.0972 S 74.5577 W` valen las dos.

---

## Sobre las coordenadas que incluye

Los seis marcadores llevan un campo `confianza`:

- `alta` — coordenada muy citada y estable entre versiones. Solo el KSC.
- `media` — aproximada. Te deja el punto a la vista, pero no la uses para
  aterrizar a ciegas.

**Las anomalías no están.** Monolitos, pirámides, cráter, restos… no vienen
incluidos, y es deliberado: no me sé sus coordenadas con precisión suficiente y
poner números inventados es peor que no poner nada. Añádelas tú conforme las
encuentres, o pega tu propia lista en `data/landmarks.json` siguiendo el mismo
formato.

---

## Cómo funciona por dentro

**La proyección.** El mapa usa plate carrée (equirectangular), que es la
proyección en la que KSP guarda las texturas de sus cuerpos: longitud y latitud
se convierten directamente en X e Y. Leaflet ya la trae como `CRS.EPSG4326`; lo
único que se cambia es el radio, de los 6371 km de la Tierra a los 600 km de
Kerbin, para que la escala y las distancias salgan bien (`js/geo.js`).

**Las teselas.** Cargar una imagen de 8192×4096 como un solo elemento se
atraganta al ampliar. En vez de obligarte a trocear la textura en miles de
ficheros, `KM.ImageLayer` hereda de `L.GridLayer` y pinta cada tesela recortando
el trozo que toca de un `ImageBitmap` en memoria. Se comporta como un servidor de
teselas sin serlo (`js/layers.js`).

El mapa de color se interpola al ampliar; los de biomas y altura **no**, porque
ahí el valor exacto del píxel es el dato.

**La órbita.** KSP usa cónicas parcheadas: dentro de la esfera de influencia de
Kerbin la órbita es una elipse kepleriana exacta, sin achatamiento ni J2 que la
perturben. Así que basta resolver la ecuación de Kepler por Newton-Raphson,
convertir a coordenadas inerciales y restar la rotación del planeta
(`js/orbit.js`). Las trazas que salen son las del juego, no una aproximación.

**Constantes de Kerbin** (`js/config.js`): radio 600 km, μ = 3,5316×10¹² m³/s²,
día sidéreo 21 549,425 s, día solar 6 h exactas, atmósfera hasta 70 km,
SOI 84 159 286 m.

---

## Estructura

```
index.html            interfaz
css/app.css           estilos
js/config.js          constantes de Kerbin y capas disponibles
js/geo.js             CRS de Kerbin y geodesia (gran círculo, rumbos, antimeridiano)
js/storage.js         IndexedDB para imágenes, localStorage para ajustes
js/layers.js          capa desde imagen, retícula, etiquetas de borde, XYZ
js/globe.js           vista 3D en WebGL2 (esfera, atmósfera, pines, picking)
js/probe.js           sonda de píxel y extracción de la leyenda de biomas
js/orbit.js           propagación kepleriana y traza terrestre
js/savefile.js        lector de .sfs y calibración de la rotación con las naves
js/markers.js         marcadores de referencia y propios
js/tools.js           regla y huella
js/app.js             cableado de la interfaz
data/landmarks.json   puntos de referencia
data/maps.json        qué fichero va en cada ranura, su desfase y su procedencia
data/                 aquí van tus PNG del mapa
tiles/                teselas ya troceadas (opcional; ver tiles/LEEME.txt)
tools/png.mjs         codificador PNG sin dependencias
tools/make-test-map.mjs  genera imágenes de prueba (color y biomas)
tools/decode-banded-map.html  figura de elevación por bandas -> heightmap en grises
server.mjs            servidor estático sin dependencias
vendor/leaflet/       Leaflet 1.9.4
```

---

## Naves de una partida

Panel «Naves de una partida»: arrastra el `persistent.sfs` de
`saves/<tu partida>/`. Se lee en el navegador, no sale de tu máquina, y un
fichero de 6 MB tarda unos 200 ms.

Se dibujan las naves que orbitan Kerbin; las de otros cuerpos se cuentan pero no
se pintan, porque su latitud y longitud son de otro sitio. Los escombros vienen
ocultos por defecto: suelen ser la mitad de la lista. Pincha una nave para ver su
traza y sus datos (Pe, Ap, inclinación, excentricidad, periodo).

La traza del globo es una sola: si pinchas una nave sustituye a la órbita que
hubieras dibujado a mano en su panel, y al revés. Manda la última que pediste.

### El problema de la longitud, y cómo se resolvió

De cada nave el save guarda sus elementos orbitales (`SMA`, `ECC`, `INC`, `LPE`,
`LAN`, `MNA`) referidos a una época `EPH`. Con eso la forma de la órbita, su
periodo y su inclinación salen exactos. Pero para saber **sobre qué punto del
suelo** está hace falta el ángulo que ha girado Kerbin en ese instante, y eso el
save no lo guarda.

Usar una constante no vale. Tras cientos de miles de vueltas, **7·10⁻⁵ s de error
en el periodo ya desplazan un grado**, y el periodo tabulado da unos 2,6° de error
medio en una partida larga.

Lo que sí guarda el save, para cada nave, es su latitud y longitud en el momento
de su época. Con eso se mide la rotación directamente:

1. **El norte es el eje Z.** Comprobado con las 107 naves en órbita de una
   partida real: error mediano de latitud 0,2° con Z, 43° con Y. La latitud no
   depende de la rotación, así que esta prueba es independiente de todo lo demás.
2. **La lat/lon del save es la de la época EPH, no la del momento de guardar.**
   Mediana 0,2° contra 1,2°.
3. Cada nave cuya latitud calculada cuadra con la guardada (a 0,01°) da la
   rotación en su época: longitud inercial menos longitud del mapa. Se llevan
   todas al instante del save con el día sidéreo y se promedian **recortando
   las incoherentes**: media circular, se descarta lo que se aleje más de 5 veces
   la dispersión mediana (con un suelo de 2°), y se recalcula. El panel enseña
   con cuántas naves se ha medido, su dispersión mediana y cuántas descartó.
4. Hay un **ajuste de longitud** manual por si una partida trae pocas naves
   aprovechables. Normalmente no hace falta tocarlo.

Por qué la rotación sidérea y no la solar: con el día sidéreo las medidas de las
distintas naves se concentran (R = 0,67 sin filtrar), con el solar se dispersan
(R = 0,27).

**El método da la misma constante en partidas distintas.** La rotación en el
instante del save cambia con cada guardado, pero descontando el UT tiene que salir
siempre el mismo origen. Con dos guardados de la misma partida separados por 31
días de Kerbin: 87,364° y 87,365°. Si ese origen es en realidad 90° con un periodo
2·10⁻⁴ s más corto que el tabulado no se puede distinguir, porque el valor
publicado no tiene tanta precisión. Al visor le da igual: calibra cada partida por
separado.

**Verificación con grupo de control.** Se calibró con la mitad de las naves y se
predijo la longitud de la otra mitad, que no participó en la calibración. Con las
dos particiones: error mediano **0,02°** en la peor (unos 2 km) y 0,0004° en la
mejor. Calibrar y comprobar con las mismas naves no habría demostrado nada. El
código del visor se ejecutó tal cual en Node sobre la partida y en el navegador,
y los dos dan el mismo ángulo.

**Naves incoherentes.** En esa partida hay dos: una sale 43,5° desviada y un
escombro 2,9°. Lo más probable es que su lat/lon sea de otro instante y hayan
pasado el filtro de latitud por casualidad. Por eso la calibración recorta: sin
recortar, la de 43,5° arrastraba el resultado casi un grado (0,996°), y el
escombro fue el que estropeó la primera validación, que dio 0,18° en vez de 0,02°.

Límites: solo órbitas cerradas (se descartan las hiperbólicas y las radiales
degeneradas que KSP asigna a las naves posadas), solo el cuerpo del visor, y no se
conecta al juego en vivo: todo se simula a partir del guardado.

### Simulación en el tiempo

Al cargar una partida aparece abajo una barra de tiempo. El reloj arranca en el
instante del guardado y en pausa.

| Control | Atajo | Qué hace |
|---|---|---|
| ▶ / ❚❚ | espacio | continuar / pausar |
| ▶▶ | `.` | un escalón más rápido hacia delante |
| ◀◀ | `,` | un escalón más hacia atrás |
| Guardado | | vuelve al instante de la partida |
| Seguir | F | la cámara no pierde a la nave seleccionada |

Los escalones son los de la aceleración de tiempo de KSP (×1, 5, 10, 50, 100, 1000,
10 000 y 100 000) y continúan en negativo en una sola escala: ◀◀ baja un escalón
cada vez (×100 000 … ×5, ×1) y luego pasa a ×−1, ×−5 … ×−100 000, y ▶▶ recorre el
camino inverso, sin saltos. Cambiar la velocidad arranca el reloj, como en el
juego. Los atajos no actúan mientras escribes en un campo.

**Es Kepler puro desde el estado guardado.** Hacia atrás ves dónde *habría estado*
cada nave según su órbita actual: sin maniobras, sin lanzamientos posteriores y sin
frenado atmosférico. Una nave cuya órbita corta el suelo se oculta mientras está
por debajo.

**Todas las órbitas.** La casilla del panel dibuja las de todas las naves visibles.
En 2D son trazas terrestres desde el instante simulado (hasta 3 vueltas, en un
canvas, porque decenas de líneas redibujándose varias veces por segundo saturan el
SVG de Leaflet). En 3D son anillos cerrados: una órbita kepleriana es una elipse
fija en el espacio y lo que se mueve es Kerbin debajo, así que cada anillo se
construye una vez y en cada fotograma solo se gira en el shader. La nave
seleccionada lleva además su huella en el suelo.

**Seguir.** En 3D la cámara se coloca sobre la nave mirando al centro de Kerbin: la
nave queda en el centro de la pantalla con el suelo pasando por debajo, y no deja
acercarse por dentro de su órbita. En 2D el mapa se recentra sobre ella. Arrastrar
lo cancela.

**Zoom.** El alejamiento ya no está topado en 12 radios: llega a tres veces la
órbita más lejana de la partida. Los planos de recorte dependen de la distancia;
con el cercano fijo, a cientos de radios el búfer de profundidad se queda sin
precisión y las órbitas parpadean contra el planeta.

**El reloj no pierde tiempo con pocos fotogramas.** Si la pestaña pasa a segundo
plano, el hueco se descarta al volver, para no saltar horas de golpe. Pero el tope
no puede ser por fotograma: una primera versión cortaba cada paso a 0,25 s, y con
una escena pesada a 3 fotogramas por segundo el ×1 simulaba 0,28 s por segundo real.

**Verificado:**

- Cada nave va sobre su anillo girado en instantes de −5000 s a +250 000 s.
- Sobre el propio render (leyendo los píxeles), con la nave de mayor inclinación
  (89,83°) y los dos sentidos de giro separados 60°: hay color del anillo junto a
  la nave con el giro correcto (34 píxeles) y ninguno con el giro invertido ni sin
  giro. Con una órbita casi ecuatorial esta prueba no discrimina: un anillo
  ecuatorial girado sobre el polo cae sobre sí mismo.
- Los marcadores coinciden exactamente con Kepler en el instante simulado, la
  escala de velocidades recorre y satura como se describe arriba, la pausa congela
  el reloj y «Guardado» vuelve exacto al instante de la partida.
- Seguir deja la cámara exactamente sobre la nave en 3D, y en 2D con el redondeo a
  píxel de Leaflet. Arrastrar lo cancela en los dos modos.
- Con 62 naves, el alejamiento llega a los 263,5 radios que pide la órbita más
  lejana.
- Reloj: midiendo cada fotograma contra su propia marca de tiempo, el tiempo
  simulado avanza exactamente a la velocidad elegida (×1, ×1000, ×−1000 y
  ×100 000; error por fotograma de 2 partes por millón como mucho), aun con la
  ventana limitada a 2–4 fotogramas por segundo.

## La vista 3D

WebGL directo, sin motor 3D de por medio (`js/globe.js`). Para lo que hace falta
aquí —una esfera con la textura equirectangular pegada— traerse una librería de
600 KB no compensa: ese es justo el mapeo UV natural de una esfera.

Detalles que no son evidentes y que costó afinar:

- **La costura del meridiano.** Los vértices de la columna donde `u` pasa de 1 a 0
  están duplicados; si se cosen, la interpolación va 1 → 0 dentro de un triángulo
  y sale una franja con la textura entera comprimida. Y como el desfase de
  longitud mueve `u` fuera de [0,1], se envuelve con `fract()` pero las derivadas
  se pasan sin envolver a `textureGrad`: con `texture()` normal, el mipmap ve una
  derivada enorme justo en la costura, elige el nivel más borroso y deja una línea.
- **Requiere WebGL2**, por lo anterior y porque las texturas de biomas no son
  potencia de dos (1800×900). Si el navegador no lo trae, te lo dice y se queda
  en 2D.
- **Los índices de atributo se fijan a mano** antes de enlazar: el planeta y la
  atmósfera comparten el mismo VAO, y si cada programa los numerase a su manera,
  uno de los dos leería basura.
- **La base de cámara del picking tiene que coincidir con la del renderizado.**
  `lookAt` construye su eje X como `arriba × atrás`, y es fácil calcularlo en el
  picking como `arriba × adelante`, que es el mismo vector cambiado de signo: el
  ratón queda espejado en X respecto a lo que se ve. El fallo es traicionero
  porque el picking sigue siendo coherente consigo mismo, así que cualquier
  prueba que compare `pick` contra `pick` lo da por bueno. La única que lo caza es
  comparar el píxel **dibujado** con el color que la textura tiene en la
  coordenada que devuelve `pick`: espejado da error medio 100, correcto da 3.
- **El arrastre agarra la superficie.** Los grados que gira cada píxel no son un
  factor a ojo: se despejan de la geometría, `θ = asin(d·sen α) − α`, para que el
  punto que agarras siga al cursor. Un ritmo constante solo vale junto al centro;
  según te alejas la esfera se escorza, el mismo píxel abarca cada vez más arco, y
  linealizarlo es lo que hace que el globo se dispare. Verificado midiendo: el
  punto se desvía menos de 31 km sobre 3770 de circunferencia.
- **Los marcadores son HTML** sobre el lienzo, no geometría: nítidos a cualquier
  zoom y con el mismo aspecto que en 2D. Se ocultan solos al pasar tras el
  horizonte, y cuando varios se amontonan solo el primero conserva el nombre —el
  punto se mantiene siempre, porque la posición es el dato.
- **El relieve desplaza vértices y recalcula la normal.** Lo primero es obvio; lo
  segundo no, y es lo que decide si se ve algo: con la normal de la esfera la luz
  no se entera de que hay montañas y el relieve solo se distingue en la silueta
  del limbo. Se muestrean dos vecinos a un par de téxeles del heightmap y se hace
  el producto vectorial de las tangentes. Lleva exageración porque a escala real
  el pico más alto de Kerbin es un 1% del radio y no se notaría.

### La traza orbital sobre el globo

Al dibujar una órbita se pintan dos cosas a partir de los mismos puntos: la
**huella sobre el suelo** y el **camino a su altitud real**, que se aleja del
planeta hasta el apoapsis y vuelve.

Las dos van en el marco fijo al cuerpo, igual que la textura del globo. Por eso
el camino en altura **no se cierra en una elipse**: es la trayectoria vista desde
el planeta que gira, y ese desmadejarse hacia el oeste es exactamente la deriva
que hace que cada vuelta pase por un sitio distinto. El número que da el panel
como «deriva por vuelta» es esa misma cosa, medida.

Dos detalles:

- **Aquí no hay que partir nada por el antimeridiano.** Sobre una esfera la línea
  es continua, así que todo el troceo que en 2D es obligatorio simplemente
  desaparece. Es de las pocas cosas que salen más fáciles en 3D.
- **La huella se levanta 0,0016 del suelo.** La esfera es un poliedro inscrito:
  entre vértices su superficie queda por debajo de r=1, y una línea pegada a r=1
  se hundiría a trozos. Con el relieve activado la línea se desplaza igual que el
  terreno, para que no quede flotando.

Lo que **no** hace: no hay terreno 3D real (solo desplazamiento de vértices desde
el heightmap, si lo tienes) ni retícula lat/lon en 3D.

---

## Límites conocidos

- **Solo Kerbin.** Añadir Mun, Minmus o el resto de cuerpos es cuestión de meter
  sus parámetros en `js/config.js` y un selector; no está hecho.
- **La altura depende de que consigas un heightmap.** No hay ninguno dentro del
  juego: la vía practicable es exportarlo con SCANsat en escala de grises. Ver arriba.
- **Sin sombreado de relieve.**
- **Las huellas que rodean un polo** se dibujan de forma tosca: el círculo se
  parte en el antimeridiano y cerca del polo eso se nota.
- **Sin vista 3D.** Es un mapa plano deslizante, no un globo.
