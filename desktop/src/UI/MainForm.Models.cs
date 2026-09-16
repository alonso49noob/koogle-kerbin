using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using KerbinMaps.Core;
using KerbinMaps.Ksp;

namespace KerbinMaps.UI
{
    /* Modelos de las naves: se montan con los .mu y las texturas de la instalación de KSP
       del usuario, leídos en su sitio (nada se copia). */
    public sealed partial class MainForm
    {
        PartCatalog kspCatalog;
        VesselAssembler kspAssembler;
        string kspCatalogDir;
        readonly Dictionary<Vessel, AssembledVessel> vesselModels = new();
        readonly HashSet<Vessel> modelsLoading = new();
        string modelStatus;
        string loadedSavePath;
        DarkCheck chkModels;
        RichLabel modelInfo;

        void BuildModelControls(Section naves)
        {
            chkModels = new DarkCheck("Ver la nave con sus piezas al acercarse (usa los modelos de tu KSP)", state.ShowVesselModels);
            chkModels.CheckedChanged += (s, e) =>
            {
                state.ShowVesselModels = chkModels.Checked;
                SaveSettings();
                if (state.ShowVesselModels && Following && sv.Sel != null) RequestModel(sv.Sel);
                else ClearModel();
            };
            naves.Add(Checks(chkModels));
            var elegir = new DarkButton("Carpeta de KSP…", ButtonVariant.Ghost, small: true);
            elegir.Click += (s, e) => PickKspFolder();
            naves.Add(new BtnRow(elegir));
            modelInfo = naves.Add(Readout());
        }

        /* GameData: la elegida a mano, la de la instalación a la que pertenece la partida
           cargada (saves\<partida>\persistent.sfs) o la de Steam por defecto. */
        string FindGameData()
        {
            var cands = new List<string>();
            if (!string.IsNullOrEmpty(state.KspPath)) cands.Add(Path.Combine(state.KspPath, "GameData"));
            if (loadedSavePath != null)
            {
                var d = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(loadedSavePath)));
                if (d != null) cands.Add(Path.Combine(d, "GameData"));
            }
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Kerbal Space Program", "GameData"));
            foreach (var c in cands) if (Directory.Exists(c)) return c;
            return null;
        }

        void PickKspFolder()
        {
            using var dlg = new FolderBrowserDialog { Description = "Carpeta de Kerbal Space Program (la que contiene GameData)", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (!Directory.Exists(Path.Combine(dlg.SelectedPath, "GameData")))
            {
                Flash("Esa carpeta no tiene GameData dentro: elige la carpeta donde está instalado KSP.");
                return;
            }
            state.KspPath = dlg.SelectedPath;
            SaveSettings();
            kspCatalog = null; kspAssembler = null; kspCatalogDir = null;
            vesselModels.Clear();
            if (Following && sv.Sel != null) RequestModel(sv.Sel);
        }

        void SetModelStatus(string s)
        {
            modelStatus = s;
            modelInfo?.SetText(s == null ? "" : RichLabel.Esc(s));
        }

        async void RequestModel(Vessel v)
        {
            if (v == null || !state.ShowVesselModels || !glOk) return;
            if (vesselModels.TryGetValue(v, out var ready)) { ApplyModel(v, ready); return; }
            if (!modelsLoading.Add(v)) return;
            string gd = FindGameData();
            if (gd == null)
            {
                modelsLoading.Remove(v);
                SetModelStatus("No encuentro la instalación de KSP. Usa «Carpeta de KSP…» para indicarla.");
                return;
            }
            SetModelStatus("Montando la nave con las piezas de KSP…");
            try
            {
                var built = await Task.Run(() =>
                {
                    lock (vesselModels)
                    {
                        if (kspCatalog == null || kspCatalogDir != gd)
                        {
                            kspCatalog = PartCatalog.Load(gd);
                            kspAssembler = new VesselAssembler(kspCatalog);
                            kspCatalogDir = gd;
                        }
                    }
                    var a = kspAssembler.Build(v);
                    a.LoadTextures();
                    return a;
                });
                vesselModels[v] = built;
                if (sv.Sel == v && Following) ApplyModel(v, built);
            }
            catch (Exception ex)
            {
                SetModelStatus("No se pudo montar la nave: " + ex.Message);
            }
            finally { modelsLoading.Remove(v); }
        }

        void ApplyModel(Vessel v, AssembledVessel a)
        {
            if (a.Items.Count == 0)
            {
                ClearModel();
                SetModelStatus("Esta nave no tiene piezas con modelo (" + a.PartsTotal + " piezas).");
                return;
            }
            globe.FocusModel = a;
            globe.FocusTag = v;
            // no dejar que la cámara se meta dentro del casco
            globe.FocusMinDist = Math.Max(a.Radius * 1.5, 3) / Body.Radius;
            if (globe.FocusDist < globe.FocusMinDist) globe.FocusDist = globe.FocusMinDist;
            SetModelStatus(Lang.F("Modelo: {0} de {1} piezas, {2} m de punta a punta.", a.PartsDrawn, a.PartsTotal, Geo.F(a.Radius * 2, 1)) +
                           (a.PartsDrawn < a.PartsTotal ? Lang.T(" Las que faltan no tienen malla (procedurales o asteroides).") : "") +
                           Lang.T(" Acércate con la rueda para verla."));
            RequestRender();
        }

        void ClearModel()
        {
            globe.FocusModel = null;
            globe.FocusTag = null;
            globe.FocusMinDist = 0.0003;
            RequestRender();
        }
    }
}
