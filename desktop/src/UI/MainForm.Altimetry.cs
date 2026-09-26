using System;
using System.Collections.Generic;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* Filtro de altimetría: se elige una franja de altura y el terreno que cae dentro se
       pinta con una paleta tipo SCANsat, mientras que el de fuera se apaga. Sirve para
       ver de un vistazo dónde hay meseta a tal altura, dónde queda por encima de la
       cota de un océano o qué zona está por debajo de una altitud de aterrizaje.

       Necesita mapa de alturas: el de Kerbin que trae el visor, o el del cuerpo si has
       apuntado a una carpeta con las texturas del juego. La conversión de gris a metros
       es la calibración de la ranura (o la de SCANsat para ese cuerpo). */
    public sealed partial class MainForm
    {
        DarkCheck chkAlt;
        DarkTextBox altMinBox, altMaxBox;
        DarkSlider altOpSlider;
        RichLabel altInfo;
        FieldHeader altOpHeader;

        (double Min, double Max) RangoFiltro()
        {
            var r = RangoAltura();
            double min = state.AltMin ?? r.Min, max = state.AltMax ?? r.Max;
            if (max <= min) max = min + 1;
            return (min, max);
        }

        void AplicarAltimetria()
        {
            var (min, max) = RangoFiltro();
            var (hmin, hmax) = RangoAltura();
            bool on = state.AltFilter && MapTex("height") != null;

            map.AltTex = on ? MapTex("height") : null;
            map.AltOffset = HeightOffNow;
            map.AltOpacity = on ? state.AltOpacity : 0;
            map.AltHMin = hmin; map.AltHMax = hmax;
            map.AltMin = min; map.AltMax = max;

            globe.AltAmt = on ? state.AltOpacity : 0;
            globe.AltMin = min; globe.AltMax = max;

            RenderAltInfo();
            ActualizarEstadosSecciones();
            RequestRender();
        }

        /* Cuánta superficie queda dentro de la franja, pesando por cos(lat) como en los
           biomas: sin eso los polos contarían muchísimo más de lo que ocupan. */
        double PorcentajeEnRango(ImageData img, double min, double max)
        {
            if (img == null) return double.NaN;
            var (hmin, hmax) = RangoAltura();
            double tot = 0, ok = 0;
            for (int y = 0; y < 180; y++)
            {
                double lat = 89.5 - y, w = Math.Cos(lat * Geo.D2R);
                for (int x = 0; x < 360; x++)
                {
                    double lon = x - 179.5;
                    double alt = img.Height_(lat, lon, hmin, hmax, HeightOffNow);
                    tot += w;
                    if (alt >= min && alt <= max) ok += w;
                }
            }
            return tot <= 0 ? double.NaN : ok / tot * 100;
        }

        void RenderAltInfo()
        {
            if (altInfo == null) return;
            var img = MapImg("height");
            if (img == null)
            {
                altInfo.SetText(Lang.F("{0} no tiene mapa de alturas cargado: el filtro no puede medir nada.", Body.Current.Label));
                return;
            }
            var (min, max) = RangoFiltro();
            var (hmin, hmax) = RangoAltura();
            var partes = new List<string>
            {
                Lang.F("Franja <b>{0}</b> a <b>{1}</b> m sobre el mapa de {2} ({3} a {4} m).",
                    Geo.F(min, 0), Geo.F(max, 0), Body.Current.Label, Geo.F(hmin, 0), Geo.F(hmax, 0)),
            };
            if (state.AltFilter)
            {
                double pct = PorcentajeEnRango(img, min, max);
                if (!double.IsNaN(pct)) partes.Add(Lang.F("Dentro de la franja: <b>{0} %</b> de la superficie.", Geo.F(pct, 1)));
            }
            altInfo.SetText(string.Join("\n", partes));
        }

        /* Al cambiar de cuerpo, una franja que no tiene sentido en el nuevo (Kerbin llega
           a 6500 m, Gilly a 6000 menos) se devuelve al rango entero. */
        void AjustarFiltroAlCuerpo()
        {
            var r = RangoAltura();
            if (state.AltMin is double mn && state.AltMax is double mx && (mn < r.Min - 1 || mx > r.Max + 1 || mx <= mn))
            {
                state.AltMin = null; state.AltMax = null;
                SaveSettings();
            }
            var f = RangoFiltro();
            altMinBox?.SetNumber(f.Min);
            altMaxBox?.SetNumber(f.Max);
            AplicarAltimetria();
        }

        void SetAltFilter(bool on)
        {
            state.AltFilter = on;
            SaveSettings();
            AplicarAltimetria();
        }

        void SetAltRange(double? min, double? max)
        {
            state.AltMin = min;
            state.AltMax = max;
            SaveSettings();
            AplicarAltimetria();
        }

        /* Vuelve al rango entero del cuerpo, que es lo que hay que hacer al cambiar de
           cuerpo: una franja de Kerbin no significa nada en Tylo. */
        void ResetAltRange()
        {
            var r = RangoAltura();
            state.AltMin = null; state.AltMax = null;
            altMinBox?.SetNumber(r.Min);
            altMaxBox?.SetNumber(r.Max);
            SaveSettings();
            AplicarAltimetria();
        }
    }
}
