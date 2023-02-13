using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using System.Linq;

public class CameraStackingCompositing : CustomPass
{
    public HDCameraUI[] uiCameras;
    [SerializeField, HideInInspector]
    Shader blitWithBlending; // Force the serialization of the shader in the scene so it ends up in the build
    [SerializeField, HideInInspector]
    Shader blitInitBackground; // Force the serialization of the shader in the scene so it ends up in the build

    public static Material compositingMaterial;
    public static Material backgroundBlitMaterial;
    public static MaterialPropertyBlock uiProperties;
    readonly Dictionary<Camera, HDAdditionalCameraData> hdAdditionalCameraData = new();
    ProfilingSampler compositingSampler;

    protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
    {
        compositingSampler = new ProfilingSampler("Composite UI Camera Stacking");

        uiProperties ??= new();

        if (blitWithBlending == null)
            blitWithBlending = Shader.Find("Hidden/HDRP/UI_Compositing");
        if (blitInitBackground == null)
            blitInitBackground = Shader.Find("Hidden/HDRP/InitTransparentUIBackground");

        if (compositingMaterial == null)
            compositingMaterial = CoreUtils.CreateEngineMaterial(blitWithBlending);
        if (backgroundBlitMaterial == null)
            backgroundBlitMaterial = CoreUtils.CreateEngineMaterial(blitInitBackground);
    }

    protected override void Execute(CustomPassContext ctx)
    {
        if (RenderPipelineManager.currentPipeline is not HDRenderPipeline)
            return;

        if (ctx.hdCamera == null) return;

        var camera = ctx.hdCamera.camera;

        // Only composite game camera with UI for now
        if (camera.cameraType != CameraType.Game)
            return;

        // Also skip camera that have a custom render (like UI only cameras)
        if (!hdAdditionalCameraData.TryGetValue(camera, out var hdData))
            hdData = hdAdditionalCameraData[camera] = camera.GetComponent<HDAdditionalCameraData>();
        if (hdData == null)
            hdData = hdAdditionalCameraData[camera] = camera.GetComponent<HDAdditionalCameraData>();

        if (hdData != null && hdData.hasCustomRender)
            return;

        var cmd = CommandBufferPool.Get();
        using (new ProfilingScope(cmd, compositingSampler))
        {
            //CoreUtils.SetRenderTarget(ctx.cmd, BuiltinRenderTextureType.CameraTarget);
            foreach (var ui in uiCameras.Where(x => x && x.IsActive).OrderBy(x => x.priority))
            {
                // Check if the target camera in HDCameraUI matches the current camera
                switch (ui.targetCamera)
                {
                    case HDCameraUI.TargetCamera.Main:
                        if (camera != Camera.main)
                            continue;
                        break;
                    case HDCameraUI.TargetCamera.Layer:
                        if (((1 << camera.gameObject.layer) & ui.targetCameraLayer) == 0)
                            continue;
                        break;
                    case HDCameraUI.TargetCamera.Specific:
                        if (camera != ui.targetCameraObject)
                            continue;
                        break;
                }

                // Render the UI of the camera using the current back buffer as clear value
                RenderTargetIdentifier target = camera.targetTexture != null ? camera.targetTexture : BuiltinRenderTextureType.CameraTarget;
                ui.RenderUI(ctx.renderContext, cmd, target);

                if (!ui.DirectRendering)
                {
                    uiProperties.SetTexture("_MainTex", ui.internalRenderTexture);

                    cmd.SetRenderTarget(target);

                    cmd.SetViewport(camera.pixelRect);

                    // Do the UI compositing
                    switch (ui.compositingMode)
                    {
                        default:
                        case HDCameraUI.CompositingMode.Automatic:
                            cmd.DrawProcedural(Matrix4x4.identity, compositingMaterial, 0, MeshTopology.Triangles, 3, 1, uiProperties);
                            break;
                        case HDCameraUI.CompositingMode.Custom:
                            if (ui.compositingMaterial != null)
                            {
                                cmd.DrawProcedural(Matrix4x4.identity, ui.compositingMaterial, ui.compositingMaterialPass, MeshTopology.Triangles, 3, 1, uiProperties);
                            }
                            break;
                        case HDCameraUI.CompositingMode.Manual:
                            // The user manually composite the UI.
                            break;
                    }
                }
            }
        }
        ctx.renderContext.ExecuteCommandBuffer(cmd);
        ctx.renderContext.Submit();
        CommandBufferPool.Release(cmd);
    }
}
