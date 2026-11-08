using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RenderCore;
using RenderCore.API;
using RenderCore.Data;
using UnityEngine;
using UnityEngine.NVIDIA;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CarX.DlssDx11;

internal static class Sharpening
{
    private const int FlippedCopyPass = 6;
    private static ConfigEntry<float> sharpness;
    private static ManualLogSource logger;
    private static Material rcasMaterial;
    private static MaterialPropertyBlock properties;
    private static int temporaryTarget;
    private static int textureId;
    private static int constantsId;
    private static bool failed;
    private static bool reported;
    private static bool reportedSkip;

    internal static void Install(Harmony harmony, ConfigFile config, ManualLogSource log)
    {
        sharpness = config.Bind("DLSS", "Sharpness", 0.35f, new ConfigDescription(
            "Contrast-adaptive sharpening for upscaled modes. Use 0 to disable and 1 for maximum strength.",
            new AcceptableValueRange<float>(0f, 1f)));
        logger = log;
        var target = AccessTools.DeclaredMethod(typeof(DlssPass), nameof(DlssPass.CopyToFrameBuffer));
        if (target == null)
        {
            logger.LogWarning("This game build has no supported DLSS final pass.");
            return;
        }

        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Sharpening), nameof(SharpenFinalCopy)));
            logger.LogInfo("Hooked the DirectX 11 DLSS final copy for sharpening.");
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Could not hook the DLSS final copy: {exception}");
        }
    }

    private static bool SharpenFinalCopy(DlssPass.CopyToFrameBufferPassData __0, UnsafeGraphContext __1)
    {
        var amount = sharpness.Value;
        if (failed || !float.IsFinite(amount) || amount <= 0f)
            return true;

        try
        {
            var camera = __0?.camera;
            var renderContext = RenderCoreAPI.s_context?.renderContext;
            var settings = renderContext?.globalUpscalerHandler?.dlssUpscalerSettings;
            if (settings == null)
                return Skip("the active DLSS settings were unavailable");
            if (!settings.enabled || settings.reset || settings.quality == DLSSQuality.DLAA)
                return true;
            if (camera == null || renderContext.camera?.Pointer != camera.Pointer || renderContext.useMultiViewportBuffer)
                return Skip("the final copy is not for the active single viewport");

            var native = camera.native;
            if (native == null || native.targetTexture != null || native.cameraType != CameraType.Game ||
                native.targetDisplay != 0 || native.stereoEnabled)
                return Skip("the camera does not target the primary display in normal game mode");

            var rect = native.pixelRect;
            var width = Screen.width;
            var height = Screen.height;
            if (width < 2 || height < 2 || rect.x != 0f || rect.y != 0f || rect.width != width || rect.height != height ||
                ScalableBufferManager.widthScaleFactor != 1f || ScalableBufferManager.heightScaleFactor != 1f)
                return Skip($"the viewport is {rect}, the screen is {width}x{height}, or hardware scaling is active");

            RenderTexture source = __0.input;
            var blit = __0.blitMaterial;
            if (source == null || source.width != width || source.height != height || source.antiAliasing != 1 ||
                source.dimension != TextureDimension.Tex2D || source.volumeDepth != 1 || !__0.output.IsValid())
                return Skip($"the final copy textures are incompatible, source {source?.width}x{source?.height}");
            if (blit == null || blit.shader?.name != "Hidden/RenderCore/Blit" || blit.passCount <= FlippedCopyPass ||
                !EnsureSharpenMaterial())
                return Skip("the stock final shaders or RCAS variant are unavailable");

            var unsafeBuffer = __1?.cmd;
            if (unsafeBuffer == null) return Skip("the final pass command buffer is unavailable");
            var buffer = CommandBufferHelpers.GetNativeCommandBuffer(unsafeBuffer);
            var viewportBuffer = unsafeBuffer.TryCast<IBaseCommandBuffer>();
            if (buffer == null || viewportBuffer == null) return Skip("the final pass command buffer could not be unwrapped");

            // FinalPost consumes only .x of the RCAS constant. Unity maps linear strength to 2.5 sharpening stops.
            properties.SetVector(constantsId, new Vector4(Mathf.Pow(2f, -(1f - amount) * 2.5f), 0f, 0f, 0f));
            properties.SetTexture(textureId, source);
            DrawSharpenedCopy(__0, viewportBuffer, buffer, rect, source, blit);
            if (!reported)
            {
                reported = true;
                logger.LogInfo($"Applying post-DLSS RCAS at {width}x{height} in {settings.quality}.");
            }
            return false;
        }
        catch (Exception exception)
        {
            failed = true;
            logger.LogWarning($"Disabled DLSS sharpening after an unsupported final pass: {exception}");
            return true;
        }
    }

    private static bool Skip(string reason)
    {
        if (!reportedSkip)
        {
            reportedSkip = true;
            logger.LogInfo($"Using the stock DLSS final copy because {reason}.");
        }
        return true;
    }

    private static bool EnsureSharpenMaterial()
    {
        if (rcasMaterial != null) return true;
        var pipeline = GraphicsSettings.currentRenderPipeline?.TryCast<RenderCorePipelineAsset>();
        var shader = pipeline?.shaders?.finalPostPS;
        if (shader == null || shader.name != "Hidden/RenderCore/FinalPost" || !shader.isSupported)
            return false;

        var rcasKeyword = new LocalKeyword(shader, "USE_RCAS");
        if (!rcasKeyword.isValid) return false;

        rcasMaterial = new Material(shader);
        rcasMaterial.EnableKeyword("USE_RCAS");
        rcasMaterial.DisableKeyword("LINEAR_TO_SRGB_CONVERSION");
        properties = new MaterialPropertyBlock();
        textureId = PropertyIDs.texture;
        constantsId = Shader.PropertyToID("_FsrRcasConstants");
        temporaryTarget = Shader.PropertyToID("_CarXDX11DlssRcasTemporary");
        return true;
    }

    private static void DrawSharpenedCopy(DlssPass.CopyToFrameBufferPassData pass, IBaseCommandBuffer viewportBuffer,
        CommandBuffer buffer, Rect rect, RenderTexture source, Material blit)
    {
        var temporary = new RenderTargetIdentifier(temporaryTarget);
        RenderTargetIdentifier destination = pass.output;
        RenderTargetIdentifier original = pass.input;
        buffer.GetTemporaryRT(temporaryTarget, source.width, source.height, 0, FilterMode.Bilinear,
            source.graphicsFormat, 1);

        try
        {
            // Sharpen at native resolution and at the target origin. The shader addresses input by pixel position.
            buffer.SetRenderTarget(temporary, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            buffer.SetViewport(rect);
            buffer.DrawProcedural(Matrix4x4.identity, rcasMaterial, 0, MeshTopology.Triangles, 3, 1, properties);

            pass.camera.SetViewportForBackbufferAttachment(viewportBuffer, false, true, false);
            try
            {
                buffer.SetRenderTarget(destination, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                    RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
                buffer.SetGlobalTexture(textureId, temporary);
                buffer.DrawProcedural(Matrix4x4.identity, blit, FlippedCopyPass, MeshTopology.Triangles, 3, 1);
            }
            finally
            {
                // Match the game’s native post-pass texture binding before later passes execute.
                buffer.SetGlobalTexture(textureId, original);
                pass.camera.ReleaseBackbufferViewport(viewportBuffer, true);
            }
        }
        finally
        {
            buffer.ReleaseTemporaryRT(temporaryTarget);
        }
    }
}
