using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.UI
{
    /* The body being viewed: the selector, the live switch and reading the KSP installation's
       solar system (Kopernicus) at startup. */
    public sealed partial class MainForm
    {
        DarkCombo bodyCombo;
        RichLabel bodySource;

        /* The maps, biomes, heights and markers the viewer ships are Kerbin's. */
        bool OnMapBody => Body.Current.Name == "Kerbin";
        Texture MapTex(string slot) => OnMapBody ? textures.GetValueOrDefault(slot) : bodyTextures.GetValueOrDefault(slot);
        ImageData MapImg(string slot) => OnMapBody ? Img(slot) : bodyImages.GetValueOrDefault(slot);

        void RenderBodyList()
        {
            if (bodyCombo == null) return;
            var items = new List<(string, string)>();
            foreach (var (b, depth) in SolarSystem.Tree())
            {
                // in progression mode, what the save hasn't visited yet is marked
                string sufijo = "";
                if (Progresion && extras != null && !b.IsStar && extras.Hitos(b.Name)?.Visitado != true)
                    sufijo = "  ·  " + Lang.T("sin visitar");
                items.Add((b.Name, new string(' ', depth * 4) + b.Label + sufijo));
            }
            bodyCombo.SetItems(items);
            bodyCombo.SelectedId = Body.Name;
            int moons = SolarSystem.Bodies.Count(b => b.Parent != null && !b.Parent.IsStar);
            bodySource?.SetText(SolarSystem.LoadedFrom == null
                ? Lang.F("Sistema de serie de KSP: {0} cuerpos.", SolarSystem.Bodies.Count)
                : Lang.F("Sistema leído de Kopernicus en tu instalación de KSP: {0} cuerpos ({1} lunas).", SolarSystem.Bodies.Count, moons));
        }

        /* Switches body without restarting: vessels, Sun, air, maps and tools move to the new one. */
        void SetBody(string name)
        {
            var b = SolarSystem.Find(name);
            if (b == null || b == Body.Current) return;
            Body.Current = b;
            state.BodyName = b.Name;
            SaveSettings();

            popup.Hide();
            if (skyPicking) ToggleSkyPick();
            ClearTools();
            LimpiarRuta();
            ClearOrbit();
            ClearModel();
            globe.ExitFocus();
            // the sky observer: the KSC pad on Kerbin, the equator on the others
            if (OnMapBody) SetObserver(KscLat, KscLon, null);
            else SetObserver(0, 0, 0);

            FiltrarNavesDelCuerpo();
            ActualizarScan();
            _ = CargarMapasDelCuerpo();
            RenderBodyMapsInfo();
            AjustarFiltroAlCuerpo();
            CambiarCuerpoFacciones();
            ApplyMapTextures();
            SyncGlobe();
            RenderBodyInfo();
            RenderWaypointInfo();
            RenderDestinos();
            ActualizarEstadosSecciones();
            // the Parallax ground textures and the clouds are per body
            if (terrenoDe != null) _ = CargarTexturasDeTerreno();
            if (nubesDe != null) _ = CargarNubes();
            if (scattersDe != null) _ = CargarScatters();
            AplicarKonstructs();
            transfer = null;
            trList?.SetItems(new List<object>());
            RenderTransferInfo();
            if (isSky) RefreshSkyHud(); else UpdateHud(null);
            if (sv.Data != null) { RenderReloj(); RenderOrbitInfo(); }
            RequestRender();
        }

        /* The Sun at the current time, for the globe, the sky and the map. */
        void ActualizarSol()
        {
            var sol = SunNow();
            globe.SunLat = map.SunLat = sol.Lat;
            globe.SunLon = map.SunLon = sol.Lon;
            globe.SunAngularRadius = sol.AngularRadius;
            // on the star itself there's no day or night
            globe.Light = map.DayNight = state.DayNight && sol.Exists;
        }

        /* Reads the solar system of the KSP installation. Without Kopernicus the stock one stays. */
        async Task CargarSistemaSolar()
        {
            string gd = FindGameData();
            if (gd == null) return;
            int n = await Task.Run(() => SolarSystem.LoadKopernicus(gd));
            if (n > 0)
            {
                Body.Current = SolarSystem.Find(state.BodyName) ?? SolarSystem.Home;
                BodyIcon.Clear();                 // other bodies, other colors
            }
            RenderBodyList();
            RenderBodyInfo();
            ApplyMapTextures();
            SyncGlobe();
            if (isSky) RefreshSkyHud(); else UpdateHud(null);
            RequestRender();
        }
    }
}
