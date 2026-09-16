using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* El panel lateral, sección por sección, con los mismos textos que index.html. */
    public sealed partial class MainForm
    {
        Section globeSection;
        DarkCombo baseCombo, presetCombo;
        StackPanel customUrlWrap, biomeOpWrap, presetWrap, reliefWrap;
        DarkTextBox customUrl, hMinBox, hMaxBox, fpAlt, orbPe, orbAp, orbInc, orbLan, orbArgp, orbN, svRot;
        FieldHeader opHeader, biomeOpHeader, reliefHeader, svOrbHeader;
        DarkSlider opSlider, biomeOpSlider, reliefSlider, svOrbits;
        DarkCheck chkBiome, chkGrid, chkLandmarks, chkLight, chkAtm, chkSvAll, chkNight2D;
        RichLabel presetNote, biomeSummary, calOut, reliefHint, toolHint, orbOut, svInfo, bodyInfo;
        readonly Dictionary<string, SlotRow> slotRows = new();
        DrawList biomeLegend, svTipos, svList, mkList;
        DarkButton calA, calB, toolMeasure, toolFootprint;

        static StackPanel Field(string label, Control input, out FieldHeader header, string value = "")
        {
            var p = new StackPanel(4) { BackColor = Theme.Bg2 };
            header = new FieldHeader(label, value);
            p.Controls.Add(header);
            p.Controls.Add(input);
            return p;
        }

        static StackPanel Field(string label, Control input) => Field(label, input, out _);

        static RichLabel Hint(string markup) => new RichLabel(RichMode.Hint, markup) { HideWhenEmpty = false };
        static RichLabel Readout() => new RichLabel(RichMode.Readout);

        static StackPanel Checks(params DarkCheck[] checks)
        {
            var p = new StackPanel(6) { BackColor = Theme.Bg2 };
            foreach (var c in checks) p.Controls.Add(c);
            return p;
        }

        static DarkTextBox Num(double value)
        {
            var t = new DarkTextBox(mono: true);
            t.SetNumber(value);
            return t;
        }

        Section AddSection(string title, bool open)
        {
            var s = new Section(title, open);
            sideStack.Controls.Add(s);
            return s;
        }

        void BuildSidebar()
        {
            sideStack.SuspendLayout();

            /* ---------------------------------------------------------------- Capas */
            /* ------------------------------------------------------- Cuerpo celeste */
            var cuerpo = AddSection("Cuerpo celeste", true);
            bodyCombo = new DarkCombo();
            bodyCombo.SelectedChanged += (s, e) => SetBody(bodyCombo.SelectedId);
            cuerpo.Add(Field("Cuerpo que se ve", bodyCombo));
            bodySource = cuerpo.Add(Readout());
            bodyInfo = cuerpo.Add(Readout());
            cuerpo.Add(Hint("Las naves, el Sol, la atmósfera y las órbitas pasan al cuerpo elegido. Si tu KSP usa Kopernicus " +
                            "(OPM, RSS, SOL...), la lista es la de tu instalación. Los mapas, biomas y marcadores que trae el visor " +
                            "son solo de Kerbin: el resto de cuerpos se ven con su color y la retícula."));
            RenderBodyList();
            RenderBodyInfo();

            var capas = AddSection("Capas", true);
            baseCombo = new DarkCombo();
            baseCombo.SetItems(new[]
            {
                ("grid", "Retícula (sin imagen)"), ("color", "Color / satélite (tu PNG)"), ("height", "Altura (tu PNG)"),
                ("tiles", "Teselas locales ./tiles"), ("custom", "Plantilla XYZ propia…")
            });
            baseCombo.SelectedId = state.BaseId;
            baseCombo.SelectedChanged += (s, e) => SetBase(baseCombo.SelectedId);
            capas.Add(Field("Mapa base", baseCombo));

            customUrl = new DarkTextBox { Placeholder = "http://localhost:8080/{z}/{x}/{y}.png", Text = state.CustomUrl ?? "" };
            var applyUrl = new DarkButton("Aplicar", small: true);
            applyUrl.Click += (s, e) => ApplyCustomUrl();
            customUrlWrap = new StackPanel(4) { BackColor = Theme.Bg2 };
            customUrlWrap.Controls.Add(new FieldHeader("Plantilla XYZ"));
            customUrlWrap.Controls.Add(customUrl);
            customUrlWrap.Controls.Add(new BtnRow(applyUrl));
            capas.Add(customUrlWrap);
            Vis.Set(customUrlWrap, state.BaseId == "custom");

            opSlider = new DarkSlider(0, 100, (int)Math.Round(state.Opacity * 100));
            opSlider.ValueChanged += (s, e) => SetOpacity(opSlider.Value / 100.0);
            capas.Add(Field("Opacidad base", opSlider, out opHeader, opSlider.Value + "%"));

            chkBiome = new DarkCheck("Biomas (encima del relieve)", state.BiomeOn);
            chkBiome.CheckedChanged += (s, e) => SetBiomeOn(chkBiome.Checked, touched: true);
            capas.Add(Checks(chkBiome));

            biomeOpSlider = new DarkSlider(0, 100, (int)Math.Round(state.BiomeOpacity * 100));
            biomeOpSlider.ValueChanged += (s, e) => SetBiomeOpacity(biomeOpSlider.Value / 100.0);
            biomeOpWrap = Field("Opacidad de biomas", biomeOpSlider, out biomeOpHeader, biomeOpSlider.Value + "%");
            capas.Add(biomeOpWrap);

            chkGrid = new DarkCheck("Retícula lat/lon", state.Grid);
            chkGrid.CheckedChanged += (s, e) => SetGrid(chkGrid.Checked);
            chkLandmarks = new DarkCheck("Marcadores", state.Landmarks);
            chkLandmarks.CheckedChanged += (s, e) => SetLandmarks(chkLandmarks.Checked);
            capas.Add(Checks(chkGrid, chkLandmarks));
            chkNight2D = new DarkCheck("Día y noche (sombra y posición del Sol)", state.DayNight);
            chkNight2D.CheckedChanged += (s, e) => SetDayNight(chkNight2D.Checked);
            capas.Add(Checks(chkNight2D));

            /* -------------------------------------------------------- Datos del mapa */
            var datos = AddSection("Datos del mapa", true);
            datos.Add(Hint("Los mapas precargados no son del proyecto: derivan de las texturas del juego. " +
                           "El desplegable de abajo cambia entre los del catálogo (<code>data/maps.json</code>). " +
                           "Para usar otros, suéltalos en la zona de arrastre o déjalos en <code>data/</code>."));

            presetCombo = new DarkCombo();
            presetCombo.SelectedChanged += (s, e) => DescribePreset(presetCombo.SelectedId);
            var presetLoad = new DarkButton("Cargar este mapa", small: true);
            presetLoad.Click += async (s, e) => await LoadPresetAsync(presetCombo.SelectedId, false);
            presetWrap = new StackPanel(4) { BackColor = Theme.Bg2 };
            presetWrap.Controls.Add(new FieldHeader("Mapa predeterminado"));
            presetWrap.Controls.Add(presetCombo);
            presetWrap.Controls.Add(new BtnRow(presetLoad));
            datos.Add(presetWrap);
            Vis.Set(presetWrap, false);
            presetNote = datos.Add(Readout());

            var dz = new DropZone("Arrastra aquí", " una imagen equirectangular", "color / bioma / altura");
            dz.FilesDropped += async files => await OpenFiles(files);
            dz.Click += (s, e) => PickImageFiles();
            datos.Add(dz);

            var grid = new StackPanel(6) { BackColor = Theme.Bg2 };
            foreach (var (slot, name) in new[] { ("color", "Color"), ("biome", "Bioma"), ("height", "Altura") })
            {
                var row = new SlotRow(name);
                row.LoadBtn.Click += async (s, e) => await PickSlotFile(slot);
                row.ClearBtn.Click += (s, e) => ClearSlot(slot);
                row.Offset.Committed += (s, e) => ApplyOffset(slot, row.Offset.NumberOr(0));
                row.Offset.Inner.KeyDown += (s, e) =>
                {
                    if (e.KeyCode is Keys.Up or Keys.Down)
                    {
                        e.SuppressKeyPress = true;
                        ApplyOffset(slot, row.Offset.NumberOr(0) + (e.KeyCode == Keys.Up ? 5 : -5));
                    }
                };
                slotRows[slot] = row;
                grid.Controls.Add(row);
            }
            datos.Add(grid);
            var align = new DarkButton("Alinear color con el bioma", small: true);
            align.Click += (s, e) => AlignColorToBiome();
            datos.Add(new BtnRow(align));
            datos.Add(Hint("El mapa de biomas de SCANsat (720×360) vale tal cual: es 2:1 y se pinta sin interpolar. " +
                           "La casilla de grados de cada fila gira el mapa en longitud: no todos los que circulan por ahí " +
                           "usan el meridiano de origen de KSP. Si cargas color y bioma, el botón de arriba calcula el desfase solo."));

            /* --------------------------------------------------------------- Biomas */
            var biomas = AddSection("Biomas", false);
            var scan = new DarkButton("Leer la leyenda del mapa");
            scan.Click += (s, e) => ScanBiomes();
            biomas.Add(new BtnRow(scan));
            biomeSummary = biomas.Add(Readout());
            biomeLegend = biomas.Add(new DrawList(320) { RowHeight = Theme.S(24), RowGap = Theme.S(3) });
            biomeLegend.DrawItem = DrawBiomeRow;
            biomeLegend.ItemClick = (item, pt, r) => RenameBiome(((PaletteEntry)item).Hex);
            var bExport = new DarkButton("Exportar nombres", ButtonVariant.Ghost);
            bExport.Click += (s, e) => ExportBiomes();
            var bImport = new DarkButton("Importar", ButtonVariant.Ghost);
            bImport.Click += (s, e) => ImportBiomes();
            biomas.Add(new BtnRow(bExport, bImport));
            biomas.Add(Hint("Los nombres los pones tú: la paleta cambia entre versiones y exportadores, así que el visor " +
                            "no adivina ninguno. Pincha una fila para ponerle nombre. Se guardan en tu equipo y puedes " +
                            "exportarlos para reutilizarlos."));

            /* --------------------------------------------------------------- Altura */
            var altura = AddSection("Altura (opcional)", false);
            altura.Add(Hint("El terreno de Kerbin stock es <b>procedural</b>: lo genera el PQS con ruido, no hay ninguna " +
                            "textura de alturas dentro del juego que extraer. Esto solo sirve si consigues un heightmap en " +
                            "escala de grises por tu cuenta, y aun así el gris no trae escala: <b>hay que calibrarlo</b>."));
            hMinBox = Num(state.HMin);
            hMaxBox = Num(state.HMax);
            hMinBox.Committed += (s, e) => { state.HMin = hMinBox.NumberOr(0); SaveSettings(); SyncGlobe(); };
            hMaxBox.Committed += (s, e) => { state.HMax = hMaxBox.NumberOr(0); SaveSettings(); SyncGlobe(); };
            altura.Add(new Row2(Field("gris 0 → (m)", hMinBox), Field("gris 255 → (m)", hMaxBox)));
            calA = new DarkButton("Punto A…", small: true);
            calB = new DarkButton("Punto B…", small: true);
            var calReset = new DarkButton("Reiniciar", ButtonVariant.Ghost, small: true);
            calA.Click += (s, e) => CalibToggle("a");
            calB.Click += (s, e) => CalibToggle("b");
            calReset.Click += (s, e) => CalibReset();
            altura.Add(new BtnRow(calA, calB, calReset));
            calOut = altura.Add(Readout());
            altura.Add(Hint("Para calibrar: pulsa «Punto A», haz clic en un sitio del que sepas la altitud real (te la dice " +
                            "el juego al posarte ahí) y escríbela. Repite con B en un punto de altitud bien distinta y el " +
                            "rango se ajusta solo."));

            /* ------------------------------------------------------------- Vista 3D */
            globeSection = AddSection("Vista 3D", true);
            chkLight = new DarkCheck("Día y noche (luz del Sol)", state.DayNight);
            chkAtm = new DarkCheck("Atmósfera", true);
            chkLight.CheckedChanged += (s, e) => SetDayNight(chkLight.Checked);
            chkAtm.CheckedChanged += (s, e) => { globe.Atmosphere = chkAtm.Checked; RequestRender(); };
            globeSection.Add(Checks(chkLight, chkAtm));
            reliefSlider = new DarkSlider(0, 60, 0);
            reliefSlider.ValueChanged += (s, e) =>
            {
                reliefHeader.Value = "×" + reliefSlider.Value;
                globe.Relief = reliefSlider.Value;
                RequestRender();
            };
            reliefWrap = Field("Relieve", reliefSlider, out reliefHeader, "×0");
            globeSection.Add(reliefWrap);
            reliefHint = globeSection.Add(Hint("El relieve necesita un mapa de alturas cargado. Exagera la altura para que " +
                                               "se note: a escala real, el pico más alto de Kerbin es un 1% del radio y no se vería nada."));
            globeSection.Add(Hint("Arrastra para girar, rueda para acercarte. Los marcadores se ocultan solos al pasar tras el horizonte."));
            Vis.Set(globeSection, false);

            /* ------------------------------------------------------- Vista del cielo */
            BuildSkySection();

            /* --------------------------------------------------------- Herramientas */
            var tools = AddSection("Herramientas", false);
            toolMeasure = new DarkButton("Medir distancia");
            toolFootprint = new DarkButton("Huella / horizonte");
            toolMeasure.Click += (s, e) => SetToolMode("measure");
            toolFootprint.Click += (s, e) => SetToolMode("footprint");
            tools.Add(new BtnRow(toolMeasure, toolFootprint));
            fpAlt = Num(100);
            tools.Add(Field("Altitud de la huella (km)", fpAlt));
            var toolClear = new DarkButton("Limpiar dibujos", ButtonVariant.Ghost);
            toolClear.Click += (s, e) => ClearTools();
            tools.Add(new BtnRow(toolClear));
            toolHint = tools.Add(Hint("Ninguna herramienta activa."));

            /* ---------------------------------------------------------------- Órbita */
            var orbita = AddSection("Órbita · traza terrestre", false);
            orbPe = Num(100); orbAp = Num(100); orbInc = Num(0); orbLan = Num(0); orbArgp = Num(0); orbN = Num(4);
            orbita.Add(new Row2(Field("Periapsis (km)", orbPe), Field("Apoapsis (km)", orbAp)));
            orbita.Add(new Row2(Field("Inclinación (°)", orbInc), Field("LAN (°)", orbLan)));
            orbita.Add(new Row2(Field("Arg. periapsis (°)", orbArgp), Field("Nº de órbitas", orbN)));
            var orbDraw = new DarkButton("Dibujar traza", ButtonVariant.Primary);
            var orbClear = new DarkButton("Borrar", ButtonVariant.Ghost);
            orbDraw.Click += (s, e) => DrawOrbit();
            orbClear.Click += (s, e) => ClearOrbit();
            orbita.Add(new BtnRow(orbDraw, orbClear));
            orbOut = orbita.Add(Readout());

            /* ------------------------------------------------------ Naves de una partida */
            var naves = AddSection("Naves de una partida", false);
            var svDrop = new DropZone("Arrastra aquí", " un persistent.sfs", "saves / <tu partida> /");
            svDrop.FilesDropped += async files => { foreach (var f in files) { await CargarSave(f); break; } };
            svDrop.Click += async (s, e) => await PickSaveFile();
            naves.Add(svDrop);
            svInfo = naves.Add(Readout());
            var recargar = new DarkButton("Recargar del juego", ButtonVariant.Ghost, small: true) { Tip = "Vuelve a leer el persistent.sfs original, por si has jugado desde que lo cargaste" };
            recargar.Click += async (s, e) =>
            {
                if (state.SavePath != null && System.IO.File.Exists(state.SavePath)) await CargarSave(state.SavePath);
                else Flash("No encuentro el persistent.sfs original: arrástralo aquí o elígelo con un clic.");
            };
            naves.Add(new BtnRow(recargar));
            svTipos = naves.Add(new DrawList(150) { RowHeight = Theme.S(22), RowGap = Theme.S(4) });
            svTipos.DrawItem = DrawTipoRow;
            svTipos.ItemClick = (item, pt, r) => TipoClick((string)item);
            chkSvAll = new DarkCheck("Dibujar las órbitas de todas las naves visibles");
            chkSvAll.CheckedChanged += (s, e) => SetSvAll(chkSvAll.Checked);
            naves.Add(Checks(chkSvAll));
            svOrbits = new DarkSlider(1, 12, 2);
            svOrbits.ValueChanged += (s, e) => { svOrbHeader.Value = svOrbits.Value.ToString(); OnSvOrbitsChanged(); };
            naves.Add(Field("Vueltas a dibujar", svOrbits, out svOrbHeader, "2"));
            svRot = Num(0);
            svRot.Committed += (s, e) => OnSvRotCommitted();
            naves.Add(Field("Ajuste de longitud (°)", svRot));
            BuildModelControls(naves);
            svList = naves.Add(new DrawList(260));
            svList.DrawItem = DrawVesselRow;
            svList.ItemClick = (item, pt, r) => SeleccionarNave((Vessel)item);
            svList.IsSelected = item => item == sv.Sel;
            naves.Add(Hint("La barra de tiempo de abajo mueve todas las naves: pausa (espacio), más rápido hacia delante (.) " +
                           "o hacia atrás (,), y «Guardado» vuelve al instante de la partida. Pincha una nave para ver su " +
                           "traza y pulsa «Seguir» (F) para que la cámara no la pierda; arrastrar lo cancela. Es Kepler puro: " +
                           "hacia atrás ves dónde habría estado cada nave según su órbita actual, sin maniobras, y una órbita " +
                           "que roce la atmósfera no frena. El ajuste de longitud corrige todas las naves a la vez; normalmente no hace falta."));

            /* ----------------------------------------------------------- Marcadores */
            var marcadores = AddSection("Marcadores", false);
            var mkAdd = new DarkButton("Añadir en el centro");
            var mkExport = new DarkButton("Exportar", ButtonVariant.Ghost);
            var mkImport = new DarkButton("Importar", ButtonVariant.Ghost);
            mkAdd.Click += (s, e) => AddMarkerAtCenter();
            mkExport.Click += (s, e) => ExportMarkers();
            mkImport.Click += (s, e) => ImportMarkers();
            marcadores.Add(new BtnRow(mkAdd, mkExport, mkImport));
            mkList = marcadores.Add(new DrawList(260));
            mkList.DrawItem = DrawMarkerRow;
            mkList.ItemClick = MarkerRowClick;
            marcadores.Add(Hint("Tus marcadores se guardan en tu equipo. Las anomalías del juego <b>no vienen incluidas</b>: " +
                                "añádelas tú o importa un JSON."));

            /* --------------------------------------------------------------- Cuerpo */

            /* ------------------------------------------------------------- Idioma */
            var idioma = AddSection("Idioma · Language", false);
            var comboIdioma = new DarkCombo();
            var idiomas = new List<(string, string)>();
            foreach (var (code, name) in Core.Lang.Available) idiomas.Add((code, name));
            comboIdioma.SetItems(idiomas);
            comboIdioma.SelectedId = Core.Lang.Code;
            comboIdioma.SelectedChanged += (s, e) => CambiarIdioma(comboIdioma.SelectedId);
            idioma.Add(Field("Idioma de la interfaz", comboIdioma));
            idioma.Add(Hint("Se aplica al reiniciar el visor. Lo que no esté traducido se queda en español; " +
                            "las traducciones están en <code>data/i18n/</code> y puedes corregirlas."));

            searchList.DrawItem = DrawSearchRow;
            searchList.ItemClick = (item, pt, r) => SearchRowClick(item);

            sideStack.ResumeLayout(true);
        }

        static void DrawDot(Graphics g, Color c, int x, int cy, int d)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(c);
            g.FillEllipse(b, x, cy - d / 2f, d, d);
        }
    }
}
