using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.UI
{
    /* El cuerpo que se ve: el selector, el cambio en vivo y la lectura del sistema solar de
       la instalación de KSP (Kopernicus) al arrancar. */
    public sealed partial class MainForm
    {
        DarkCombo bodyCombo;
        RichLabel bodySource;

        /* Los mapas, biomas, alturas y marcadores que trae el visor son de Kerbin. */
        bool OnMapBody => Body.Current.Name == "Kerbin";
        Texture MapTex(string slot) => OnMapBody ? textures.GetValueOrDefault(slot) : null;
        ImageData MapImg(string slot) => OnMapBody ? Img(slot) : null;

        void RenderBodyList()
        {
            if (bodyCombo == null) return;
            var items = new List<(string, string)>();
            foreach (var (b, depth) in SolarSystem.Tree())
                items.Add((b.Name, new string(' ', depth * 4) + b.Label));
            bodyCombo.SetItems(items);
            bodyCombo.SelectedId = Body.Name;
            int moons = SolarSystem.Bodies.Count(b => b.Parent != null && !b.Parent.IsStar);
            bodySource?.SetText(SolarSystem.LoadedFrom == null
                ? Lang.F("Sistema de serie de KSP: {0} cuerpos.", SolarSystem.Bodies.Count)
                : Lang.F("Sistema leído de Kopernicus en tu instalación de KSP: {0} cuerpos ({1} lunas).", SolarSystem.Bodies.Count, moons));
        }

        /* Cambia de cuerpo sin reiniciar: naves, Sol, aire, mapas y herramientas pasan al nuevo. */
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
            ClearOrbit();
            ClearModel();
            globe.ExitFocus();
            // el observador del cielo: la plataforma del KSC en Kerbin, el ecuador en los demás
            if (OnMapBody) SetObserver(KscLat, KscLon, null);
            else SetObserver(0, 0, 0);

            FiltrarNavesDelCuerpo();
            ApplyMapTextures();
            SyncGlobe();
            RenderBodyInfo();
            if (isSky) RefreshSkyHud(); else UpdateHud(null);
            if (sv.Data != null) { RenderReloj(); RenderOrbitInfo(); }
            RequestRender();
        }

        /* El Sol del instante actual, para el globo, el cielo y el mapa. */
        void ActualizarSol()
        {
            var sol = SunNow();
            globe.SunLat = map.SunLat = sol.Lat;
            globe.SunLon = map.SunLon = sol.Lon;
            globe.SunAngularRadius = sol.AngularRadius;
            // en la propia estrella no hay día ni noche
            globe.Light = map.DayNight = state.DayNight && sol.Exists;
        }

        /* Lee el sistema solar de la instalación de KSP. Sin Kopernicus se queda el de serie. */
        async Task CargarSistemaSolar()
        {
            string gd = FindGameData();
            if (gd == null) return;
            int n = await Task.Run(() => SolarSystem.LoadKopernicus(gd));
            if (n > 0) Body.Current = SolarSystem.Find(state.BodyName) ?? SolarSystem.Home;
            RenderBodyList();
            RenderBodyInfo();
            ApplyMapTextures();
            SyncGlobe();
            if (isSky) RefreshSkyHud(); else UpdateHud(null);
            RequestRender();
        }
    }
}
