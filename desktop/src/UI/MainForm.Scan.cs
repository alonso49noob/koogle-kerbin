using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Gfx;
using KerbinMaps.Views;

namespace KerbinMaps.UI
{
    /* SCANsat y progreso de la partida.

       De la partida se saca lo que el juego ya sabe: qué has escaneado con SCANsat, qué
       cuerpos has visitado y con qué hitos. Con eso el visor puede enseñar solo lo
       descubierto (modo progresión) o todo (modo sandbox), como el juego mismo. */
    public sealed partial class MainForm
    {
        SaveExtras extras;                          // null mientras no haya partida
        ScanCoverage cov;                           // cobertura del cuerpo que se ve
        List<Anomaly> anomalias = new();
        Texture scanTex;
        readonly MapLayer anomalyLayer = new();

        DarkCheck chkProgresion, chkScan, chkAnomalias;
        RichLabel scanInfo, progInfo;
        DrawList anomList;

        bool Progresion => state.Progresion;

        /* La partida trae su propio modo; al cargarla se propone el que le toca. */
        void AplicarExtras(SaveExtras nuevos, bool proponerModo)
        {
            extras = nuevos;
            if (proponerModo && extras != null && !extras.Sandbox && !state.Progresion)
            {
                state.Progresion = true;
                if (chkProgresion != null) chkProgresion.Checked = true;
                SaveSettings();
            }
            RenderBodyList();
            ActualizarScan();
        }

        /* Cobertura, anomalías y rótulos del cuerpo que se está viendo. */
        void ActualizarScan()
        {
            cov = extras?.Cobertura(Body.Name);
            if (cov != null && cov.Empty) cov = null;

            anomalias = Anomalies.OfBody(Body.Name);
            Anomalies.Aplicar(anomalias, cov);

            ConstruirScanTex();
            RenderAnomalias();
            RenderScanInfo();
            RequestRender();
        }

        /* Textura de 360×180 con lo no escaneado tapado. Se reconstruye al cambiar de
           cuerpo o de partida, no por fotograma. */
        void ConstruirScanTex()
        {
            bool quiere = state.ShowScan && cov != null;
            if (!quiere)
            {
                if (scanTex != null && surface.MakeCurrent()) { scanTex.Dispose(); scanTex = null; }
                else if (scanTex != null) scanTex = null;
                map.ScanTex = null; map.ScanOpacity = 0;
                globe.ScanTex = null; globe.ScanAmt = 0;
                return;
            }

            const int W = ScanCoverage.W, H = ScanCoverage.H;
            var rgba = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    // la fila 0 de la textura es el norte; la celda 0 de SCANsat es el sur
                    int lat = H - 1 - y;
                    bool visto = cov.Cells[x * H + lat] != 0;
                    int i = (y * W + x) * 4;
                    rgba[i] = 8; rgba[i + 1] = 12; rgba[i + 2] = 20;
                    rgba[i + 3] = (byte)(visto ? 0 : 215);
                }

            if (!surface.MakeCurrent()) return;
            scanTex?.Dispose();
            scanTex = Texture.FromRgba(rgba, W, H, TexFilter.Nearest, true);
            map.ScanTex = scanTex; map.ScanOpacity = 0.82;
            globe.ScanTex = scanTex; globe.ScanAmt = 0.82;
        }

        /* Los pines de las anomalías. En progresión solo salen las detectadas, que es lo
           que el juego te deja ver. */
        void RenderAnomalias()
        {
            anomalyLayer.Clear();
            var visibles = Visibles();
            foreach (var a in visibles)
                anomalyLayer.Dots.Add(new MapDot
                {
                    Lat = a.Lat,
                    Lon = a.Lon,
                    Style = DotStyle.Pin,
                    Fill = ColorF.Hex(a.Identificada ? "#7ee787" : a.Detectada ? "#ffb454" : "#b98cff"),
                    Tooltip = a.Name + " · " + Lang.T(a.Estado),
                    Tag = a,
                });
            anomalyLayer.Visible = state.ShowAnomalies;
            anomList?.SetItems(visibles.Cast<object>());
            SyncGlobe();
        }

        List<Anomaly> Visibles() =>
            (Progresion ? anomalias.Where(a => a.Detectada) : anomalias).ToList();

        void RenderScanInfo()
        {
            if (scanInfo == null) return;

            if (extras == null)
            {
                scanInfo.SetText(Lang.T("Sin partida cargada: carga un persistent.sfs para ver lo que llevas escaneado."));
                progInfo?.SetText("");
                return;
            }

            var partes = new List<string>();
            string modo = extras.Mode ?? "SANDBOX";
            partes.Add(Lang.F("Partida en modo <b>{0}</b>.", modo));

            if (cov == null)
                partes.Add(Lang.F("Sin cobertura de SCANsat en {0}.", Body.Current.Label));
            else
            {
                partes.Add(Lang.F("Escaneado de {0}: altura <b>{1} %</b>, biomas <b>{2} %</b>, anomalías <b>{3} %</b>.",
                    Body.Current.Label,
                    Geo.F(cov.Percent(ScanType.Altura), 1), Geo.F(cov.Percent(ScanType.Biomas), 1),
                    Geo.F(cov.Percent(ScanType.Anomalia), 1)));
                if (!double.IsNaN(cov.MinHeight) && !double.IsNaN(cov.MaxHeight))
                    partes.Add(Lang.F("Rango de altura de SCANsat: {0} a {1} m.", Geo.F(cov.MinHeight, 0), Geo.F(cov.MaxHeight, 0)));
            }

            int det = anomalias.Count(a => a.Detectada);
            if (anomalias.Count > 0)
                partes.Add(Lang.F("Anomalías del catálogo aquí: <b>{0}</b>, detectadas <b>{1}</b>.", anomalias.Count, det));
            scanInfo.SetText(string.Join("\n", partes));

            var p = extras.Hitos(Body.Name);
            progInfo?.SetText(p == null
                ? Lang.F("{0}: sin visitar en esta partida.", Body.Current.Label)
                : Lang.F("{0}: {1}.", Body.Current.Label, Lang.T(p.Resumen())));
        }

        /* ------------------------------------------------------------------ lista */

        void DrawAnomalyRow(Graphics g, Rectangle r, object item, bool hover)
        {
            var a = (Anomaly)item;
            int x = r.X + Theme.S(7);
            DrawDot(g, Theme.Hex(a.Identificada ? "#7ee787" : a.Detectada ? "#ffb454" : "#b98cff"), x, r.Y + r.Height / 2, Theme.S(8));
            x += Theme.S(15);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int wEstado = Theme.S(86);
            TextRenderer.DrawText(g, a.Name, Theme.UISmall, new Rectangle(x, r.Y, r.Right - x - wEstado, r.Height), Theme.Fg,
                flags | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Lang.T(a.Estado), Theme.Tiny, new Rectangle(r.Right - wEstado, r.Y, wEstado - Theme.S(6), r.Height),
                a.Identificada ? Theme.Accent2 : a.Detectada ? Theme.Warn : Theme.FgDim, flags | TextFormatFlags.Right);
        }

        void AnomalyRowClick(object item, Point pt, Rectangle rect)
        {
            var a = (Anomaly)item;
            if (is3D) globe.SetCenter(a.Lat, a.Lon);
            else map.SetView(a.Lat, a.Lon, Math.Max(map.Zoom, 6));
            RequestRender();
        }

        /* ---------------------------------------------------------------- ajustes */

        void SetProgresion(bool on)
        {
            state.Progresion = on;
            SaveSettings();
            RenderBodyList();
            ActualizarScan();
        }

        void SetShowScan(bool on)
        {
            state.ShowScan = on;
            SaveSettings();
            ConstruirScanTex();
            RequestRender();
        }

        void SetShowAnomalias(bool on)
        {
            state.ShowAnomalies = on;
            SaveSettings();
            anomalyLayer.Visible = on;
            SyncGlobe();
            RequestRender();
        }
    }
}
