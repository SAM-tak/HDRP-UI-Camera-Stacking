using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.HighDefinition;
using System;

/// <summary>
/// When this component is added to a camera, it replaces the standard rendering by a single optimized pass to render the GUI in screen space.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(Camera))]
public class HDCameraUI : MonoBehaviour
{
    /// <summary>
    /// Specifies the compositing mode to use when combining the UI render texture and the camera color.
    /// </summary>
    public enum CompositingMode
    {
        /// <summary>Automatically combines both UI and camera color after the rendering of the main camera.</summary>
        Automatic,
        /// <summary>Disables the automatic compositing so you have to manually composite the UI buffer with the camera color.</summary>
        Manual,
        /// <summary>Automatically combines both UI and camera color using a custom material (compositingMaterial field). The material must be compatible with the Blit() command.</summary>
        Custom,
    }

    /// <summary>
    /// Specifies on which camera the UI needs to be rendered.
    /// </summary>
    public enum TargetCamera
    {
        /// <summary>Only render the UI on the camera with the tag "Main Camera".</summary>
        Main,
        /// <summary>Render the UI on all cameras.</summary>
        All,
        /// <summary>Render the UI on all cameras in a specific layer.</summary>
        Layer,
        /// <summary>Only render the UI on a specific camera.</summary>
        Specific,
    }

    public LayerMask uiLayerMask => attachedCamera ? (LayerMask)attachedCamera.cullingMask : (LayerMask)0;

    public float priority => attachedCamera ? attachedCamera.depth : 0f;

    /// <summary>
    /// Specifies the compositing mode to use when combining the UI render texture and the camera color.
    /// </summary>
    [Tooltip("Select how the UI will be composited. Custom requires a material with a fullscreen shader. Manual let's you do the compositing in C# manually using the after and before UI rendering events.")]
    public CompositingMode compositingMode;

    /// <summary>
    /// Use this property to apply a post process shader effect on the UI. The shader must be compatible with Graphics.Blit().
    /// see https://github.com/h33p/Unity-Graphics-Demo/blob/master/Assets/Asset%20Store/PostProcessing/Resources/Shaders/Blit.shader
    /// </summary>
    [Tooltip("Apply a post process effect on the UI buffer. The shader must be compatible with Graphics.Blit().")]
    public Material compositingMaterial;

    /// <summary>
    /// Apply post processes to the UI. Use the camera volume layer mask to control which post process are applied.
    /// </summary>
    // public bool postProcess;

    /// <summary>
    /// The pass name of the compositing material to use.
    /// </summary>
    public int compositingMaterialPass;

    /// <summary>
    /// Override Material. Leaving it to null, render normally.
    /// </summary>
    public Material overrideMaterial;

    /// <summary>
    /// The pass name of the override material to use.
    /// </summary>
    public int overrideMaterialPass;

    /// <summary>
    /// Specifies the graphics format to use when rendering the UI.
    /// </summary>
    [Tooltip("Specifies the graphics format to use when rendering the UI.")]
    public GraphicsFormat graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

    /// <summary>
    /// Specifies on which camera the UI needs to be rendered. The default is Main Camera only.
    /// </summary>
    public TargetCamera targetCamera = TargetCamera.Main;

    /// <summary>
    /// Specifies which layer target camera(s) are using. All cameras using this layer will have the same UI applied.
    /// </summary>
    public LayerMask targetCameraLayer = 1;

    /// <summary>
    /// Specifies the camera where the UI should be rendered.
    /// </summary>
    public Camera targetCameraObject;

    /// <summary>
    /// Disable the copy of the main camera color buffer to the internal UI render texture. If enabled it can cause issues with semi-transparent UI and blending.
    /// </summary>
    [Tooltip("Avoid initialization of camera color buffer. Enabling this option is only supported for cameras that renders to a RenderTexture.")]
    public bool skipCameraColorInit = true;

    /// <summary>
    /// Disable the clear of depth buffer before UI rendering. Useful if you need culling UI from geometry intentionally.
    /// </summary>
    public bool noClearDepth;

    internal struct RenderingData
    {
        public HDCamera hdCamera;
    }

    internal RenderingData currentRenderingData;

    RenderTexture internalDepthBuffer;

    public RenderTexture internalRenderTexture { get; protected set; }

    CullingResults cullingResults;

    [SerializeField]
    internal bool showAdvancedSettings;
    [SerializeField]
    Shader blitWithBlending; // Force the serialization of the shader in the scene so it ends up in the build
    [SerializeField]
    Shader blitInitBackground;

    internal Camera attachedCamera;
    HDAdditionalCameraData data;
    ShaderTagId[] hdTransparentPassNames;
    ProfilingSampler cullingSampler;
    ProfilingSampler renderingSampler;
    ProfilingSampler uiCameraStackingSampler;
    ProfilingSampler initTransparentUIBackgroundSampler;

    // Start is called before the first frame update
    void OnEnable()
    {
        data = GetComponent<HDAdditionalCameraData>();
        attachedCamera = GetComponent<Camera>();

        if (data == null)
            return;

        data.customRender -= StoreHDCamera;
        data.customRender += StoreHDCamera;

        hdTransparentPassNames = new ShaderTagId[]
        {
            HDShaderPassNames.s_TransparentBackfaceName,
            HDShaderPassNames.s_ForwardOnlyName,
            HDShaderPassNames.s_ForwardName,
            HDShaderPassNames.s_SRPDefaultUnlitName
        };

        // TODO: Add VR support
        if (internalRenderTexture != null)
        {
            internalRenderTexture.Release();
            internalRenderTexture = null;
        }
        internalRenderTexture = new RenderTexture(1, 1, 0, graphicsFormat, 1)
        {
            dimension = TextureXR.dimension,
            volumeDepth = 1,
            depth = 0,
            name = $"HDCameraUI ({name}) Output Target"
        };

        internalDepthBuffer = new RenderTexture(1, 1, GraphicsFormat.None, depthStencilFormat: GraphicsFormat.D32_SFloat_S8_UInt)
        {
            name = "HDCameraUI Depth Target"
        };

        cullingSampler = new ProfilingSampler("UI Culling");
        renderingSampler = new ProfilingSampler("UI Rendering");
        uiCameraStackingSampler = new ProfilingSampler("Render UI Camera Stacking");
        initTransparentUIBackgroundSampler = new ProfilingSampler("Init Transparent UI Background");

        if (blitWithBlending == null)
            blitWithBlending = Shader.Find("Hidden/HDRP/UI_Compositing");
        if (blitInitBackground == null)
            blitInitBackground = Shader.Find("Hidden/HDRP/InitTransparentUIBackground");

        CameraStackingCompositing.uiList.Add(this);
    }

    void OnDisable()
    {
        if(internalRenderTexture) {
            internalRenderTexture.Release();
            DestroyImmediate(internalRenderTexture);
            internalRenderTexture = null;
        }

        if(data == null)
            return;

        data.customRender -= StoreHDCamera;
        CameraStackingCompositing.uiList.Remove(this);

        if (internalRenderTexture != null)
        {
            internalRenderTexture.Release();
            internalRenderTexture = null;
        }
    }

    void UpdateRenderTexture(Camera camera)
    {
        if(!internalRenderTexture && !DirectRendering)
        {
            // TODO: Add VR support
            internalRenderTexture = new RenderTexture(Mathf.Max(4, camera.pixelWidth), Mathf.Max(4, camera.pixelHeight), 24, graphicsFormat, 1) {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = 1,
                name = $"HDCameraUI ({name}) Output Target"
            };
        }
        else if(internalRenderTexture && DirectRendering)
        {
            internalRenderTexture.Release();
            DestroyImmediate(internalRenderTexture);
            internalRenderTexture = null;
        }
        else if(internalRenderTexture
            && ( camera.pixelWidth != internalRenderTexture.width
              || camera.pixelHeight != internalRenderTexture.height
              || internalRenderTexture.graphicsFormat != graphicsFormat))
        {
            internalRenderTexture.Release();
            internalRenderTexture.width = Mathf.Max(4, camera.pixelWidth);
            internalRenderTexture.height = Mathf.Max(4, camera.pixelHeight);
            internalRenderTexture.graphicsFormat = graphicsFormat;
            internalRenderTexture.Create();

            internalDepthBuffer.Release();
            internalDepthBuffer.width = Mathf.Max(4, camera.pixelWidth);
            internalDepthBuffer.height = Mathf.Max(4, camera.pixelHeight);
            internalDepthBuffer.Create();
        }
    }

    bool CullUI(CommandBuffer cmd, ScriptableRenderContext ctx, Camera camera)
    {
        bool cullingOk = false;

        using (new ProfilingScope(cmd, cullingSampler))
        {
            if (camera.TryGetCullingParameters(out var cullingParameters))
            {
                cullingParameters.cullingOptions = CullingOptions.None;
                cullingParameters.cullingMask = (uint)uiLayerMask.value;
                cullingResults = ctx.Cull(ref cullingParameters);
                cullingOk = true;
            }
        }

        return cullingOk;
    }

    void RenderUI(CommandBuffer cmd, ScriptableRenderContext ctx, Camera camera, RenderTexture colorBuffer, RenderTexture depthBuffer, RenderTexture targetClearValue)
    {
        beforeUIRendering?.Invoke();

        using (new ProfilingScope(cmd, renderingSampler))
        {
            if (!skipCameraColorInit && targetClearValue != null)
            {
                using (new ProfilingScope(cmd, initTransparentUIBackgroundSampler))
                {
                    for (int i = 0; i < colorBuffer.volumeDepth; i++)
                        cmd.Blit(targetClearValue, colorBuffer, sourceDepthSlice: 0, destDepthSlice: i);
                }
            }

            // Prefer using the color buffer depth if there is any (user provided render texture)
            if (colorBuffer.depthStencilFormat != GraphicsFormat.None)
                CoreUtils.SetRenderTarget(cmd, colorBuffer.colorBuffer, colorBuffer.depthBuffer, skipCameraColorInit || targetClearValue == null ? ClearFlag.All : ClearFlag.DepthStencil);
            else
                CoreUtils.SetRenderTarget(cmd, colorBuffer, depthBuffer, skipCameraColorInit || targetClearValue == null ? ClearFlag.All : ClearFlag.DepthStencil);
        }

        var drawSettings = new DrawingSettings
        {
            sortingSettings = new SortingSettings(camera) { criteria = SortingCriteria.CommonTransparent | SortingCriteria.CanvasOrder | SortingCriteria.RendererPriority }
        };
        for (int i = 0; i < hdTransparentPassNames.Length; i++)
            drawSettings.SetShaderPassName(i, hdTransparentPassNames[i]);

        var filterSettings = new FilteringSettings(RenderQueueRange.all, uiLayerMask);

        ctx.ExecuteCommandBuffer(cmd);
        ctx.DrawRenderers(cullingResults, ref drawSettings, ref filterSettings);

        cmd.Clear();

        afterUIRendering?.Invoke();
    }

    void StoreHDCamera(ScriptableRenderContext ctx, HDCamera hdCamera)
        => currentRenderingData.hdCamera = hdCamera;

    internal bool DirectRendering => compositingMode == CompositingMode.Automatic && !skipCameraColorInit;

    internal bool IsActive => isActiveAndEnabled && attachedCamera.isActiveAndEnabled;

    internal void RenderUI(ScriptableRenderContext ctx, CommandBuffer cmd, RenderTexture target)
    {
        if(RenderPipelineManager.currentPipeline is not HDRenderPipeline hdrp)
            return;

        var hdCamera = currentRenderingData.hdCamera;

        if (hdCamera == null || !hdCamera.camera.enabled)
            return;

        // Update the internal render texture only if we use it
        if (hdCamera.camera.targetTexture == null)
            UpdateRenderTexture(hdCamera.camera);

        // Setup render context for rendering GUI
        ctx.SetupCameraProperties(hdCamera.camera, hdCamera.xr.enabled);

        // Setup HDRP camera properties to render HDRP shaders
        hdrp.UpdateCameraCBuffer(cmd, hdCamera);

        using (new ProfilingScope(cmd, uiCameraStackingSampler))
        {
            if(CullUI(cmd, ctx, hdCamera.camera))
            {
                RenderUI(cmd, ctx, hdCamera.camera, renderTexture, internalDepthBuffer, targetClearValue);

                using (new ProfilingScope(cmd, renderingSampler))
                if (renderInCameraBuffer && hdCamera.camera.targetTexture == null)
                {
                    if(!internalRenderTexture)
                    {
                        CoreUtils.SetRenderTarget(cmd, target, noClearDepth ? ClearFlag.None : ClearFlag.Depth);
                    }
                    else if(skipCameraColorInit)
                    {
                        CoreUtils.SetRenderTarget(cmd, internalRenderTexture.colorBuffer, internalRenderTexture.depthBuffer, noClearDepth ? ClearFlag.Color : ClearFlag.Color | ClearFlag.Depth);
                    }
                    else
                    {
                        using (new ProfilingScope(cmd, initTransparentUIBackgroundSampler))
                            cmd.Blit(target, internalRenderTexture, CameraStackingCompositing.backgroundBlitMaterial);
                        CoreUtils.SetRenderTarget(cmd, internalRenderTexture.colorBuffer, internalRenderTexture.depthBuffer, noClearDepth ? ClearFlag.None : ClearFlag.Depth);
                    }
                }

                cmd.SetViewport(hdCamera.camera.pixelRect);

                var drawSettings = new DrawingSettings {
                    overrideMaterial = overrideMaterial,
                    overrideMaterialPassIndex = overrideMaterialPass,
                    sortingSettings = new SortingSettings(hdCamera.camera) {
                        criteria = SortingCriteria.CommonTransparent | SortingCriteria.CanvasOrder | SortingCriteria.RendererPriority
                    }
                };
                for(int i = 0; i < hdTransparentPassNames.Length; i++)
                    drawSettings.SetShaderPassName(i, hdTransparentPassNames[i]);

                var filterSettings = new FilteringSettings(RenderQueueRange.all, uiLayerMask);

                ctx.ExecuteCommandBuffer(cmd);
                ctx.DrawRenderers(cullingResults, ref drawSettings, ref filterSettings);

                cmd.Clear();
            }
            ctx.ExecuteCommandBuffer(cmd);
        }
    }
}
