# MNXFile: start here

Open `MNXFile.slnx` for this component only. Read [README.md](README.md) for build and runtime setup.

Run `./build.sh` on Linux or `build.cmd` (or `./build.ps1`) on Windows. Output is `../MNX_Builds/mnxfile`; intermediates are `../mnx_clutter/build/mnxfile`. Each repository owns its build counter. Use `MNXBuildNumber=2` to reproduce the initial `1.2.0-alpha.2` client package/release without advancing the counter.

The MNXFile shared engine is installed once. MNX-CLI and MNX-GUI consume its thin client NuGet package and require the installed runtime for archive operations. Their source projects do not reference the engine source.
