using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Ksp;

namespace KerbinMaps.UI
{
    /* Kerbal Konstructs building editor.

       With «Editar edificios» on, a click on a building in flight or sky view selects it (it
       shows highlighted). It moves relative to where the camera is looking, rotates around the
       vertical, scales, drops to the ground, is duplicated or deleted; and new models can be
       placed in front of the camera. Nothing touches the disk until you press «Guardar en los
       .cfg», which changes only the necessary lines and leaves a copy of each file first (see
       KonstructsWriter). Better with KSP closed: KK rewrites its files when saving from the
       game.

       Keys with a building selected: W/S forward and back, A/D sideways, R/F up and down (the
       same keys as flying, which take over the building while it's selected; I/K, J/L and U/O
       still work), Shift for ten steps, Q/E rotate, Del delete, Esc release. */
    public sealed partial class MainForm
    {
        bool kkEditando;
        KkInstance kkSel;
        DateTime kkLeidoEn;
        DarkCheck chkKKEditar;
        RichLabel kkSelInfo, kkCambiosInfo;
        DarkCombo kkPaso, kkGiro;
        FieldHeader kkEscalaHeader;
        DrawList kkModelList;
        DarkTextBox kkFiltro;
        KkModel kkModeloElegido;
        readonly List<Control> kkEditControles = new();

        void BuildKKEditor(Section s)
        {
            chkKKEditar = new DarkCheck("Editar edificios (clic en uno, en el vuelo o el cielo)", false);
            chkKKEditar.CheckedChanged += (o, e) => SetKKEditando(chkKKEditar.Checked);
            s.Add(Checks(chkKKEditar));

            T Ed<T>(T c) where T : Control { kkEditControles.Add(c); return s.Add(c); }

            // shown only while editing, not whenever they get text (Readout would show itself)
            kkSelInfo = Ed(new RichLabel(RichMode.Readout) { HideWhenEmpty = false });
            kkPaso = new DarkCombo();
            kkPaso.SetItems(new[] { ("0.1", "Paso 0,1 m"), ("1", "Paso 1 m"), ("10", "Paso 10 m"), ("100", "Paso 100 m") });
            kkPaso.SelectedId = "1";
            kkGiro = new DarkCombo();
            kkGiro.SetItems(new[] { ("1", "Giro 1°"), ("5", "Giro 5°"), ("15", "Giro 15°"), ("45", "Giro 45°"), ("90", "Giro 90°") });
            kkGiro.SelectedId = "15";
            Ed(new Row2(kkPaso, kkGiro));

            DarkButton B(string t, Action a, ButtonVariant v = ButtonVariant.Normal)
            {
                var b = new DarkButton(t, v, small: true);
                b.Click += (o, e) => a();
                return b;
            }
            /* The pad laid out like the keys: rotate on either side of forward, the three
               directions below it; then height, size and what's done with the building. */
            var mover = new StackPanel(6) { BackColor = Theme.Bg2 };
            mover.Controls.Add(new FieldHeader("Mover", Lang.T("W A S D · Q/E gira")));
            mover.Controls.Add(new EqualRow(B("⟲ Girar", () => GirarKK(-1)), B("Adelante", () => MoverKK(1, 0, 0)), B("Girar ⟳", () => GirarKK(1))));
            mover.Controls.Add(new EqualRow(B("Izquierda", () => MoverKK(0, -1, 0)), B("Atrás", () => MoverKK(-1, 0, 0)), B("Derecha", () => MoverKK(0, 1, 0))));
            Ed(mover);
            var altura = new StackPanel(6) { BackColor = Theme.Bg2 };
            altura.Controls.Add(new FieldHeader("Altura", "R / F"));
            altura.Controls.Add(new EqualRow(B("Subir", () => MoverKK(0, 0, 1)), B("Bajar", () => MoverKK(0, 0, -1)), B("Al suelo", AlSueloKK)));
            Ed(altura);
            var tamano = new StackPanel(6) { BackColor = Theme.Bg2 };
            kkEscalaHeader = new FieldHeader("Tamaño");
            tamano.Controls.Add(kkEscalaHeader);
            tamano.Controls.Add(new EqualRow(B("Más pequeño", () => EscalarKK(1 / 1.1)), B("Más grande", () => EscalarKK(1.1))));
            Ed(tamano);
            Ed(new EqualRow(B("Duplicar", DuplicarKK), B("Borrar", BorrarKK), B("Soltar", () => SeleccionarKK(null))));

            Ed(BuildKKSitio(B));

            Ed(new FieldHeader("Añadir un edificio"));
            kkFiltro = Ed(new DarkTextBox { Placeholder = "Buscar modelo para añadir..." });
            kkFiltro.Edited += (o, e) => RenderKKModelos();
            kkModelList = Ed(new DrawList(180));
            kkModelList.DrawItem = DrawKKModelRow;
            kkModelList.ItemClick = (item, pt, r) => { kkModeloElegido = (KkModel)item; kkModelList.Invalidate(); };
            kkModelList.IsSelected = item => item == kkModeloElegido;
            Ed(new BtnRow(B("Poner delante de la cámara", PonerKK)));

            kkCambiosInfo = Ed(new RichLabel(RichMode.Readout) { HideWhenEmpty = false });
            Ed(new BtnRow(B("Guardar en los .cfg", GuardarKK, ButtonVariant.Primary), B("Descartar cambios", DescartarKK)));
            Ed(Hint("Los cambios no tocan el disco hasta <b>Guardar</b>, que modifica solo las líneas de cada edificio y " +
                    "antes copia el fichero a <code>%LOCALAPPDATA%\\KoogleKerbin\\kk-copias</code>. Guarda con KSP cerrado: " +
                    "KK reescribe sus ficheros desde el juego. Con un edificio elegido: <b>W/S</b> adelante y atrás, <b>A/D</b> a los lados, " +
                    "<b>R/F</b> subir y bajar (con <b>Mayús</b>, diez pasos), <b>Q/E</b> girar, <b>Supr</b> borrar, " +
                    "<b>Esc</b> soltar y volver a volar con WASD."));
            foreach (var c in kkEditControles) Vis.Set(c, false);
        }

        void SetKKEditando(bool on)
        {
            kkEditando = on;
            foreach (var c in kkEditControles) Vis.Set(c, on);
            if (!on) SeleccionarKK(null);
            RenderKKModelos();
            RenderKKSel();
            CargarKKSitio();
        }

        void SeleccionarKK(KkInstance i)
        {
            kkSel = i;
            globe.StaticSelected = i;
            RenderKKSel();
            CargarKKSitio();
            RequestRender();
        }

        /* ------------------------------------------------------------ changes */

        double PasoKK => double.TryParse(kkPaso?.SelectedId, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 1;
        double GiroKK => double.TryParse(kkGiro?.SelectedId, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 15;
        double AzCamara => isFree ? globe.FreeAz : isSky ? globe.SkyAz : is3D ? globe.CamHeading : 0;
        bool VistaDeSuelo => isFree || isSky || (is3D && globe.PlanetaCerca);

        bool PuedeEditar(KkInstance i, bool duplicar = false)
        {
            if (i == null) { Flash("Elige antes un edificio: clic sobre él en el vuelo o en el cielo."); return false; }
            if (i.DelJuego && !duplicar) { Flash("Es un edificio del KSC de serie: no está en ningún .cfg y no se puede mover ni borrar. «Duplicar» hace una copia de Kerbal Konstructs que sí."); return false; }
            if (i.Legacy) { Flash("Este edificio usa el formato antiguo de KK, sin grupo: ábrelo una vez con KK en el juego para que lo convierta."); return false; }
            return true;
        }

        /* forward/back and sideways according to where the camera looks; up, the vertical */
        void MoverKK(double adelante, double lado, double arriba)
        {
            if (!PuedeEditar(kkSel)) return;
            double d = PasoKK, az = AzCamara * Math.PI / 180;
            double dN = (adelante * Math.Cos(az) - lado * Math.Sin(az)) * d;
            double dE = (adelante * Math.Sin(az) + lado * Math.Cos(az)) * d;
            KkDatabase.Move(kkSel, dE, dN, arriba * d);
            TrasCambioKK();
        }

        void GirarKK(int sentido)
        {
            if (!PuedeEditar(kkSel)) return;
            KkDatabase.Rotate(kkSel, sentido * GiroKK);
            TrasCambioKK();
        }

        void EscalarKK(double k)
        {
            if (!PuedeEditar(kkSel)) return;
            kkSel.Scale = Math.Clamp(kkSel.Scale * k, 0.01, 100);
            kkSel.Cambiado = true;
            TrasCambioKK();
        }

        void AlSueloKK()
        {
            if (!PuedeEditar(kkSel)) return;
            KkDatabase.Move(kkSel, 0, 0, AlturaDelSuelo(kkSel.Lat, kkSel.Lon) - kkSel.Alt);
            TrasCambioKK();
        }

        void DuplicarKK()
        {
            if (!PuedeEditar(kkSel, duplicar: true)) return;
            var n = kk.Duplicate(kkSel, Body.Radius);
            SeleccionarKK(n);
            TrasCambioKK();
        }

        void BorrarKK()
        {
            if (kkSel == null || (kkSel.DelJuego && !PuedeEditar(kkSel))) return;
            kkSel.Borrado = true;
            kkSel.Placed = false;
            if (kkSel.GroupRef != null) kkSel.GroupRef.Count--;
            SeleccionarKK(null);
            TrasCambioKK();
            MarcadoresKK();
            SyncGlobe();
        }

        /* A new model 60 m in front of the camera, on the ground, with its back to it. */
        void PonerKK()
        {
            if (kk == null) return;
            if (kkModeloElegido == null) { Flash("Elige un modelo de la lista."); return; }
            double lat, lon;
            if (isFree || isSky)
            {
                double la0 = isFree ? globe.FreeLat : state.ObsLat, lo0 = isFree ? globe.FreeLon : state.ObsLon;
                double az = AzCamara * Math.PI / 180, dist = 60 / Body.Radius * 180 / Math.PI;
                lat = la0 + Math.Cos(az) * dist;
                lon = lo0 + Math.Sin(az) * dist / Math.Max(0.05, Math.Cos(la0 * Math.PI / 180));
            }
            else if (GlobeVisible) { var c = globe.Center(); lat = c.Lat; lon = c.Lon; }
            else { lat = map.CenterLat; lon = Geo.WrapLon(map.CenterLon); }
            var i = kk.Add(kkModeloElegido, Body.Name, Body.Radius, lat, lon, AlturaDelSuelo(lat, lon), AzCamara,
                           AlturaDelSuelo, "KoogleKerbin");
            SeleccionarKK(i);
            TrasCambioKK();
            MarcadoresKK();
            SyncGlobe();
        }

        void TrasCambioKK()
        {
            if (kkSel != null && !kkSel.Borrado) kk.PlaceOne(kkSel, Body.Radius);
            RenderKKSel();
            RequestRender();
        }

        async void GuardarKK()
        {
            if (kk == null || !KonstructsWriter.HayCambios(kk)) { Flash("No hay cambios que guardar."); return; }
            string uuid = kkSel?.Uuid;
            KonstructsWriter.Resultado r;
            try { r = KonstructsWriter.Guardar(kk, Path.Combine(Store.LocalDir, "kk-copias"), kkLeidoEn); }
            catch (Exception ex) { Flash(Lang.T("No se pudo guardar: ") + ex.Message); return; }
            string msg = Lang.F("Guardado: {0} edificios y {1} grupos en {2} ficheros.", r.Instancias, r.Grupos, r.Ficheros);
            if (r.Errores.Count > 0) msg += " " + Lang.F("Con problemas: {0}", string.Join("; ", r.Errores.Take(3)));
            Flash(msg);
            await CargarKonstructs(forzar: true);
            if (uuid != null && kk != null) SeleccionarKK(kk.Instances.FirstOrDefault(i => i.Uuid == uuid));
        }

        async void DescartarKK()
        {
            if (kk == null || !KonstructsWriter.HayCambios(kk)) return;
            SeleccionarKK(null);
            await CargarKonstructs(forzar: true);
            Flash("Cambios descartados: los edificios vuelven a estar como en los .cfg.");
        }

        /* ------------------------------------------------------------ keys */

        bool KKKeyDown(KeyEventArgs e)
        {
            if (!kkEditando || kkSel == null || !VistaDeSuelo || FocusInText()) return false;
            // with a building selected, WASD moves it instead of the flight camera; Shift, ten steps at once
            double k = e.Shift ? 10 : 1;
            switch (e.KeyCode)
            {
                case Keys.W: case Keys.I: MoverKK(k, 0, 0); break;
                case Keys.S: case Keys.K: MoverKK(-k, 0, 0); break;
                case Keys.A: case Keys.J: MoverKK(0, -k, 0); break;
                case Keys.D: case Keys.L: MoverKK(0, k, 0); break;
                case Keys.R: case Keys.O: MoverKK(0, 0, k); break;
                case Keys.F: case Keys.U: MoverKK(0, 0, -k); break;
                case Keys.Q: GirarKK(-1); break;
                case Keys.E: GirarKK(1); break;
                case Keys.Delete: BorrarKK(); break;
                case Keys.Escape: SeleccionarKK(null); break;
                default: return false;
            }
            e.Handled = true;
            // F would also toggle following the vessel through KeyPress
            e.SuppressKeyPress = true;
            return true;
        }

        /* Click in flight or sky view with the editor active: the building under the mouse. */
        bool KKClick(int x, int y)
        {
            if (!kkEditando || !VistaDeSuelo) return false;
            var i = globe.PickStatic(x, y);
            if (i == null) return false;
            SeleccionarKK(i);
            return true;
        }

        /* ------------------------------------------------------------ texts */

        void RenderKKSel()
        {
            if (kkSelInfo == null) return;
            if (kkEscalaHeader != null) kkEscalaHeader.Value = kkSel == null ? "" : "×" + Geo.F(kkSel.Scale, 2);
            RenderKKSitioEstado();
            if (kkSel == null)
                kkSelInfo.SetText(Lang.T("Ningún edificio elegido. Haz clic en uno en el vuelo o en el cielo."));
            else
            {
                var i = kkSel;
                string h = "<b>" + RichLabel.Esc(i.Label) + "</b>";
                if (i.Label != i.Model) h += " <m>(" + RichLabel.Esc(i.Model) + ")</m>";
                h += "\n" + Lang.F("Grupo {0}", RichLabel.Esc(i.GroupRef != null ? NombreGrupo(i.GroupRef) : i.Group));
                h += "\n<m>" + Geo.FmtLat(i.Lat) + "  ·  " + Geo.FmtLon(i.Lon) + "</m>";
                h += "\n<m>" + Lang.F("altura {0} (suelo {1}) · rumbo {2}° · escala {3}", Geo.FmtAlt(i.Alt), Geo.FmtAlt(AlturaDelSuelo(i.Lat, i.Lon)),
                                         Geo.F(KkDatabase.Heading(i), 1), Geo.F(i.Scale, 2)) + "</m>";
                if (i.CfgPath != null && kk?.GameData != null) h += "\n<m>" + RichLabel.Esc(Path.GetRelativePath(kk.GameData, i.CfgPath)) + "</m>";
                else if (i.Nuevo) h += "\n" + Lang.T("Nuevo: irá a KerbalKonstructs/NewInstances al guardar.");
                else if (i.DelJuego) h += "\n" + Lang.T("Del KSC de serie: se ve, pero no se edita.");
                kkSelInfo.SetText(h);
            }
            if (kkCambiosInfo != null)
            {
                int n = kk == null ? 0 : kk.Instances.Count(i => i.Cambiado || i.Borrado || i.Nuevo);
                kkCambiosInfo.SetText(n == 0 ? Lang.T("Sin cambios pendientes.") : Lang.F("<b>{0}</b> edificios con cambios sin guardar.", n));
            }
        }

        void RenderKKModelos()
        {
            if (kkModelList == null) return;
            if (kk == null || !kkEditando) { kkModelList.SetItems(new List<object>()); return; }
            string f = kkFiltro?.Text?.Trim() ?? "";
            var lista = kk.Models.Values.Where(m => m.HasMesh)
                .Where(m => f.Length == 0 || m.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                            || (m.Title ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                            || (m.Category ?? "").Contains(f, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Category ?? "~").ThenBy(m => m.Title ?? m.Name).Cast<object>();
            kkModelList.SetItems(lista);
        }

        void DrawKKModelRow(Graphics g, Rectangle r, object item, bool hover)
        {
            var m = (KkModel)item;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            int x = r.X + Theme.S(8), wCat = Theme.S(90);
            TextRenderer.DrawText(g, string.IsNullOrEmpty(m.Title) ? m.Name : m.Title, Theme.UISmall,
                new Rectangle(x, r.Y, r.Right - x - wCat, r.Height), m == kkModeloElegido ? Theme.Accent : Theme.Fg, flags);
            TextRenderer.DrawText(g, m.Category ?? "", Theme.Tiny, new Rectangle(r.Right - wCat, r.Y, wCat - Theme.S(6), r.Height),
                Theme.FgDim, flags | TextFormatFlags.Right);
        }
    }
}
