using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using RenderCore.API;
using RenderCore.Enums;
using RenderCore.PostProcessing;
using UnityEngine;
using UnityEngine.Rendering;

namespace CarX.DlssDx11;

[BepInPlugin(Id, "CarX DRO2 DLSS on DirectX 11", "1.1.0")]
public sealed class Plugin : BasePlugin
{
    private const string Id = "carx.dlss.dx11";
    private static ManualLogSource logger;
    private static bool handled;

    public override void Load()
    {
        const int nvidiaVendorId = 0x10DE;
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 ||
            SystemInfo.graphicsDeviceVendorID != nvidiaVendorId)
            return;

        logger = Log;
        var resolve = AccessTools.DeclaredMethod(typeof(RenderCoreAPI), nameof(RenderCoreAPI.ResolveScalabilityChanges));
        if (resolve == null)
        {
            Log.LogWarning("This game build has no supported scalability resolver.");
            return;
        }

        var harmony = new Harmony(Id);
        harmony.Patch(resolve, prefix: new HarmonyMethod(typeof(Plugin), nameof(AllowDlss)));
        CameraJitter.Install(harmony, Log);
        PostDlssMotionBlur.Install(harmony, Log);
        MotionBlurGate.Install(harmony, Log);
        Sharpening.Install(harmony, Config, Log);
    }

    private static void AllowDlss()
    {
        if (handled) return;

        try
        {
            var controller = RenderCoreAPI.s_context?.scalabilityController;
            var components = controller?.m_parameterLayer?.components;
            if (components == null) return;

            for (var i = 0; i < components.Count; i++)
            {
                var filter = components[i]?.TryCast<UpscalingOptions>()?.upscalingFilter;
                if (filter == null || !filter.overrideState || filter.value != UpscalingFilter.Off) continue;

                handled = true;
                if (!RenderCoreAPI.IsDlssSupported())
                {
                    logger.LogInfo("The NVIDIA backend did not report DLSS support on this device.");
                    return;
                }

                // The startup parameter layer overrides the user's graphics preset.
                filter.overrideState = false;
                controller.m_unresolvedChanges = true;
                logger.LogInfo("DirectX 11 DLSS is available through the game's graphics settings.");
                return;
            }
        }
        catch (Exception exception)
        {
            handled = true;
            logger.LogWarning($"Could not enable DirectX 11 DLSS: {exception.Message}");
        }
    }
}
