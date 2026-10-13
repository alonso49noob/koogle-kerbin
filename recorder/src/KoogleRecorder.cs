/* Koogle Recorder: records the flights of the active vessel so Koogle Kerbin can replay them.

   A flight starts when the vessel leaves the ground (or when the flight scene opens with it
   already in the air or in orbit) and ends when it switches vessel, leaves the scene, is
   destroyed, or stays landed for half a minute. Each flight is one text file in
   GameData/KoogleRecorder/PluginData/Grabaciones/<save>/, written as it goes, so the app can
   also follow it almost live.

   File format (one record per line, decimals with a dot):
     KOOGLERECORDER 1                         header, then «key = value» lines
     B <body>                                 the body the following samples are around
     SNAP <ut> ... ENDSNAP                    the vessel as KSP saves it (a VESSEL node), at the
                                              start and every time its parts change (staging)
     S ut;lat;lon;alt;qx;qy;qz;qw;ovx;ovy;ovz;svx;svy;svz;vvert;throttle;stage;mass;g;situation
     EVT <ut> <text>                          events, such as a stage being activated
     END <ut>                                 the flight closed cleanly

   q is srfRelRotation, the same rotation KSP saves as «rot»: relative to the body, in Unity's
   frame. ov and sv are the orbital and surface velocities in that same body frame, so the app
   handles them exactly like the saves' rotations. Samples go 10 times a second, and twice a
   second during time warp or on rails.

   Written in C# 7.3 against KSP 1.12's own assemblies; see construir.ps1. */

using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KoogleRecorder
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class Grabador : MonoBehaviour
    {
        public const string Version = "1.0.0";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        Vessel vessel;
        StreamWriter w;
        string cuerpo;
        int partes;
        float ultimaMuestra, ultimoVolcado, ultimaFoto, enTierraDesde = -1;

        void Start()
        {
            GameEvents.onStageActivate.Add(AlActivarEtapa);
        }

        void OnDestroy()
        {
            GameEvents.onStageActivate.Remove(AlActivarEtapa);
            Cerrar();
        }

        void Update()
        {
            var activa = FlightGlobals.ActiveVessel;
            if (activa != vessel)
            {
                Cerrar();
                vessel = activa;
                enTierraDesde = -1;
            }
            if (vessel == null || vessel.state == Vessel.State.DEAD) { Cerrar(); return; }

            var sit = vessel.situation;
            bool enTierra = sit == Vessel.Situations.PRELAUNCH || sit == Vessel.Situations.LANDED || sit == Vessel.Situations.SPLASHED;
            float ahora = Time.realtimeSinceStartup;
            if (w == null)
            {
                // a flight starts when the vessel leaves the ground
                if (enTierra) return;
                Empezar();
                if (w == null) return;
            }
            else if (enTierra)
            {
                // landed for half a minute: the flight is over (taking off again starts another one)
                if (enTierraDesde < 0) enTierraDesde = ahora;
                else if (ahora - enTierraDesde > 30) { Muestra(); Cerrar(); return; }
            }
            else enTierraDesde = -1;

            float intervalo = TimeWarp.CurrentRate > 1.01f || vessel.packed ? 0.5f : 0.1f;
            if (ahora - ultimaMuestra >= intervalo) Muestra();
            if (vessel.parts.Count != partes && ahora - ultimaFoto > 1) Foto();
            if (ahora - ultimoVolcado > 2) { w.Flush(); ultimoVolcado = ahora; }
        }

        void Empezar()
        {
            try
            {
                string dir = Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "KoogleRecorder"),
                                          Path.Combine("PluginData", Path.Combine("Grabaciones", Seguro(HighLogic.SaveFolder ?? "partida"))));
                Directory.CreateDirectory(dir);
                string fichero = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv) + " " + Seguro(vessel.vesselName) + ".kgr");
                w = new StreamWriter(fichero, false, new UTF8Encoding(false));
                double ut = Planetarium.GetUniversalTime();
                w.WriteLine("KOOGLERECORDER 1");
                w.WriteLine("recorder = " + Version);
                w.WriteLine("ksp = " + Versioning.GetVersionString());
                w.WriteLine("save = " + (HighLogic.SaveFolder ?? ""));
                w.WriteLine("vessel = " + vessel.vesselName);
                w.WriteLine("pid = " + vessel.id.ToString("N"));
                w.WriteLine("type = " + vessel.vesselType);
                w.WriteLine("start = " + N(ut));
                cuerpo = null;
                Foto();
                Muestra();
                w.Flush();
                ultimoVolcado = Time.realtimeSinceStartup;
                Debug.Log("[KoogleRecorder] recording " + fichero);
            }
            catch (Exception ex)
            {
                Debug.LogError("[KoogleRecorder] can't record: " + ex.Message);
                w = null;
            }
        }

        void Muestra()
        {
            if (w == null || vessel == null) return;
            ultimaMuestra = Time.realtimeSinceStartup;
            var b = vessel.mainBody;
            if (b.bodyName != cuerpo) { cuerpo = b.bodyName; w.WriteLine("B " + cuerpo); }
            var inv = Quaternion.Inverse(b.bodyTransform.rotation);
            var q = vessel.srfRelRotation;
            Vector3 ov = inv * (Vector3)vessel.obt_velocity, sv = inv * (Vector3)vessel.srf_velocity;
            var sb = new StringBuilder(220);
            sb.Append("S ").Append(N(Planetarium.GetUniversalTime())).Append(';')
              .Append(N(vessel.latitude)).Append(';').Append(N(vessel.longitude)).Append(';').Append(N(vessel.altitude)).Append(';')
              .Append(F(q.x)).Append(';').Append(F(q.y)).Append(';').Append(F(q.z)).Append(';').Append(F(q.w)).Append(';')
              .Append(F(ov.x)).Append(';').Append(F(ov.y)).Append(';').Append(F(ov.z)).Append(';')
              .Append(F(sv.x)).Append(';').Append(F(sv.y)).Append(';').Append(F(sv.z)).Append(';')
              .Append(F((float)vessel.verticalSpeed)).Append(';')
              .Append(F(vessel.ctrlState != null ? vessel.ctrlState.mainThrottle : 0)).Append(';')
              .Append(vessel.currentStage.ToString(Inv)).Append(';')
              .Append(F((float)vessel.GetTotalMass())).Append(';')
              .Append(F((float)vessel.geeForce)).Append(';')
              .Append(vessel.situation);
            w.WriteLine(sb.ToString());
        }

        /* The vessel as KSP saves it, so the app can assemble its model with the same parts. */
        void Foto()
        {
            if (w == null || vessel == null) return;
            ultimaFoto = Time.realtimeSinceStartup;
            partes = vessel.parts.Count;
            try
            {
                var pv = vessel.BackupVessel();
                var n = new ConfigNode("VESSEL");
                pv.Save(n);
                w.WriteLine("SNAP " + N(Planetarium.GetUniversalTime()));
                w.WriteLine(n.ToString());
                w.WriteLine("ENDSNAP");
            }
            catch (Exception ex) { Debug.LogWarning("[KoogleRecorder] snapshot failed: " + ex.Message); }
        }

        void AlActivarEtapa(int etapa)
        {
            if (w == null) return;
            w.WriteLine("EVT " + N(Planetarium.GetUniversalTime()) + " stage " + etapa.ToString(Inv));
        }

        void Cerrar()
        {
            if (w == null) return;
            try
            {
                w.WriteLine("END " + N(Planetarium.GetUniversalTime()));
                w.Close();
            }
            catch { }
            w = null;
        }

        static string N(double v) { return v.ToString("R", Inv); }
        static string F(float v) { return v.ToString("R", Inv); }

        static string Seguro(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.Length > 0 ? sb.ToString() : "_";
        }
    }
}
