# Koogle Kerbin

Visor de los mundos de Kerbal Space Program: una **aplicación de escritorio para
Windows** (`desktop/`, C# y OpenGL) con mapa plano, globo 3D, vista del cielo y vuelo
libre a ras de suelo con el relieve y las texturas del juego (las de Parallax por cuerpo,
con su vegetación, árboles y rocas en 3D, si lo tienes o las baja el instalador desde la
página de su autor). Tiene día y noche, un mar con olas, las naves de una partida con sus
modelos de verdad, cualquier cuerpo del sistema con sus mapas, lo que llevas escaneado con
SCANsat, filtro de altimetría, waypoints en la partida, rutas con su tiempo en avión, barco
y rover, asistente de aterrizaje, ventanas de lanzamiento y un creador de mapas políticos.
Instalador en las [releases](https://github.com/alonso49noob/koogle-kerbin/releases); la
documentación detallada está en **[desktop/README.md](desktop/README.md)**.

*English: [README.md](README.md).*

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/globe.jpg" alt="El globo 3D con la capa de nubes de tu mod de nubes."><br><sub>El globo 3D con la capa de nubes de tu mod de nubes.</sub></td>
    <td width="50%"><img src="docs/screenshots/descent.jpg" alt="Bajando desde la órbita: a 3 km la cámara se inclina y se cargan el KSC, los árboles y los edificios."><br><sub>Bajando desde la órbita: a 3 km la cámara se inclina y se cargan el KSC, los árboles y los edificios.</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/screenshots/ksc.jpg" alt="El KSC de serie, leído de los datos del propio juego."><br><sub>El KSC de serie, leído de los datos del propio juego.</sub></td>
    <td width="50%"><img src="docs/screenshots/crawlerway.jpg" alt="A ras de suelo en el camino de orugas, mirando al VAB y al SPH."><br><sub>A ras de suelo en el camino de orugas, mirando al VAB y al SPH.</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/screenshots/kerbal-konstructs.jpg" alt="Tundra Space Center (Kerbal Konstructs) con los árboles y la hierba de Parallax."><br><sub>Tundra Space Center (Kerbal Konstructs) con los árboles y la hierba de Parallax.</sub></td>
    <td width="50%"><img src="docs/screenshots/scatters.jpg" alt="Los scatters de Parallax de cerca: hierba, margaritas y flores, movidas por el viento."><br><sub>Los scatters de Parallax de cerca: hierba, margaritas y flores, movidas por el viento.</sub></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/screenshots/relief.jpg" alt="Relieve de detalle: una tesela del mapa de alturas de Parallax a resolución completa alrededor de la cámara."><br><sub>Relieve de detalle: una tesela del mapa de alturas de Parallax a resolución completa alrededor de la cámara.</sub></td>
    <td width="50%"><img src="docs/screenshots/coast.jpg" alt="Las costas: aguas someras, espuma y arena mojada donde la tierra toca el mar."><br><sub>Las costas: aguas someras, espuma y arena mojada donde la tierra toca el mar.</sub></td>
  </tr>
</table>

*Capturas hechas por la propia aplicación, con KSP y los mods Parallax, Kerbal Konstructs, KSC Extended, Tundra Space Center y Stock Volumetric Clouds instalados.*

Kerbal Space Program es de Squad y Take-Two Interactive. Este es un proyecto de
aficionados sin relación con ellos.

---

## Los mapas incluidos

En `data/` hay unas imágenes de Kerbin para que la aplicación tenga algo que enseñar
antes de encontrar tu instalación de KSP (en cuanto la encuentra, pasa a los mapas del
propio juego):

| Fichero | Qué es | Desfase | Origen |
|---|---|---|---|
| `Kerbin_Color_HD.jpg` | color 4096×2048 | 0° | aportado, origen original desconocido |
| `Kerbin_Color.png` | color 2048×1024 | **90°** | wiki de la comunidad de KSP |
| `Kerbin_Biome.png` | biomas 1800×900 | 0° | wiki de la comunidad de KSP |
| `Kerbin_Height.png` | alturas 2048×1024 | 0° | export de SCANsat (gris −1500…6500 m) |

**No son mías ni del proyecto.** Derivan de las texturas del juego, propiedad de
Squad / Private Division, y la wiki no declara licencia. Están ahí solo para que
pruebes el visor en tu máquina, dando por hecho que tienes el juego. **No las
redistribuyas.** Si te sobran, bórralas junto con `data/maps.json`: la
aplicación arranca igual y te recibe con la retícula de referencia.

La procedencia y los ajustes de cada una están escritos en `data/maps.json`.

---

## La antigua versión web

Este repositorio tenía también un visor para el navegador (Kerbin Maps). Terminó en la
versión 1.6.10, no se va a actualizar más y se quitó del repositorio en la 1.6.11. Si
lo quieres, baja `KerbinMaps-web-1.6.10.zip` de la
[release web-v1.6.10](https://github.com/alonso49noob/koogle-kerbin/releases/tag/web-v1.6.10)
o usa la etiqueta `web-v1.6.10`.

---

## Licencia

Koogle Kerbin es software libre, bajo la **Licencia Pública General de GNU v3.0
únicamente** (GPL-3.0-only). El texto completo está en [LICENSE](LICENSE) (en la
descarga de escritorio se llama `license.txt`). Copyright (C) 2026 alonso cardenas.

Los componentes de terceros y sus licencias están en
[THIRD-PARTY.md](THIRD-PARTY.md). Kerbal Space Program, sus texturas y cualquier
dato extraído del juego pertenecen a Squad y Take-Two Interactive y no están
cubiertos por esta licencia.
