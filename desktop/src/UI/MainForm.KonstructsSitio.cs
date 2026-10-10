using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Ksp;

namespace KerbinMaps.UI
{
    /* Launch sites in the building editor: turning the selected building into a KK launch site
       that works in the game, not just a model that looks like a pad.

       KK registers a site from the LaunchSite node of an instance: a unique name, whether it
       launches from the VAB, the SPH or either, and the transform of the model the vessel spawns
       on. That transform has to exist in the .mu or KK skips the site, so the choice is limited
       to the model's real spawn transforms (KkModel.SpawnTransforms). A model without any can't
       be a site; KK's «Universal Spawn Point» exists for that: place it on the pad and make it
       the site. New sites are saved open and free, so they're usable right away in career too. */
    public sealed partial class MainForm
    {
        StackPanel kkSitioWrap, kkSitioCampos;
        FieldHeader kkSitioHeader;
        RichLabel kkSitioInfo;
        DarkTextBox kkSitioNombre, kkSitioLargo, kkSitioAncho, kkSitioAlto, kkSitioMasa;
        DarkCombo kkSitioDesde, kkSitioTipo, kkSitioPunto;
        DarkButton kkSitioAplicar, kkSitioQuitar;

        StackPanel BuildKKSitio(Func<string, Action, ButtonVariant, DarkButton> B)
        {
            kkSitioWrap = new StackPanel(8) { BackColor = Theme.Bg2, Padding = new Padding(0, Theme.S(6), 0, 0) };
            kkSitioHeader = new FieldHeader("Sitio de lanzamiento");
            kkSitioWrap.Controls.Add(kkSitioHeader);
            kkSitioInfo = new RichLabel(RichMode.Hint) { HideWhenEmpty = false };
            kkSitioWrap.Controls.Add(kkSitioInfo);

            kkSitioCampos = new StackPanel(8) { BackColor = Theme.Bg2 };
            kkSitioNombre = new DarkTextBox { Placeholder = "Nombre del sitio" };
            kkSitioCampos.Controls.Add(Field("Nombre (único: es el que sale en el selector de KK)", kkSitioNombre));
            kkSitioDesde = new DarkCombo();
            kkSitioDesde.SetItems(new[] { ("VAB", "VAB (cohetes)"), ("SPH", "SPH (aviones)"), ("Any", "Los dos") });
            kkSitioTipo = new DarkCombo();
            kkSitioTipo.SetItems(new[] { ("RocketPad", "Rampa"), ("Runway", "Pista"), ("Helipad", "Helipuerto"), ("Waterlaunch", "Agua"), ("Other", "Otro") });
            kkSitioCampos.Controls.Add(new Row2(Field("Se lanza desde", kkSitioDesde), Field("Tipo", kkSitioTipo)));
            kkSitioPunto = new DarkCombo();
            kkSitioCampos.Controls.Add(Field("Punto de salida (transform del modelo)", kkSitioPunto));
            kkSitioLargo = Num(30); kkSitioAncho = Num(30); kkSitioAlto = Num(50); kkSitioMasa = Num(0);
            kkSitioCampos.Controls.Add(new Row2(Field("Largo (m)", kkSitioLargo), Field("Ancho (m)", kkSitioAncho)));
            kkSitioCampos.Controls.Add(new Row2(Field("Alto (m)", kkSitioAlto), Field("Masa máx. (t, 0 = sin límite)", kkSitioMasa)));
            kkSitioAplicar = B("Hacer sitio de lanzamiento", AplicarKKSitio, ButtonVariant.Primary);
            kkSitioQuitar = B("Quitar sitio", QuitarKKSitio, ButtonVariant.Normal);
            kkSitioCampos.Controls.Add(new EqualRow(kkSitioAplicar, kkSitioQuitar));
            kkSitioWrap.Controls.Add(kkSitioCampos);
            return kkSitioWrap;
        }

        /* Fills the fields from the selected building: its site if it has one, or sensible
           defaults from its model if not. Only when the selection changes, so moving the
           building doesn't wipe what's being typed. */
        void CargarKKSitio()
        {
            if (kkSitioWrap == null) return;
            var i = kkSel;
            bool editable = i != null && !i.DelJuego && !i.Legacy && i.Sitio?.Legacy != true;
            Vis.Set(kkSitioCampos, kkEditando && editable && PuntosDeSalida(i).Count > 0);
            if (i == null) { RenderKKSitioEstado(); return; }

            var s = i.Sitio;
            var m = i.ModelRef;
            string cat = (m?.Category ?? "") + " " + (m?.Title ?? "") + " " + i.Model;
            bool pista = cat.Contains("runway", StringComparison.OrdinalIgnoreCase);
            bool heli = cat.Contains("heli", StringComparison.OrdinalIgnoreCase);
            kkSitioNombre.Text = s?.Name ?? NombreLibre(i);
            kkSitioDesde.SelectedId = s?.Type ?? (pista ? "SPH" : heli ? "Any" : "VAB");
            kkSitioTipo.SelectedId = s?.Category ?? (pista ? "Runway" : heli ? "Helipad" : "RocketPad");
            var puntos = PuntosDeSalida(i);
            kkSitioPunto.SetItems(puntos.Select(p => (p, p)));
            kkSitioPunto.SelectedId = puntos.FirstOrDefault();
            double defL = m?.DefaultSiteLength > 0 ? m.DefaultSiteLength : pista ? 200 : 30;
            double defW = m?.DefaultSiteWidth > 0 ? m.DefaultSiteWidth : pista ? 30 : 30;
            kkSitioLargo.SetNumber(s?.Length > 0 ? s.Length : defL);
            kkSitioAncho.SetNumber(s?.Width > 0 ? s.Width : defW);
            kkSitioAlto.SetNumber(s?.Height > 0 ? s.Height : pista ? 20 : 50);
            kkSitioMasa.SetNumber(s?.MaxMass ?? 0);
            RenderKKSitioEstado();
        }

        /* The model's spawn transforms, with the one the site already uses first even if it
           isn't among them (an existing site keeps working as it is). */
        static List<string> PuntosDeSalida(KkInstance i)
        {
            var res = new List<string>();
            if (i == null) return res;
            if (!string.IsNullOrEmpty(i.Sitio?.Transform)) res.Add(i.Sitio.Transform);
            foreach (var p in i.ModelRef?.SpawnTransforms() ?? Array.Empty<string>())
                if (!res.Contains(p)) res.Add(p);
            return res;
        }

        string NombreLibre(KkInstance i)
        {
            var usados = kk?.NombresDeSitios(i) ?? new HashSet<string>();
            string baseName = i.GroupRef != null ? NombreGrupo(i.GroupRef) : i.Label;
            if (string.IsNullOrWhiteSpace(baseName) || baseName == "Ungrouped") baseName = i.Label;
            string n = baseName;
            for (int k = 2; usados.Contains(n); k++) n = baseName + " " + k;
            return n;
        }

        void RenderKKSitioEstado()
        {
            if (kkSitioInfo == null) return;
            var i = kkSel;
            string estado, info;
            if (i == null) { estado = ""; info = Lang.T("Elige un edificio para convertirlo en un sitio de lanzamiento de Kerbal Konstructs."); }
            else if (i.DelJuego) { estado = ""; info = Lang.T("Es del KSC de serie: el juego ya tiene sus sitios de lanzamiento."); }
            else if (i.Sitio?.Legacy == true) { estado = i.Sitio.Name; info = Lang.T("Sitio en el formato antiguo de KK: ábrelo una vez con KK en el juego para que lo convierta antes de editarlo aquí."); }
            else if (PuntosDeSalida(i).Count == 0)
            {
                estado = "";
                info = Lang.T("Este modelo no tiene ningún punto de salida (un transform «spawn» o «launch»), así que KK no puede usarlo como sitio. " +
                              "Pon encima un <b>Universal Spawn Point</b> (en la lista de modelos de abajo) y haz sitio de lanzamiento ese.");
            }
            else if (i.Sitio == null) { estado = Lang.T("no"); info = Lang.T("Al guardar, aparece en el selector de sitios de KK en el VAB o el SPH y las naves salen sobre él. Se guarda abierto y gratis."); }
            else
            {
                estado = Lang.T(i.SitioCambiado ? "sin guardar" : "activo");
                info = Lang.F("<b>{0}</b>: sale en el selector de sitios de KK.", RichLabel.Esc(i.Sitio.Name));
            }
            kkSitioHeader.Value = estado;
            kkSitioInfo.SetText(info);
            if (kkSitioAplicar != null)
            {
                kkSitioAplicar.Text = Lang.T(i?.Sitio == null ? "Hacer sitio de lanzamiento" : "Aplicar cambios al sitio");
                Vis.Set(kkSitioQuitar, i?.Sitio != null);
                kkSitioAplicar.Parent?.PerformLayout();
            }
        }

        void AplicarKKSitio()
        {
            var i = kkSel;
            if (!PuedeEditar(i)) return;
            string nombre = kkSitioNombre.Text.Trim();
            if (nombre.Length == 0) { Flash("Ponle un nombre al sitio de lanzamiento."); return; }
            if (kk.NombresDeSitios(i).Contains(nombre)) { Flash(Lang.F("Ya hay un sitio llamado «{0}»: KK necesita nombres distintos.", nombre)); return; }
            string punto = kkSitioPunto.SelectedId;
            if (string.IsNullOrEmpty(punto)) { Flash("Este modelo no tiene punto de salida: usa un Universal Spawn Point."); return; }

            var s = i.Sitio?.Clone() ?? new KkLaunchSite { Author = "Koogle Kerbin", State = "Open" };
            s.Name = nombre;
            s.Transform = punto;
            s.Type = kkSitioDesde.SelectedId ?? "Any";
            s.Category = kkSitioTipo.SelectedId ?? "Other";
            s.Length = Math.Max(1, kkSitioLargo.NumberOr(30));
            s.Width = Math.Max(1, kkSitioAncho.NumberOr(30));
            s.Height = Math.Max(1, kkSitioAlto.NumberOr(50));
            s.MaxMass = Math.Max(0, kkSitioMasa.NumberOr(0));
            if (string.IsNullOrEmpty(s.Description)) s.Description = Lang.F("Sitio creado con Koogle Kerbin sobre {0}.", i.Label);
            bool nuevo = i.Sitio == null;
            i.Sitio = s;
            i.LaunchSite = s.Name;
            i.Cambiado = i.SitioCambiado = true;
            TrasCambioKK();
            MarcadoresKK();
            Flash(nuevo ? Lang.F("«{0}» será un sitio de lanzamiento al pulsar «Guardar en los .cfg».", s.Name)
                        : Lang.F("Cambios en «{0}» listos para guardar.", s.Name));
        }

        void QuitarKKSitio()
        {
            var i = kkSel;
            if (!PuedeEditar(i) || i.Sitio == null) return;
            string n = i.Sitio.Name;
            i.Sitio = null;
            i.LaunchSite = null;
            i.Cambiado = i.SitioCambiado = true;
            TrasCambioKK();
            MarcadoresKK();
            CargarKKSitio();
            Flash(Lang.F("«{0}» dejará de ser un sitio de lanzamiento al guardar.", n));
        }
    }
}
