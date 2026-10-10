using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Ksp;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* Kerbal Konstructs buildings: read from the KSP installation's .cfg files, their groups
       show up as markers on the map and the globe, and in flight and sky views they're seen
       with their models and textures (the stock KSC ones, from the game's data). */
    public sealed partial class MainForm
    {
        KkDatabase kk;
        string kkDe;                                // the GameData it was read from
        object[] kkFirma;                           // which body and heights it was placed with
        readonly Dictionary<KkModel, AssembledVessel> kkModelos = new();
        readonly HashSet<KkModel> kkCargando = new();
        readonly MapLayer kkLayer = new();
        DarkCheck chkKK;
        RichLabel kkInfo;
        DrawList kkList;
        Section secKK;

        static readonly string ColorKK = "#e3b341";

        void BuildKonstructsSection()
        {
            var s = secKK = AddSection("Edificios (Kerbal Konstructs)", false);
            chkKK = new DarkCheck("Ver los edificios de Kerbal Konstructs", state.ShowStatics);
            chkKK.CheckedChanged += async (o, e) =>
            {
                state.ShowStatics = chkKK.Checked;
                SaveSettings();
                MarcarGraficosPersonalizado();
                if (chkKK.Checked) await CargarKonstructs();
                else AplicarKonstructs();
            };
            s.Add(Checks(chkKK));
            kkInfo = s.Add(Readout());
            kkList = s.Add(new DrawList(220));
            kkList.DrawItem = DrawKKRow;
            kkList.ItemClick = KKRowClick;
            var recargar = new DarkButton("Volver a leer");
            recargar.Click += async (o, e) => await CargarKonstructs(forzar: true);
            var carpeta = new DarkButton("Carpeta de KSP…", ButtonVariant.Ghost, small: true);
            carpeta.Click += (o, e) => PickKspFolder();
            s.Add(new BtnRow(recargar, carpeta));
            BuildKKEditor(s);
            s.Add(Hint("Los grupos de edificios de <b>Kerbal Konstructs</b> que tengas instalados (KSC Extended, Tundra " +
                       "Space Center, Kerbin Side...) salen como marcadores. En el vuelo y en el cielo se ven con sus " +
                       "modelos, colocados con las mismas cuentas que KK, sobre el terreno del mapa de alturas. Las texturas " +
                       "del KSC que reutilizan se leen de los datos del propio juego."));
            RenderKKInfo();
        }

        /* Reads (or rereads) the installation's KK database. */
        async Task CargarKonstructs(bool forzar = false)
        {
            if (!state.ShowStatics) { AplicarKonstructs(); return; }
            string gd = FindGameData();
            if (gd == null) { kk = null; kkDe = null; AplicarKonstructs(); return; }
            if (!forzar && kk != null && kkDe == gd) { AplicarKonstructs(); return; }
            kkDe = gd;
            kkInfo?.SetText(Lang.T("Leyendo los edificios de Kerbal Konstructs..."));
            var leidoEn = DateTime.UtcNow;
            string casa = SolarSystem.Home.Name;
            var db = await Task.Run(() =>
            {
                try { return KkDatabase.Load(gd, casa); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[kk] " + ex.Message); return null; }
            });
            if (kkDe != gd) return;                 // another folder was chosen in the meantime
            QuitarModelosKK();
            SeleccionarKK(null);
            db?.NivelesKsc(NivelKsc);
            kk = db;
            kkLeidoEn = leidoEn;
            kkFirma = null;
            AplicarKonstructs();
            RenderKKModelos();
            RenderKKSel();
        }

        /* The level of a KSC facility in the loaded save, or null (the highest). */
        double? NivelKsc(string instalacion) =>
            instalacion != null && extras != null && extras.NivelesKsc.TryGetValue(instalacion, out double v) ? v : null;

        /* When loading another save: the KSC with its levels. */
        void NivelesKscDePartida()
        {
            if (kk == null || !kk.NivelesKsc(NivelKsc)) return;
            kkFirma = null;
            AplicarKonstructs();
        }

        /* Places the buildings of the viewed body and rebuilds markers, list and globe. */
        void AplicarKonstructs()
        {
            ColocarKK();
            MarcadoresKK();
            ColocarNavesEnSuelo();         // the terrain under the vessels may have changed
            SyncGlobe();
        }

        /* Places them again if the body or the height map changed (groups sit on the terrain).
           Says whether it did anything. */
        bool ColocarKK()
        {
            bool on = state.ShowStatics && kk != null;
            globe.Statics = on ? kk : null;
            globe.StaticsOn = on;
            globe.StaticModel = on ? ModeloKK : null;
            if (!on) { PonerAplanados(null); return false; }
            var firma = new object[] { Body.Name, MapImg("height"), HMinNow, HMaxNow, HeightOffNow };
            if (kkFirma != null && firma.SequenceEqual(kkFirma)) return false;
            PonerAplanados(ExplanadaKsc());
            kk.Place(Body.Name, Body.Radius, AlturaDelSuelo);
            kkFirma = firma;
            return true;
        }

        /* The KSC's leveled area: flat at the height of its lawns up to 2 km from the center
           (the runway is 2.9) and blended into the terrain up to 3.5 km. Only if its stock
           buildings are shown, which is what needs it. */
        Aplanado[] aplanados;

        Aplanado[] ExplanadaKsc()
        {
            if (kk == null || !kk.Groups.TryGetValue(Body.Name + "_KSC_Builtin", out var g) || !g.Builtin) return null;
            if (!kk.Instances.Any(i => i.DelJuego && i.Body == Body.Name)) return null;
            // the lawns are 24.8 m above the KSC center, and that is at its height above the sea
            return new[] { Aplanado.En(g.Lat, g.Lon, 2000, 3500, g.RadiusOffset + 24.8 - 0.4) };
        }

        void PonerAplanados(Aplanado[] a)
        {
            bool igual = (a == null && aplanados == null) || (a != null && aplanados != null && a.Length == aplanados.Length
                          && a.Zip(aplanados).All(p => p.First.H == p.Second.H && p.First.N.SequenceEqual(p.Second.N)));
            if (igual) return;
            aplanados = a;
            globe.Aplanados = a ?? Array.Empty<Aplanado>();
        }

        void MarcadoresKK()
        {
            bool on = state.ShowStatics && kk != null;
            kkLayer.Clear();
            var grupos = GruposVisibles();
            foreach (var g in grupos)
                kkLayer.Dots.Add(new MapDot
                {
                    Lat = g.Lat, Lon = g.Lon, Style = DotStyle.Pin, Fill = ColorF.Hex(ColorKK),
                    Tooltip = NombreGrupo(g) + " · " + Lang.F("{0} edificios", g.Count), Tag = g,
                });
            kkLayer.Visible = on;
            kkList?.SetItems(grupos.Cast<object>());
            RenderKKInfo();
        }

        List<KkGroup> GruposVisibles() =>
            kk == null || !state.ShowStatics ? new List<KkGroup>()
                : kk.Groups.Values.Where(g => g.Body == Body.Name && g.Count > 0).OrderByDescending(g => g.Count).ToList();

        static string NombreGrupo(KkGroup g) =>
            g.Builtin ? g.Name.Replace("_Builtin", "") + " (" + Lang.T("del juego") + ")" : g.Name;

        IEnumerable<GlobePin> PinesKK() =>
            GruposVisibles().Select(g => new GlobePin { Lat = g.Lat, Lon = g.Lon, Name = NombreGrupo(g), Color = ColorF.Hex(ColorKK), Tag = g });

        void RenderKKInfo()
        {
            if (kkInfo == null) return;
            if (!state.ShowStatics) { kkInfo.SetText(""); return; }
            if (kk == null)
            {
                kkInfo.SetText(FindGameData() == null
                    ? Lang.T("No encuentro la instalación de KSP. Usa «Carpeta de KSP…» para indicarla.")
                    : Lang.T("Sin leer todavía."));
                return;
            }
            var aqui = kk.Instances.Where(i => i.Body == Body.Name).ToList();
            var partes = new List<string>
            {
                Lang.F("<b>{0}</b> modelos y <b>{1}</b> edificios en {2} ficheros.", kk.Models.Count, kk.Instances.Count, kk.Files),
                Lang.F("En {0}: <b>{1}</b> edificios en <b>{2}</b> grupos.", Body.Current.Label, aqui.Count(i => i.Placed), GruposVisibles().Count),
            };
            int stock = aqui.Count(i => i.ModelRef == null);
            if (stock > 0) partes.Add(Lang.F("{0} usan modelos que no encuentro (ni .mu ni edificios del juego).", stock));
            int ksc = aqui.Count(i => i.DelJuego);
            if (ksc > 0) partes.Add(Lang.F("El KSC de serie: {0} instalaciones, leídas de los datos del juego.", ksc));
            if (globe.StaticsVisible > 0) partes.Add(Lang.F("A la vista: {0}.", globe.StaticsVisible));
            if (kkCargando.Count > 0) partes.Add(Lang.F("Cargando {0} modelos...", kkCargando.Count));
            kkInfo.SetText(string.Join("\n", partes));
        }

        /* The assembled model of a building. The first time it's requested in the background
           and null is returned; once it's ready, it's painted again. */
        AssembledVessel ModeloKK(KkModel m)
        {
            if (kkModelos.TryGetValue(m, out var a)) return a;
            if (kkCargando.Add(m))
            {
                string gd = kk?.GameData;
                var stock = StockAssets.For(gd);
                Task.Run(() =>
                {
                    try
                    {
                        var r = m.Build(gd, stock);
                        r.LoadTextures();
                        return r;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[kk] " + m.Name + ": " + ex.Message);
                        return new AssembledVessel();
                    }
                }).ContinueWith(t =>
                {
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (!kkCargando.Remove(m)) return;   // it was discarded in the meantime
                            kkModelos[m] = t.Result;
                            RenderKKInfo();
                            RequestRender();
                        }));
                    }
                    catch (InvalidOperationException) { }
                });
            }
            return null;
        }

        void QuitarModelosKK()
        {
            kkModelos.Clear();
            kkCargando.Clear();
            if (glOk && surface.MakeCurrent()) globe.DisposeStatics();
        }

        /* ------------------------------------------------------------ list */

        void DrawKKRow(Graphics g, Rectangle r, object item, bool hover)
        {
            var gr = (KkGroup)item;
            int x = r.X + Theme.S(7);
            DrawDot(g, Theme.Hex(ColorKK), x, r.Y + r.Height / 2, Theme.S(8));
            x += Theme.S(15);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int wN = Theme.S(70);
            TextRenderer.DrawText(g, NombreGrupo(gr), Theme.UISmall, new Rectangle(x, r.Y, r.Right - x - wN, r.Height), Theme.Fg, flags | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Lang.F("{0} edificios", gr.Count), Theme.Tiny, new Rectangle(r.Right - wN, r.Y, wN - Theme.S(6), r.Height),
                Theme.FgDim, flags | TextFormatFlags.Right);
        }

        void KKRowClick(object item, Point pt, Rectangle rect)
        {
            var g = (KkGroup)item;
            if (isFree) { VolarAGrupo(g); return; }
            if (GlobeVisible) globe.SetCenter(g.Lat, g.Lon);
            else map.SetView(g.Lat, g.Lon, Math.Max(map.Zoom, 9));
            ShowKKPopup(g);
            RequestRender();
        }

        void ShowKKPopup(KkGroup g)
        {
            string h = "<b>" + RichLabel.Esc(NombreGrupo(g)) + "</b>\n<m>" + Geo.FmtLat(g.Lat) + "  ·  " + Geo.FmtLon(g.Lon) + "</m>";
            h += "\n" + Lang.F("{0} edificios · altura del centro {1}", g.Count, Geo.FmtAlt(g.Alt));
            if (g.CfgPath != null && kk?.GameData != null)
                h += "\n<m>" + RichLabel.Esc(Path.GetRelativePath(kk.GameData, g.CfgPath)) + "</m>";
            var sitios = kk.Instances.Where(i => i.GroupRef == g && !string.IsNullOrEmpty(i.LaunchSite)).Select(i => i.LaunchSite).ToList();
            if (sitios.Count > 0) h += "\n" + Lang.T("Sitios de lanzamiento") + ": " + RichLabel.Esc(string.Join(", ", sitios.Take(6))) + (sitios.Count > 6 ? "..." : "");
            string coords = Geo.F(g.Lat, 6) + ", " + Geo.F(g.Lon, 6);
            ShowPopup(new LatLon(g.Lat, g.Lon), h,
                ("Volar aquí", b => { popup.Hide(); VolarAGrupo(g); }),
                ("Copiar coordenadas", b => CopyText(coords, b)));
        }

        /* To the flight view, half a kilometer south of the group and looking at it. */
        void VolarAGrupo(KkGroup g)
        {
            if (!isFree) SetViewMode("free");
            if (!isFree) return;
            double atras = 600 / Body.Radius * 180 / Math.PI;
            globe.SetFree(g.Lat - atras, g.Lon, AlturaDelSuelo(g.Lat - atras, g.Lon) + 120, 0);
            globe.FreeEl = -8;
            RequestRender();
        }
    }
}
