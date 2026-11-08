using System;
using BepInEx.Logging;
using HarmonyLib;
using RenderCore;
using RenderCore.Enums;

namespace CarX.DlssDx11;

internal static class CameraJitter
{
    private static ManualLogSource logger;
    private static bool reported;

    internal static void Install(Harmony harmony, ManualLogSource log)
    {
        var update = AccessTools.DeclaredMethod(typeof(RenderCamera), nameof(RenderCamera.UpdateCameraParameters),
            new[] { typeof(float), typeof(float), typeof(JitterPhaseMode), typeof(bool) });
        if (update == null)
        {
            log.LogWarning("This game build has no supported camera jitter method.");
            return;
        }

        logger = log;
        try
        {
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(CameraJitter), nameof(UseFullPixelJitter)));
        }
        catch (Exception exception)
        {
            log.LogWarning($"Could not install the DLSS camera jitter correction: {exception.Message}");
        }
    }

    private static void UseFullPixelJitter(float __0, ref JitterPhaseMode __2)
    {
        if (__2 != JitterPhaseMode.Dlss || !(__0 > 0f && __0 < 0.9999f))
            return;

        // The camera's STP branch uses the same Halton sequence at full source-pixel amplitude.
        // The camera jitter mode is independent of the selected upscaler and NVIDIA preset.
        __2 = JitterPhaseMode.Stp;
        if (!reported)
        {
            reported = true;
            logger.LogInfo("Using full source-pixel camera jitter for DirectX 11 DLSS upscaling.");
        }
    }
}
