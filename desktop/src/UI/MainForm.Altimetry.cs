using System;
using System.Collections.Generic;
using KerbinMaps.Core;

namespace KerbinMaps.UI
{
    /* Altimetry filter: a height band is chosen and the terrain that falls inside it is painted
       with a SCANsat-style palette, while the rest is dimmed. It shows at a glance where
       there's plateau at a given height, where things stand above an ocean's level or which
       area is below a landing altitude.

       It needs a height map: the Kerbin one the viewer ships, or the body's if you've pointed
       to a folder with the game's textures. The gray-to-meters conversion is the slot's
       calibration (or SCANsat's for that body). */
    public sealed partial class MainForm
    {
        DarkCheck chkAlt;
        DarkTextBox altMinBox, altMaxBox;
        DarkSlider altOpSlider;
        RichLabel altInfo;
        FieldHeader altOpHeader;

        (double Min, double Max) RangoFiltro()
        {
            var r = RangoTerreno();
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

        /* How much surface is inside the band, weighting by cos(lat) as with biomes: without it
           the poles would count far more than they cover. */
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

        /* When changing body, a band that makes no sense on the new one (Kerbin reaches 6500 m,
           Gilly 6000 less) goes back to the full range. */
        void AjustarFiltroAlCuerpo()
        {
            var r = RangoTerreno();
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

        /* Calibrates the height slot as what a dump of the game's textures is: gray 0 at the
           body's minTerrainAltitude and the ramp topping out at gray 145. That's what fixes a
           map taken from the game that was left with the scale of a SCANsat export, which is
           from somewhere else. */
        void CalibrarComoVolcado()
        {
            if (!parallaxRanges.TryGetValue(Body.Name, out var pr))
            {
                Flash(Lang.F("No tengo el rango de alturas de {0}: hace falta Parallax en tu instalación de KSP.", Body.Current.Label));
                return;
            }
            var c = BodyMaps.Calibracion(pr.Min, pr.Max);
            state.HMin = Math.Round(c.Min);
            state.HMax = Math.Round(c.Max);
            hMinBox?.SetNumber(state.HMin);
            hMaxBox?.SetNumber(state.HMax);
            SaveSettings();
            SyncGlobe();
            AjustarFiltroAlCuerpo();
            calOut?.SetText(Lang.F("Calibrado como volcado del juego: gris 0 = <b>{0} m</b>, gris 255 = <b>{1} m</b> " +
                                   "(el terreno de {2} va de {3} a {4} m y esas texturas llegan al gris 145).",
                Geo.F(state.HMin, 0), Geo.F(state.HMax, 0), Body.Current.Label, Geo.F(pr.Min, 0), Geo.F(pr.Max, 0)));
            RequestRender();
        }

        /* A Kerbin height map loaded by hand that is a dump of the game's textures (its gray
           stops at 145) calibrates itself, once per file: with a SCANsat export's scale, the
           KSC came out at −684 m and all the low land ended up under the sea. If the range is
           changed by hand afterwards, that's respected. */
        void CalibrarVolcadoSiToca()
        {
            var img = Img("height");
            string nombre = metas.GetValueOrDefault("height")?.Name;
            if (img == null || nombre == null || state.VolcadoCalibrado == nombre) return;
            if (!parallaxRanges.TryGetValue("Kerbin", out var pr)) return;
            int tope = img.MaxGray();
            if (Math.Abs(tope - BodyMaps.GrisTope) > 6) return;
            state.VolcadoCalibrado = nombre;
            var c = BodyMaps.Calibracion(pr.Min, pr.Max);
            if (Math.Abs(state.HMin - Math.Round(c.Min)) < 1 && Math.Abs(state.HMax - Math.Round(c.Max)) < 1) { SaveSettings(); return; }
            state.HMin = Math.Round(c.Min);
            state.HMax = Math.Round(c.Max);
            hMinBox?.SetNumber(state.HMin);
            hMaxBox?.SetNumber(state.HMax);
            SaveSettings();
            if (OnMapBody) { SyncGlobe(); AjustarFiltroAlCuerpo(); }
            Flash(Lang.F("«{0}» es un volcado de las texturas del juego (su gris llega al {1}, no al 255): calibrado como tal, " +
                         "gris 0 = {2} m y gris 255 = {3} m. Puedes cambiarlo en «Altura (opcional)».",
                nombre, tope, Geo.F(state.HMin, 0), Geo.F(state.HMax, 0)));
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

        /* Goes back to the body's full range, which is what has to happen when changing body: a
           Kerbin band means nothing on Tylo. */
        void ResetAltRange()
        {
            var r = RangoTerreno();
            state.AltMin = null; state.AltMax = null;
            altMinBox?.SetNumber(r.Min);
            altMaxBox?.SetNumber(r.Max);
            SaveSettings();
            AplicarAltimetria();
        }
    }
}
