using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Creador de países y facciones.

       Cada cuerpo tiene su mapa político (ver MapaPolitico): una lista de facciones con su
       nombre y su color, y de quién es cada trozo de tierra. Se pinta en el mapa 2D o en el
       globo con cuatro herramientas: pincel, relleno (una isla entera, o el hueco que deja
       una frontera), polígono y goma. Solo sobre tierra: el mar no es de nadie, y el dibujo
       recorta por la costa. Con una herramienta activa el botón izquierdo pinta y el derecho
       mueve el mapa; Ctrl+Z deshace.

       Se guarda solo, por cuerpo, en %APPDATA%\KoogleKerbin\facciones-<cuerpo>.json, y se
       puede exportar (el mismo JSON, o una imagen equirectangular en PNG) e importar. */
    public sealed partial class MainForm
    {
        MapaPolitico politico;
        Texture facTex;
        Faccion facSel;
        string facTool;                           // null, "pincel", "rellenar", "poligono" o "goma"
        readonly List<LatLon> facPoli = new();
        LatLon? facCursor, facUltimo;
        bool facPintando, facPan, facMidiendo;
        Dictionary<int, double> facAreas = new();
        List<EtiquetaFaccion> facEtiq = new();
        double facTierraKm2;
        object facTierraDe;                       // de qué mapas sale la máscara de tierra
        byte[] facTierra;                         // y la máscara, para no rehacerla al importar
        int facMedida = -1, facGuardada;
        readonly Timer facGuardarTimer = new() { Interval = 1500 };
        readonly MapLayer facLayer = new();

        Section secFacciones;
        DarkCheck chkFac, chkFacNombres, chkFacRespetar;
        DrawList facList;
        DarkButton facPincelBtn, facRellenarBtn, facPoligonoBtn, facGomaBtn, facDeshacerBtn, facRehacerBtn;
        DarkSlider facTamSlider, facOpSlider;
        FieldHeader facTamHeader, facOpHeader;
        RichLabel facInfo;

        bool FacActivo => facTool != null && politico != null;
        /* Se pinta en el mapa 2D y en el globo; en el vuelo y en el cielo el ratón hace lo de siempre. */
        bool FacPuedePintar => FacActivo && (!GlobeVisible || is3D);

        static string ArchivoFacciones(string cuerpo) =>
            "facciones-" + string.Concat((cuerpo ?? "Kerbin").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_')) + ".json";

        static string RutaFacciones(string cuerpo) => Path.Combine(Store.RoamingDir, ArchivoFacciones(cuerpo));

        /* ------------------------------------------------------------ panel */

        void BuildFaccionesSection()
        {
            var s = secFacciones = AddSection("Países y facciones", false);
            chkFac = new DarkCheck("Ver los territorios en el mapa y el globo", state.FaccionesOn);
            chkFac.CheckedChanged += (o, e) => { state.FaccionesOn = chkFac.Checked; SaveSettings(); AplicarFacciones(); };
            s.Add(Checks(chkFac));

            facList = s.Add(new DrawList(230) { RowHeight = Theme.S(28) });
            facList.DrawItem = DrawFaccionRow;
            facList.ItemClick = FaccionRowClick;
            facList.IsSelected = item => item == facSel;

            DarkButton B(string t, Action a, ButtonVariant v = ButtonVariant.Normal, bool small = false)
            {
                var b = new DarkButton(t, v, small);
                b.Click += (o, e) => a();
                return b;
            }
            s.Add(new BtnRow(B("Nueva", NuevaFaccion, ButtonVariant.Primary), B("Renombrar", RenombrarFaccion, ButtonVariant.Ghost),
                             B("Color…", ColorFaccion, ButtonVariant.Ghost), B("Eliminar", BorrarFaccion, ButtonVariant.Ghost)));

            facPincelBtn = B("Pincel", () => SetFacTool("pincel"));
            facRellenarBtn = B("Rellenar", () => SetFacTool("rellenar"));
            facPoligonoBtn = B("Polígono", () => SetFacTool("poligono"));
            facGomaBtn = B("Goma", () => SetFacTool("goma"));
            facPincelBtn.Tip = "Pinta arrastrando, con el radio de abajo";
            facRellenarBtn.Tip = "Se queda con la isla, o con la zona cerrada por fronteras, donde hagas clic";
            facPoligonoBtn.Tip = "Clic para cada vértice; se cierra con doble clic, Intro o pinchando en el primero";
            facGomaBtn.Tip = "Borra territorio arrastrando; con «No pisar…» marcado, solo el de la facción elegida";
            s.Add(Field("Herramienta", new BtnRow(facPincelBtn, facRellenarBtn, facPoligonoBtn, facGomaBtn)));

            facTamSlider = new DarkSlider(0, 100, PincelAPaso(state.FacPincelKm));
            facTamSlider.ValueChanged += (o, e) =>
            {
                state.FacPincelKm = PasoAPincel(facTamSlider.Value);
                facTamHeader.Value = FmtPincel();
                SaveSettings();
                FacPrevia();
                RenderFaccionesInfo();
                RequestRender();
            };
            s.Add(Field("Radio del pincel y la goma", facTamSlider, out facTamHeader, FmtPincel()));

            chkFacRespetar = new DarkCheck("No pisar el territorio de otras facciones", state.FacRespetar);
            chkFacRespetar.CheckedChanged += (o, e) => { state.FacRespetar = chkFacRespetar.Checked; SaveSettings(); };
            chkFacNombres = new DarkCheck("Nombres sobre cada territorio", state.FacNombres);
            chkFacNombres.CheckedChanged += (o, e) => { state.FacNombres = chkFacNombres.Checked; SaveSettings(); AplicarFacciones(); };
            s.Add(Checks(chkFacRespetar, chkFacNombres));

            facOpSlider = new DarkSlider(10, 90, (int)Math.Round(state.FacRelleno * 100));
            facOpSlider.ValueChanged += (o, e) =>
            {
                state.FacRelleno = facOpSlider.Value / 100.0;
                facOpHeader.Value = facOpSlider.Value + " %";
                SaveSettings();
                AplicarFacciones();
            };
            s.Add(Field("Opacidad del relleno", facOpSlider, out facOpHeader, facOpSlider.Value + " %"));

            facDeshacerBtn = B("Deshacer", FacDeshacer, ButtonVariant.Ghost, true);
            facRehacerBtn = B("Rehacer", FacRehacer, ButtonVariant.Ghost, true);
            facDeshacerBtn.Tip = "Ctrl+Z";
            facRehacerBtn.Tip = "Ctrl+Y";
            s.Add(new BtnRow(facDeshacerBtn, facRehacerBtn, B("Exportar…", ExportarFacciones, ButtonVariant.Ghost, true),
                             B("Importar…", ImportarFacciones, ButtonVariant.Ghost, true), B("Imagen PNG…", ExportarImagenFacciones, ButtonVariant.Ghost, true)));
            facInfo = s.Add(Readout());
            s.Add(Hint("Crea una facción, elige una herramienta y pinta en el mapa 2D o en el globo. Solo se pinta <b>sobre " +
                       "tierra</b>: el mar no es de nadie y el dibujo se recorta por la costa. Con una herramienta activa, el " +
                       "botón derecho mueve el mapa, <b>[</b> y <b>]</b> cambian el radio del pincel, <b>Ctrl+Z</b> deshace y " +
                       "<b>Esc</b> suelta la herramienta. Pincha la muestra de color de una fila para cambiarlo y el círculo de " +
                       "la derecha para ocultarla. Se guarda solo, un mapa por cuerpo celeste."));

            facGuardarTimer.Tick += (o, e) =>
            {
                facGuardarTimer.Stop();
                if (facPintando) { facGuardarTimer.Start(); return; }    // a mitad de pincelada, luego
                GuardarFacciones();
            };
        }

        /* El radio del pincel va en escala logarítmica: de 1 a 316 km. */
        static int PincelAPaso(double km) => (int)Math.Round(Math.Clamp(40 * Math.Log10(Math.Max(km, 1)), 0, 100));
        static double PasoAPincel(int paso) => Math.Round(Math.Pow(10, paso / 40.0), paso < 40 ? 1 : 0);
        string FmtPincel() => Geo.F(state.FacPincelKm, state.FacPincelKm < 10 ? 1 : 0) + " km";

        static string FmtArea(double km2) => km2 < 10 ? Geo.F(km2, 1) + " km²" : Geo.FmtIntEs((long)Math.Round(km2)) + " km²";

        void DrawFaccionRow(Graphics g, Rectangle r, object item, bool hover)
        {
            var f = (Faccion)item;
            int sw = Theme.S(14), x = r.X + Theme.S(6);
            var col = Theme.Hex(f.Color);
            Theme.FillRound(g, f.Visible ? col : Color.FromArgb(70, col), Color.FromArgb(80, 255, 255, 255),
                            new RectangleF(x, r.Y + (r.Height - sw) / 2f, sw, sw), Theme.Sf(3));
            x += sw + Theme.S(8);
            int aw = Theme.S(92), ew = Theme.S(24);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, f.Nombre, f == facSel ? Theme.Semibold : Theme.UISmall,
                new Rectangle(x, r.Y, Math.Max(0, r.Right - x - aw - ew), r.Height), f.Visible ? Theme.Fg : Theme.FgDim, flags);
            string area = facAreas.TryGetValue(f.Id, out var a) && a > 0 ? FmtArea(a) : "—";
            TextRenderer.DrawText(g, area, Theme.MonoSmall, new Rectangle(r.Right - aw - ew, r.Y, aw, r.Height), Theme.FgDim, flags | TextFormatFlags.Right);
            TextRenderer.DrawText(g, f.Visible ? "◉" : "○", Theme.UISmall, new Rectangle(r.Right - ew, r.Y, ew, r.Height),
                hover ? Theme.Fg : Theme.FgDim, flags | TextFormatFlags.HorizontalCenter);
        }

        void FaccionRowClick(object item, Point pt, Rectangle rect)
        {
            var f = (Faccion)item;
            facSel = f;
            if (pt.X < Theme.S(26)) { ColorFaccion(); return; }
            if (pt.X > rect.Width - Theme.S(26))
            {
                f.Visible = !f.Visible;
                politico.Version++;
                FacCambiado();
                return;
            }
            facList.Invalidate();
            FacPrevia();
            RenderFaccionesInfo();
            RequestRender();
        }

        /* ------------------------------------------------------------ facciones */

        void NuevaFaccion() => CrearFaccion(preguntar: true);

        Faccion CrearFaccion(bool preguntar)
        {
            if (politico == null) return null;
            if (politico.Facciones.Count >= MapaPolitico.MaxFacciones)
            {
                Flash(Lang.F("Como mucho {0} facciones por cuerpo.", MapaPolitico.MaxFacciones));
                return null;
            }
            string nombre = Lang.F("Facción {0}", politico.Facciones.Count + 1);
            if (preguntar)
            {
                nombre = InputBox.Ask(this, Lang.T("Nombre del país o facción:"), nombre);
                if (string.IsNullOrWhiteSpace(nombre)) return null;
            }
            var f = politico.Nueva(nombre.Trim());
            facSel = f;
            FacCambiado();
            return f;
        }

        void RenombrarFaccion()
        {
            if (facSel == null) { Flash("Elige antes una facción de la lista."); return; }
            string nombre = InputBox.Ask(this, Lang.T("Nombre del país o facción:"), facSel.Nombre);
            if (string.IsNullOrWhiteSpace(nombre)) return;
            facSel.Nombre = nombre.Trim();
            politico.Version++;
            FacCambiado();
        }

        void ColorFaccion()
        {
            if (facSel == null) { Flash("Elige antes una facción de la lista."); return; }
            using var dlg = new ColorDialog { Color = Theme.Hex(facSel.Color), FullOpen = true, AnyColor = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            facSel.Color = "#" + dlg.Color.R.ToString("x2") + dlg.Color.G.ToString("x2") + dlg.Color.B.ToString("x2");
            politico.Version++;
            FacCambiado();
        }

        void BorrarFaccion()
        {
            if (facSel == null) { Flash("Elige antes una facción de la lista."); return; }
            if (MessageBox.Show(this, Lang.F("¿Borrar «{0}» y todo su territorio? No se puede deshacer.", facSel.Nombre),
                                "Koogle Kerbin", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            politico.Borrar(facSel);
            facSel = politico.Facciones.FirstOrDefault();
            if (facSel == null && facTool != "goma") SetFacTool(null);
            FacCambiado();
        }

        /* Tras cualquier cambio: guardar (en un momento), medir y volver a dibujar. */
        void FacCambiado()
        {
            facGuardarTimer.Stop();
            facGuardarTimer.Start();
            FacMedir();
            RenderFacciones();
            AplicarFacciones();
        }

        void FacDeshacer()
        {
            if (politico?.Deshacer() == true) FacCambiado();
        }

        void FacRehacer()
        {
            if (politico?.Rehacer() == true) FacCambiado();
        }

        /* ------------------------------------------------------------ herramientas */

        void SetFacTool(string t)
        {
            if (politico == null) return;
            if (t != null && facTool == t) t = null;
            if (t != null && t != "goma" && facSel == null)
            {
                facSel = politico.Facciones.FirstOrDefault() ?? CrearFaccion(preguntar: false);
                if (facSel == null) return;
            }
            if (t != null && ToolMode != null) SetToolMode(null);
            if (facPintando) { politico.TerminarTrazo(); facPintando = false; FacCambiado(); }
            facTool = t;
            facPoli.Clear();
            facPincelBtn.Active = t == "pincel";
            facRellenarBtn.Active = t == "rellenar";
            facPoligonoBtn.Active = t == "poligono";
            facGomaBtn.Active = t == "goma";
            surface.Cursor = t != null ? Cursors.Cross : Cursors.Default;
            // si se va a pintar, que se vea
            if (t != null && !state.FaccionesOn) chkFac.Checked = true;
            if (t != null && GlobeVisible && !is3D) Flash("Los territorios se pintan en el mapa 2D o en el globo 3D.");
            FacPrevia();
            RenderFaccionesInfo();
            RequestRender();
        }

        /* Dónde cae el cursor: en el 2D, el mapa; en el globo, el terreno (de cerca, con su
           relieve). En el vuelo y en el cielo no se pinta. */
        LatLon? FacPunto(int x, int y)
        {
            if (!GlobeVisible)
            {
                var ll = map.Unproject(x, y);
                if (ll.Lat > 90 || ll.Lat < -90) return null;
                return new LatLon(ll.Lat, Geo.WrapLon(ll.Lon));
            }
            if (!is3D) return null;
            return globe.PickSuelo(x, y);
        }

        (byte Id, byte Propia) FacPintura() =>
            ((byte)(facTool == "goma" ? 0 : facSel?.Id ?? 0), (byte)(facSel?.Id ?? 0));

        /* true si el clic es de la herramienta (y nadie más tiene que hacer nada con él). */
        bool FacMouseDown(MouseEventArgs e)
        {
            if (!FacPuedePintar) return false;
            if (e.Button == MouseButtons.Right)
            {
                facPan = true;
                mouseDown = true; moved = false;
                downPt = lastPt = e.Location;
                if (GlobeVisible) globe.BeginDrag(e.X, e.Y);
                return true;
            }
            if (e.Button != MouseButtons.Left) return false;
            var p = FacPunto(e.X, e.Y);
            if (p == null) return true;
            var (id, propia) = FacPintura();
            switch (facTool)
            {
                case "pincel":
                case "goma":
                    politico.EmpezarTrazo();
                    politico.Pincel(p.Value.Lat, p.Value.Lon, state.FacPincelKm * 1000, id, state.FacRespetar, propia);
                    facUltimo = p;
                    facPintando = true;
                    mouseDown = true; moved = false;
                    break;
                case "rellenar":
                    politico.EmpezarTrazo();
                    politico.Rellenar(p.Value.Lat, p.Value.Lon, id, state.FacRespetar, propia);
                    if (politico.TerminarTrazo()) FacCambiado();
                    else if (!politico.EsTierra(p.Value.Lat, p.Value.Lon)) Flash("Ahí es mar: los territorios solo van sobre tierra.");
                    else
                    {
                        int dueno = politico.IdEn(p.Value.Lat, p.Value.Lon);
                        if (dueno != 0 && dueno != id && state.FacRespetar)
                            Flash(Lang.F("Esa tierra ya es de «{0}» (está marcado «No pisar el territorio de otras facciones»).", politico.Buscar(dueno)?.Nombre ?? "?"));
                    }
                    break;
                case "poligono":
                    FacPoligonoClic(p.Value, e.Location);
                    break;
            }
            RequestRender();
            return true;
        }

        /* Pintando, el arrastre pinta: devuelve true para que no mueva el mapa. */
        bool FacMouseMove(MouseEventArgs e)
        {
            if (!FacPuedePintar || facPan) return false;
            var p = FacPunto(e.X, e.Y);
            facCursor = p;
            if (facPintando && p != null && facUltimo != null)
            {
                var (id, propia) = FacPintura();
                politico.Trazo(facUltimo.Value, p.Value, state.FacPincelKm * 1000, id, state.FacRespetar, propia);
                facUltimo = p;
            }
            FacPrevia();
            RequestRender();
            if (facPintando) UpdateHud(p);
            return facPintando;
        }

        bool FacMouseUp(MouseEventArgs e)
        {
            if (facPan && e.Button == MouseButtons.Right)
            {
                facPan = false;
                mouseDown = false; moved = false;
                if (GlobeVisible) globe.EndDrag();
                surface.Cursor = FacPuedePintar ? Cursors.Cross : Cursors.Default;
                saveViewTimer.Stop(); saveViewTimer.Start();
                RequestRender();
                return true;
            }
            if (e.Button != MouseButtons.Left) return false;
            if (facPintando)
            {
                facPintando = false;
                facUltimo = null;
                mouseDown = false;
                if (politico.TerminarTrazo()) FacCambiado();
                RequestRender();
                return true;
            }
            if (!FacPuedePintar) return false;
            mouseDown = false;
            return true;
        }

        PointF? APantalla(LatLon ll)
        {
            if (!GlobeVisible) { var (x, y) = map.ProjectNear(ll.Lat, ll.Lon); return new PointF((float)x, (float)y); }
            return globe.ToScreen(GlobeView.Sph(ll.Lat, ll.Lon, 1), out double sx, out double sy) ? new PointF((float)sx, (float)sy) : null;
        }

        static double Dist(PointF a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        void FacPoligonoClic(LatLon p, Point pt)
        {
            if (facPoli.Count >= 3 && APantalla(facPoli[0]) is PointF a && Dist(a, pt) < Theme.S(10)) { CerrarPoligono(); return; }
            // el segundo clic de un doble clic no es otro vértice
            if (facPoli.Count > 0 && APantalla(facPoli[^1]) is PointF b && Dist(b, pt) < Theme.S(4)) return;
            facPoli.Add(p);
            FacPrevia();
            RenderFaccionesInfo();
        }

        void CerrarPoligono()
        {
            if (facPoli.Count < 3) { Flash("Un polígono necesita al menos tres puntos."); return; }
            var (id, propia) = FacPintura();
            politico.EmpezarTrazo();
            politico.Poligono(facPoli, id, state.FacRespetar, propia);
            bool cambio = politico.TerminarTrazo();
            facPoli.Clear();
            FacPrevia();
            if (cambio) FacCambiado();
            else Flash("Dentro de ese polígono no había tierra que pintar.");
            RenderFaccionesInfo();
        }

        bool FacKeyDown(KeyEventArgs e)
        {
            if (!FacActivo || FocusInText()) return false;
            switch (e.KeyCode)
            {
                case Keys.Enter when facTool == "poligono":
                    CerrarPoligono();
                    break;
                case Keys.Back when facPoli.Count > 0:
                    facPoli.RemoveAt(facPoli.Count - 1);
                    FacPrevia();
                    RenderFaccionesInfo();
                    break;
                default:
                    return false;
            }
            e.Handled = true;
            return true;
        }

        /* [ y ] cambian el radio del pincel. Por el carácter y no por la tecla: en un teclado
           español se escriben con AltGr y la tecla es otra. */
        bool FacKeyPress(char c)
        {
            if (!FacActivo || FocusInText() || (c != '[' && c != ']')) return false;
            facTamSlider.Value += c == ']' ? 5 : -5;
            return true;
        }

        /* Esc: primero el polígono a medias, luego la herramienta. */
        bool FacEscape()
        {
            if (!FacActivo) return false;
            if (facPoli.Count > 0) { facPoli.Clear(); FacPrevia(); RenderFaccionesInfo(); }
            else SetFacTool(null);
            return true;
        }

        /* Lo que se ve de la herramienta: el círculo del pincel y el polígono a medias. */
        void FacPrevia()
        {
            facLayer.Clear();
            globe.Trazos.Clear();
            globe.TrazoPuntos.Clear();
            if (!FacActivo) return;
            var color = facTool == "goma" ? ColorF.White : ColorF.Hex(facSel?.Color ?? "#ffffff");
            if ((facTool == "pincel" || facTool == "goma") && facCursor is LatLon c)
            {
                var circ = Geo.Circle(c.Lat, c.Lon, state.FacPincelKm * 1000, 96);
                facLayer.Lines.Add(new MapLine(circ, ColorF.Rgba(0, 0, 0, 0.55f), 3.2f));
                facLayer.Lines.Add(new MapLine(circ, facTool == "goma" ? ColorF.White : color, 1.6f, dashed: facTool == "goma"));
                globe.Trazos.Add((circ, facTool == "goma" ? ColorF.White : color, true));
            }
            if (facTool == "poligono" && facPoli.Count > 0)
            {
                var pts = new List<LatLon>(facPoli);
                if (facCursor is LatLon c2) pts.Add(c2);
                var lados = Densificar(pts);
                facLayer.Lines.Add(new MapLine(lados, ColorF.Rgba(0, 0, 0, 0.55f), 3.2f));
                facLayer.Lines.Add(new MapLine(lados, color, 1.8f, dashed: true));
                foreach (var v in facPoli) facLayer.Dots.Add(new MapDot { Lat = v.Lat, Lon = v.Lon, Style = DotStyle.Point, Fill = color });
                globe.Trazos.Add((lados, color, false));
                foreach (var v in facPoli) globe.TrazoPuntos.Add((v, color));
            }
        }

        /* Los lados del polígono son rectos en el mapa (lat/lon): en el globo hay que trocearlos
           para que se vean igual que lo que se va a pintar. */
        static List<LatLon> Densificar(List<LatLon> pts)
        {
            var u = Geo.Unwrap(pts);
            var r = new List<LatLon>();
            for (int i = 0; i < u.Count; i++)
            {
                if (i == 0) { r.Add(u[0]); continue; }
                var a = u[i - 1];
                var b = u[i];
                int n = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(b.Lat - a.Lat), Math.Abs(b.Lon - a.Lon)) / 0.5));
                for (int k = 1; k <= n; k++)
                    r.Add(new LatLon(a.Lat + (b.Lat - a.Lat) * k / n, a.Lon + (b.Lon - a.Lon) * k / n));
            }
            return r;
        }

        /* ------------------------------------------------------------ carga, máscara y dibujo */

        void InitFacciones()
        {
            if (!map.Layers.Contains(facLayer)) map.Layers.Add(facLayer);
            CargarFacciones(Body.Name);
        }

        void CargarFacciones(string cuerpo)
        {
            MapaPolitico m = null;
            try
            {
                string p = RutaFacciones(cuerpo);
                if (File.Exists(p)) m = MapaPolitico.FromJson(File.ReadAllText(p));
            }
            catch (Exception ex) { Flash(Lang.T("No se pudo leer el mapa político guardado: ") + ex.Message); }
            m ??= new MapaPolitico();
            m.Cuerpo = cuerpo;
            UsarPolitico(m);
            facGuardada = m.Version;
        }

        void UsarPolitico(MapaPolitico m)
        {
            politico = m;
            facSel = m.Facciones.FirstOrDefault();
            facAreas = new Dictionary<int, double>();
            facEtiq = new List<EtiquetaFaccion>();
            facMedida = -1;
            facPoli.Clear();
            facPintando = false;
            if (facTex != null && glOk && surface.MakeCurrent()) { facTex.Dispose(); facTex = null; }
            m.SucioY0 = 0; m.SucioY1 = MapaPolitico.Alto - 1;
            ActualizarTierraFacciones();
            FacMedir();
            RenderFacciones();
            AplicarFacciones();
            FacPrevia();
        }

        /* Al cambiar de cuerpo: se guarda lo de este y se carga lo del otro. */
        void CambiarCuerpoFacciones()
        {
            if (politico == null || politico.Cuerpo == Body.Name) return;
            GuardarFacciones();
            SetFacTool(null);
            CargarFacciones(Body.Name);
        }

        void GuardarFacciones()
        {
            facGuardarTimer.Stop();
            if (politico == null || politico.Version == facGuardada) return;
            try
            {
                string p = RutaFacciones(politico.Cuerpo);
                // un cuerpo sin facciones y sin nada guardado no deja fichero
                if (politico.Facciones.Count == 0 && !File.Exists(p)) { facGuardada = politico.Version; return; }
                Directory.CreateDirectory(Store.RoamingDir);
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, politico.ToJson());
                File.Move(tmp, p, true);
                facGuardada = politico.Version;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[facciones] " + ex.Message); }
        }

        /* La máscara de tierra sale del mapa de alturas (lo que queda por encima del mar) o, sin
           él, del de color (lo que no es azul). En un cuerpo sin mar todo es superficie. Se
           calcula en segundo plano cada vez que cambian los mapas o su calibración. */
        void ActualizarTierraFacciones()
        {
            if (politico == null) return;
            var alt = MapImg("height");
            var col = MapImg("color");
            var (hmin, hmax) = RangoAltura();
            double ho = HeightOffNow, co = ColorOffNow;
            bool mar = Body.Current.Ocean && (alt != null || col != null);
            var clave = (Body.Name, mar, alt, col, hmin, hmax, ho, co);
            var destino = politico;
            if (Equals(facTierraDe, clave))
            {
                if (destino.Tierra != facTierra) { destino.PonerTierra(facTierra); FacMedir(); }
                return;
            }
            facTierraDe = clave;
            double radio = Body.Radius;
            if (!mar)
            {
                facTierra = null;
                destino.PonerTierra(null);
                facTierraKm2 = MapaPolitico.AreaTierra(null, radio);
                FacMedir();
                AplicarFacciones();
                return;
            }
            Func<double, double, bool> esTierra = alt != null
                ? (la, lo) => alt.HeightSmooth(la, lo, hmin, hmax, ho) > 0
                : (la, lo) => { var c = col.SampleBilinear(la, lo, co); return c.B - Math.Max(c.R, c.G) < 0.055f; };
            Task.Run(() =>
            {
                var t = MapaPolitico.ConstruirTierra(esTierra);
                return (Tierra: t, Km2: MapaPolitico.AreaTierra(t, radio));
            }).ContinueWith(task =>
            {
                if (task.IsFaulted) { System.Diagnostics.Debug.WriteLine("[facciones] " + task.Exception?.GetBaseException().Message); return; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (!Equals(facTierraDe, clave)) return;
                        facTierra = task.Result.Tierra;
                        facTierraKm2 = task.Result.Km2;
                        if (politico != null && politico.Cuerpo == Body.Name) politico.PonerTierra(facTierra);
                        FacMedir(forzar: true);
                        AplicarFacciones();
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        /* Superficie y sitio de los nombres, en segundo plano con una copia de la rejilla. */
        void FacMedir(bool forzar = false)
        {
            if (politico == null) return;
            if (!forzar && facMedida == politico.Version) return;
            if (facMidiendo) return;                  // al acabar se vuelve a mirar si hace falta
            facMidiendo = true;
            var m = politico;
            int ver = m.Version;
            var celdas = (byte[])m.Celdas.Clone();
            var tierra = m.Tierra;
            double radio = Body.Radius;
            Task.Run(() => (Areas: MapaPolitico.AreasDe(celdas, tierra, radio), Etiq: MapaPolitico.Etiquetas(celdas, tierra, radio)))
                .ContinueWith(t =>
                {
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            facMidiendo = false;
                            if (politico != m) { FacMedir(); return; }
                            if (!t.IsFaulted)
                            {
                                facAreas = t.Result.Areas;
                                facEtiq = t.Result.Etiq;
                                facMedida = ver;
                                RenderFacciones();
                                AplicarFacciones();
                            }
                            if (politico.Version != facMedida) FacMedir();
                        }));
                    }
                    catch (InvalidOperationException) { }
                });
        }

        /* Sube a la GPU las filas de la rejilla que han cambiado (lo llama cada fotograma). */
        void FacSubir()
        {
            if (politico == null || politico.SucioY1 < 0) return;
            if (facTex == null)
            {
                if (politico.Facciones.Count == 0) return;   // sin facciones no hace falta la textura
                facTex = Texture.FromR8(politico.Celdas, MapaPolitico.Ancho, MapaPolitico.Alto);
                politico.LimpiarSucio();
                AplicarFacciones();
                return;
            }
            facTex.SubirFilasR8(politico.Celdas, politico.SucioY0, politico.SucioY1);
            politico.LimpiarSucio();
        }

        /* Empuja al mapa y al globo la rejilla, los colores, la máscara y los nombres. */
        void AplicarFacciones()
        {
            bool on = state.FaccionesOn && politico != null && politico.Facciones.Count > 0 && facTex != null;
            var colores = FaccionesGlsl.Colores(politico);
            var (hmin, hmax) = RangoAltura();
            map.FacTex = on ? facTex : null;
            map.FacColores = colores;
            map.FacRelleno = state.FacRelleno;
            map.FacConMar = politico?.HayMascara == true;
            map.FacAltura = MapTex("height");
            map.FacAlturaOff = HeightOffNow;
            map.FacHMin = hmin; map.FacHMax = hmax;
            map.FacColor = MapTex("color");
            map.FacColorOff = ColorOffNow;
            globe.FacTex = on ? facTex : null;
            globe.FacColores = colores;
            globe.FacRelleno = state.FacRelleno;
            globe.FacConMar = map.FacConMar;
            map.Etiquetas.Clear();
            globe.FacEtiquetas.Clear();
            if (on && state.FacNombres)
                foreach (var e in facEtiq)
                {
                    var f = politico.Buscar(e.Id);
                    if (f == null || !f.Visible) continue;
                    var et = new MapEtiqueta { Lat = e.Lat, Lon = e.Lon, RadioM = e.RadioM, Texto = f.Nombre, Color = ColorF.Hex(f.Color) };
                    map.Etiquetas.Add(et);
                    globe.FacEtiquetas.Add(et);
                }
            EstadoFacciones();
            RequestRender();
        }

        /* El resumen de la sección cuando está plegada. */
        void EstadoFacciones()
        {
            if (secFacciones == null) return;
            int n = politico?.Facciones.Count ?? 0;
            secFacciones.Estado = n == 0 ? Lang.T("ninguna")
                : n == 1 ? Lang.F("1 facción en {0}", Body.Current.Label) : Lang.F("{0} facciones en {1}", n, Body.Current.Label);
        }

        void RenderFacciones()
        {
            if (facList == null || politico == null) return;
            facList.SetItems(politico.Facciones.Cast<object>());
            facDeshacerBtn.Enabled = politico.PuedeDeshacer;
            facRehacerBtn.Enabled = politico.PuedeRehacer;
            RenderFaccionesInfo();
        }

        void RenderFaccionesInfo()
        {
            if (facInfo == null || politico == null) return;
            var lineas = new List<string>();
            int n = politico.Facciones.Count;
            if (n == 0) lineas.Add(Lang.F("Ninguna facción en {0} todavía.", Body.Current.Label));
            else
            {
                double total = facAreas.Values.Sum();
                string pct = facTierraKm2 > 0 ? " (" + Geo.F(total / facTierraKm2 * 100, 1) + " %)" : "";
                lineas.Add(Lang.F("<b>{0}</b> facciones · <b>{1}</b> repartidos", n, FmtArea(total)) + pct);
                if (facTierraKm2 > 0)
                    lineas.Add(Lang.F("{0} de {1} en {2}", politico.HayMascara ? Lang.T("tierra") : Lang.T("superficie"), FmtArea(facTierraKm2), Body.Current.Label));
            }
            if (facTool != null)
            {
                string con = facSel != null ? "«" + RichLabel.Esc(facSel.Nombre) + "»" : "";
                lineas.Add(facTool switch
                {
                    "pincel" => Lang.F("<b>Pincel</b> de {0} con {1}: clic y arrastrar.", FmtPincel(), con),
                    "goma" => Lang.F("<b>Goma</b> de {0}: clic y arrastrar.", FmtPincel()),
                    "rellenar" => Lang.F("<b>Rellenar</b> con {0}: clic en una isla o en una zona cerrada.", con),
                    _ => facPoli.Count == 0 ? Lang.F("<b>Polígono</b> con {0}: clic en cada vértice.", con)
                         : Lang.F("<b>Polígono</b>: {0} vértices. Doble clic o Intro para cerrarlo, Retroceso quita el último.", facPoli.Count)
                });
            }
            facInfo.SetText(string.Join("\n", lineas));
        }

        /* ------------------------------------------------------------ ficheros */

        void ExportarFacciones()
        {
            if (politico == null) return;
            using var dlg = new SaveFileDialog { FileName = ArchivoFacciones(Body.Name.ToLowerInvariant()), Filter = "JSON (*.json)|*.json" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(dlg.FileName, politico.ToJson());
                Flash(Lang.F("Mapa político guardado en {0}.", Path.GetFileName(dlg.FileName)));
            }
            catch (Exception ex) { Flash(Lang.T("No se pudo guardar: ") + ex.Message); }
        }

        void ImportarFacciones()
        {
            if (politico == null) return;
            using var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json|Todos|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            MapaPolitico m;
            try { m = MapaPolitico.FromJson(File.ReadAllText(dlg.FileName)); }
            catch (Exception ex) { Flash(Lang.T("Ese fichero no se pudo leer: ") + ex.Message); return; }
            if (politico.Facciones.Count > 0 &&
                MessageBox.Show(this, Lang.F("Esto sustituye las {0} facciones de {1} por las {2} del fichero. ¿Seguir?",
                                             politico.Facciones.Count, Body.Current.Label, m.Facciones.Count),
                                "Koogle Kerbin", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SetFacTool(null);
            m.Cuerpo = Body.Name;
            m.Version = politico.Version + 1;            // para que se guarde
            UsarPolitico(m);
            FacCambiado();
            Flash(Lang.F("Importadas {0} facciones.", m.Facciones.Count));
        }

        /* El mapa político como imagen equirectangular de 4096×2048, con transparencia donde
           no hay nadie (y en el mar): para usarla en otro sitio o volver a cargarla como capa. */
        void ExportarImagenFacciones()
        {
            if (politico == null || politico.Facciones.Count == 0) { Flash("No hay ningún territorio que exportar."); return; }
            using var dlg = new SaveFileDialog { FileName = "mapa-politico-" + Body.Name.ToLowerInvariant() + ".png", Filter = "PNG (*.png)|*.png" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                int w = MapaPolitico.Ancho, h = MapaPolitico.Alto;
                var col = new int[256];
                var ver = new bool[256];
                foreach (var f in politico.Facciones)
                {
                    if (!f.Visible) continue;
                    col[f.Id] = Theme.Hex(f.Color).ToArgb() & 0xFFFFFF;
                    ver[f.Id] = true;
                }
                var tierra = politico.Tierra;
                using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                var fila = new int[w];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        byte id = politico.Celdas[i];
                        int a = !ver[id] ? 0 : tierra == null ? 255 : tierra[i];
                        fila[x] = (a << 24) | col[id];
                    }
                    Marshal.Copy(fila, 0, data.Scan0 + y * data.Stride, w);
                }
                bmp.UnlockBits(data);
                bmp.Save(dlg.FileName, ImageFormat.Png);
                Flash(Lang.F("Imagen guardada en {0}.", Path.GetFileName(dlg.FileName)));
            }
            catch (Exception ex) { Flash(Lang.T("No se pudo guardar: ") + ex.Message); }
        }
    }
}
