# Koogle Recorder

A small KSP mod that records your flights so [Koogle Kerbin](https://github.com/alonso49noob/koogle-kerbin)
can replay them: the path on the map and the globe, the vessel's model, and its navball or
webcast-style telemetry, with the time bar to go back, forward or faster.

It's separate from Koogle Kerbin on purpose: the viewer doesn't need it, and the mod
doesn't need the viewer to record. Koogle Kerbin suggests installing it from its
*Flight recordings* section.

## Install

Unzip `KoogleRecorder-<version>.zip` into your KSP folder, so you end up with
`GameData/KoogleRecorder/Plugins/KoogleRecorder.dll`. Koogle Kerbin can also do it for you.
Built for KSP 1.12.

## What it records

The active vessel, from the moment it leaves the ground (or from when the flight scene
opens with it already flying) until you switch vessel, leave the scene, it's destroyed, or
it stays landed for half a minute. Ten samples a second (two during time warp): position,
altitude, orientation, orbital and surface velocity, vertical speed, throttle, stage, mass
and g-force, plus a copy of the vessel at the start and each time its parts change, so the
replay can show it with its real parts.

Each flight is a text file in `GameData/KoogleRecorder/PluginData/Grabaciones/<save>/`,
written as you fly. Delete the ones you don't want; nothing else is touched.

## Building

`powershell -ExecutionPolicy Bypass -File recorder\construir.ps1` compiles it against your
own KSP's assemblies (`-Ksp <folder>` if it isn't in Steam's default folder) and leaves the
zip in `recorder/dist/`. It needs the .NET SDK.

## License

GPL-3.0, like Koogle Kerbin.
