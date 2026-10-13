using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* Finding your way around the sidebar: the sections in labelled groups, a search box that
       filters them by title and by the names of their options, a button to collapse them all,
       and a «getting started» card the first time the app opens. */
    public sealed partial class MainForm
    {
        readonly Dictionary<string, Section> secciones = new();
        readonly List<(Control Etiqueta, List<Section> Secciones)> grupos = new();
        readonly Dictionary<Section, string> textoBuscable = new();
        DarkTextBox filtroSecciones;
        TextLabel sinResultados;
        StackPanel bienvenida;
        bool abriendoPorBusqueda;
        readonly HashSet<Section> abiertasPorBusqueda = new();

        /* Groups in the order they appear, and the sections in each (by their Spanish title,
           the same key that remembers whether they're open). */
        static readonly (string Grupo, string[] Secciones)[] Grupos =
        {
            ("Mapa", new[] { "Cuerpo celeste", "Capas", "Marcadores", "Biomas", "Filtro de altimetría", "Países y facciones" }),
            ("Vistas", new[] { "Vista 3D", "Vista del cielo", "Vuelo", "Calidad gráfica" }),
            ("Partida y naves", new[] { "Naves de una partida", "SCANsat y progreso", "Órbita · traza terrestre", "Transferencias y ventanas", "Aterrizaje", "Grabaciones de vuelo" }),
            ("Herramientas y edificios", new[] { "Herramientas", "Rutas: aire, mar y tierra", "Edificios (Kerbal Konstructs)" }),
            ("Datos e imágenes", new[] { "Datos del mapa", "Altura (opcional)" }),
            ("Ajustes", new[] { "Apariencia", "Idioma · Language", "Actualizaciones" }),
        };

        /* The search bar sits under the sidebar header and doesn't scroll away. */
        Control BuildFiltroSecciones()
        {
            var barra = new Panel { Dock = DockStyle.Top, Height = Theme.S(48), BackColor = Theme.Bg2 };
            filtroSecciones = new DarkTextBox { Placeholder = "Buscar en el panel…", Icon = Theme.Glyph.Search };
            filtroSecciones.Edited += (s, e) => AplicarFiltroSecciones();
            filtroSecciones.Inner.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; filtroSecciones.Text = ""; AplicarFiltroSecciones(); surface?.Focus(); }
            };
            var plegar = new DarkButton(Theme.Glyph.ChevronUp, ButtonVariant.Ghost) { IconFont = Theme.IconSmall, Tip = "Plegar todas las secciones" };
            plegar.Click += (s, e) => PlegarSecciones();
            barra.Controls.Add(filtroSecciones);
            barra.Controls.Add(plegar);
            barra.Layout += (s, e) =>
            {
                int m = Theme.S(12), h = Theme.S(32), y = (barra.Height - h) / 2;
                plegar.SetBounds(barra.Width - m - h + Theme.S(4), y, h, h);
                filtroSecciones.SetBounds(m, y, plegar.Left - Theme.S(6) - m, h);
            };
            return barra;
        }

        /* Called at the end of BuildSidebar: the getting-started card first, then each group
           label followed by its sections. Anything not listed keeps its place at the end. */
        void OrdenarSecciones()
        {
            var orden = new List<Control>();
            bienvenida = BuildBienvenida();
            if (bienvenida != null) { sideStack.Controls.Add(bienvenida); orden.Add(bienvenida); }
            foreach (var (grupo, titulos) in Grupos)
            {
                var lista = titulos.Select(t => secciones.GetValueOrDefault(t)).Where(s => s != null).ToList();
                if (lista.Count == 0) continue;
                var etiqueta = new GrupoLabel(grupo);
                sideStack.Controls.Add(etiqueta);
                orden.Add(etiqueta);
                orden.AddRange(lista);
                grupos.Add((etiqueta, lista));
            }
            sinResultados = new TextLabel(Lang.T("Nada coincide con la búsqueda.")) { Dim = true, Height = Theme.S(48), Center = true };
            sideStack.Controls.Add(sinResultados);
            Vis.Set(sinResultados, false);
            orden.Add(sinResultados);
            sideStack.Reordenar(orden);
        }

        void PlegarSecciones()
        {
            sideStack.SuspendLayout();
            foreach (var s in secciones.Values) if (s.Expanded) s.Expanded = false;
            sideStack.ResumeLayout(true);
            sideScroll.ScrollTo(0);
        }

        /* ------------------------------------------------------------ search */

        void AplicarFiltroSecciones()
        {
            var terminos = Normalizar(filtroSecciones.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool filtrando = terminos.Length > 0;
            sideStack.SuspendLayout();
            int visibles = 0;
            foreach (var (etiqueta, lista) in grupos)
            {
                int enGrupo = 0;
                foreach (var s in lista)
                {
                    bool coincide = !filtrando || terminos.All(TextoBuscable(s).Contains);
                    Vis.Filter(s, !coincide);
                    if (coincide && Vis.Wanted(s)) enGrupo++;
                }
                Vis.Filter(etiqueta, enGrupo == 0);
                visibles += enGrupo;
            }
            // a handful of results open on their own; they close again when the search is cleared
            var halladas = grupos.SelectMany(g => g.Secciones).Where(Vis.Shown).ToList();
            abriendoPorBusqueda = true;
            try
            {
                foreach (var s in abiertasPorBusqueda.Where(s => !filtrando || !halladas.Contains(s) || halladas.Count > 3).ToList())
                {
                    s.Expanded = false;
                    abiertasPorBusqueda.Remove(s);
                }
                if (filtrando && halladas.Count <= 3)
                    foreach (var s in halladas.Where(s => !s.Expanded)) { s.Expanded = true; abiertasPorBusqueda.Add(s); }
            }
            finally { abriendoPorBusqueda = false; }
            if (bienvenida != null) Vis.Filter(bienvenida, filtrando);
            Vis.Set(sinResultados, filtrando && visibles == 0);
            sideStack.ResumeLayout(true);
            sideScroll.ScrollTo(0);
        }

        /* A section is found by its title and by the visible names of its options (checkboxes,
           buttons, field labels); the long hints are left out, they'd match almost anything. */
        string TextoBuscable(Section s)
        {
            if (textoBuscable.TryGetValue(s, out var t)) return t;
            var sb = new StringBuilder(Lang.T(s.Titulo)).Append(' ').Append(s.Titulo);
            void Recorrer(Control c)
            {
                switch (c)
                {
                    case DarkCheck k: sb.Append(' ').Append(k.Text); break;
                    case DarkButton b when b.IconFont == null: sb.Append(' ').Append(b.Text); break;
                    case FieldHeader f: sb.Append(' ').Append(f.Text); break;
                }
                foreach (Control h in c.Controls) Recorrer(h);
            }
            Recorrer(s);
            return textoBuscable[s] = Normalizar(sb.ToString());
        }

        /* Lowercase and without accents, so «vision», «altimetria» or «paises» find their sections. */
        static string Normalizar(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /* ------------------------------------------------------------ getting started */

        /* The first time the app opens: what it is, what to try first and the controls that
           aren't obvious. It stays until «Entendido» and doesn't come back. */
        StackPanel BuildBienvenida()
        {
            if (state.BienvenidaVista) return null;
            var p = new CardPanel();
            p.Controls.Add(new TextLabel(Lang.T("Primeros pasos")) { TextFont = Theme.Header, Height = Theme.S(22) });
            p.Controls.Add(new RichLabel(RichMode.Hint,
                "Un mapa de Kerbin (y de cualquier cuerpo de tu KSP) en 2D, en un globo 3D, desde el suelo mirando al cielo " +
                "o volando a ras de suelo. Arrastra para moverte, la rueda acerca y un clic en el mapa te dice qué hay ahí. " +
                "Arriba puedes buscar sitios como <b>KSC</b> o unas coordenadas.") { HideWhenEmpty = false, BackColor = Theme.Bg3 });

            DarkButton B(string texto, Action a, ButtonVariant v = ButtonVariant.Normal)
            {
                var b = new DarkButton(texto, v, small: true);
                b.Click += (s, e) => a();
                return b;
            }
            p.Controls.Add(new EqualRow(B("Abrir mi partida…", async () => await PickSaveFile(), ButtonVariant.Primary), B("Ir al KSC", IrAlKsc)));
            p.Controls.Add(new EqualRow(B("Ver el globo 3D", () => SetViewMode("3d")), B("Volar a ras de suelo", () => SetViewMode("free"))));
            p.Controls.Add(new RichLabel(RichMode.Hint,
                "Con la partida (<code>saves/…/persistent.sfs</code>) salen tus naves con su órbita y lo que hayas descubierto. " +
                "Todo lo demás está en las secciones de abajo, por grupos; el buscador de encima del panel las filtra.")
                { HideWhenEmpty = false, BackColor = Theme.Bg3 });
            p.Controls.Add(new BtnRow(B("Entendido", CerrarBienvenida, ButtonVariant.Ghost)));
            return p;
        }

        void CerrarBienvenida()
        {
            state.BienvenidaVista = true;
            SaveSettings();
            if (bienvenida != null) Vis.Set(bienvenida, false);
        }

        void IrAlKsc()
        {
            const double lat = -0.0972, lon = -74.5577;
            if (Body.Current != SolarSystem.Home) SetBody(SolarSystem.Home.Name);
            if (GlobeVisible && !is3D) SetViewMode("2d");
            if (is3D) globe.SetCenter(lat, lon, 1.08);
            else map.SetView(lat, lon, Math.Max(map.Zoom, 8));
            SaveView();
            RequestRender();
        }

        /* The label over each group of sections. */
        sealed class GrupoLabel : DarkControl
        {
            public GrupoLabel(string texto)
            {
                Text = Lang.T(texto);
                Height = Theme.S(34);
                Cursor = Cursors.Default;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(ParentBack);
                TextRenderer.DrawText(g, Text, Theme.Semibold, new Rectangle(Theme.S(16), Theme.S(10), Width - Theme.S(32), Height - Theme.S(14)),
                    Theme.Accent, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                using var pen = new Pen(Theme.Line);
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }
        }

        /* A highlighted card inside the sidebar (the getting-started one). */
        sealed class CardPanel : StackPanel
        {
            public CardPanel() : base(10)
            {
                BackColor = Theme.Bg3;
                Padding = new Padding(Theme.S(14), Theme.S(12), Theme.S(14), Theme.S(12));
            }

            /* The card's own colour is painted inside the margin around it, against the sidebar. */
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Bg3);
                using var pen = new Pen(Theme.Current.LineStrong);
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                using var b = new SolidBrush(Theme.Accent);
                g.FillRectangle(b, 0, 0, Theme.S(3), Height);
            }
        }
    }
}
