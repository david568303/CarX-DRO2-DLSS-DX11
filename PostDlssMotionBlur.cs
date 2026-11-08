using System;
using BepInEx.Logging;
using HarmonyLib;
using RenderCore;
using RenderCore.PostProcessing;
using UnityEngine;
using UnityEngine.NVIDIA;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CarX.DlssDx11;

internal static class PostDlssMotionBlur
{
    [ThreadStatic] private static FrameScope frame;
    private static ManualLogSource logger;
    private static bool installed;
    private static bool failed;
    private static bool reported;
    private static bool presented;
    private static IntPtr copyOwner;
    private static BaseRenderFunc<DlssPass.CopyToFrameBufferPassData, UnsafeGraphContext> copyRenderFunc;

    internal static bool IsAvailable => installed && !failed;

    internal static void Install(Harmony harmony, ManualLogSource log)
    {
        logger = log;
        var motion = AccessTools.DeclaredMethod(typeof(MotionBlur), nameof(MotionBlur.IsActive), Type.EmptyTypes);
        var write = AccessTools.DeclaredMethod(typeof(RenderGraphUtils), nameof(RenderGraphUtils.WriteTexture),
            new[] { typeof(IUnsafeRenderGraphBuilder), typeof(TextureHandle).MakeByRefType() });
        var dlss = AccessTools.DeclaredMethod(typeof(DlssPass), nameof(DlssPass.RecordDlssPass),
            new[] { typeof(RenderContext), typeof(TextureHandle), typeof(TextureHandle), typeof(TextureHandle),
                typeof(DlssUpscalerSettings).MakeByRefType() });
        var post = AccessTools.DeclaredMethod(typeof(PostProcessSystem), nameof(PostProcessSystem.RecordRenderGraph),
            new[] { typeof(RenderContext), typeof(TextureHandle), typeof(BlueNoise) });
        if (motion == null || write == null || dlss == null || post == null)
        {
            logger.LogWarning("Post-DLSS motion blur requires the expected CarX render graph methods.");
            return;
        }

        try
        {
            harmony.Patch(motion, postfix: Hook(nameof(SkipStockBlur)));
            harmony.Patch(write, prefix: Hook(nameof(CaptureDlssOutput)));
            harmony.Patch(dlss, prefix: Hook(nameof(BeginDlssCapture)), postfix: Hook(nameof(AppendMotionBlur)));
            harmony.Patch(post, prefix: Hook(nameof(BeginPostProcess)), finalizer: Hook(nameof(EndPostProcess)));
            installed = true;
            logger.LogInfo("Post-DLSS motion blur is available for full-viewport cameras.");
        }
        catch (Exception exception)
        {
            Disable(exception.Message);
        }
    }

    private static HarmonyMethod Hook(string name) => new(typeof(PostDlssMotionBlur), name);

    private static void BeginPostProcess(PostProcessSystem __instance, RenderContext __0, out FrameScope __state)
    {
        __state = frame;
        if (failed || frame != null)
            return;

        try
        {
            var camera = __0?.camera;
            var native = camera?.native;
            var resources = __0?.graphFrameResources;
            var settings = __0?.globalUpscalerHandler?.dlssUpscalerSettings;
            if (native == null || resources == null || settings == null || !__instance.IsDlssActive() ||
                !settings.enabled || settings.reset || settings.quality == DLSSQuality.DLAA ||
                __0.useMultiViewportBuffer || native.cameraType != CameraType.Game || native.targetDisplay != 0 ||
                native.targetTexture != null || native.stereoEnabled || !resources.backBufferColor.IsValid() ||
                !resources.depthBuffer.IsValid() || !resources.motionVectors.IsValid())
                return;

            var data = camera.data;
            var rect = native.pixelRect;
            int width = data.unscaledPixelWidth;
            int height = data.unscaledPixelHeight;
            if (width < 2 || height < 2 || data.pixelWidth >= width || data.pixelHeight >= height ||
                width != Screen.width || height != Screen.height || rect.x != 0f || rect.y != 0f ||
                rect.width != width || rect.height != height || ScalableBufferManager.widthScaleFactor != 1f ||
                ScalableBufferManager.heightScaleFactor != 1f)
                return;

            var motion = __instance.m_motionBlurPass;
            if (motion?.m_params?.IsActive() != true)
                return;

            frame = new FrameScope(__0, motion, width, height);
        }
        catch (Exception exception)
        {
            Disable(exception.Message);
        }
    }

    private static void EndPostProcess(Exception __exception, FrameScope __state)
    {
        if (frame != null && frame != __state && !frame.Appended)
            Disable(__exception?.Message ?? "The stock DLSS graph did not produce a usable output.");

        frame = __state;
    }

    private static void SkipStockBlur(ref bool __result)
    {
        if (!failed && frame != null && !frame.RecordingMovedBlur)
            __result = false;
    }

    private static void BeginDlssCapture(RenderContext __0)
    {
        if (!failed && frame != null && frame.Context.Pointer == __0?.Pointer)
            frame.Capturing = true;
    }

    private static void CaptureDlssOutput(ref TextureHandle __1)
    {
        if (frame?.Capturing != true)
            return;

        frame.DlssOutput = __1;
        frame.WriteCount++;
    }

    private static void AppendMotionBlur(DlssPass __instance)
    {
        var current = frame;
        if (failed || current?.Capturing != true)
            return;

        current.Capturing = false;
        try
        {
            if (current.WriteCount != 1 || !current.DlssOutput.IsValid())
                return;

            var graph = current.Context.graph;
            var source = current.DlssOutput.GetDescriptor(graph);
            var blit = __instance.m_blitMaterial;
            if (source.width != current.Width || source.height != current.Height ||
                blit == null || blit.shader?.name != "Hidden/RenderCore/Blit" || blit.passCount <= 6)
                return;

            current.RecordingMovedBlur = true;
            current.Motion.RecordRenderGraph(current.Context, current.DlssOutput, out var blurred, true);
            if (!blurred.IsValid())
                return;

            AppendStockCopy(__instance, current, blurred);
            current.Appended = true;
            if (!reported)
            {
                reported = true;
                logger.LogInfo($"Recorded the stock motion blur after DLSS at {current.Width}x{current.Height}.");
            }
        }
        catch (Exception exception)
        {
            Disable(exception.ToString());
        }
        finally
        {
            current.RecordingMovedBlur = false;
        }
    }

    private static void AppendStockCopy(DlssPass dlss, FrameScope current, TextureHandle blurred)
    {
        if (copyRenderFunc == null || copyOwner != dlss.Pointer)
        {
            Action<DlssPass.CopyToFrameBufferPassData, UnsafeGraphContext> copy = (pass, context) =>
            {
                dlss.CopyToFrameBuffer(pass, context);
                if (!presented)
                {
                    presented = true;
                    logger.LogInfo("Executed the final copy of the post-DLSS motion blur output.");
                }
            };
            copyRenderFunc = copy;
            copyOwner = dlss.Pointer;
        }

        var graph = current.Context.graph;
        var builder = graph.AddUnsafePass<DlssPass.CopyToFrameBufferPassData>(
            "Copy post-DLSS motion blur", out var pass, nameof(PostDlssMotionBlur), 0);
        try
        {
            var basic = builder.Cast<IBaseRenderGraphBuilder>();
            basic.AllowPassCulling(false);
            basic.AllowGlobalStateModification(true);
            var output = current.Context.graphFrameResources.backBufferColor;
            pass.input = RenderGraphUtils.ReadTexture(builder, ref blurred);
            pass.output = RenderGraphUtils.WriteTexture(builder, ref output);
            pass.camera = current.Context.camera;
            pass.blitMaterial = dlss.m_blitMaterial;
            pass.pixelWidth = current.Width;
            pass.pixelHeight = current.Height;
            builder.SetRenderFunc(copyRenderFunc);
        }
        finally
        {
            builder.Cast<Il2CppSystem.IDisposable>().Dispose();
        }
    }

    private static void Disable(string reason)
    {
        if (failed)
            return;

        failed = true;
        logger.LogWarning($"Could not move motion blur after DLSS: {reason}");
    }

    private sealed class FrameScope
    {
        internal FrameScope(RenderContext context, MotionBlurPass motion, int width, int height)
        {
            Context = context;
            Motion = motion;
            Width = width;
            Height = height;
        }

        internal readonly RenderContext Context;
        internal readonly MotionBlurPass Motion;
        internal readonly int Width;
        internal readonly int Height;
        internal TextureHandle DlssOutput;
        internal int WriteCount;
        internal bool Capturing;
        internal bool RecordingMovedBlur;
        internal bool Appended;
    }
}
