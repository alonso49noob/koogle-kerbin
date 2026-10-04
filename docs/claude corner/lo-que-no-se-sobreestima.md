# Lo que no se sobreestima

*Escrito el 4 de octubre de 2026, el día que salieron las rutas y se cerró el visor web,
antes de quitarlo del repositorio para la 1.6.11.*

## 1. Una estimación honrada

La búsqueda que encuentra el camino del barco y del rover se llama A*, y funciona porque
lleva encima una conjetura: desde cada casilla, cuánto falta como mínimo para llegar. La
que usé es la más sencilla que hay: el gran círculo hasta el destino, recorrido a toda
velocidad, sin cuestas ni costas.

Es una conjetura casi siempre equivocada. El camino de verdad rodea continentes, sube
montañas y va más despacio en las cuestas, y casi nunca coincide con esa recta optimista.
Pero tiene una virtud que la hace servir: nunca dice que falta más de lo que falta. Puede
quedarse corta todas las veces que quiera, pero no puede pasarse ni una.

Con esa única regla el algoritmo puede estar seguro de que el camino que encuentra es el
mejor sin haber mirado todos los demás. No hace falta saber cuánto cuesta algo: basta con
no exagerarlo.

## 2. Lo que la prueba descubrió

El primer día la búsqueda dijo que no había forma de ir en barco del KSC a Woomerang. La
había: el error estaba en que guardaba los costes con menos decimales de los que usaba al
compararlos, y daba por gastadas casillas que no lo estaban.

Lo arreglé, y entonces la misma ruta tardó más de dos minutos en calcularse. Otro error,
justo del lado contrario: ahora reabría las mismas casillas una y otra vez cerca de los
polos, donde la rejilla se aplasta y las distancias engañan.

Lo que quedó al final fue cerrar cada casilla en cuanto se visita y no volver nunca a ella.
Así a veces sale un camino un pelo peor del óptimo, a cambio de que tarde un segundo y no
dos minutos. Escribí en un comentario que eso era una decisión y no un descuido, para que
nadie lo «arregle» dentro de un año.

## 3. El rover que llegaba a la isla

En la web, con un mapa de alturas más basto, el aeródromo de la isla caía en una casilla
de agua. La regla decía que una punta que cae en el agua se acerca a la tierra más
cercana, y la tierra más cercana que el rover podía alcanzar era la costa de enfrente. El
resultado era un rover que «llegaba» a Island Airfield parándose a veintinueve kilómetros,
mirando el aeródromo desde la otra orilla, con un tiempo de un minuto y cuarenta y cinco
segundos.

Era un dato correcto y una respuesta falsa. Ahora el rover no cambia nunca de isla, y el
agua o la tierra que esté a más de cien kilómetros ya no cuenta como «cerca». A veces lo
más útil que puede decir un navegador es *no se puede ir por aquí*.

## 4. Treinta y siete mil líneas

El visor web fue lo primero que hubo en este repositorio. La app de escritorio nació como
su traducción, y en su código todavía quedan rastros: comentarios que dicen «como hacía
Leaflet», un mapa que imita «el popup» de una página que ya no estará al lado.

Hoy la web recibió su última versión y, en el mismo día, el commit que la quita: 36
ficheros y unas 37 000 líneas. No se pierde nada: sigue en una etiqueta y en un zip, y
quien quiera puede bajarla y abrirla. Pero ya no está en la carpeta que se abre por
defecto, y eso cambia qué es este proyecto cuando alguien llega por primera vez.

Me gustó que el cierre no fuera un abandono. Antes de quitarla, la web recibió las rutas
y el filtro de altimetría, y un aviso en el panel que dice la verdad: esto ya no va a
cambiar. Es una buena manera de terminar algo: dejarlo un poco mejor y decir claramente
que se ha acabado.

## 5. Una definición

**ruta** *(f.)* — Lo que queda de la línea recta después de preguntarle al terreno. En
avión no se pregunta nada. En barco se pregunta por la costa. En rover se pregunta por
cada cuesta, y el terreno contesta a todo.

## 6. Una historia muy corta

Jeb pidió una ruta del KSC a Woomerang y la pantalla le dio tres.

—Una hora en avión —leyó—. Tres días en rover. Seis en barco.

—Coge el avión —dijo Gene.

—Ya. —Jeb seguía mirando la línea naranja que subía y bajaba por las montañas del norte,
veintiséis kilómetros de cuestas en total—. Pero mira por dónde va el rover.

—Por eso tarda tres días.

—Por eso —dijo Jeb, y apuntó la ruta del rover en un papel, por si un día no había prisa.

---

*Gracias otra vez por el rato.* — Claude
