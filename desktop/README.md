# Koogle Kerbin

La versión nativa para Windows del visor de Kerbin. Hace lo mismo que la web
(mapa plano, globo 3D, biomas, alturas, herramientas, órbitas y naves de una
partida con su simulación en el tiempo), pero es un `.exe`: no hace falta
navegador ni servidor. Además tiene bastantes cosas que la web no: cualquier cuerpo
del sistema (de serie o de Kopernicus), día y noche con atmósfera física, la vista
del cielo desde la superficie, el foco de la cámara en una nave con su modelo
montado pieza a pieza, lo que la partida lleva escaneado con SCANsat, un filtro de
altimetría, waypoints que se escriben en la propia partida, un asistente de
aterrizaje y una calculadora de ventanas de lanzamiento.

Está escrita en **C# con .NET 10**. La interfaz usa WinForms con controles
dibujados a mano para copiar el tema oscuro de la web, y el mapa, el globo y el
cielo se pintan con **OpenGL 3.3** llamado directamente. No usa ningún paquete
externo: compila sin conexión.

*This document is in Spanish. English overview of the project:
[../README.md](../README.md).*

## Instalarla

Descarga `KoogleKerbin-Setup-<versión>.exe` de las *releases* del repositorio y
ábrelo. El asistente:

1. comprueba que el equipo tiene Windows de 64 bits y el **runtime de escritorio de
   .NET 10**; si falta, abre la página de descarga de Microsoft y detecta solo cuando
   lo has instalado;
2. instala para tu usuario, sin pedir administrador, en
   `%LOCALAPPDATA%\Programs\Koogle Kerbin` (o donde elijas), con accesos directos en
   el escritorio y el menú Inicio;
3. añade Koogle Kerbin a *Configuración › Aplicaciones* para desinstalarlo. Al
   desinstalar se borra solo lo que se instaló; tus ajustes y la partida guardada se
   conservan salvo que marques borrarlos.

Si ya estaba instalado, el asistente actualiza en la misma carpeta. Como el
instalador no está firmado, Windows SmartScreen puede avisar la primera vez: «Más
información › Ejecutar de todas formas».

Opciones para instalar sin ventanas: `/silent`, `/dir=<carpeta>`, `/noshortcuts`,
`/noregistry`; y `desinstalar.exe /uninstall /silent` para quitarlo.

## Usarla

Abre Koogle Kerbin desde su acceso directo, o `dist\KoogleKerbin\KoogleKerbin.exe` si
lo has compilado tú. Junto al `.exe` va la carpeta `data\`
con los mapas y catálogos, los mismos ficheros que usa la web: puedes cambiarlos
sin recompilar.

Requisitos del equipo:

- Windows 10 u 11 de 64 bits.
- El **runtime de escritorio de .NET 10** (Microsoft .NET Desktop Runtime 10).
- Una tarjeta gráfica con OpenGL 3.3, que es cualquiera de los últimos quince años.

También se le puede pasar un fichero al abrirla, por ejemplo arrastrando un
`persistent.sfs` o una imagen sobre el `.exe`, o desde la línea de órdenes:

```bash
KoogleKerbin.exe "C:\ruta\a\persistent.sfs"
```

Los ficheros también se pueden soltar directamente sobre el mapa: las partidas
(`.sfs`) cargan las naves y las imágenes van a su ranura según el nombre.

## Idioma

Español o inglés, en la sección «Idioma · Language» del panel lateral (la primera
vez, el de Windows). Cambiarlo reinicia el visor. Las traducciones están en
`data/i18n/en.json`, con el texto en español como clave: lo que falte se queda en
español, y se puede corregir sin recompilar. El instalador también tiene los dos
idiomas y deja el visor en el que se elija.

## Cuerpos celestes y sistemas personalizados

La sección «Cuerpo celeste», arriba del panel, elige el cuerpo que se ve. Al
cambiarlo pasan a ese cuerpo las naves de la partida, el Sol (con su latitud: la
órbita puede estar inclinada), la atmósfera, el día y la noche, la vista del cielo y
la ficha con sus datos.

- **Sistema de serie**: los 17 cuerpos de KSP, con los datos del juego (radio,
  gravedad, rotación, atmósfera, esfera de influencia y órbita), aunque no esté
  instalado. Cada uno con su color y su aire: Eve violeta, Duna rojizo, Jool verde.
- **Kopernicus** (OPM, RSS, SOL...): si la instalación de KSP lo usa, la lista sale de
  la caché de ModuleManager, ya con los parches del pack. Cada cuerpo hereda de la
  plantilla de serie que nombre y se le aplican sus `Properties`, `Orbit` y
  `Atmosphere`. La jerarquía (qué orbita a qué) y las esferas de influencia que no se
  indiquen se calculan.
- **La rotación** de cada cuerpo se mide con las naves de la partida que lo orbitan,
  como en Kerbin; sin ninguna, se usa la del juego.

Los mapas, biomas, alturas y marcadores que **trae** el visor son de Kerbin, pero los
demás cuerpos pueden tener los suyos: ver «Mapas de los demás cuerpos», más abajo. Sin
ellos, cada cuerpo se ve con su color y la retícula.

## Las tres vistas

Arriba a la izquierda, **2D**, **3D** y **Cielo**.

- **2D**: el mapa plano.
- **3D**: el globo. Al pinchar una nave, el foco de la cámara pasa del centro de
  Kerbin a la nave y la sigue: arrastrar gira alrededor de ella y la rueda se
  acerca o se aleja. Esc, F o pincharla otra vez devuelven el foco al planeta.
- **Cielo**: de pie en un punto de la superficie, mirando alrededor. Las naves
  cruzan el cielo con la barra de tiempo; sus órbitas se tapan con el horizonte.
  El punto se elige en la sección «Vista del cielo» (KSC, centro de la vista o un
  clic en el mapa) o con «Ver el cielo desde aquí» al pinchar en el mapa.

## Día y noche

Las tres vistas iluminan Kerbin con el Sol en el instante de la barra de tiempo:
en 2D, la mitad de noche sombreada y un disco en el punto subsolar; en 3D, el
terminador con el atardecer rojizo y la atmósfera apagándose en la cara de noche;
en el cielo, el disco del Sol, el cielo azul de día, el horizonte encendido al
atardecer y las estrellas de noche. El HUD da la hora solar local (el día de Kerbin
dura 6 h: las 3:00 son mediodía) y, en el cielo, la altura del Sol. La nave
enfocada se queda a oscuras cuando entra en la sombra del planeta.

De dónde sale: Kerbin gira alrededor de Kerbol en una órbita circular sin
inclinación, así que su longitud es la anomalía media y el Sol está en la opuesta.
Es el mismo marco inercial que usan las órbitas de las naves, y la rotación del
planeta es la que el visor mide con ellas, de modo que el Sol queda coherente con
las posiciones de las naves. Sin partida cargada se toma el comienzo del juego
(UT 0). Se desactiva con «Día y noche» en «Capas» o en «Vista 3D», y en el cielo
«Forzar la noche» lo deja de noche para seguir mejor las naves. Las estrellas son
decorativas, pero giran con Kerbin como lo harían las de verdad.

## Atmósfera y superficie

El globo y la vista del cielo usan el mismo modelo de luz, con base física:

- **Dispersión atmosférica** de Rayleigh (el azul del cielo y el rojo de los
  atardeceres) y de Mie (la bruma y el halo del Sol), integrada a lo largo de cada
  rayo sobre los 70 km de atmósfera de Kerbin. Desde órbita se ve el borde azul del
  planeta y la bruma que come contraste hacia el limbo; desde el suelo, el cielo
  degradado, el horizonte encendido al ponerse el Sol y el paso a espacio al subir.
- **La luz del Sol llega filtrada** por el aire que atraviesa: la profundidad óptica
  sale de la aproximación de Schüler a la función de Chapman, que da también la
  sombra del planeta con su penumbra.
- **Relieve por píxel** a partir del mapa de alturas: las laderas se sombrean con
  más detalle que la malla, aunque el relieve exagerado esté a cero.
- **Agua** reconocida por el color del mapa (o por la cota cero si no hay mapa de
  color), lisa, con Fresnel y el brillo del Sol reflejado.
- **Estrellas** de fondo también en el globo; un cielo luminoso las tapa.
- La luz se calcula en lineal y se lleva a pantalla con un tonemapping filmic
  (ACES), así que ni el cielo ni el reflejo del mar se queman.

Con «Día y noche» apagado se vuelve a la vista plana de siempre, y con «Atmósfera»
apagada se quita el aire (sin bruma ni cielo sobre el globo).

## Las naves con sus piezas

Al seguir una nave en el globo, si te acercas lo suficiente se ve la nave de
verdad, montada pieza a pieza con los modelos y texturas de **tu instalación de
KSP** (se leen en su sitio; no se copia nada). La rueda acerca hasta pocos metros
y el doble clic salta directamente a una distancia en la que se ve entera.

Cómo se monta, igual que hace el juego al cargarla:

- De la partida salen las piezas de cada nave, su posición y giro respecto a la
  pieza raíz, la orientación de la nave respecto a Kerbin y las variantes elegidas
  (`moduleVariantName` de las variantes de serie y `currentSubtype` de
  B9PartSwitch).
- De la caché de ModuleManager (`GameData/ModuleManager.ConfigCache`) sale qué
  modelos forma cada pieza ya con los parches de los mods aplicados (ReStock cambia
  los de serie, por ejemplo), con su escala y sus variantes. Sin ModuleManager se
  leen los `.cfg` tal cual.
- Los modelos `.mu` se leen con el formato que documenta io_object_mu, y las
  texturas DDS (DXT1/DXT5) se suben comprimidas tal cual a la GPU.

La carpeta de KSP se busca sola: la de la partida cargada o la de Steam por
defecto. Si está en otro sitio, «Carpeta de KSP…» en la sección de naves.

Comprobado con una partida de 196 naves y 928 piezas, con más de cien mods: el
catálogo se lee en menos de un segundo, 896 de las 928 piezas tienen malla y las 172
texturas que usan se leen todas. La orientación se contrastó con una nave posada:
su «arriba» cae a menos de 2° de la vertical del suelo en su posición.

Y del estado guardado de cada pieza:

- **Paneles, antenas, radiadores y demás desplegables**: la animación del modelo se
  evalúa en su punto (desplegada, plegada o a medias) y los paneles que siguen al
  Sol llevan el giro guardado. Las mallas que se doblan con huesos (paneles de
  lona, mástiles) se deforman con su esqueleto como en el juego.
- **Cubiertas de motor** (`ModuleJettison`): no salen si se desecharon o si no hay
  nada enganchado debajo.
- **Estructuras de interetapa** de las cofias (`ModuleStructuralNode`): solo si el
  juego las ha generado.
- **Placas de motores**: solo la malla del juego de nodos elegido.
- **Paracaídas** (de serie y RealChute de FAR): plegados solo se ve la tapa;
  abiertos, la campana en la pose de su animación; cortados, ninguna de las dos.
- Lo que el modelo marca como `Icon_Only` (la cofia de muestra del icono del
  editor) no se dibuja.

Lo que no se reproduce:

- **Piezas procedurales** (los paneles de las cofias, ProceduralParts) y
  **asteroides**: su forma se genera en el juego y aquí no aparecen. De una cofia
  se ve la base.
- **Recoloreados y cambios de textura** de TexturesUnlimited o de los subtipos de
  B9PartSwitch que solo cambian texturas: se ve la textura base del modelo.
- **La luz** es aproximada: el Sol del globo más una luz desde la cámara, sin
  brillos, mapas de normales ni sombras de unas piezas sobre otras.

## Mapas de los demás cuerpos

El visor solo trae mapas de Kerbin. Si tienes volcadas a PNG las texturas de KSP (con
cualquier extractor de assets), en «Cuerpo celeste» → **Carpeta de mapas…** se apunta a
esa carpeta y cada cuerpo se ve con su mapa de verdad, sus alturas y sus biomas si los
hay. Se reconocen por el nombre: `Duna_Color.png`, `Mun_Height.png`,
`Eeloo_Biomes.png`… (los de normales se ignoran, y entre dos del mismo tipo gana el de
más resolución).

**Esas texturas vienen en espejo horizontal y giradas 90° en longitud**, y el visor lo
corrige solo. No es una suposición: se cuadraron las texturas contra los mapas de
biomas de la wiki comparando siluetas y bordes. Kerbin encaja al 95,6 % con
espejo + 90° (el control entre los dos mapas que ya traía el visor da 95,9 %), y el
mismo par sale en Duna, Eve, Laythe, Moho y Dres. Si tu volcado viene de otra
herramienta, los dos ajustes (`BodyMapOffset` y `BodyMapMirror`) están en los ajustes.

La escala de gris a metros de cada cuerpo se saca de SCANsat: del propio guardado si lo
trae, y si no de `SCANsat/Resources/SCANcolors.cfg` de tu instalación.

## SCANsat, anomalías y modo progresión

La sección «SCANsat y progreso» lee de la partida lo que el juego ya sabe:

- **Cobertura de SCANsat** por cuerpo: lo que no has escaneado se tapa, en el mapa y en
  el globo. El mod la guarda como un `Int16[360,180]` comprimido con LZF dentro de una
  serialización de .NET; el descompresor de aquí se comprobó byte a byte contra el del
  propio mod (40 de 40 bloques idénticos) y el mapeo de celdas a lat/lon, con un
  guardado de cobertura parcial que sale como la banda ecuatorial que le corresponde.
- **Anomalías**: el catálogo (`data/anomalies.json`, 25 con coordenadas, del proyecto
  Kerbal Maps, Apache-2.0) se cruza con tu cobertura. Cada una sale como **sin
  detectar**, **detectada** (pasó el sensor de anomalías) o **identificada** (pasó
  también el de detalle), igual que en el juego. Se pueden mandar a la partida como
  waypoints.
- **Hitos** de `ProgressTracking`: qué cuerpos has alcanzado, sobrevolado, orbitado o
  pisado.

Con **modo progresión** el visor enseña solo lo descubierto: la cobertura tapa lo no
escaneado, las anomalías salen solo si las has detectado y los cuerpos sin visitar se
marcan en la lista. En sandbox se ve todo. Al cargar una partida que no sea sandbox, el
modo se propone solo.

## Filtro de altimetría

Se elige una franja de altura y el terreno que cae dentro se pinta con paleta de
altimetría (azul abajo, verde en las llanuras, blanco en las cumbres); el de fuera se
apaga. Va en las dos vistas y dice qué porcentaje de la superficie queda dentro,
pesando por `cos(lat)` (sin eso, los polos contarían muchísimo más de lo que ocupan).

Necesita mapa de alturas: el de Kerbin que trae el visor o el del cuerpo, de la carpeta
de mapas. La conversión de gris a metros es la de la ranura de altura, o la que SCANsat
tiene tabulada para ese cuerpo.

## Waypoints en la partida

Tus marcadores —y las anomalías— se pueden escribir **dentro de la partida**, en el
nodo `ScenarioCustomWaypoints`, que es de donde KSP saca los waypoints del mapa y del
navball: no hace falta ningún mod. También se traen de vuelta como marcadores.

Tocar un `.sfs` es cosa seria, así que antes de escribir se hace copia de seguridad al
lado del fichero (`partida.sfs.bak-<fecha>`), se escribe en un temporal y se sustituye
al final. Si el waypoint ya existía se reemplaza **conservando su `navigationId`**, para
no duplicarlo ni perder el destino que tuvieras puesto en el juego. Y hay que cerrar
KSP antes: con el juego abierto, la partida está en memoria y se sobrescribe al guardar.

## Aterrizaje desde órbita

Con una nave en órbita del cuerpo y un objetivo en el suelo, «Aterrizaje» busca **cuándo
frenar** y **cuánto**, con una sola quemada retrógrada, e integra el descenso con RK4:
gravedad siempre y arrastre con atmósfera exponencial si el cuerpo tiene aire. Dibuja la
traza en el mapa y en el globo, y da el instante de la frenada, el Δv, el periapsis
resultante, la entrada en atmósfera, el punto de contacto y a qué distancia queda del
objetivo.

Sin atmósfera el cálculo es exacto salvo por el relieve. El error que queda es el
**físico**: con una sola quemada retrógrada no se cambia de plano, así que si la órbita
no pasa por encima del objetivo, lo mejor posible es la distancia de la traza al punto.
Medido en Mun sobre una partida real: el plan da 7,55 km de error y la traza de esa
órbita pasa a 7,54 km del objetivo.

Con atmósfera es una estimación —el frenado depende de la nave, y con FAR o Kerbalism el
modelo del juego no es este—; el coeficiente balístico (masa entre Cd por área) es el
mando para ajustarla.

## Transferencias y ventanas de lanzamiento

Se elige destino y el visor busca las salidas más baratas a partir del instante de la
barra de tiempo: **cuándo salir**, Δv de eyección desde la órbita de aparcamiento, Δv de
captura, tiempo de vuelo, **ángulo de fase** entre los dos cuerpos y **ángulo de
eyección** respecto al prógrado. Pinchar una ventana lleva la barra de tiempo a ese día.

Es lo mismo que hace el juego: cónicas parcheadas. La transferencia se resuelve con
Lambert (variables universales, funciones de Stumpff y bisección en `z`) sobre una
rejilla de instantes de salida y tiempos de vuelo, así que es **balística**: una sola
quemada, sin correcciones a medio camino. Vale entre planetas (con la estrella de cuerpo
central) y hacia una luna del propio cuerpo.

Contrastado con las cifras conocidas del sistema de serie, desde 100 km de aparcamiento:
Kerbin → Duna 1089 m/s y 309 días de vuelo con fase de unos 44° (lo publicado ronda
1050 m/s), Kerbin → Mun 842 m/s, Kerbin → Minmus 913 m/s, Kerbin → Eve 1026 m/s. Cada
búsqueda tarda menos de un cuarto de segundo.

## Controles

| Acción | Cómo |
|---|---|
| Mover el mapa, girar el globo o mirar alrededor en el cielo | arrastrar |
| Zoom (en el cielo, el campo de visión) | rueda; doble clic acerca en 2D; `+`/`-` y flechas con la vista enfocada |
| Información de un punto | clic en el mapa |
| Seguir una nave | clic en ella (en el mapa, el globo o la lista) |
| Soltarla | Esc, F, o clic otra vez en ella |
| Datos de su órbita (altitud, velocidad, Ap/Pe y cuánto falta, periodo, elementos) | I o «Órbita» en la barra de tiempo |
| Pausa / continuar | espacio |
| Más rápido hacia delante / hacia atrás | `.` / `,` |
| Salir de una herramienta o de «Elegir en el mapa» | Esc |

## Dónde guarda las cosas

- `%APPDATA%\KoogleKerbin\`: `settings.json` (capas, vista, ventana, observador
  del cielo), `markers.json` (tus marcadores) y `biomes.json` (los nombres de
  bioma).
- `%LOCALAPPDATA%\KoogleKerbin\slots\`: una copia de las imágenes que cargas a
  mano, para que sigan ahí la próxima vez. Las de `data\` no se copian.
- `%LOCALAPPDATA%\KoogleKerbin\partida\persistent.sfs`: una copia de la última
  partida cargada. Al abrir el visor se carga sola, en el instante de la barra de
  tiempo en que se cerró; «Recargar del juego» vuelve a leer el original por si has
  jugado desde entonces.

Si quedan las carpetas `KerbinMaps` de cuando la app se llamaba así, se renombran
solas la primera vez. Borrar las carpetas deja la aplicación como recién instalada.

## Compilar

Con el SDK de .NET 10:

```bash
powershell -ExecutionPolicy Bypass -File publicar.ps1
```

Deja el resultado en `dist\KoogleKerbin`. El instalador se construye encima:

```bash
powershell -ExecutionPolicy Bypass -File installer\construir.ps1
```

Publica, mete `dist\KoogleKerbin` en un zip dentro del instalador y lo compila
(`installer\Instalador.cs`) contra .NET Framework 4.8 con el compilador de C# del SDK,
sin descargar nada: queda en `dist\KoogleKerbin-Setup-<versión>.exe`. Va sobre .NET
Framework, que ya trae Windows, para poder arrancar en un equipo sin .NET 10 y
avisar de que falta. Para depurar basta con
`dotnet build` y ejecutar lo que queda en `bin\Debug\net10.0-windows`; mientras
se desarrolla, la app encuentra `data\` subiendo carpetas hasta la raíz del
proyecto.

## Cómo está organizado

| Carpeta | Qué hay |
|---|---|
| `src\Core` | Lo que no depende de la pantalla: geodesia, Kepler, lectura de partidas y calibración de la rotación, imágenes (sonda, paleta de biomas, giro automático), catálogo y almacenamiento. Es la traducción directa de `geo.js`, `orbit.js`, `savefile.js`, `probe.js` y `storage.js`, más lo que la web no tiene: sistema solar (`SolarSystem.cs`), SCANsat (`ScanSat.cs`), el resto de la partida (`SaveExtras.cs`: hitos y waypoints), anomalías, mapas por cuerpo, descenso (`Landing.cs`) y transferencias (`Transfer.cs`, con Lambert). |
| `src\Gfx` | Enlaces a OpenGL, el control con el contexto (con antialias multimuestra), shaders, texturas, dibujo 2D por lotes y rótulos. |
| `src\Views` | El mapa plano, el globo (`GlobeView.cs`, con sus tres modos de cámara), el cielo (`GlobeView.Sky.cs`) y el modelo de la nave enfocada (`VesselModelRenderer.cs`, en metros y relativo a la cámara para que no tiemble). |
| `src\Ksp` | Lectura de la instalación de KSP: ConfigNode, modelos `.mu`, texturas DDS/TGA/PNG, catálogo de piezas y montaje de naves. |
| `src\UI` | La ventana, el panel lateral y los controles de tema oscuro. `MainForm` está repartida como `app.js`: mapas, naves, herramientas y cielo. |

Decisiones que no son evidentes:

- **Sin costuras ni teselas en el mapa plano.** Un shader calcula la latitud y
  longitud de cada píxel y lee la textura, con el desfase de longitud aplicado
  ahí. Las copias del mundo salen solas y las líneas se pintan continuas en vez
  de partirse por el antimeridiano.
- **El cielo es trazado de rayos, no una malla.** Desde unos metros de altura el
  búfer de profundidad no distingue el suelo de una órbita a cientos de
  kilómetros, y los triángulos de la esfera se verían en el horizonte. Cada píxel
  lanza un rayo contra la esfera: el horizonte sale exacto y las órbitas se tapan
  con el mismo corte, hecho por vértice.
- **Las transiciones entre modos interpolan el ojo por la esfera**, dirección y
  radio por separado, para que la cámara no atraviese el planeta.
- **Un clic en una nave la selecciona sin abrir además la información del punto**,
  que en la web salía a la vez.
- **Los nombres de bioma se ponen pinchando la fila de la leyenda**, en vez de
  escribirlos en una casilla dentro de la lista.
