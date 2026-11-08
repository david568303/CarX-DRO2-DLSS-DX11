using System;
using BepInEx.Logging;
using HarmonyLib;
using RenderCore;
using RenderCore.API;
using RenderCore.Enums;
using RenderCore.PostProcessing;

namespace CarX.DlssDx11;

internal static class MotionBlurGate
{
    private static ManualLogSource logger;
    private static bool reported;
    private static bool disabled;

    internal static void Install(Harmony harmony, ManualLogSource log)
    {
        var isActive = AccessTools.DeclaredMethod(typeof(MotionBlur), nameof(MotionBlur.IsActive));
        if (isActive == null)
        {
            log.LogWarning("This game build has no supported motion blur check.");
            return;
        }

        logger = log;
        try
        {
            harmony.Patch(isActive, postfix: new HarmonyMethod(typeof(MotionBlurGate), nameof(SkipPreDlssBlur)));
        }
        catch (Exception exception)
        {
            log.LogWarning($"Could not install the DLSS motion blur bypass: {exception.Message}");
        }
    }

    private static void SkipPreDlssBlur(ref bool __result)
    {
        if (!__result || disabled || PostDlssMotionBlur.IsAvailable)
            return;

        try
        {
            var context = RenderCoreAPI.s_context?.renderContext;
            var dlss = context?.globalUpscalerHandler?.dlssUpscalerSettings;
            if (dlss?.enabled != true || !DlssPass.IsDlssSupported)
                return;

            var options = VolumeManagerMod.instance?.stack?.GetComponent<UpscalingOptions>();
            if (options?.upscalingFilter?.value != UpscalingFilter.DLSS)
                return;

            bool upscaling = options.dlssQuality?.value switch
            {
                DLSSQuality.UltraPerformance or DLSSQuality.Performance or DLSSQuality.Balanced or DLSSQuality.Quality => true,
                DLSSQuality.Custom => options.renderScale?.value is < 100f,
                _ => false
            };
            if (!upscaling)
                return;

            // Keep the low-resolution image sharp until DLSS has reconstructed it.
            __result = false;
            if (!reported)
            {
                reported = true;
                logger.LogInfo("Bypassing pre-DLSS motion blur for DirectX 11 upscaling.");
            }
        }
        catch (Exception exception)
        {
            disabled = true;
            logger.LogWarning($"Using the stock motion blur path: {exception.Message}");
        }
    }
}
