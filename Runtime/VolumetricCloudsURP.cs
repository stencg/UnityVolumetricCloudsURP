using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
using Unity.Mathematics;
using static Unity.Mathematics.math;

#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

/// <summary>
/// A renderer feature that adds volumetric clouds support to the URP volume.
/// </summary>
[DisallowMultipleRendererFeature("Volumetric Clouds URP")]
[Tooltip("Add this Renderer Feature to support volumetric clouds in URP Volume.")]
[HelpURL("https://github.com/stencg/UnityVolumetricCloudsURP/tree/main")]
public class VolumetricCloudsURP : ScriptableRendererFeature
{
    [Header("Setup")]
    [Tooltip("The material of volumetric clouds shader.")]
    [SerializeField] private Material material;
    [Tooltip("Enable this to render volumetric clouds in Rendering Debugger view. \nThis is disabled by default to avoid affecting the individual lighting previews.")]
    [SerializeField] private bool renderingDebugger = false;

    [Header("Performance")]
    [Tooltip("Specifies if URP renders volumetric clouds in both real-time and baked reflection probes. \nVolumetric clouds in real-time reflection probes may reduce performance.")]
    [SerializeField] private bool reflectionProbe = false;
    [Range(0.25f, 1.0f), Tooltip("The resolution scale for volumetric clouds rendering.")]
    [SerializeField] private float resolutionScale = 0.5f;
    [Tooltip("Select the method to use for upscaling volumetric clouds.")]
    [SerializeField] private CloudsUpscaleMode upscaleMode = CloudsUpscaleMode.Bilinear;
    [Tooltip("Smooths cloud coverage only along opaque/sky boundaries. This is a low-cost approximation for aliased cloud/geometry contours; it can slightly soften small foreground details.")]
    [SerializeField] private bool depthAwareEdgeFilter = false;
    [Tooltip("Shows the opaque/sky boundary detected by Depth Aware Edge Filter as a white overlay.")]
    [SerializeField] private bool depthAwareEdgeFilterDebug = false;

    [Header("Lighting")]
    [Tooltip("Specifies the volumetric clouds ambient lighting source. Static uses RenderSettings.ambientProbe, including the probe published by Physically Based Sky URP. Dynamic uses a fast 16x16 cubemap and requires the VC_AMBIENT scripting define; otherwise Static is used.")]
    [SerializeField] private CloudsAmbientMode ambientProbe = CloudsAmbientMode.Static;
    [Tooltip("Specifies if URP calculates physically based sun attenuation for volumetric clouds.")]
    [SerializeField] private bool sunAttenuation = false;

    [Header("Wind")]
    [Tooltip("Enable to reset the wind offsets to their initial states when start playing.")]
    [SerializeField] private bool resetOnStart = true;

    [Header("Depth")]
    [Tooltip("Outputs volumetric clouds average depth to the global shader texture \"_VolumetricCloudsDepthTexture\". Enable this for physically accurate atmospheric scattering based on cloud distance. When neither this nor the experimental camera-depth output is enabled, cloud scattering remains enabled and uses a far-plane approximation.")]
    [SerializeField] private bool outputDepth = true;

    [Header("Experimental"), Tooltip("Specifies if URP also outputs volumetric clouds average depth to \"_CameraDepthTexture\".")]
    [SerializeField] private bool depthTexture = false;

    private const string shaderName = "Hidden/Sky/VolumetricClouds";
    private const string VOLUMETRIC_CLOUDS = "VOLUMETRIC_CLOUDS";
    private VolumetricCloudsPass volumetricCloudsPass;
    private VolumetricCloudsAmbientPass volumetricCloudsAmbientPass;
    private VolumetricCloudsShadowsPass volumetricCloudsShadowsPass;
    private Material runtimeMaterial;
    private static bool compatibilityModeErrorLogged;

    // Pirnt message only once.
    private bool isLogPrinted = false;
#if VC_SHADOWS
    private bool isCookiePrinted = false;
#endif // VC_SHADOWS

    /// <summary>
    /// Gets or sets the material of volumetric clouds shader.
    /// </summary>
    /// <value>
    /// The material of volumetric clouds shader.
    /// </value>
    public Material CloudsMaterial
    {
        get { return material; }
        set { material = (value.shader == Shader.Find(shaderName)) ? value : material; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether to render volumetric clouds in Rendering Debugger view.
    /// </summary>
    /// <value>
    /// <c>true</c> if rendering volumetric clouds in Rendering Debugger view; otherwise, <c>false</c>.
    /// </value>
    /// <remarks>
    /// This is disabled by default to avoid affecting the individual lighting previews.
    /// </remarks>
    public bool RenderingDebugger
    {
        get { return renderingDebugger; }
        set { renderingDebugger = value; }
    }

    /// <summary>
    /// Gets or sets the resolution scale for volumetric clouds rendering.
    /// </summary>
    /// <value>
    /// The resolution scale for volumetric clouds rendering, ranging from 0.25 to 1.0.
    /// </value>
    public float ResolutionScale
    {
        get { return resolutionScale; }
        set { resolutionScale = Mathf.Clamp(value, 0.25f, 1.0f); }
    }

    /// <summary>
    /// Gets or sets the ambient probe update frequency for volumetric clouds.
    /// </summary>
    /// <value>
    /// The ambient probe update frequency for volumetric clouds, either Static or Dynamic.
    /// </value>
    public CloudsAmbientMode AmbientUpdateMode
    {
        get { return ambientProbe; }
        set { ambientProbe = value; }
    }

    /// <summary>
    /// Gets or sets the method used for upscaling volumetric clouds.
    /// </summary>
    /// <value>
    /// The method to use for upscaling volumetric clouds.
    /// </value>
    public CloudsUpscaleMode UpscaleMode
    {
        get { return upscaleMode; }
        set { upscaleMode = value; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether cloud coverage is smoothed at opaque/sky boundaries.
    /// </summary>
    public bool DepthAwareEdgeFilter
    {
        get { return depthAwareEdgeFilter; }
        set { depthAwareEdgeFilter = value; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether to show the depth-aware edge-filter mask.
    /// </summary>
    public bool DepthAwareEdgeFilterDebug
    {
        get { return depthAwareEdgeFilterDebug; }
        set { depthAwareEdgeFilterDebug = value; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether to reset wind offsets for volumetric clouds when entering playmode.
    /// </summary>
    /// <value>
    /// <c>true</c> if resetting wind offsets when entering playmode; otherwise, <c>false</c>.
    /// </value>
    public bool ResetWindOnStart
    {
        get { return resetOnStart; }
        set { resetOnStart = value; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether URP calculates physically based sun attenuation for volumetric clouds.
    /// </summary>
    /// <value>
    /// <c>true</c> if URP calculates physically based sun attenuation for volumetric clouds; otherwise, <c>false</c>.
    /// </value>
    public bool SunAttenuation
    {
        get { return sunAttenuation; }
        set { sunAttenuation = value; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether URP outputs volumetric clouds average depth to a global shader texture named "_VolumetricCloudsDepthTexture".
    /// </summary>
    /// <value>
    /// <c>true</c> if URP outputs volumetric clouds average depth; otherwise, <c>false</c>.
    /// </value>
    public bool OutputCloudsDepth
    {
        get { return outputDepth; }
        set { outputDepth = value; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether URP also outputs volumetric clouds average depth to "_CameraDepthTexture".
    /// </summary>
    public bool OutputToSceneDepth
    {
        get { return depthTexture; }
        set { depthTexture = value; }
    }

    public enum CloudsAmbientMode
    {
        [Tooltip("Use RenderSettings.ambientProbe for volumetric clouds rendering, including the probe published by Physically Based Sky URP.")]
        Static,

        [Tooltip("Use a fast dynamic 16x16 cubemap. Requires the VC_AMBIENT scripting define; otherwise Static is used.")]
        Dynamic
    }

    public enum CloudsUpscaleMode
    {
        [Tooltip("Use simple but fast filtering for volumetric clouds upscale.")]
        Bilinear,

        [Tooltip("Use more computationally expensive filtering for volumetric clouds upscale. \nThis blurs the cloud details but reduces the noise that may appear at lower clouds resolutions.")]
        Bilateral
    }

    public override void Create()
    {
        DisposeResources();

        // Check if the volumetric clouds material uses the correct shader.
        if (material == null)
        {
#if UNITY_EDITOR || DEBUG
            Debug.LogError("Volumetric Clouds URP: Material is empty.");
#endif
            return;
        }

        if (material.shader != Shader.Find(shaderName))
        {
#if UNITY_EDITOR || DEBUG
            Debug.LogErrorFormat("Volumetric Clouds URP: Material shader is not {0}.", shaderName);
#endif
            return;
        }

        runtimeMaterial = new Material(material)
        {
            name = $"{material.name} (Runtime)",
            hideFlags = HideFlags.HideAndDontSave
        };

        // Store the current enable state of volumetric clouds in a global shader keyword
        bool isDebugger = DebugManager.instance.isAnyDebugUIActive;
        var stack = VolumeManager.instance.stack;
        VolumetricClouds cloudsVolume = stack.GetComponent<VolumetricClouds>();
        bool isVolumeActive = cloudsVolume != null && cloudsVolume.IsActive() && (!isDebugger || renderingDebugger);

        if (!isActive || !isVolumeActive)
            Shader.DisableKeyword(VOLUMETRIC_CLOUDS);
        else
            Shader.EnableKeyword(VOLUMETRIC_CLOUDS);

        if (volumetricCloudsPass == null)
        {
            volumetricCloudsPass = new(runtimeMaterial, resolutionScale);
            volumetricCloudsPass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents; // Use camera previous matrix to do reprojection
        }
        else
        {
            // Update every frame to support runtime changes to these properties.
            volumetricCloudsPass.resolutionScale = resolutionScale;
            volumetricCloudsPass.upscaleMode = upscaleMode;
            volumetricCloudsPass.dynamicAmbientProbe = ambientProbe == CloudsAmbientMode.Dynamic;
        }

#if VC_SHADOWS
        if (volumetricCloudsShadowsPass == null)
        {
            volumetricCloudsShadowsPass = new(runtimeMaterial);
            volumetricCloudsShadowsPass.renderPassEvent = RenderPassEvent.BeforeRendering;
        }
#endif // VC_SHADOWS
    }

    protected override void Dispose(bool disposing)
    {
        DisposeResources();
    }

    private void DisposeResources()
    {
        if (volumetricCloudsPass != null)
        {
            volumetricCloudsPass.Dispose();
            volumetricCloudsPass = null;
        }
        if (volumetricCloudsAmbientPass != null)
        {
            volumetricCloudsAmbientPass.Dispose();
            volumetricCloudsAmbientPass = null;
        }
#if VC_SHADOWS
        if (volumetricCloudsShadowsPass != null)
        {
            volumetricCloudsShadowsPass.Dispose();
            volumetricCloudsShadowsPass = null;
        }
#endif // VC_SHADOWS
        CoreUtils.Destroy(runtimeMaterial);
        runtimeMaterial = null;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (runtimeMaterial == null || volumetricCloudsPass == null)
            return;

#pragma warning disable CS0618 // Obsolete in Unity 6.4+, where Compatibility Mode no longer exists and this is always false.
        RenderGraphSettings renderGraphSettings = GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>();
        bool compatibilityModeEnabled = renderGraphSettings != null && renderGraphSettings.enableRenderCompatibilityMode;
#pragma warning restore CS0618
        if (compatibilityModeEnabled)
        {
            if (!compatibilityModeErrorLogged)
            {
                Debug.LogError("Volumetric Clouds URP requires URP Render Graph. Disable Compatibility Mode in Project Settings > Graphics before using this renderer feature.");
                compatibilityModeErrorLogged = true;
            }

            Shader.DisableKeyword(VOLUMETRIC_CLOUDS);
            return;
        }

#if UNITY_EDITOR
        bool isEditingPrefab = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null;
        bool isSceneViewFocused = UnityEditor.SceneView.lastActiveSceneView != null && UnityEditor.SceneView.lastActiveSceneView.hasFocus;
        // Disable Volumetric Clouds when entering prefab mode.
        if (isEditingPrefab && isSceneViewFocused)
            return;
    #endif

        var stack = VolumeManager.instance.stack;
        VolumetricClouds cloudsVolume = stack.GetComponent<VolumetricClouds>();
        ColorAdjustments colorAdjustments = stack.GetComponent<ColorAdjustments>();
        bool isDebugger = DebugManager.instance.isAnyDebugUIActive;
        bool isVolumeActive = cloudsVolume != null && cloudsVolume.IsActive() && (!isDebugger || renderingDebugger);

        bool isProbeCamera = renderingData.cameraData.cameraType == CameraType.Reflection && reflectionProbe;

        if (isVolumeActive)
            Shader.EnableKeyword(VOLUMETRIC_CLOUDS);
        else
            Shader.DisableKeyword(VOLUMETRIC_CLOUDS);

        if (isVolumeActive && (renderingData.cameraData.cameraType == CameraType.Game || renderingData.cameraData.cameraType == CameraType.SceneView || isProbeCamera))
        {
        #if URP_PBSKY
            VisualEnvironment visualEnvironment = stack.GetComponent<VisualEnvironment>();
            PhysicallyBasedSky pbrSky = stack.GetComponent<PhysicallyBasedSky>();
        #endif
#if VC_AMBIENT
            bool dynamicAmbientProbe = ambientProbe == CloudsAmbientMode.Dynamic;
#else
            bool dynamicAmbientProbe = false;
#endif
            bool trackStaticAmbientProbe = !dynamicAmbientProbe;
            bool updateAmbientProbe = dynamicAmbientProbe;
            volumetricCloudsPass.cloudsVolume = cloudsVolume;
            volumetricCloudsPass.colorAdjustments = colorAdjustments;
            volumetricCloudsPass.dynamicAmbientProbe = dynamicAmbientProbe;
            volumetricCloudsPass.trackStaticAmbientProbe = trackStaticAmbientProbe;
            volumetricCloudsPass.depthAwareEdgeFilter = depthAwareEdgeFilter;
            volumetricCloudsPass.depthAwareEdgeFilterDebug = depthAwareEdgeFilterDebug;
            volumetricCloudsPass.resetWindOnStart = resetOnStart;
            volumetricCloudsPass.outputDepth = depthTexture || outputDepth; // Implicitly enable clouds depth when we need to output to scene depth
            volumetricCloudsPass.outputToSceneDepth = depthTexture;
            volumetricCloudsPass.sunAttenuation = sunAttenuation;

            // The cloud shaders sample _CameraDepthTexture to stop clouds behind opaque geometry.
            // Declare this before enqueuing so URP schedules the camera-depth texture for this frame.
            ScriptableRenderPassInput requiredInputs = ScriptableRenderPassInput.Depth;
            if (cloudsVolume.perceptualBlending.value > 0.0f)
                requiredInputs |= ScriptableRenderPassInput.Color;
            volumetricCloudsPass.ConfigureInput(requiredInputs);
        #if URP_PBSKY
            Fog fog = stack.GetComponent<Fog>();
            volumetricCloudsPass.hasAtmosphericScattering = visualEnvironment != null && visualEnvironment.IsActive() && visualEnvironment.skyType.value == (int)VisualEnvironment.SkyType.PhysicallyBased && pbrSky != null && pbrSky.atmosphericScattering.value;
            volumetricCloudsPass.hasAtmosphericScattering |= fog != null && fog.IsActive();
            volumetricCloudsPass.visualEnvVolume = visualEnvironment;
#else
            volumetricCloudsPass.hasAtmosphericScattering = false;
#endif

#if VC_SHADOWS
            volumetricCloudsShadowsPass.cloudsVolume = cloudsVolume;
#endif // VC_SHADOWS

            renderer.EnqueuePass(volumetricCloudsPass);

#if VC_SHADOWS
            if (cloudsVolume.shadows.value)
            {
                // Check if URP supports "Light Cookies"
                UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
                if (asset.supportsLightCookies)
                {
                    isCookiePrinted = false;
#if URP_PBSKY
                    volumetricCloudsShadowsPass.visualEnvVolume = visualEnvironment;
#endif
                    renderer.EnqueuePass(volumetricCloudsShadowsPass);
                }
#if UNITY_EDITOR || DEBUG
                else
                {
                    // URP may have stripped light cookie variants (in build), so skip the shadow cookie rendering
                    if (!isCookiePrinted) { Debug.LogWarning("Volumetric Clouds URP: Light Cookies are disabled in the active URP asset. The volumetric clouds shadows will not be rendered."); isCookiePrinted = true; }
                }
#endif
            }
#endif // VC_SHADOWS

#if VC_AMBIENT
            // No need to render dynamic ambient probe for reflection probes.
            if (updateAmbientProbe && !isProbeCamera)
            {
                if (volumetricCloudsAmbientPass == null)
                {
                    volumetricCloudsAmbientPass = new(runtimeMaterial);
                    volumetricCloudsAmbientPass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents - 1;
                }

                renderer.EnqueuePass(volumetricCloudsAmbientPass);
            }
#endif // VC_AMBIENT

            isLogPrinted = false;
        }
#if UNITY_EDITOR || DEBUG
        else if (isDebugger && !renderingDebugger && !isLogPrinted)
        {
            Debug.Log("Volumetric Clouds URP: Disable effect to avoid affecting rendering debugging.");
            isLogPrinted = true;
        }
#endif
    }

#if OPTIMISATION
#if ENABLE_BURST_1_0_0_OR_NEWER
    [Unity.Burst.BurstCompile(FloatMode = Unity.Burst.FloatMode.Fast)]
#endif // ENABLE_BURST_1_0_0_OR_NEWER
#endif // OPTIMISATION
    public class VolumetricCloudsPass : ScriptableRenderPass
    {
        private const int combinePass = 1;
        private const string physicallyBasedSkyCombinePassName = "Volumetric Clouds - Combine with PBSky Scattering";
        private const string traceRenderGraphPassName = "Volumetric Clouds: Trace";
        private const string temporalRenderGraphPassName = "Volumetric Clouds: Temporal Accumulation";
        private const string combineRenderGraphPassName = "Volumetric Clouds: Combine with Camera Color";
        private const string physicallyBasedSkyCombineRenderGraphPassName = "Volumetric Clouds: Combine with PBSky Scattering";
        private const string copyDepthRenderGraphPassName = "Volumetric Clouds: Copy Camera Depth";
        private const string updateDepthRenderGraphPassName = "Volumetric Clouds: Update Camera Depth";
        private static readonly ProfilingSampler traceProfilingSampler = new(traceRenderGraphPassName);
        private static readonly ProfilingSampler temporalProfilingSampler = new(temporalRenderGraphPassName);
        private static readonly ProfilingSampler combineProfilingSampler = new(combineRenderGraphPassName);
        private static readonly ProfilingSampler physicallyBasedSkyCombineProfilingSampler = new(physicallyBasedSkyCombineRenderGraphPassName);
        private static readonly ProfilingSampler copyDepthProfilingSampler = new(copyDepthRenderGraphPassName);
        private static readonly ProfilingSampler updateDepthProfilingSampler = new(updateDepthRenderGraphPassName);
        private static bool missingPhysicallyBasedSkyCombinePassLogged;

        public VolumetricClouds cloudsVolume;
        public ColorAdjustments colorAdjustments;
    #if URP_PBSKY
        public VisualEnvironment visualEnvVolume;
    #endif
        public float resolutionScale;
        public CloudsUpscaleMode upscaleMode;
        public bool depthAwareEdgeFilter;
        public bool depthAwareEdgeFilterDebug;
        public bool dynamicAmbientProbe;
        public bool trackStaticAmbientProbe;
        public bool resetWindOnStart;
        public bool outputDepth;
        public bool outputToSceneDepth;
        public bool sunAttenuation;
        public bool hasAtmosphericScattering;

        private bool denoiseClouds;
#if VC_DENOISE
        private readonly Dictionary<HistoryKey, CloudHistory> renderGraphHistory = new();
#endif

        private readonly Material cloudsMaterial;
        private bool staticAmbientProbeInitialized;
        private SphericalHarmonicsL2 staticAmbientProbe;
#if URP_PBSKY
        private readonly Material physicallyBasedSkyCombineMaterial;
        private readonly int physicallyBasedSkyCombinePass = -1;
#endif
        private readonly LocalKeyword perceptualBlendingKeyword;
        private static bool missingOpaqueTextureLogged;

#if VC_DENOISE
        private readonly struct HistoryKey : IEquatable<HistoryKey>
        {
            internal readonly Camera camera;
            private readonly int cameraId;
            private readonly int xrMultipassId;

            internal HistoryKey(Camera camera, int xrMultipassId)
            {
                this.camera = camera;
                cameraId = camera != null ? camera.GetHashCode() : 0;
                this.xrMultipassId = xrMultipassId;
            }

            public bool Equals(HistoryKey other)
            {
                return ReferenceEquals(camera, other.camera) && xrMultipassId == other.xrMultipassId;
            }

            public override bool Equals(object obj)
            {
                return obj is HistoryKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return (cameraId * 397) ^ xrMultipassId;
            }
        }

        private sealed class CloudHistory : IDisposable
        {
            internal RTHandle textureA;
            internal RTHandle textureB;
            internal int readIndex;
            internal int lastFrameRendered = -1;
            internal bool valid;

            internal RTHandle ReadTexture => readIndex == 0 ? textureA : textureB;
            internal RTHandle WriteTexture => readIndex == 0 ? textureB : textureA;

            internal void Commit(int frameCount)
            {
                readIndex = 1 - readIndex;
                lastFrameRendered = frameCount;
                valid = true;
            }

            public void Dispose()
            {
                textureA?.Release();
                textureB?.Release();
            }
        }
#endif

        private static readonly int numPrimarySteps = Shader.PropertyToID("_NumPrimarySteps");
        private static readonly int numLightSteps = Shader.PropertyToID("_NumLightSteps");
        private static readonly int maxStepSize = Shader.PropertyToID("_MaxStepSize");
        private static readonly int highestCloudAltitude = Shader.PropertyToID("_HighestCloudAltitude");
        private static readonly int lowestCloudAltitude = Shader.PropertyToID("_LowestCloudAltitude");
        private static readonly int shapeNoiseOffset = Shader.PropertyToID("_ShapeNoiseOffset");
        private static readonly int verticalShapeNoiseOffset = Shader.PropertyToID("_VerticalShapeNoiseOffset");
        private static readonly int globalOrientation = Shader.PropertyToID("_WindDirection");
        private static readonly int globalSpeed = Shader.PropertyToID("_WindVector");
        private static readonly int verticalShapeDisplacement = Shader.PropertyToID("_VerticalShapeWindDisplacement");
        private static readonly int verticalErosionDisplacement = Shader.PropertyToID("_VerticalErosionWindDisplacement");
        private static readonly int shapeSpeedMultiplier = Shader.PropertyToID("_MediumWindSpeed");
        private static readonly int erosionSpeedMultiplier = Shader.PropertyToID("_SmallWindSpeed");
        private static readonly int altitudeDistortion = Shader.PropertyToID("_AltitudeDistortion");
        private static readonly int densityMultiplier = Shader.PropertyToID("_DensityMultiplier");
        private static readonly int powderEffectIntensity = Shader.PropertyToID("_PowderEffectIntensity");
        private static readonly int shapeScale = Shader.PropertyToID("_ShapeScale");
        private static readonly int shapeFactor = Shader.PropertyToID("_ShapeFactor");
        private static readonly int erosionScale = Shader.PropertyToID("_ErosionScale");
        private static readonly int erosionFactor = Shader.PropertyToID("_ErosionFactor");
        private static readonly int erosionOcclusion = Shader.PropertyToID("_ErosionOcclusion");
        private static readonly int microErosionScale = Shader.PropertyToID("_MicroErosionScale");
        private static readonly int microErosionFactor = Shader.PropertyToID("_MicroErosionFactor");
        private static readonly int fadeInStart = Shader.PropertyToID("_FadeInStart");
        private static readonly int fadeInDistance = Shader.PropertyToID("_FadeInDistance");
        private static readonly int multiScattering = Shader.PropertyToID("_MultiScattering");
        private static readonly int scatteringTint = Shader.PropertyToID("_ScatteringTint");
        private static readonly int ambientProbeDimmer = Shader.PropertyToID("_AmbientProbeDimmer");
        private static readonly int sunLightDimmer = Shader.PropertyToID("_SunLightDimmer");
        private static readonly int earthRadius = Shader.PropertyToID("_EarthRadius");
        private static readonly int accumulationFactor = Shader.PropertyToID("_AccumulationFactor");
        private static readonly int improvedTransmittanceBlend = Shader.PropertyToID("_ImprovedTransmittanceBlend");
        //private static readonly int normalizationFactor = Shader.PropertyToID("_NormalizationFactor");
        private static readonly int cloudsCurveLut = Shader.PropertyToID("_CloudCurveTexture");
        private static readonly int cloudnearPlane = Shader.PropertyToID("_CloudNearPlane");
        private static readonly int sunColor = Shader.PropertyToID("_SunColor");
        private static readonly int planetCenterRadius = Shader.PropertyToID("_PlanetCenterRadius");
        private static readonly int screenResolution = Shader.PropertyToID("_ScreenResolution");
        private static readonly int postExposure = Shader.PropertyToID("_PostExposure");

        private static readonly int cameraDepthTexture = Shader.PropertyToID(_CameraDepthTexture);
        private static readonly int volumetricCloudsColorTexture = Shader.PropertyToID(_VolumetricCloudsColorTexture);
        private static readonly int volumetricCloudsHistoryTexture = Shader.PropertyToID(_VolumetricCloudsHistoryTexture);
        private static readonly int volumetricCloudsDepthTexture = Shader.PropertyToID(_VolumetricCloudsDepthTexture);
        private static readonly int volumetricCloudsLightingTexture = Shader.PropertyToID(_VolumetricCloudsLightingTexture); // Same as "_VolumetricCloudsColorTexture"
        private static readonly int volumetricCloudsHistoryValid = Shader.PropertyToID("_VolumetricCloudsHistoryValid");
#if URP_PBSKY
        private static readonly int airSingleScatteringTexture = Shader.PropertyToID("_AirSingleScatteringTexture");
        private static readonly int aerosolSingleScatteringTexture = Shader.PropertyToID("_AerosolSingleScatteringTexture");
        private static readonly int multipleScatteringTexture = Shader.PropertyToID("_MultipleScatteringTexture");
        private static readonly int fogSkyTexture = Shader.PropertyToID("_FogSkyTexture");
#endif

        // unity_SH is not available when performing full screen blit pass
        private static readonly int shAr = Shader.PropertyToID("clouds_SHAr");
        private static readonly int shAg = Shader.PropertyToID("clouds_SHAg");
        private static readonly int shAb = Shader.PropertyToID("clouds_SHAb");
        private static readonly int shBr = Shader.PropertyToID("clouds_SHBr");
        private static readonly int shBg = Shader.PropertyToID("clouds_SHBg");
        private static readonly int shBb = Shader.PropertyToID("clouds_SHBb");
        private static readonly int shC = Shader.PropertyToID("clouds_SHC");

        // Main light globals � set manually in the raster pass because URP's light
        // setup unsafe pass has no resource dependency on the clouds pass and may be
        // reordered after it by the render graph compiler.
        private static readonly int mainLightPositionID = Shader.PropertyToID("_MainLightPosition");
        private static readonly int mainLightColorID = Shader.PropertyToID("_MainLightColor");

        private const string localClouds = "_LOCAL_VOLUMETRIC_CLOUDS";
        private const string microErosion = "_CLOUDS_MICRO_EROSION";
        private const string lowResClouds = "_LOW_RESOLUTION_CLOUDS";
        private const string cloudsAmbientProbe = "_CLOUDS_AMBIENT_PROBE";
        private const string outputCloudsDepth = "_OUTPUT_CLOUDS_DEPTH";
        private const string physicallyBasedSun = "_PHYSICALLY_BASED_SUN";
        private const string perceptualBlending = "_PERCEPTUAL_BLENDING";
        private const string depthAwareCloudEdgeFilterKeyword = "_DEPTH_AWARE_CLOUD_EDGE_FILTER";
        private const string depthAwareCloudEdgeFilterDebugKeyword = "_DEPTH_AWARE_CLOUD_EDGE_FILTER_DEBUG";

        private const string _CameraDepthTexture = "_CameraDepthTexture";
        private const string _VolumetricCloudsColorTexture = "_VolumetricCloudsColorTexture";
        private const string _VolumetricCloudsHistoryTexture = "_VolumetricCloudsHistoryTexture";
        private const string _VolumetricCloudsCurrentTexture = "_VolumetricCloudsCurrentTexture";
        private const string _VolumetricCloudsHistoryTextureA = "_VolumetricCloudsHistoryTextureA";
        private const string _VolumetricCloudsHistoryTextureB = "_VolumetricCloudsHistoryTextureB";
        private const string _VolumetricCloudsDepthTexture = "_VolumetricCloudsDepthTexture";
        private const string _VolumetricCloudsLightingTexture = "_VolumetricCloudsLightingTexture"; // Same as "_VolumetricCloudsColorTexture"
        private const string _CameraTempDepthTexture = "_CameraTempDepthTexture";
        private static readonly int depthAwareCloudEdgeFilterWidth = Shader.PropertyToID("_DepthAwareCloudEdgeFilterWidth");

        private static readonly Vector4 m_ScaleBias = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);

        private Texture2D customLutPresetMap;
#if OPTIMISATION
#else
        private readonly Color[] customLutColorArray = new Color[customLutMapResolution];
#endif // OPTIMISATION

        public const float earthRad = 6378100.0f;
        public const float windNormalizationFactor = 100000.0f; // NOISE_TEXTURE_NORMALIZATION_FACTOR in "VolumetricCloudsUtilities.hlsl"
        public const int customLutMapResolution = 64;

        // Wind offsets
        private bool prevIsPlaying;
        private float prevTotalTime = -1.0f;
        private float verticalShapeOffset = 0.0f;
        private float verticalErosionOffset = 0.0f;
        private Vector2 windVector = Vector2.zero;

        private static float square(float x) => x * x;

        private void UpdateMaterialProperties(Camera camera)
        {
            UpdateMaterialProperties(camera.transform.position, camera.nearClipPlane);
        }

        private void UpdateMaterialProperties(Vector3 cameraPosition, float nearClipPlane)
        {
#if URP_PBSKY
            bool isVolumeActive = visualEnvVolume != null && visualEnvVolume.IsActive() && visualEnvVolume.skyType.value != 0;
            if (isVolumeActive)
            {
                if (visualEnvVolume.renderingSpace.value == VisualEnvironment.RenderingSpace.World) { cloudsMaterial.EnableKeyword(localClouds); }
                else { cloudsMaterial.DisableKeyword(localClouds); }
            }
            else
            {
                if (cloudsVolume.localClouds.value) { cloudsMaterial.EnableKeyword(localClouds); }
                else { cloudsMaterial.DisableKeyword(localClouds); }
            }
#else
            if (cloudsVolume.localClouds.value) { cloudsMaterial.EnableKeyword(localClouds); }
            else { cloudsMaterial.DisableKeyword(localClouds); }
#endif

            if (cloudsVolume.microErosion.value) { cloudsMaterial.EnableKeyword(microErosion); }
            else { cloudsMaterial.DisableKeyword(microErosion); }

            if (resolutionScale < 1.0f && upscaleMode == CloudsUpscaleMode.Bilateral) { cloudsMaterial.EnableKeyword(lowResClouds); }
            else { cloudsMaterial.DisableKeyword(lowResClouds); }

            if (dynamicAmbientProbe) { cloudsMaterial.EnableKeyword(cloudsAmbientProbe); }
            else { cloudsMaterial.DisableKeyword(cloudsAmbientProbe); }

            if (outputDepth) { cloudsMaterial.EnableKeyword(outputCloudsDepth); }
            else { cloudsMaterial.DisableKeyword(outputCloudsDepth); }

            if (sunAttenuation) { cloudsMaterial.EnableKeyword(physicallyBasedSun); }
            else { cloudsMaterial.DisableKeyword(physicallyBasedSun); }

            if (depthAwareEdgeFilter || depthAwareEdgeFilterDebug) { cloudsMaterial.EnableKeyword(depthAwareCloudEdgeFilterKeyword); }
            else { cloudsMaterial.DisableKeyword(depthAwareCloudEdgeFilterKeyword); }

            if (depthAwareEdgeFilterDebug) { cloudsMaterial.EnableKeyword(depthAwareCloudEdgeFilterDebugKeyword); }
            else { cloudsMaterial.DisableKeyword(depthAwareCloudEdgeFilterDebugKeyword); }

            cloudsMaterial.SetFloat(numPrimarySteps, cloudsVolume.numPrimarySteps.value);
            cloudsMaterial.SetFloat(numLightSteps, cloudsVolume.numLightSteps.value);
            cloudsMaterial.SetFloat(maxStepSize, cloudsVolume.altitudeRange.value / 8.0f);

#if URP_PBSKY
            float4 planetCenterRad = visualEnvVolume.GetPlanetCenterRadius(cameraPosition);
            float actualEarthRad = isVolumeActive ? planetCenterRad.w : Mathf.Lerp(1.0f, 0.025f, cloudsVolume.earthCurvature.value) * earthRad;
            bool useVisualEnvironmentPlanet = isVolumeActive && visualEnvVolume.renderingSpace.value == VisualEnvironment.RenderingSpace.World;
            planetCenterRad = useVisualEnvironmentPlanet ? planetCenterRad : float4(0.0f, -actualEarthRad, 0.0f, actualEarthRad);

            cloudsMaterial.SetVector(planetCenterRadius, planetCenterRad);
#else
            float actualEarthRad = Mathf.Lerp(1.0f, 0.025f, cloudsVolume.earthCurvature.value) * earthRad;

            cloudsMaterial.SetVector(planetCenterRadius, float4(0.0f, -actualEarthRad, 0.0f, actualEarthRad));
#endif

            float bottomAltitude = cloudsVolume.bottomAltitude.value + actualEarthRad;
            float highestAltitude = bottomAltitude + cloudsVolume.altitudeRange.value;
            cloudsMaterial.SetFloat(highestCloudAltitude, highestAltitude);
            cloudsMaterial.SetFloat(lowestCloudAltitude, bottomAltitude);
            cloudsMaterial.SetVector(shapeNoiseOffset, new Vector4(cloudsVolume.shapeOffset.value.x, cloudsVolume.shapeOffset.value.z, 0.0f, 0.0f));
            cloudsMaterial.SetFloat(verticalShapeNoiseOffset, cloudsVolume.shapeOffset.value.y);

            // Wind animation
            float totalTime = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
            float deltaTime = totalTime - prevTotalTime;
            if (prevTotalTime == -1.0f)
                deltaTime = 0.0f;

#if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isPaused)
                deltaTime = 0.0f;
#endif

            // Conversion from km/h to m/s is the 0.277778f factor
            // We apply a minus to see something moving in the right direction
            deltaTime *= -0.277778f;

            float theta = cloudsVolume.globalOrientation.value / 180.0f * Mathf.PI;
            Vector2 windDirection = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));

            if (resetWindOnStart && prevIsPlaying != Application.isPlaying)
            {
                windVector = Vector2.zero;
                verticalShapeOffset = 0.0f;
                verticalErosionOffset = 0.0f;
            }
            else
            {
                windVector += deltaTime * cloudsVolume.globalSpeed.value * windDirection;
                verticalShapeOffset += deltaTime * cloudsVolume.verticalShapeWindSpeed.value;
                verticalErosionOffset += deltaTime * cloudsVolume.erosionSpeedMultiplier.value;
                // Reset the accumulated wind variables periodically to avoid extreme values.
                windVector.x %= windNormalizationFactor;
                windVector.y %= windNormalizationFactor;
                verticalShapeOffset %= windNormalizationFactor;
                verticalErosionOffset %= windNormalizationFactor;
            }

            // Update previous values
            prevTotalTime = totalTime;
            prevIsPlaying = Application.isPlaying;

            // We apply a minus to see something moving in the right direction
            cloudsMaterial.SetVector(globalOrientation, new Vector4(-windDirection.x, -windDirection.y, 0.0f, 0.0f));
            cloudsMaterial.SetVector(globalSpeed, windVector);
            cloudsMaterial.SetFloat(shapeSpeedMultiplier, cloudsVolume.shapeSpeedMultiplier.value);
            cloudsMaterial.SetFloat(erosionSpeedMultiplier, cloudsVolume.erosionSpeedMultiplier.value);
            cloudsMaterial.SetFloat(altitudeDistortion, cloudsVolume.altitudeDistortion.value * 0.25f);
            cloudsMaterial.SetFloat(verticalShapeDisplacement, verticalShapeOffset);
            cloudsMaterial.SetFloat(verticalErosionDisplacement, verticalErosionOffset);

            cloudsMaterial.SetFloat(densityMultiplier, cloudsVolume.densityMultiplier.value * cloudsVolume.densityMultiplier.value * 2.0f);
            cloudsMaterial.SetFloat(powderEffectIntensity, cloudsVolume.powderEffectIntensity.value);
            cloudsMaterial.SetFloat(shapeScale, cloudsVolume.shapeScale.value);
            cloudsMaterial.SetFloat(shapeFactor, cloudsVolume.shapeFactor.value);
            cloudsMaterial.SetFloat(erosionScale, cloudsVolume.erosionScale.value);
            cloudsMaterial.SetFloat(erosionFactor, cloudsVolume.erosionFactor.value);
            cloudsMaterial.SetFloat(erosionOcclusion, cloudsVolume.erosionOcclusion.value);
            cloudsMaterial.SetFloat(microErosionScale, cloudsVolume.microErosionScale.value);
            cloudsMaterial.SetFloat(microErosionFactor, cloudsVolume.microErosionFactor.value);

            bool autoFadeIn = cloudsVolume.fadeInMode.value == VolumetricClouds.CloudFadeInMode.Automatic;
            cloudsMaterial.SetFloat(fadeInStart, autoFadeIn ? Mathf.Max(cloudsVolume.altitudeRange.value * 0.2f, nearClipPlane) : Mathf.Max(cloudsVolume.fadeInStart.value, nearClipPlane));
            cloudsMaterial.SetFloat(fadeInDistance, autoFadeIn ? cloudsVolume.altitudeRange.value * 0.3f : cloudsVolume.fadeInDistance.value);
            cloudsMaterial.SetFloat(multiScattering, 1.0f - cloudsVolume.multiScattering.value * 0.95f);
            cloudsMaterial.SetColor(scatteringTint, Color.white - cloudsVolume.scatteringTint.value * 0.75f);
            cloudsMaterial.SetFloat(ambientProbeDimmer, cloudsVolume.ambientLightProbeDimmer.value);
            cloudsMaterial.SetFloat(sunLightDimmer, cloudsVolume.sunLightDimmer.value);
            cloudsMaterial.SetFloat(earthRadius, actualEarthRad);
            cloudsMaterial.SetFloat(accumulationFactor, cloudsVolume.temporalAccumulationFactor.value);
            cloudsMaterial.SetFloat(improvedTransmittanceBlend, cloudsVolume.perceptualBlending.value);
            cloudsMaterial.SetFloat(depthAwareCloudEdgeFilterWidth, Mathf.Max(1.0f, 1.0f / resolutionScale));
            Vector3 cameraPosPS = cameraPosition - new Vector3(0.0f, -actualEarthRad, 0.0f);
            cloudsMaterial.SetFloat(cloudnearPlane, max(GetCloudNearPlane(cameraPosPS, bottomAltitude, highestAltitude), nearClipPlane));

            // Custom cloud map is not supported yet.
            //float lowerCloudRadius = (bottomAltitude + highestAltitude) * 0.5f - actualEarthRad;
            //cloudsMaterial.SetFloat(normalizationFactor, Mathf.Sqrt((earthRad + lowerCloudRadius) * (earthRad + lowerCloudRadius) - earthRad * actualEarthRad));

            float postExposureLinear = colorAdjustments != null && colorAdjustments.active ? Mathf.Pow(2.0f, colorAdjustments.postExposure.value) : 1.0f;
            cloudsMaterial.SetFloat(postExposure, postExposureLinear);

            SetupAmbientProbeIfNeeded(cloudsMaterial);

            PrepareCustomLutData(cloudsVolume);
        }

        private void UpdateClouds(Light mainLight, Camera camera)
        {
            // When using PBSky, we already applied the sun attenuation to "_MainLightColor"
            if (sunAttenuation)
            {
                bool isLinearColorSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Color mainLightColor = Color.black;
                if (mainLight != null)
                    mainLightColor = (isLinearColorSpace ? mainLight.color.linear : mainLight.color.gamma) * (mainLight.useColorTemperature ? Mathf.CorrelatedColorTemperatureToRGB(mainLight.colorTemperature) : Color.white) * mainLight.intensity;

#if URP_PHYSICAL_LIGHT
                bool isPhysicalLight = mainLight.GetComponent<AdditionalLightData>() != null;

                mainLightColor = isPhysicalLight ? mainLightColor : mainLightColor * PI;
#else
                mainLightColor *= PI;
#endif

                // Pass the actual main light color to volumetric clouds shader.
                cloudsMaterial.SetVector(sunColor, mainLightColor);
            }

            // Update preset values
            VolumetricClouds.CloudPresets cloudPreset = cloudsVolume.cloudPreset;
            cloudsVolume.cloudPreset = cloudPreset;

            UpdateMaterialProperties(camera);
            denoiseClouds = cloudsVolume.temporalAccumulationFactor.value >= 0.01f;
        }

        private void PrepareCustomLutData(VolumetricClouds clouds)
        {
            if (customLutPresetMap == null)
            {
                customLutPresetMap = new Texture2D(1, customLutMapResolution, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None)
                {
                    name = "Custom LUT Curve",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                customLutPresetMap.hideFlags = HideFlags.HideAndDontSave;
            }

#if OPTIMISATION
            var pixels = customLutPresetMap.GetRawTextureData<half4>();
#else
            var pixels = customLutColorArray;
#endif // OPTIMISATION

            var densityCurve = clouds.densityCurve.value;
            var erosionCurve = clouds.erosionCurve.value;
            var ambientOcclusionCurve = clouds.ambientOcclusionCurve.value;
#if OPTIMISATION
            var white = new half4(1);
#else
            Color white = Color.white;
#endif // OPTIMISATION
            if (densityCurve == null || densityCurve.length == 0)
            {
                for (int i = 0; i < customLutMapResolution; i++)
                    pixels[i] = white;
            }
            else
            {
                float step = 1.0f / (customLutMapResolution - 1f);

                for (int i = 0; i < customLutMapResolution; i++)
                {
                    float currTime = step * i;
                    float density = (i == 0 || i == customLutMapResolution - 1) ? 0 : Mathf.Clamp(densityCurve.Evaluate(currTime), 0.0f, 1.0f);
                    float erosion = Mathf.Clamp(erosionCurve.Evaluate(currTime), 0.0f, 1.0f);
                    float ambientOcclusion = Mathf.Clamp(1.0f - ambientOcclusionCurve.Evaluate(currTime), 0.0f, 1.0f);
#if OPTIMISATION
                    pixels[i] = new half4((half)density, (half)erosion, (half)ambientOcclusion, (half)1);
#else
                    pixels[i] = new Color(density, erosion, ambientOcclusion, 1.0f);
#endif // OPTIMISATION
                }
            }

#if OPTIMISATION
#else
            customLutPresetMap.SetPixels(pixels);
#endif // OPTIMISATION
            customLutPresetMap.Apply();

            cloudsMaterial.SetTexture(cloudsCurveLut, customLutPresetMap);
        }

        private void SetupAmbientProbeIfNeeded(Material cloudsMaterial)
        {
            if (!dynamicAmbientProbe)
            {
                SphericalHarmonicsL2 ambientProbe = RenderSettings.ambientProbe;

                if (trackStaticAmbientProbe && (!staticAmbientProbeInitialized || !AmbientProbesEqual(staticAmbientProbe, ambientProbe)))
                {
                    staticAmbientProbe = ambientProbe;
                    staticAmbientProbeInitialized = true;
#if VC_DENOISE
                    foreach (CloudHistory history in renderGraphHistory.Values)
                        history.valid = false;
#endif
                }

                cloudsMaterial.SetVector(shAr, new Vector4(ambientProbe[0, 3], ambientProbe[0, 1], ambientProbe[0, 2], ambientProbe[0, 0] - ambientProbe[0, 6]));
                cloudsMaterial.SetVector(shAg, new Vector4(ambientProbe[1, 3], ambientProbe[1, 1], ambientProbe[1, 2], ambientProbe[1, 0] - ambientProbe[1, 6]));
                cloudsMaterial.SetVector(shAb, new Vector4(ambientProbe[2, 3], ambientProbe[2, 1], ambientProbe[2, 2], ambientProbe[2, 0] - ambientProbe[2, 6]));
                cloudsMaterial.SetVector(shBr, new Vector4(ambientProbe[0, 4], ambientProbe[0, 5], ambientProbe[0, 6] * 3, ambientProbe[0, 7]));
                cloudsMaterial.SetVector(shBg, new Vector4(ambientProbe[1, 4], ambientProbe[1, 5], ambientProbe[1, 6] * 3, ambientProbe[1, 7]));
                cloudsMaterial.SetVector(shBb, new Vector4(ambientProbe[2, 4], ambientProbe[2, 5], ambientProbe[2, 6] * 3, ambientProbe[2, 7]));
                cloudsMaterial.SetVector(shC, new Vector4(ambientProbe[0, 8], ambientProbe[1, 8], ambientProbe[2, 8], 1));
            }
        }

        private static bool AmbientProbesEqual(SphericalHarmonicsL2 lhs, SphericalHarmonicsL2 rhs)
        {
            for (int rgb = 0; rgb < 3; rgb++)
                for (int coefficient = 0; coefficient < 9; coefficient++)
                    if (lhs[rgb, coefficient] != rhs[rgb, coefficient])
                        return false;

            return true;
        }

#if OPTIMISATION
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
#endif // OPTIMISATION
        private static Vector2 IntersectSphere(float sphereRadius, float cosChi,
                                          float radialDistance, float rcpRadialDistance)
        {
            // r_o = float2(0, r)
            // r_d = float2(sinChi, cosChi)
            // p_s = r_o + t * r_d
            //
            // R^2 = dot(r_o + t * r_d, r_o + t * r_d)
            // R^2 = ((r_o + t * r_d).x)^2 + ((r_o + t * r_d).y)^2
            // R^2 = t^2 + 2 * dot(r_o, r_d) + dot(r_o, r_o)
            //
            // t^2 + 2 * dot(r_o, r_d) + dot(r_o, r_o) - R^2 = 0
            //
            // Solve: t^2 + (2 * b) * t + c = 0, where
            // b = r * cosChi,
            // c = r^2 - R^2.
            //
            // t = (-2 * b + sqrt((2 * b)^2 - 4 * c)) / 2
            // t = -b + sqrt(b^2 - c)
            // t = -b + sqrt((r * cosChi)^2 - (r^2 - R^2))
            // t = -b + r * sqrt((cosChi)^2 - 1 + (R/r)^2)
            // t = -b + r * sqrt(d)
            // t = r * (-cosChi + sqrt(d))
            //
            // Why do we do this? Because it is more numerically robust.

            float d = square(sphereRadius * rcpRadialDistance) - saturate(1 - cosChi * cosChi);

            // Return the value of 'd' for debugging purposes.
            return (d < 0.0f) ? new Vector2(-1.0f, -1.0f) : (radialDistance * new Vector2(-cosChi - sqrt(d),
                                                          -cosChi + sqrt(d)));
        }

#if OPTIMISATION
#if ENABLE_BURST_1_0_0_OR_NEWER
        [Unity.Burst.BurstCompile(FloatMode = Unity.Burst.FloatMode.Fast)]
#endif // ENABLE_BURST_1_0_0_OR_NEWER
        private static float GetCloudNearPlane(in Vector3 originPS, float lowerBoundPS, float higherBoundPS)
#else
        private static float GetCloudNearPlane(Vector3 originPS, float lowerBoundPS, float higherBoundPS)
#endif // OPTIMISATION
        {
            float radialDistance = length(originPS);
            float rcpRadialDistance = rcp(radialDistance);
            float cosChi = 1.0f;
            Vector2 tInner = IntersectSphere(lowerBoundPS, cosChi, radialDistance, rcpRadialDistance);
            Vector2 tOuter = IntersectSphere(higherBoundPS, -cosChi, radialDistance, rcpRadialDistance);

            if (tInner.x < 0.0f && tInner.y >= 0.0f) // Below the lower bound
                return tInner.y;
            else // Inside or above the cloud volume
                return max(tOuter.x, 0.0f);
        }

        public VolumetricCloudsPass(Material material, float resolution)
        {
            cloudsMaterial = material;
#if URP_PBSKY
            Shader physicallyBasedSkyShader = Shader.Find("Hidden/Sky/PhysicallyBasedSkyPrecomputation");
            if (physicallyBasedSkyShader != null)
            {
                physicallyBasedSkyCombineMaterial = CoreUtils.CreateEngineMaterial(physicallyBasedSkyShader);
                physicallyBasedSkyCombinePass = physicallyBasedSkyCombineMaterial.FindPass(physicallyBasedSkyCombinePassName);
            }
#endif
            resolutionScale = resolution;
            perceptualBlendingKeyword = new LocalKeyword(material.shader, perceptualBlending);
        }

#if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        static
        private Light GetMainLight(UniversalLightData lightData)
        {
            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex != -1)
            {
                VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
                Light light = shadowLight.light;
                if ((light.shadows != LightShadows.None || RenderSettings.sun != null && !RenderSettings.sun.isActiveAndEnabled) && shadowLight.lightType == LightType.Directional)
                    return light;
            }

            return RenderSettings.sun;
        }

        private class CloudRasterData
        {
            internal Material material;
            internal TextureHandle source;
            internal TextureHandle namedTexture;
            internal int namedTextureId;
            internal TextureHandle secondNamedTexture;
            internal int secondNamedTextureId;
#if VC_DEPTH
            internal TextureHandle thirdNamedTexture;
            internal int thirdNamedTextureId;
#endif
#if URP_PBSKY
            internal TextureHandle airSingleScattering;
            internal TextureHandle aerosolSingleScattering;
            internal TextureHandle multipleScattering;
            internal TextureHandle fogSky;
#endif
            internal LocalKeyword keyword;
            internal bool setKeyword;
            internal bool keywordEnabled;
            internal int pass;
            internal Vector4 mainLightPosition;
            internal Vector4 mainLightColor;
            internal Vector4 screenResolution;
            internal bool setMainLight;
#if VC_DENOISE
            internal bool setHistoryValid;
            internal bool historyValid;
#endif
#if VC_DEPTH
            internal bool copyTexture;
#endif
        }

        private static void DrawCloudRaster(CloudRasterData data, RasterGraphContext context)
        {
            RasterCommandBuffer cmd = context.cmd;
            if (data.setMainLight)
            {
                cmd.SetGlobalVector(mainLightPositionID, data.mainLightPosition);
                cmd.SetGlobalVector(mainLightColorID, data.mainLightColor);
            }
            if (data.screenResolution.x > 0.0f)
                cmd.SetGlobalVector(screenResolution, data.screenResolution);
            if (data.setKeyword)
                cmd.SetKeyword(data.material, data.keyword, data.keywordEnabled);
#if VC_DENOISE
            if (data.setHistoryValid)
                cmd.SetGlobalFloat(volumetricCloudsHistoryValid, data.historyValid ? 1.0f : 0.0f);
#endif
            if (data.namedTexture.IsValid())
                cmd.SetGlobalTexture(data.namedTextureId, data.namedTexture);
            if (data.secondNamedTexture.IsValid())
                cmd.SetGlobalTexture(data.secondNamedTextureId, data.secondNamedTexture);
#if VC_DEPTH
            if (data.thirdNamedTexture.IsValid())
                cmd.SetGlobalTexture(data.thirdNamedTextureId, data.thirdNamedTexture);
#endif
#if URP_PBSKY
            if (data.airSingleScattering.IsValid())
                cmd.SetGlobalTexture(airSingleScatteringTexture, data.airSingleScattering);
            if (data.aerosolSingleScattering.IsValid())
                cmd.SetGlobalTexture(aerosolSingleScatteringTexture, data.aerosolSingleScattering);
            if (data.multipleScattering.IsValid())
                cmd.SetGlobalTexture(multipleScatteringTexture, data.multipleScattering);
            if (data.fogSky.IsValid())
                cmd.SetGlobalTexture(fogSkyTexture, data.fogSky);
#endif
#if VC_DEPTH
            if (data.copyTexture)
                Blitter.BlitTexture(cmd, data.source, m_ScaleBias, 0.0f, false);
            else
#endif
            if (data.source.IsValid())
                Blitter.BlitTexture(cmd, data.source, m_ScaleBias, data.material, data.pass);
            else
                Blitter.BlitTexture(cmd, m_ScaleBias, data.material, data.pass);
        }

#if VC_DENOISE
        private void CleanupHistory(int frame)
        {
            if ((frame & 63) != 0)
                return;
            List<HistoryKey> stale = null;
            foreach (var pair in renderGraphHistory)
                if (pair.Key.camera == null || frame - pair.Value.lastFrameRendered > 300)
                {
                    stale ??= new List<HistoryKey>();
                    stale.Add(pair.Key);
                }
            if (stale == null)
                return;
            foreach (HistoryKey key in stale)
            {
                renderGraphHistory[key].Dispose();
                renderGraphHistory.Remove(key);
            }
        }
#endif

        private static RenderTextureDescriptor CloudDescriptor(RenderTextureDescriptor source, RenderTextureFormat format, float scale)
        {
            source.width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            source.height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            source.msaaSamples = 1;
            source.bindMS = false;
            source.useMipMap = false;
            source.autoGenerateMips = false;
            source.depthBufferBits = 0;
            source.depthStencilFormat = GraphicsFormat.None;
            source.colorFormat = format;
            source.memoryless = RenderTextureMemoryless.None;
            return source;
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            Camera camera = cameraData.camera;
#if VC_DENOISE
            int frame = Time.frameCount;
            CleanupHistory(frame);
            XRPass xr = cameraData.xr;
            int xrMultipassId = xr != null && xr.enabled && !xr.singlePassEnabled ? xr.multipassId : 0;
            HistoryKey historyKey = new HistoryKey(camera, xrMultipassId);
#endif

            Light mainLight = GetMainLight(lightData);
            UpdateClouds(mainLight, camera);
            Vector4 lightPosition = Vector4.zero;
            Vector4 lightColor = Vector4.zero;
            if (mainLight != null)
            {
                Vector4 direction = -mainLight.transform.localToWorldMatrix.GetColumn(2);
                lightPosition = new Vector4(direction.x, direction.y, direction.z, 0.0f);
                bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Color color = (linear ? mainLight.color.linear : mainLight.color.gamma)
                    * (mainLight.useColorTemperature ? Mathf.CorrelatedColorTemperatureToRGB(mainLight.colorTemperature) : Color.white)
                    * mainLight.intensity;
                lightColor = color;
            }

            RenderTextureDescriptor fullDesc = CloudDescriptor(cameraData.cameraTargetDescriptor, RenderTextureFormat.ARGBHalf, 1.0f);
            RenderTextureDescriptor lowDesc = CloudDescriptor(cameraData.cameraTargetDescriptor, RenderTextureFormat.ARGBHalf, resolutionScale);
            TextureHandle cloud = UniversalRenderer.CreateRenderGraphTexture(renderGraph, lowDesc, _VolumetricCloudsCurrentTexture, false, FilterMode.Bilinear, TextureWrapMode.Clamp);
#if VC_DEPTH
            bool writeCloudDepth = outputDepth || outputToSceneDepth;
            RenderTextureDescriptor cloudDepthDesc = CloudDescriptor(cameraData.cameraTargetDescriptor, RenderTextureFormat.RFloat, resolutionScale);
            TextureHandle cloudDepth = writeCloudDepth
                ? UniversalRenderer.CreateRenderGraphTexture(renderGraph, cloudDepthDesc, _VolumetricCloudsDepthTexture, false, FilterMode.Point, TextureWrapMode.Clamp)
                : TextureHandle.nullHandle;
#endif
            bool perceptual = cloudsVolume.perceptualBlending.value > 0.0f;
            TextureHandle traceSource = TextureHandle.nullHandle;
            if (perceptual)
            {
                traceSource = resources.cameraOpaqueTexture;
                if (!traceSource.IsValid())
                {
                    perceptual = false;
                    if (!missingOpaqueTextureLogged)
                    {
                        Debug.LogWarning("Volumetric Clouds: perceptual blending requires URP's Opaque Texture; disabling it for this camera.");
                        missingOpaqueTextureLogged = true;
                    }
                }
            }

            using (var builder = renderGraph.AddRasterRenderPass<CloudRasterData>(traceRenderGraphPassName, out var data, traceProfilingSampler))
            {
                data.material = cloudsMaterial;
                data.source = traceSource;
                data.namedTexture = resources.cameraDepthTexture;
                data.namedTextureId = cameraDepthTexture;
                data.keyword = perceptualBlendingKeyword;
                data.setKeyword = true;
                data.keywordEnabled = perceptual;
                data.pass = 0;
                data.setMainLight = true;
                data.mainLightPosition = lightPosition;
                data.mainLightColor = lightColor;
                builder.AllowGlobalStateModification(true);
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                if (traceSource.IsValid())
                    builder.UseTexture(traceSource, AccessFlags.Read);
                builder.SetRenderAttachment(cloud, 0, AccessFlags.WriteAll);
#if VC_DEPTH
                if (writeCloudDepth)
                    builder.SetRenderAttachment(cloudDepth, 1, AccessFlags.WriteAll);
#endif
                builder.SetRenderFunc(static (CloudRasterData d, RasterGraphContext c) => DrawCloudRaster(d, c));
            }

            TextureHandle finalCloud = cloud;
#if VC_DENOISE
            if (denoiseClouds)
            {
                if (!renderGraphHistory.TryGetValue(historyKey, out CloudHistory history))
                {
                    history = new CloudHistory();
                    renderGraphHistory.Add(historyKey, history);
                }
                bool reallocA = RenderingUtils.ReAllocateHandleIfNeeded(ref history.textureA, lowDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _VolumetricCloudsHistoryTextureA);
                bool reallocB = RenderingUtils.ReAllocateHandleIfNeeded(ref history.textureB, lowDesc, FilterMode.Point, TextureWrapMode.Clamp, name: _VolumetricCloudsHistoryTextureB);
                bool resetHistory = camera.TryGetComponent(out UniversalAdditionalCameraData additional) && additional.resetHistory;
                bool consecutiveRender = history.lastFrameRendered == frame - 1 || history.lastFrameRendered == frame;
                bool historyValid = history.valid && consecutiveRender && !reallocA && !reallocB && !resetHistory;
                TextureHandle historyRead = renderGraph.ImportTexture(history.ReadTexture);
                TextureHandle historyWrite = renderGraph.ImportTexture(history.WriteTexture);
                using (var builder = renderGraph.AddRasterRenderPass<CloudRasterData>(temporalRenderGraphPassName, out var data, temporalProfilingSampler))
                {
                    data.material = cloudsMaterial;
                    data.source = cloud;
                    data.pass = 3;
                    data.namedTexture = historyRead;
                    data.namedTextureId = volumetricCloudsHistoryTexture;
                    data.secondNamedTexture = resources.cameraDepthTexture;
                    data.secondNamedTextureId = cameraDepthTexture;
                    data.setHistoryValid = true;
                    data.historyValid = historyValid;
                    builder.AllowGlobalStateModification(true);
                    builder.UseTexture(cloud, AccessFlags.Read);
                    builder.UseTexture(historyRead, AccessFlags.Read);
                    builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(historyWrite, 0, AccessFlags.WriteAll);
                    builder.SetRenderFunc(static (CloudRasterData d, RasterGraphContext c) => DrawCloudRaster(d, c));
                }
                history.Commit(frame);
                finalCloud = historyWrite;
            }
#endif

            Vector4 screen = new Vector4(fullDesc.width, fullDesc.height, 1.0f / fullDesc.width, 1.0f / fullDesc.height);
            Material combineMaterial = cloudsMaterial;
            int combineShaderPass = combinePass;
#if URP_PBSKY
            PBSkyFrameResources physicallyBasedSkyResources = frameData.GetOrCreate<PBSkyFrameResources>();
            bool usePhysicallyBasedSkyCombine = hasAtmosphericScattering &&
                physicallyBasedSkyCombineMaterial != null &&
                physicallyBasedSkyCombinePass >= 0 &&
                physicallyBasedSkyResources.airSingleScattering.IsValid() &&
                physicallyBasedSkyResources.aerosolSingleScattering.IsValid() &&
                physicallyBasedSkyResources.multipleScattering.IsValid();
            if (usePhysicallyBasedSkyCombine)
            {
                combineMaterial = physicallyBasedSkyCombineMaterial;
                combineShaderPass = physicallyBasedSkyCombinePass;
                CoreUtils.SetKeyword(combineMaterial, lowResClouds, cloudsMaterial.IsKeywordEnabled(lowResClouds));
                CoreUtils.SetKeyword(combineMaterial, outputCloudsDepth, cloudsMaterial.IsKeywordEnabled(outputCloudsDepth));
                CoreUtils.SetKeyword(combineMaterial, depthAwareCloudEdgeFilterKeyword, cloudsMaterial.IsKeywordEnabled(depthAwareCloudEdgeFilterKeyword));
                CoreUtils.SetKeyword(combineMaterial, depthAwareCloudEdgeFilterDebugKeyword, cloudsMaterial.IsKeywordEnabled(depthAwareCloudEdgeFilterDebugKeyword));
                combineMaterial.SetFloat(depthAwareCloudEdgeFilterWidth, cloudsMaterial.GetFloat(depthAwareCloudEdgeFilterWidth));
            }
            else if (hasAtmosphericScattering && !missingPhysicallyBasedSkyCombinePassLogged)
            {
                Debug.LogWarning($"Volumetric Clouds URP: PBSky shader pass '{physicallyBasedSkyCombinePassName}' or its frame resources are unavailable. Atmospheric scattering and fog will not affect volumetric clouds; using the standard combine pass instead.");
                missingPhysicallyBasedSkyCombinePassLogged = true;
            }
#endif
            bool selectedPhysicallyBasedSkyCombine = combineMaterial != cloudsMaterial;
            string selectedCombineRenderGraphPassName = selectedPhysicallyBasedSkyCombine ? physicallyBasedSkyCombineRenderGraphPassName : combineRenderGraphPassName;
            ProfilingSampler selectedCombineProfilingSampler = selectedPhysicallyBasedSkyCombine ? physicallyBasedSkyCombineProfilingSampler : combineProfilingSampler;
            using (var builder = renderGraph.AddRasterRenderPass<CloudRasterData>(selectedCombineRenderGraphPassName, out var data, selectedCombineProfilingSampler))
            {
                data.material = combineMaterial;
                data.source = finalCloud;
                data.pass = combineShaderPass;
                data.namedTexture = finalCloud;
                data.namedTextureId = volumetricCloudsLightingTexture;
                data.secondNamedTexture = resources.cameraDepthTexture;
                data.secondNamedTextureId = cameraDepthTexture;
#if VC_DEPTH
                if (writeCloudDepth)
                {
                    data.thirdNamedTexture = cloudDepth;
                    data.thirdNamedTextureId = volumetricCloudsDepthTexture;
                }
#endif
#if URP_PBSKY
                if (selectedPhysicallyBasedSkyCombine)
                {
                    data.airSingleScattering = physicallyBasedSkyResources.airSingleScattering;
                    data.aerosolSingleScattering = physicallyBasedSkyResources.aerosolSingleScattering;
                    data.multipleScattering = physicallyBasedSkyResources.multipleScattering;
                    data.fogSky = physicallyBasedSkyResources.fogSky;
                }
#endif
                data.screenResolution = screen;
                builder.AllowGlobalStateModification(true);
                builder.UseTexture(finalCloud, AccessFlags.Read);
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
#if VC_DEPTH
                if (writeCloudDepth) builder.UseTexture(cloudDepth, AccessFlags.Read);
#endif
#if URP_PBSKY
                if (selectedPhysicallyBasedSkyCombine)
                {
                    builder.UseTexture(data.airSingleScattering, AccessFlags.Read);
                    builder.UseTexture(data.aerosolSingleScattering, AccessFlags.Read);
                    builder.UseTexture(data.multipleScattering, AccessFlags.Read);
                    if (data.fogSky.IsValid()) builder.UseTexture(data.fogSky, AccessFlags.Read);
                }
#endif
                builder.UseAllGlobalTextures(true);
                // Fixed-function blending loads the attachment; the shader does not sample it.
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(finalCloud, volumetricCloudsColorTexture);
                builder.SetGlobalTextureAfterPass(finalCloud, volumetricCloudsLightingTexture);
#if VC_DEPTH
                if (writeCloudDepth)
                    builder.SetGlobalTextureAfterPass(cloudDepth, volumetricCloudsDepthTexture);
#endif
                builder.SetRenderFunc(static (CloudRasterData d, RasterGraphContext c) => DrawCloudRaster(d, c));
            }

#if VC_DEPTH
            bool updateSceneDepth = outputToSceneDepth && (camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView);
            if (updateSceneDepth)
            {
                RenderTextureDescriptor tempDesc = CloudDescriptor(cameraData.cameraTargetDescriptor, RenderTextureFormat.RFloat, 1.0f);
                TextureHandle tempDepth = UniversalRenderer.CreateRenderGraphTexture(renderGraph, tempDesc, _CameraTempDepthTexture, false, FilterMode.Point, TextureWrapMode.Clamp);
                using (var builder = renderGraph.AddRasterRenderPass<CloudRasterData>(copyDepthRenderGraphPassName, out var data, copyDepthProfilingSampler))
                {
                    data.source = resources.cameraDepthTexture;
                    data.copyTexture = true;
                    builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(tempDepth, 0, AccessFlags.WriteAll);
                    builder.SetRenderFunc(static (CloudRasterData d, RasterGraphContext c) => DrawCloudRaster(d, c));
                }
                TextureDesc depthDesc = renderGraph.GetTextureDesc(resources.cameraDepthTexture);
                using (var builder = renderGraph.AddRasterRenderPass<CloudRasterData>(updateDepthRenderGraphPassName, out var data, updateDepthProfilingSampler))
                {
                    data.material = cloudsMaterial;
                    data.source = tempDepth;
                    data.pass = 6;
                    data.namedTexture = cloudDepth;
                    data.namedTextureId = volumetricCloudsDepthTexture;
                    builder.AllowGlobalStateModification(true);
                    builder.UseTexture(tempDepth, AccessFlags.Read);
                    builder.UseTexture(cloudDepth, AccessFlags.Read);
                    if (depthDesc.depthBufferBits != DepthBits.None)
                        builder.SetRenderAttachmentDepth(resources.cameraDepthTexture, AccessFlags.WriteAll);
                    else
                        builder.SetRenderAttachment(resources.cameraDepthTexture, 0, AccessFlags.WriteAll);
                    builder.SetRenderFunc(static (CloudRasterData d, RasterGraphContext c) => DrawCloudRaster(d, c));
                }
            }
#endif
        }

        #endregion
#endif

        #region Shared
        public void Dispose()
        {
#if VC_DENOISE
            foreach (CloudHistory history in renderGraphHistory.Values)
                history.Dispose();
            renderGraphHistory.Clear();
#endif

#if URP_PBSKY
            CoreUtils.Destroy(physicallyBasedSkyCombineMaterial);
#endif
            CoreUtils.Destroy(customLutPresetMap);
            customLutPresetMap = null;
        }
        #endregion
    }
    public class VolumetricCloudsAmbientPass : ScriptableRenderPass
    {
        private const string profilerTag = "Volumetric Clouds: Ambient Probe";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        private readonly Material cloudsMaterial;
        private RTHandle probeColorHandle;

        private const string _VolumetricCloudsAmbientProbe = "_VolumetricCloudsAmbientProbe";

        private const string STEREO_INSTANCING_ON = "STEREO_INSTANCING_ON";

        private static readonly int worldSpaceCameraPos = Shader.PropertyToID("_WorldSpaceCameraPos");
        private static readonly int disableSunDisk = Shader.PropertyToID("_DisableSunDisk");
        //private static readonly int unity_MatrixVP = Shader.PropertyToID("unity_MatrixVP");
        private static readonly int unity_MatrixInvVP = Shader.PropertyToID("unity_MatrixInvVP");
        private static readonly int scaledScreenParams = Shader.PropertyToID("_ScaledScreenParams");
        private static readonly int screenSize = Shader.PropertyToID("_ScreenSize");

        private static readonly int volumetricCloudsAmbientProbe = Shader.PropertyToID(_VolumetricCloudsAmbientProbe);

        // Modified from CoreUtils.lookAtList to swap the directions of up and down faces
        private static readonly Matrix4x4 frontView = new Matrix4x4(float4(-1, 0, 0, 0), float4(0, -1, 0, 0), float4(0, 0, -1, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 backView = new Matrix4x4(float4(1, 0, 0, 0), float4(0, -1, 0, 0), float4(0, 0, 1, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 upView = new Matrix4x4(float4(1, 0, 0, 0), float4(0, 0, -1, 0), float4(0, -1, 0, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 downView = new Matrix4x4(float4(1, 0, 0, 0), float4(0, 0, 1, 0), float4(0, 1, 0, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 rightView = new Matrix4x4(float4(0, 0, -1, 0), float4(0, -1, 0, 0), float4(1, 0, 0, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 leftView = new Matrix4x4(float4(0, 0, 1, 0), float4(0, -1, 0, 0), float4(-1, 0, 0, 0), float4(0, 0, 0, 1));

        // Cubemap Order: right, left, up, down, back, front. (+X, -X, +Y, -Y, +Z, -Z)
        private static readonly Matrix4x4[] skyViews = { rightView, leftView, upView, downView, backView, frontView };

#if UNITY_6000_0_OR_NEWER
        private readonly RendererListHandle[] rendererListHandles = new RendererListHandle[6];
#endif

#if UNITY_6000_0_OR_NEWER
        private readonly Matrix4x4[] skyViewMatrices = new Matrix4x4[6];
#endif // UNITY_6000_0_OR_NEWER

        private static readonly Matrix4x4 skyProjectionMatrix = Matrix4x4.Perspective(90.0f, 1.0f, 0.1f, 10.0f);
        private static readonly Vector4 skyViewScreenParams = new Vector4(16.0f, 16.0f, 1.0f + rcp(16.0f), 1.0f + rcp(16.0f));
        private static readonly Vector4 skyViewScreenSize = new Vector4(16.0f, 16.0f, rcp(16.0f), rcp(16.0f));

        public VolumetricCloudsAmbientPass(Material material)
        {
            cloudsMaterial = material;
        }

#if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        private class PassData
        {
            internal Material cloudsMaterial;

            internal TextureHandle probeColorHandle;

            internal Vector3 cameraPositionWS;
            internal Vector4 cameraScreenParams;
            internal Vector4 cameraScreenSize;
            internal Matrix4x4 worldToCameraMatrix;
            internal Matrix4x4 projectionMatrix;

            internal RendererListHandle[] rendererListHandles;
            internal Matrix4x4[] skyViewMatrices;
            internal Matrix4x4 skyProjectionMatrix;

            internal bool isStereoEnabled;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            if (data.isStereoEnabled)
                cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

            context.cmd.SetGlobalVector(worldSpaceCameraPos, Vector3.zero);
            context.cmd.SetGlobalFloat(disableSunDisk, 1.0f);

            context.cmd.SetGlobalVector(scaledScreenParams, skyViewScreenParams);
            context.cmd.SetGlobalVector(screenSize, skyViewScreenSize);

            Matrix4x4 skyMatrixP = GL.GetGPUProjectionMatrix(data.skyProjectionMatrix, true);

            for (int i = 0; i < 6; i++)
            {
                CoreUtils.SetRenderTarget(cmd, data.probeColorHandle, ClearFlag.None, 0, (CubemapFace)i);

                Matrix4x4 skyMatrixVP = skyMatrixP * data.skyViewMatrices[i];

                // Camera matrices for skybox rendering
                cmd.SetViewMatrix(data.skyViewMatrices[i]);
                //cmd.SetProjectionMatrix(skyMatrixP);
                //context.cmd.SetGlobalMatrix(unity_MatrixVP, skyMatrixVP);
                context.cmd.SetGlobalMatrix(unity_MatrixInvVP, skyMatrixVP.inverse);

                context.cmd.DrawRendererList(data.rendererListHandles[i]);
            }

            data.cloudsMaterial.SetTexture(volumetricCloudsAmbientProbe, data.probeColorHandle);

            context.cmd.SetGlobalVector(worldSpaceCameraPos, data.cameraPositionWS);
            context.cmd.SetGlobalFloat(disableSunDisk, 0.0f);

            Matrix4x4 matrixVP = GL.GetGPUProjectionMatrix(data.projectionMatrix, true) * data.worldToCameraMatrix;

            // Camera matrices for objects rendering
            cmd.SetViewMatrix(data.worldToCameraMatrix);
            //cmd.SetProjectionMatrix(data.projectionMatrix);
            //context.cmd.SetGlobalMatrix(unity_MatrixVP, matrixVP);
            context.cmd.SetGlobalMatrix(unity_MatrixInvVP, matrixVP.inverse);
            context.cmd.SetGlobalVector(scaledScreenParams, data.cameraScreenParams);
            context.cmd.SetGlobalVector(screenSize, data.cameraScreenSize);

            if (data.isStereoEnabled)
                cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(profilerTag, out var passData, m_ProfilingSampler))
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;

                float2 cameraResolution = float2(desc.width, desc.height);

                desc.msaaSamples = 1;
                desc.useMipMap = true;
                desc.autoGenerateMips = true;
                desc.width = 16;
                desc.height = 16;
                desc.dimension = TextureDimension.Cube;
                desc.depthBufferBits = 0;
                RenderingUtils.ReAllocateHandleIfNeeded(ref probeColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _VolumetricCloudsAmbientProbe);
                TextureHandle probeColorTextureHandle = renderGraph.ImportTexture(probeColorHandle);
                passData.probeColorHandle = probeColorTextureHandle;
                passData.cloudsMaterial = cloudsMaterial;

                for (int i = 0; i < 6; i++)
                {
                    //var lookAt = Matrix4x4.LookAt(Vector3.zero, CoreUtils.lookAtList[i], CoreUtils.upVectorList[i]);
                    //Matrix4x4 viewMatrix = lookAt * Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)); // Need to scale -1.0 on Z to match what is being done in the camera.wolrdToCameraMatrix API. ...

                    Matrix4x4 viewMatrix = skyViews[i];
                    viewMatrix *= Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)); // Need to scale -1.0 on Z to match what is being done in the camera.wolrdToCameraMatrix API. ...
                    skyViewMatrices[i] = viewMatrix;
                    rendererListHandles[i] = renderGraph.CreateSkyboxRendererList(cameraData.camera, skyProjectionMatrix, viewMatrix);
                    builder.UseRendererList(rendererListHandles[i]);
                }

                // Fill up the passData with the data needed by the pass
                passData.rendererListHandles = rendererListHandles;
                passData.skyViewMatrices = skyViewMatrices;
                passData.skyProjectionMatrix = skyProjectionMatrix;
                passData.cloudsMaterial = cloudsMaterial;
                passData.cameraPositionWS = cameraData.camera.transform.position;
                passData.cameraScreenSize = new Vector4(cameraResolution.x, cameraResolution.y, rcp(cameraResolution.x), rcp(cameraResolution.y));
                passData.cameraScreenParams = new Vector4(cameraResolution.x, cameraResolution.y, 1.0f + passData.cameraScreenSize.z, 1.0f + passData.cameraScreenSize.w);
                passData.worldToCameraMatrix = cameraData.camera.worldToCameraMatrix;
                passData.projectionMatrix = cameraData.camera.projectionMatrix;
                passData.isStereoEnabled = cameraData.camera.stereoEnabled;

                // UnsafePasses don't setup the outputs using UseTextureFragment/UseTextureFragmentDepth, you should specify your writes with UseTexture instead
                builder.UseTexture(passData.probeColorHandle, AccessFlags.Write);

                // Global shader property changes are considered as global state modifications
                builder.AllowGlobalStateModification(true);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
        #endregion
#endif

        #region Shared
        public void Dispose()
        {
            probeColorHandle?.Release();
        }
        #endregion
    }
    public class VolumetricCloudsShadowsPass : ScriptableRenderPass
    {
        private const string profilerTag = "Volumetric Clouds: Shadow Cookie";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        public VolumetricClouds cloudsVolume;
#if URP_PBSKY
        public VisualEnvironment visualEnvVolume;
#endif
        private readonly Material cloudsMaterial;

        private RTHandle shadowTextureHandle;

        private readonly Vector3[] frustumCorners = new Vector3[4];

        private Light targetLight;

        private static readonly int shadowCookieResolution = Shader.PropertyToID("_ShadowCookieResolution");
        private static readonly int shadowIntensity = Shader.PropertyToID("_ShadowIntensity");
        private static readonly int shadowOpacityFallback = Shader.PropertyToID("_ShadowOpacityFallback");
        private static readonly int cloudShadowSunOrigin = Shader.PropertyToID("_CloudShadowSunOrigin");
        private static readonly int cloudShadowSunRight = Shader.PropertyToID("_CloudShadowSunRight");
        private static readonly int cloudShadowSunUp = Shader.PropertyToID("_CloudShadowSunUp");
        private static readonly int cloudShadowSunForward = Shader.PropertyToID("_CloudShadowSunForward");
        private static readonly int cameraPositionPS = Shader.PropertyToID("_CameraPositionPS");
        private static readonly int volumetricCloudsShadowOriginToggle = Shader.PropertyToID("_VolumetricCloudsShadowOriginToggle");
        private static readonly int volumetricCloudsShadowScale = Shader.PropertyToID("_VolumetricCloudsShadowScale");
        //private static readonly int shadowPlaneOffset = Shader.PropertyToID("_ShadowPlaneOffset");

        private const string _VolumetricCloudsShadowTexture = "_VolumetricCloudsShadowTexture";
        private const string _VolumetricCloudsShadowTempTexture = "_VolumetricCloudsShadowTempTexture";

        private const string _LIGHT_COOKIES = "_LIGHT_COOKIES";
        private const string STEREO_INSTANCING_ON = "STEREO_INSTANCING_ON";

        private static readonly Matrix4x4 s_DirLightProj = Matrix4x4.Ortho(-0.5f, 0.5f, -0.5f, 0.5f, -0.5f, 0.5f);

        private static readonly int mainLightTexture = Shader.PropertyToID("_MainLightCookieTexture");
        private static readonly int mainLightWorldToLight = Shader.PropertyToID("_MainLightWorldToLight");
        private static readonly int mainLightCookieTextureFormat = Shader.PropertyToID("_MainLightCookieTextureFormat");

        public VolumetricCloudsShadowsPass(Material material)
        {
            cloudsMaterial = material;
        }

#if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        static
        private Light GetMainLight(UniversalLightData lightData)
        {
            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex != -1)
            {
                VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
                Light light = shadowLight.light;
                if ((light.shadows != LightShadows.None || RenderSettings.sun != null && !RenderSettings.sun.isActiveAndEnabled) && shadowLight.lightType == LightType.Directional)
                    return light;
            }

            return RenderSettings.sun;
        }

        private class PassData
        {
            internal Material cloudsMaterial;

            internal TextureHandle intermediateShadowTexture;
            internal TextureHandle shadowTexture;

            internal Matrix4x4 mainLightWorldToLight;
            internal float mainLightCookieTextureFormat;

            internal Vector4 shadowOriginToggle;
            internal Vector4 shadowScale;

            internal bool isStereoEnabled;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            if (data.isStereoEnabled)
                cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

            // Render shadow cookie texture
            Blitter.BlitCameraTexture(cmd, data.shadowTexture, data.shadowTexture, data.cloudsMaterial, pass: 4);

            // Given the low number of steps available and the absence of noise in the integration, we try to reduce the artifacts by doing two consecutive 3x3 blur passes.
            Blitter.BlitCameraTexture(cmd, data.shadowTexture, data.intermediateShadowTexture, data.cloudsMaterial, pass: 5);
            Blitter.BlitCameraTexture(cmd, data.intermediateShadowTexture, data.shadowTexture, data.cloudsMaterial, pass: 5);

            cmd.SetGlobalVector(volumetricCloudsShadowOriginToggle, data.shadowOriginToggle);
            cmd.SetGlobalVector(volumetricCloudsShadowScale, data.shadowScale); // Used in physically based sky

            cmd.SetGlobalTexture(mainLightTexture, data.shadowTexture);
            cmd.SetGlobalMatrix(mainLightWorldToLight, data.mainLightWorldToLight);
            cmd.SetGlobalFloat(mainLightCookieTextureFormat, data.mainLightCookieTextureFormat);
            cmd.EnableShaderKeyword(_LIGHT_COOKIES);

            if (data.isStereoEnabled)
                cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            // Get and update the main light
            Light light = GetMainLight(lightData);
            if (targetLight != light)
            {
                ResetShadowCookie();
                targetLight = light;
            }

            // Check if we need shadow cookie
            bool hasVolumetricCloudsShadows = targetLight != null && targetLight.isActiveAndEnabled && targetLight.intensity != 0.0f;
            if (!hasVolumetricCloudsShadows)
            {
                ResetShadowCookie();
                return;
            }

            var camera = cameraData.camera;

            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(profilerTag, out var passData, m_ProfilingSampler))
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into

                Matrix4x4 wsToLSMat = targetLight.transform.worldToLocalMatrix;
                Matrix4x4 lsToWSMat = targetLight.transform.localToWorldMatrix;

                float3 cameraPos = camera.transform.position;

                float perspectiveCorrectedShadowDistance = cloudsVolume.shadowDistance.value / cos(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);

                camera.CalculateFrustumCorners(new Rect(0, 0, 1, 1), camera.farClipPlane, Camera.MonoOrStereoscopicEye.Mono, frustumCorners);

                // Generate the light space bounds of the camera frustum
                Bounds lightSpaceBounds = new Bounds();
                lightSpaceBounds.SetMinMax(new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue));
                lightSpaceBounds.Encapsulate(wsToLSMat.MultiplyPoint(cameraPos));
                for (int cornerIdx = 0; cornerIdx < 4; ++cornerIdx)
                {
                    Vector3 corner = frustumCorners[cornerIdx];
                    float diag = corner.magnitude;
                    corner = (corner / diag) * Mathf.Min(perspectiveCorrectedShadowDistance, diag);
                    Vector3 posLightSpace = wsToLSMat.MultiplyPoint(float3(corner) + cameraPos);
                    lightSpaceBounds.Encapsulate(posLightSpace);

                    posLightSpace = wsToLSMat.MultiplyPoint(float3(-corner) + cameraPos);
                    lightSpaceBounds.Encapsulate(posLightSpace);
                }

                // Compute the four corners we need
                float3 c0 = lsToWSMat.MultiplyPoint(lightSpaceBounds.center + new Vector3(-lightSpaceBounds.extents.x, -lightSpaceBounds.extents.y, lightSpaceBounds.extents.z));
                float3 c1 = lsToWSMat.MultiplyPoint(lightSpaceBounds.center + new Vector3(lightSpaceBounds.extents.x, -lightSpaceBounds.extents.y, lightSpaceBounds.extents.z));
                float3 c2 = lsToWSMat.MultiplyPoint(lightSpaceBounds.center + new Vector3(-lightSpaceBounds.extents.x, lightSpaceBounds.extents.y, lightSpaceBounds.extents.z));

#if URP_PBSKY
                bool isVolumeActive = visualEnvVolume != null && visualEnvVolume.IsActive();

                float4 planetCenterRad = visualEnvVolume.GetPlanetCenterRadius(camera.transform.position);
                float actualEarthRad = isVolumeActive ? planetCenterRad.w : Mathf.Lerp(1.0f, 0.025f, cloudsVolume.earthCurvature.value) * VolumetricCloudsPass.earthRad;
                float3 planetCenterPos = isVolumeActive ? planetCenterRad.xyz : float3(0.0f, -actualEarthRad, 0.0f);
#else
                float actualEarthRad = Mathf.Lerp(1.0f, 0.025f, cloudsVolume.earthCurvature.value) * VolumetricCloudsPass.earthRad;
                float3 planetCenterPos = float3(0.0f, -actualEarthRad, 0.0f);
#endif

                float3 dirX = c1 - c0;
                float3 dirY = c2 - c0;

                // The shadow cookie size
                float2 regionSize = float2(length(dirX), length(dirY));

                // Should we support colored shadows?
                GraphicsFormat cookieTextureFormat = GraphicsFormat.R16_UNorm; //option 2: R8_UNorm
                bool useSingleChannel = SystemInfo.IsFormatSupported(cookieTextureFormat, GraphicsFormatUsage.Render);
                cookieTextureFormat = useSingleChannel ? cookieTextureFormat : GraphicsFormat.B10G11R11_UFloatPack32;

                int shadowResolution = (int)cloudsVolume.shadowResolution.value;
                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                desc.msaaSamples = 1;
                desc.depthBufferBits = 0;
                desc.useMipMap = false;
                desc.graphicsFormat = cookieTextureFormat;
                desc.height = shadowResolution;
                desc.width = shadowResolution;
                desc.dimension = TextureDimension.Tex2D;
                RenderingUtils.ReAllocateHandleIfNeeded(ref shadowTextureHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _VolumetricCloudsShadowTexture);
                TextureHandle shadowTexture = renderGraph.ImportTexture(shadowTextureHandle);

                TextureHandle intermediateShadowTexture = renderGraph.CreateTexture(new TextureDesc(shadowResolution, shadowResolution, false, false)
                { colorFormat = cookieTextureFormat, enableRandomWrite = false, name = _VolumetricCloudsShadowTempTexture });

                // Update material properties
                cloudsMaterial.SetFloat(shadowCookieResolution, shadowResolution);
                //cloudsMaterial.SetFloat(shadowPlaneOffset, cloudsVolume.shadowPlaneHeightOffset.value);
                cloudsMaterial.SetFloat(shadowIntensity, cloudsVolume.shadowOpacity.value);
                cloudsMaterial.SetFloat(shadowOpacityFallback, 1.0f - cloudsVolume.shadowOpacityFallback.value);
                cloudsMaterial.SetVector(cloudShadowSunOrigin, float4(c0 - planetCenterPos, 1.0f));
                cloudsMaterial.SetVector(cloudShadowSunRight, float4(dirX, 0.0f));
                cloudsMaterial.SetVector(cloudShadowSunUp, float4(dirY, 0.0f));
                cloudsMaterial.SetVector(cloudShadowSunForward, float4(-targetLight.transform.forward, 0.0f));
                cloudsMaterial.SetVector(cameraPositionPS, float4(cameraPos - planetCenterPos, 0.0f));
                cloudsMaterial.SetVector(volumetricCloudsShadowOriginToggle, float4(c0, 0.0f));

                // Apply light cookie settings
                targetLight.cookie = null;
                UniversalAdditionalLightData additonal = targetLight.GetComponent<UniversalAdditionalLightData>();
                additonal.lightCookieSize = Vector2.one;
                additonal.lightCookieOffset = Vector2.zero;

                // Apply shadow cookie
                Vector2 uvScale = 1 / regionSize;
                float minHalfValue = Unity.Mathematics.half.MinValue;
                if (Mathf.Abs(uvScale.x) < minHalfValue)
                    uvScale.x = Mathf.Sign(uvScale.x) * minHalfValue;
                if (Mathf.Abs(uvScale.y) < minHalfValue)
                    uvScale.y = Mathf.Sign(uvScale.y) * minHalfValue;

                Matrix4x4 cookieUVTransform = Matrix4x4.Scale(new Vector3(uvScale.x, uvScale.y, 1));
                //cookieUVTransform.SetColumn(3, new Vector4(uvScale.x, uvScale.y, 0, 1));
                lsToWSMat.SetColumn(3, float4(cameraPos, 1));
                Matrix4x4 cookieMatrix = s_DirLightProj * cookieUVTransform * lsToWSMat.inverse;

                float cookieFormat = (float)GetLightCookieShaderFormat(cookieTextureFormat);

                // Fill up the passData with the data needed by the pass
                passData.cloudsMaterial = cloudsMaterial;
                passData.shadowTexture = shadowTexture;
                passData.intermediateShadowTexture = intermediateShadowTexture;
                passData.mainLightWorldToLight = cookieMatrix;
                passData.mainLightCookieTextureFormat = cookieFormat;
                passData.shadowOriginToggle = float4(c0, 0.0f);
                passData.shadowScale = float4(regionSize, 0.0f, 0.0f);
                passData.isStereoEnabled = cameraData.camera.stereoEnabled;

                // This pass traces into the shadow texture, then ping-pongs through both textures while filtering.
                builder.UseTexture(passData.shadowTexture, AccessFlags.ReadWrite);
                builder.UseTexture(passData.intermediateShadowTexture, AccessFlags.ReadWrite);

                // Shader keyword changes (_LIGHT_COOKIES) are considered as global state modifications
                builder.AllowGlobalStateModification(true);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
        #endregion
#endif

        #region Shared
        private enum LightCookieShaderFormat
        {
            None = -1,

            RGB = 0,
            Alpha = 1,
            Red = 2
        }

        private LightCookieShaderFormat GetLightCookieShaderFormat(GraphicsFormat cookieFormat)
        {
            // TODO: convert this to use GraphicsFormatUtility
            switch (cookieFormat)
            {
                default:
                    return LightCookieShaderFormat.RGB;
                // A8, A16 GraphicsFormat does not expose yet.
                case (GraphicsFormat)54:
                case (GraphicsFormat)55:
                    return LightCookieShaderFormat.Alpha;
                case GraphicsFormat.R8_SRGB:
                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8_UInt:
                case GraphicsFormat.R8_SNorm:
                case GraphicsFormat.R8_SInt:
                case GraphicsFormat.R16_UNorm:
                case GraphicsFormat.R16_UInt:
                case GraphicsFormat.R16_SNorm:
                case GraphicsFormat.R16_SInt:
                case GraphicsFormat.R16_SFloat:
                case GraphicsFormat.R32_UInt:
                case GraphicsFormat.R32_SInt:
                case GraphicsFormat.R32_SFloat:
                case GraphicsFormat.R_BC4_SNorm:
                case GraphicsFormat.R_BC4_UNorm:
                case GraphicsFormat.R_EAC_SNorm:
                case GraphicsFormat.R_EAC_UNorm:
                    return LightCookieShaderFormat.Red;
            }
        }

        private void ResetShadowCookie()
        {
            if (targetLight != null)
            {
                targetLight.cookie = null;
                UniversalAdditionalLightData additionalData = targetLight.GetComponent<UniversalAdditionalLightData>();
                if (additionalData != null)
                {
                    additionalData.lightCookieSize = Vector2.one;
                    additionalData.lightCookieOffset = Vector2.zero;
                }
            }
        }

        public void Dispose()
        {
            ResetShadowCookie();
            shadowTextureHandle?.Release();
        }
        #endregion
    }
}
