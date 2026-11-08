# DX11 DLSS for CarX Drift Racing Online 2

A BepInEx plugin that enables the game's built-in NVIDIA DLSS on DirectX 11. It restores full subpixel jitter for upscaled modes and adds adjustable sharpening. For supported single-viewport cameras, it moves the game's Motion Blur effect after DLSS reconstruction while respecting the in-game toggle.

## Install

1. Install the [CarX BepInEx 6 IL2CPP runtime](https://github.com/david568303/CarX-Modding/releases/latest) and launch the game once.
2. Extract the [mod release ZIP](https://github.com/david568303/CarX-DRO2-DLSS-DX11/releases/latest) into the CarX Drift Racing Online 2 folder. The plugin goes in `BepInEx/plugins/dlss-on-dx11/`.
3. Launch the game with DirectX 11. In Steam, you can set `-force-d3d11` in the game's launch options.
4. Open **Settings → Graphics** and choose **DLSS Super Resolution** under **Resolution Scaling**. Select your preferred DLSS mode, including DLAA.

The plugin requires Windows, an NVIDIA RTX GPU, and the Steam game. Version 1.1.0 was tested in Test Drive on **0.21.4 Alpha** with an RTX 3050 Ti Laptop GPU, including the Performance-mode motion blur pass at 3840×1080. Compatibility with other builds is unverified. The motion-blur relocation and sharpening target a single viewport on the primary display.

The optional `Sharpness` setting is in `BepInEx/config/carx.dlss.dx11.cfg`. It defaults to `0.35`. Use `0` to disable sharpening.

## Build

Use a .NET SDK that can target .NET 6. Run CarX with BepInEx once so it generates its interop assemblies, then build from the repository root.

```powershell
dotnet build CarXDlssDx11.csproj -c Release '-p:CarXGameDirectory=C:\path\to\CarX Drift Racing Online 2'
```

## License

The original plugin source is [MIT licensed](LICENSE). The [third-party notices](THIRD-PARTY-NOTICES.md) identify the runtime references. Releases include a combined license notice beside the plugin. The source and release contain only the original plugin and its documentation and notices.

Codex generated most of the plugin code. The mod author directed the work and tested the graphics changes in CarX.
