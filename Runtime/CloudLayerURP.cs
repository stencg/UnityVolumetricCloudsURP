#if UNITY_6000_0_OR_NEWER
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleRendererFeature("Cloud Layer URP")]
[Tooltip("Add this Renderer Feature to support Cloud Layer in URP Volumes.")]
public sealed class CloudLayerURP : ScriptableRendererFeature
{
    private const string PackagePath = "Packages/com.jiaozi158.unity-volumetric-clouds-urp/";

    [Header("Setup")]
    [Reload("Runtime/CloudLayer.shader"), SerializeField] private Shader _cloudLayerShader;
    [Reload("Runtime/BakeCloudLayerTexture.compute"), SerializeField] private ComputeShader _bakeCloudTexture;
    [Reload("Runtime/BakeCloudLayerShadows.compute"), SerializeField] private ComputeShader _bakeCloudShadows;
    [SerializeField] private bool _renderingDebugger;
    [SerializeField] private bool _reflectionProbe;

    private Material _material;
    private CloudLayerResources _resources;
    private CloudLayerRenderPass _renderPass;
    private CloudLayerShadowPass _shadowPass;
    private bool _missingResourcesLogged;
    private bool _unsupportedCookiesLogged;

    private static class ShaderIds
    {
        internal static readonly int CloudTexture = Shader.PropertyToID("_CloudTexture");
        internal static readonly int CloudShadows = Shader.PropertyToID("_CloudShadows");
        internal static readonly int CloudMapA = Shader.PropertyToID("_CloudMapA");
        internal static readonly int CloudMapB = Shader.PropertyToID("_CloudMapB");
        internal static readonly int FlowmapA = Shader.PropertyToID("_FlowmapA");
        internal static readonly int FlowmapB = Shader.PropertyToID("_FlowmapB");
        internal static readonly int BakeParams = Shader.PropertyToID("_BakeParams");
        internal static readonly int CloudMapOpacityA = Shader.PropertyToID("_CloudMapOpacityA");
        internal static readonly int CloudMapOpacityB = Shader.PropertyToID("_CloudMapOpacityB");
        internal static readonly int CloudMapBakeParamsA = Shader.PropertyToID("_CloudMapBakeParamsA");
        internal static readonly int CloudMapBakeParamsB = Shader.PropertyToID("_CloudMapBakeParamsB");
        internal static readonly int Resolution = Shader.PropertyToID("_Resolution");
        internal static readonly int LayerCount = Shader.PropertyToID("_LayerCount");
        internal static readonly int FlowmapParamA = Shader.PropertyToID("_FlowmapParamA");
        internal static readonly int FlowmapParamB = Shader.PropertyToID("_FlowmapParamB");
        internal static readonly int LayerModes = Shader.PropertyToID("_LayerModes");
        internal static readonly int LayerEnabled = Shader.PropertyToID("_LayerEnabled");
        internal static readonly int CloudLayerParamsA = Shader.PropertyToID("_CloudLayerParamsA");
        internal static readonly int CloudLayerParamsB = Shader.PropertyToID("_CloudLayerParamsB");
        internal static readonly int AmbientParams = Shader.PropertyToID("_AmbientParams");
        internal static readonly int SunDirection = Shader.PropertyToID("_SunDirection");
        internal static readonly int PlanetaryRadius = Shader.PropertyToID("_PlanetaryRadius");
        internal static readonly int ShadowParams = Shader.PropertyToID("_ShadowParams");
        internal static readonly int ShAr = Shader.PropertyToID("_CloudLayerSHAr");
        internal static readonly int ShAg = Shader.PropertyToID("_CloudLayerSHAg");
        internal static readonly int ShAb = Shader.PropertyToID("_CloudLayerSHAb");
        internal static readonly int ShBr = Shader.PropertyToID("_CloudLayerSHBr");
        internal static readonly int ShBg = Shader.PropertyToID("_CloudLayerSHBg");
        internal static readonly int ShBb = Shader.PropertyToID("_CloudLayerSHBb");
        internal static readonly int ShC = Shader.PropertyToID("_CloudLayerSHC");
        internal static readonly int ViewRayBottomLeft = Shader.PropertyToID("_CloudLayerViewRayBottomLeft");
        internal static readonly int ViewRayTopLeft = Shader.PropertyToID("_CloudLayerViewRayTopLeft");
        internal static readonly int ViewRayTopRight = Shader.PropertyToID("_CloudLayerViewRayTopRight");
        internal static readonly int ViewRayBottomRight = Shader.PropertyToID("_CloudLayerViewRayBottomRight");
        internal static readonly int MainLightCookieTexture = Shader.PropertyToID("_MainLightCookieTexture");
        internal static readonly int MainLightWorldToLight = Shader.PropertyToID("_MainLightWorldToLight");
        internal static readonly int MainLightCookieTextureFormat = Shader.PropertyToID("_MainLightCookieTextureFormat");
    }

    /// <summary>
    /// Создаёт RenderGraph-проходы Cloud Layer и их общие ресурсы.
    /// </summary>
    public override void Create()
    {
#if UNITY_EDITOR
        ResourceReloader.TryReloadAllNullIn(this, PackagePath);
#endif

        _resources?.Dispose();
        CoreUtils.Destroy(_material);

        _material = _cloudLayerShader != null ? CoreUtils.CreateEngineMaterial(_cloudLayerShader) : null;
        int bakeCloudTextureKernel = _bakeCloudTexture != null ? _bakeCloudTexture.FindKernel("BakeCloudTexture") : -1;
        int bakeCloudShadowsKernel = _bakeCloudShadows != null ? _bakeCloudShadows.FindKernel("BakeCloudShadows") : -1;

        _resources = new CloudLayerResources(
            _bakeCloudTexture,
            bakeCloudTextureKernel,
            _bakeCloudShadows,
            bakeCloudShadowsKernel);
        _shadowPass = new CloudLayerShadowPass(_resources)
        {
            renderPassEvent = RenderPassEvent.BeforeRendering,
        };
        _renderPass = new CloudLayerRenderPass(_material, _resources)
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents - 1,
        };
        _missingResourcesLogged = false;
    }

    /// <summary>
    /// Добавляет проходы для активного Cloud Layer Volume.
    /// </summary>
    /// <param name="renderer">Активный URP renderer.</param>
    /// <param name="renderingData">Данные текущей камеры.</param>
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_material == null || _bakeCloudTexture == null || _bakeCloudShadows == null || !SystemInfo.supportsComputeShaders)
        {
            if (!_missingResourcesLogged)
            {
                Debug.LogError("Cloud Layer URP: shader resources are missing or compute shaders are not supported.");
                _missingResourcesLogged = true;
            }

            return;
        }

        if (renderingData.cameraData.renderType == CameraRenderType.Overlay)
            return;

#if UNITY_EDITOR
        bool isEditingPrefab = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null;
        bool isSceneViewFocused = UnityEditor.SceneView.lastActiveSceneView != null &&
            UnityEditor.SceneView.lastActiveSceneView.hasFocus;
        if (isEditingPrefab && isSceneViewFocused)
            return;
#endif

        CameraType cameraType = renderingData.cameraData.cameraType;
        bool supportedCamera = cameraType == CameraType.Game ||
            cameraType == CameraType.SceneView ||
            (cameraType == CameraType.Reflection && _reflectionProbe);
        if (!supportedCamera)
            return;

        CloudLayer cloudLayer = VolumeManager.instance.stack.GetComponent<CloudLayer>();
        bool debuggerAllowed = !DebugManager.instance.isAnyDebugUIActive || _renderingDebugger;
        if (cloudLayer == null || !cloudLayer.IsActive() || !debuggerAllowed)
            return;

        bool supportsCookies = UniversalRenderPipeline.asset != null && UniversalRenderPipeline.asset.supportsLightCookies;
        if (cloudLayer.CastShadows && supportsCookies)
        {
            _shadowPass.Setup(cloudLayer);
            renderer.EnqueuePass(_shadowPass);
            _unsupportedCookiesLogged = false;
        }
        else if (cloudLayer.CastShadows && !_unsupportedCookiesLogged)
        {
            Debug.LogWarning("Cloud Layer URP: Light Cookies are disabled in the active URP asset. Cloud shadows are skipped.");
            _unsupportedCookiesLogged = true;
        }

        _renderPass.Setup(cloudLayer, cloudLayer.CastShadows && supportsCookies);
        renderer.EnqueuePass(_renderPass);
    }

    /// <summary>
    /// Освобождает материал и постоянные RTHandle.
    /// </summary>
    /// <param name="disposing">Признак управляемого освобождения.</param>
    protected override void Dispose(bool disposing)
    {
        _resources?.Dispose();
        _resources = null;
        CoreUtils.Destroy(_material);
        _material = null;
    }

    private static Light GetMainLight(UniversalLightData lightData, out Color lightColor)
    {
        int mainLightIndex = lightData.mainLightIndex;
        if (mainLightIndex >= 0 && mainLightIndex < lightData.visibleLights.Length)
        {
            VisibleLight visibleLight = lightData.visibleLights[mainLightIndex];
            if (visibleLight.lightType == LightType.Directional)
            {
                lightColor = visibleLight.finalColor;
                return visibleLight.light;
            }
        }

        Light sun = RenderSettings.sun;
        if (sun != null && sun.type == LightType.Directional && sun.isActiveAndEnabled)
        {
            Color color = QualitySettings.activeColorSpace == ColorSpace.Linear ? sun.color.linear : sun.color;
            if (sun.useColorTemperature)
                color *= Mathf.CorrelatedColorTemperatureToRGB(sun.colorTemperature);
            lightColor = color * sun.intensity;
            return sun;
        }

        lightColor = Color.black;
        return null;
    }

    private sealed class CloudLayerResources
    {
        private const int ThreadGroupSize = 8;
        private static readonly ProfilingSampler BakeTextureSampler = new("Bake Cloud Layer Texture");
        private static readonly ProfilingSampler BakeShadowsSampler = new("Bake Cloud Layer Shadows");
        private static readonly Matrix4x4 DirectionalLightProjection =
            Matrix4x4.Ortho(-0.5f, 0.5f, -0.5f, 0.5f, -0.5f, 0.5f);

        private readonly ComputeShader _bakeCloudTexture;
        private readonly int _bakeCloudTextureKernel;
        private readonly ComputeShader _bakeCloudShadows;
        private readonly int _bakeCloudShadowsKernel;

        private RTHandle _cloudTexture;
        private RTHandle _cloudShadows;
        private RTHandle _cloudMapA;
        private RTHandle _cloudMapB;
        private RTHandle _flowmapA;
        private RTHandle _flowmapB;
        private Texture _cloudMapSourceA;
        private Texture _cloudMapSourceB;
        private Texture _flowmapSourceA;
        private Texture _flowmapSourceB;
        private int _cloudTextureWidth;
        private int _cloudTextureHeight;
        private int _cloudTextureSlices;
        private int _cloudShadowResolution;
        private int _lastBakingHash;
        private bool _hasBakingHash;
        private bool _cloudTextureDirty = true;
        private int _preparedFrame = -1;
#if UNITY_6000_4_OR_NEWER
        private EntityId _preparedCameraId;
#else
        private int _preparedCameraId;
#endif
        private float _lastTime = -1.0f;
        private float _scrollFactorA;
        private float _scrollFactorB;

        internal CloudLayerResources(
            ComputeShader bakeCloudTexture,
            int bakeCloudTextureKernel,
            ComputeShader bakeCloudShadows,
            int bakeCloudShadowsKernel)
        {
            _bakeCloudTexture = bakeCloudTexture;
            _bakeCloudTextureKernel = bakeCloudTextureKernel;
            _bakeCloudShadows = bakeCloudShadows;
            _bakeCloudShadowsKernel = bakeCloudShadowsKernel;
        }

        internal RTHandle CloudTexture => _cloudTexture;
        internal RTHandle FlowmapA => _flowmapA;
        internal RTHandle FlowmapB => _flowmapB;
        internal Texture FlowmapSourceA => _flowmapSourceA;
        internal Texture FlowmapSourceB => _flowmapSourceB;
        internal Vector4 FlowParametersA { get; private set; }
        internal Vector4 FlowParametersB { get; private set; }

        internal void Prepare(CloudLayer cloudLayer, Light sunLight, Camera camera, bool shadowsEnabled)
        {
#if UNITY_6000_4_OR_NEWER
            EntityId cameraId = camera.GetEntityId();
#else
            int cameraId = camera.GetInstanceID();
#endif
            if (_preparedFrame != Time.frameCount || _preparedCameraId != cameraId)
            {
                UpdateAnimation(cloudLayer);
                _preparedFrame = Time.frameCount;
                _preparedCameraId = cameraId;
            }

            FlowParametersA = cloudLayer.layerA.GetRenderingParameters(_scrollFactorA);
            FlowParametersB = cloudLayer.layerB.GetRenderingParameters(_scrollFactorB);
            Vector4 flowA = FlowParametersA;
            Vector4 flowB = FlowParametersB;
            flowA.w = cloudLayer.upperHemisphereOnly.value ? 1.0f : 0.0f;
            flowB.w = cloudLayer.opacity.value;
            FlowParametersA = flowA;
            FlowParametersB = flowB;

            EnsureExternalTexture(
                ref _cloudMapA,
                ref _cloudMapSourceA,
                cloudLayer.layerA.cloudMap.value != null ? cloudLayer.layerA.cloudMap.value : Texture2D.whiteTexture);
            EnsureExternalTexture(
                ref _cloudMapB,
                ref _cloudMapSourceB,
                cloudLayer.layerB.cloudMap.value != null ? cloudLayer.layerB.cloudMap.value : Texture2D.whiteTexture);
            EnsureExternalTexture(
                ref _flowmapA,
                ref _flowmapSourceA,
                cloudLayer.layerA.flowmap.value != null ? cloudLayer.layerA.flowmap.value : Texture2D.grayTexture);
            EnsureExternalTexture(
                ref _flowmapB,
                ref _flowmapSourceB,
                cloudLayer.layerB.flowmap.value != null ? cloudLayer.layerB.flowmap.value : Texture2D.grayTexture);

            EnsureCloudTexture(cloudLayer);
            int bakingHash = cloudLayer.GetBakingHashCode(sunLight);
            if (!_hasBakingHash || bakingHash != _lastBakingHash)
            {
                _lastBakingHash = bakingHash;
                _hasBakingHash = true;
                _cloudTextureDirty = true;
            }

            EnsureShadowTexture(cloudLayer, shadowsEnabled && sunLight != null);
        }

        internal TextureHandle RecordCloudTextureBake(RenderGraph renderGraph, CloudLayer cloudLayer, Light sunLight)
        {
            TextureHandle cloudTexture = renderGraph.ImportTexture(_cloudTexture);
            if (!_cloudTextureDirty)
                return cloudTexture;

            TextureHandle cloudMapA = renderGraph.ImportTexture(_cloudMapA);
            TextureHandle cloudMapB = renderGraph.ImportTexture(_cloudMapB);
            using (var builder = renderGraph.AddComputePass<CloudTexturePassData>(
                "Bake Cloud Layer Texture",
                out var passData,
                BakeTextureSampler))
            {
                cloudLayer.layerA.GetBakingParameters(out passData.opacityA, out passData.bakeParametersA);
                cloudLayer.layerB.GetBakingParameters(out passData.opacityB, out passData.bakeParametersB);

                Vector4 bakeParameters = sunLight != null
                    ? (Vector4)(-sunLight.transform.forward)
                    : Vector4.zero;
                bakeParameters.w = (cloudLayer.upperHemisphereOnly.value ? 1.0f : -1.0f) * cloudLayer.planetRadius.value;

                passData.computeShader = _bakeCloudTexture;
                passData.kernel = _bakeCloudTextureKernel;
                passData.output = cloudTexture;
                passData.cloudMapA = cloudMapA;
                passData.cloudMapB = cloudMapB;
                passData.bakeParameters = bakeParameters;
                passData.inverseResolution = 1.0f / _cloudTextureWidth;
                passData.layerCount = cloudLayer.NumLayers;
                passData.threadGroupsX = (_cloudTextureWidth + ThreadGroupSize - 1) / ThreadGroupSize;
                passData.threadGroupsY = (_cloudTextureHeight + ThreadGroupSize - 1) / ThreadGroupSize;

                builder.UseTexture(passData.cloudMapA, AccessFlags.Read);
                builder.UseTexture(passData.cloudMapB, AccessFlags.Read);
                builder.UseTexture(passData.output, AccessFlags.Write);
                builder.SetRenderFunc(static (CloudTexturePassData data, ComputeGraphContext context) =>
                {
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.BakeParams, data.bakeParameters);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.CloudMapOpacityA, data.opacityA);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.CloudMapOpacityB, data.opacityB);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.CloudMapBakeParamsA, data.bakeParametersA);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.CloudMapBakeParamsB, data.bakeParametersB);
                    context.cmd.SetComputeFloatParam(data.computeShader, ShaderIds.Resolution, data.inverseResolution);
                    context.cmd.SetComputeIntParam(data.computeShader, ShaderIds.LayerCount, data.layerCount);
                    context.cmd.SetComputeTextureParam(data.computeShader, data.kernel, ShaderIds.CloudMapA, data.cloudMapA);
                    context.cmd.SetComputeTextureParam(data.computeShader, data.kernel, ShaderIds.CloudMapB, data.cloudMapB);
                    context.cmd.SetComputeTextureParam(data.computeShader, data.kernel, ShaderIds.CloudTexture, data.output);
                    context.cmd.DispatchCompute(
                        data.computeShader,
                        data.kernel,
                        data.threadGroupsX,
                        data.threadGroupsY,
                        data.layerCount);
                });
            }

            _cloudTextureDirty = false;
            return cloudTexture;
        }

        internal void RecordCloudShadows(
            RenderGraph renderGraph,
            CloudLayer cloudLayer,
            Light sunLight,
            Camera camera,
            TextureHandle cloudTexture)
        {
            if (_cloudShadows == null)
                return;

            TextureHandle cloudShadows = renderGraph.ImportTexture(_cloudShadows);
            TextureHandle flowmapA = renderGraph.ImportTexture(_flowmapA);
            TextureHandle flowmapB = renderGraph.ImportTexture(_flowmapB);
            using (var builder = renderGraph.AddComputePass<CloudShadowPassData>(
                "Bake Cloud Layer Shadows",
                out var passData,
                BakeShadowsSampler))
            {
                Vector4 shadowParameters = cloudLayer.shadowTint.value;
                shadowParameters.w = cloudLayer.shadowMultiplier.value * 8.0f;

                passData.computeShader = _bakeCloudShadows;
                passData.kernel = _bakeCloudShadowsKernel;
                passData.cloudTexture = cloudTexture;
                passData.cloudShadows = cloudShadows;
                passData.flowmapA = flowmapA;
                passData.flowmapB = flowmapB;
                passData.flowParametersA = FlowParametersA;
                passData.flowParametersB = FlowParametersB;
                passData.layerModes = new Vector4(
                    (float)cloudLayer.layerA.distortionMode.value,
                    (float)cloudLayer.layerB.distortionMode.value,
                    0.0f,
                    0.0f);
                passData.layerEnabled = new Vector4(
                    cloudLayer.layerA.castShadows.value ? 1.0f : 0.0f,
                    cloudLayer.layers.value == CloudMapMode.Double && cloudLayer.layerB.castShadows.value ? 1.0f : 0.0f,
                    0.0f,
                    0.0f);
                passData.layerParametersA = sunLight.transform.right;
                passData.layerParametersA.w = cloudLayer.layerA.altitude.value;
                passData.layerParametersB = sunLight.transform.up;
                passData.layerParametersB.w = cloudLayer.layerB.altitude.value;
                passData.sunDirection = -sunLight.transform.forward;
                passData.planetRadius = cloudLayer.planetRadius.value;
                passData.shadowParameters = shadowParameters;
                passData.inverseResolution = 1.0f / _cloudShadowResolution;
                passData.threadGroups = (_cloudShadowResolution + ThreadGroupSize - 1) / ThreadGroupSize;
                passData.cookieMatrix = GetCookieMatrix(sunLight, camera, cloudLayer.shadowSize.value);

                builder.UseTexture(passData.cloudTexture, AccessFlags.Read);
                builder.UseTexture(passData.flowmapA, AccessFlags.Read);
                builder.UseTexture(passData.flowmapB, AccessFlags.Read);
                builder.UseTexture(passData.cloudShadows, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (CloudShadowPassData data, ComputeGraphContext context) =>
                {
                    context.cmd.SetComputeTextureParam(data.computeShader, data.kernel, ShaderIds.CloudTexture, data.cloudTexture);
                    context.cmd.SetComputeTextureParam(data.computeShader, data.kernel, ShaderIds.CloudShadows, data.cloudShadows);
                    context.cmd.SetComputeTextureParam(data.computeShader, data.kernel, ShaderIds.FlowmapA, data.flowmapA);
                    context.cmd.SetComputeTextureParam(data.computeShader, data.kernel, ShaderIds.FlowmapB, data.flowmapB);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.FlowmapParamA, data.flowParametersA);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.FlowmapParamB, data.flowParametersB);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.LayerModes, data.layerModes);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.LayerEnabled, data.layerEnabled);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.CloudLayerParamsA, data.layerParametersA);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.CloudLayerParamsB, data.layerParametersB);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.SunDirection, data.sunDirection);
                    context.cmd.SetComputeFloatParam(data.computeShader, ShaderIds.PlanetaryRadius, data.planetRadius);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.AmbientParams, Vector4.zero);
                    context.cmd.SetComputeVectorParam(data.computeShader, ShaderIds.ShadowParams, data.shadowParameters);
                    context.cmd.SetComputeFloatParam(data.computeShader, ShaderIds.Resolution, data.inverseResolution);
                    context.cmd.DispatchCompute(
                        data.computeShader,
                        data.kernel,
                        data.threadGroups,
                        data.threadGroups,
                        1);

                    context.cmd.SetGlobalTexture(ShaderIds.MainLightCookieTexture, data.cloudShadows);
                    context.cmd.SetGlobalMatrix(ShaderIds.MainLightWorldToLight, data.cookieMatrix);
                    context.cmd.SetGlobalFloat(ShaderIds.MainLightCookieTextureFormat, 0.0f);
                    context.cmd.EnableShaderKeyword("_LIGHT_COOKIES");
                });
            }
        }

        internal void Dispose()
        {
            _cloudTexture?.Release();
            _cloudTexture = null;
            _cloudShadows?.Release();
            _cloudShadows = null;
            _cloudMapA?.Release();
            _cloudMapA = null;
            _cloudMapB?.Release();
            _cloudMapB = null;
            _flowmapA?.Release();
            _flowmapA = null;
            _flowmapB?.Release();
            _flowmapB = null;
        }

        private static void EnsureExternalTexture(ref RTHandle handle, ref Texture currentTexture, Texture texture)
        {
            if (handle != null && currentTexture == texture)
                return;

            handle?.Release();
            currentTexture = texture;
            handle = RTHandles.Alloc(texture);
        }

        private void EnsureCloudTexture(CloudLayer cloudLayer)
        {
            int width = (int)cloudLayer.resolution.value;
            int height = cloudLayer.upperHemisphereOnly.value ? width / 2 : width;
            int slices = cloudLayer.NumLayers;
            if (_cloudTexture != null &&
                _cloudTextureWidth == width &&
                _cloudTextureHeight == height &&
                _cloudTextureSlices == slices)
            {
                return;
            }

            _cloudTexture?.Release();
            _cloudTexture = RTHandles.Alloc(
                width,
                height,
                slices: slices,
                colorFormat: GraphicsFormat.R16G16_SFloat,
                dimension: TextureDimension.Tex2DArray,
                enableRandomWrite: true,
                useMipMap: false,
                filterMode: FilterMode.Bilinear,
                wrapModeU: TextureWrapMode.Repeat,
                wrapModeV: TextureWrapMode.Clamp,
                name: "Cloud Layer Texture");
            _cloudTextureWidth = width;
            _cloudTextureHeight = height;
            _cloudTextureSlices = slices;
            _cloudTextureDirty = true;
        }

        private void EnsureShadowTexture(CloudLayer cloudLayer, bool shadowsEnabled)
        {
            if (!shadowsEnabled)
            {
                _cloudShadows?.Release();
                _cloudShadows = null;
                _cloudShadowResolution = 0;
                return;
            }

            int resolution = (int)cloudLayer.shadowResolution.value;
            if (_cloudShadows != null && _cloudShadowResolution == resolution)
                return;

            _cloudShadows?.Release();
            _cloudShadows = RTHandles.Alloc(
                resolution,
                resolution,
                colorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
                dimension: TextureDimension.Tex2D,
                enableRandomWrite: true,
                useMipMap: false,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Repeat,
                name: "Cloud Layer Shadows");
            _cloudShadowResolution = resolution;
        }

        private void UpdateAnimation(CloudLayer cloudLayer)
        {
            float currentTime = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
            float deltaTime = _lastTime < 0.0f ? 0.0f : Mathf.Max(currentTime - _lastTime, 0.0f);
            _lastTime = currentTime;

#if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isPaused)
                deltaTime = 0.0f;
#endif

            if (cloudLayer.layerA.distortionMode.value != CloudDistortionMode.None)
                _scrollFactorA += cloudLayer.layerA.scrollSpeed.value * deltaTime * 0.277778f;

            if (cloudLayer.layers.value == CloudMapMode.Double &&
                cloudLayer.layerB.distortionMode.value != CloudDistortionMode.None)
            {
                _scrollFactorB += cloudLayer.layerB.scrollSpeed.value * deltaTime * 0.277778f;
            }
        }

        private static Matrix4x4 GetCookieMatrix(Light sunLight, Camera camera, float shadowSize)
        {
            float inverseSize = 1.0f / Mathf.Max(shadowSize, 0.001f);
            Matrix4x4 lightToWorld = sunLight.transform.localToWorldMatrix;
            Vector3 cameraPosition = camera.transform.position;
            lightToWorld.SetColumn(3, new Vector4(cameraPosition.x, cameraPosition.y, cameraPosition.z, 1.0f));
            Matrix4x4 cookieScale = Matrix4x4.Scale(new Vector3(inverseSize, inverseSize, 1.0f));
            return DirectionalLightProjection * cookieScale * lightToWorld.inverse;
        }

        private sealed class CloudTexturePassData
        {
            internal ComputeShader computeShader;
            internal int kernel;
            internal TextureHandle output;
            internal TextureHandle cloudMapA;
            internal TextureHandle cloudMapB;
            internal Vector4 bakeParameters;
            internal Vector4 opacityA;
            internal Vector4 opacityB;
            internal Vector4 bakeParametersA;
            internal Vector4 bakeParametersB;
            internal float inverseResolution;
            internal int layerCount;
            internal int threadGroupsX;
            internal int threadGroupsY;
        }

        private sealed class CloudShadowPassData
        {
            internal ComputeShader computeShader;
            internal int kernel;
            internal TextureHandle cloudTexture;
            internal TextureHandle cloudShadows;
            internal TextureHandle flowmapA;
            internal TextureHandle flowmapB;
            internal Vector4 flowParametersA;
            internal Vector4 flowParametersB;
            internal Vector4 layerModes;
            internal Vector4 layerEnabled;
            internal Vector4 layerParametersA;
            internal Vector4 layerParametersB;
            internal Vector4 sunDirection;
            internal Vector4 shadowParameters;
            internal float planetRadius;
            internal float inverseResolution;
            internal int threadGroups;
            internal Matrix4x4 cookieMatrix;
        }
    }

    private sealed class CloudLayerShadowPass : ScriptableRenderPass
    {
        private readonly CloudLayerResources _resources;
        private CloudLayer _cloudLayer;

        internal CloudLayerShadowPass(CloudLayerResources resources)
        {
            _resources = resources;
        }

        internal void Setup(CloudLayer cloudLayer)
        {
            _cloudLayer = cloudLayer;
        }

        /// <summary>
        /// Записывает предварительный расчёт слоя и cookie теней в RenderGraph.
        /// </summary>
        /// <param name="renderGraph">Текущий RenderGraph.</param>
        /// <param name="frameData">Контекст текущего кадра.</param>
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            Light sunLight = GetMainLight(lightData, out _);
            if (_cloudLayer == null || sunLight == null)
                return;

            _resources.Prepare(_cloudLayer, sunLight, cameraData.camera, true);
            TextureHandle cloudTexture = _resources.RecordCloudTextureBake(renderGraph, _cloudLayer, sunLight);
            _resources.RecordCloudShadows(renderGraph, _cloudLayer, sunLight, cameraData.camera, cloudTexture);
        }
    }

    private sealed class CloudLayerRenderPass : ScriptableRenderPass
    {
        private static readonly ProfilingSampler RenderSampler = new("Render Cloud Layer");
        private static readonly MaterialPropertyBlock PropertyBlock = new();
        private static readonly Vector3[] FrustumCorners = new Vector3[4];

        private readonly Material _material;
        private readonly CloudLayerResources _resources;
        private CloudLayer _cloudLayer;
        private bool _shadowsEnabled;

        internal CloudLayerRenderPass(Material material, CloudLayerResources resources)
        {
            _material = material;
            _resources = resources;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        internal void Setup(CloudLayer cloudLayer, bool shadowsEnabled)
        {
            _cloudLayer = cloudLayer;
            _shadowsEnabled = shadowsEnabled;
        }

        /// <summary>
        /// Записывает полноэкранную композицию Cloud Layer в RenderGraph.
        /// </summary>
        /// <param name="renderGraph">Текущий RenderGraph.</param>
        /// <param name="frameData">Контекст текущего кадра.</param>
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_cloudLayer == null || _material == null)
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            Light sunLight = GetMainLight(lightData, out Color sunColor);

            _resources.Prepare(_cloudLayer, sunLight, cameraData.camera, _shadowsEnabled);
            TextureHandle cloudTexture = _resources.RecordCloudTextureBake(renderGraph, _cloudLayer, sunLight);

            using (var builder = renderGraph.AddRasterRenderPass<RenderPassData>(
                "Render Cloud Layer",
                out var passData,
                RenderSampler))
            {
                Color layerColorA = _cloudLayer.layerA.Color * sunColor;
                Color layerColorB = _cloudLayer.layerB.Color * sunColor;
                passData.material = _material;
                passData.cloudTextureHandle = cloudTexture;
                passData.cloudTexture = _resources.CloudTexture;
                passData.flowmapAHandle = renderGraph.ImportTexture(_resources.FlowmapA);
                passData.flowmapBHandle = renderGraph.ImportTexture(_resources.FlowmapB);
                passData.flowmapA = _resources.FlowmapSourceA;
                passData.flowmapB = _resources.FlowmapSourceB;
                passData.flowParametersA = _resources.FlowParametersA;
                passData.flowParametersB = _resources.FlowParametersB;
                passData.layerModes = new Vector4(
                    (float)_cloudLayer.layerA.distortionMode.value,
                    (float)_cloudLayer.layerB.distortionMode.value,
                    0.0f,
                    0.0f);
                passData.layerEnabled = new Vector4(
                    1.0f,
                    _cloudLayer.layers.value == CloudMapMode.Double ? 1.0f : 0.0f,
                    0.0f,
                    0.0f);
                passData.layerParametersA = layerColorA;
                passData.layerParametersA.w = _cloudLayer.layerA.altitude.value;
                passData.layerParametersB = layerColorB;
                passData.layerParametersB.w = _cloudLayer.layerB.altitude.value;
                passData.ambientParameters = new Vector4(
                    _cloudLayer.layerA.ambientProbeDimmer.value,
                    _cloudLayer.layerB.ambientProbeDimmer.value,
                    0.0f,
                    0.0f);
                passData.sunDirection = sunLight != null ? -sunLight.transform.forward : Vector3.zero;
                passData.planetRadius = _cloudLayer.planetRadius.value;
                Camera camera = cameraData.camera;
                camera.CalculateFrustumCorners(
                    new Rect(0.0f, 0.0f, 1.0f, 1.0f),
                    camera.farClipPlane,
                    Camera.MonoOrStereoscopicEye.Mono,
                    FrustumCorners);
                passData.viewRayBottomLeft = camera.transform.TransformVector(FrustumCorners[0]);
                passData.viewRayTopLeft = camera.transform.TransformVector(FrustumCorners[1]);
                passData.viewRayTopRight = camera.transform.TransformVector(FrustumCorners[2]);
                passData.viewRayBottomRight = camera.transform.TransformVector(FrustumCorners[3]);
                SetAmbientProbe(ref passData);

                builder.UseTexture(passData.cloudTextureHandle, AccessFlags.Read);
                builder.UseTexture(passData.flowmapAHandle, AccessFlags.Read);
                builder.UseTexture(passData.flowmapBHandle, AccessFlags.Read);
                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (RenderPassData data, RasterGraphContext context) =>
                {
                    PropertyBlock.Clear();
                    PropertyBlock.SetTexture(ShaderIds.CloudTexture, data.cloudTexture.rt);
                    PropertyBlock.SetTexture(ShaderIds.FlowmapA, data.flowmapA);
                    PropertyBlock.SetTexture(ShaderIds.FlowmapB, data.flowmapB);
                    PropertyBlock.SetVector(ShaderIds.FlowmapParamA, data.flowParametersA);
                    PropertyBlock.SetVector(ShaderIds.FlowmapParamB, data.flowParametersB);
                    PropertyBlock.SetVector(ShaderIds.LayerModes, data.layerModes);
                    PropertyBlock.SetVector(ShaderIds.LayerEnabled, data.layerEnabled);
                    PropertyBlock.SetVector(ShaderIds.CloudLayerParamsA, data.layerParametersA);
                    PropertyBlock.SetVector(ShaderIds.CloudLayerParamsB, data.layerParametersB);
                    PropertyBlock.SetVector(ShaderIds.AmbientParams, data.ambientParameters);
                    PropertyBlock.SetVector(ShaderIds.SunDirection, data.sunDirection);
                    PropertyBlock.SetFloat(ShaderIds.PlanetaryRadius, data.planetRadius);
                    PropertyBlock.SetVector(ShaderIds.ShAr, data.shAr);
                    PropertyBlock.SetVector(ShaderIds.ShAg, data.shAg);
                    PropertyBlock.SetVector(ShaderIds.ShAb, data.shAb);
                    PropertyBlock.SetVector(ShaderIds.ShBr, data.shBr);
                    PropertyBlock.SetVector(ShaderIds.ShBg, data.shBg);
                    PropertyBlock.SetVector(ShaderIds.ShBb, data.shBb);
                    PropertyBlock.SetVector(ShaderIds.ShC, data.shC);
                    PropertyBlock.SetVector(ShaderIds.ViewRayBottomLeft, data.viewRayBottomLeft);
                    PropertyBlock.SetVector(ShaderIds.ViewRayTopLeft, data.viewRayTopLeft);
                    PropertyBlock.SetVector(ShaderIds.ViewRayTopRight, data.viewRayTopRight);
                    PropertyBlock.SetVector(ShaderIds.ViewRayBottomRight, data.viewRayBottomRight);
                    context.cmd.DrawProcedural(
                        Matrix4x4.identity,
                        data.material,
                        0,
                        MeshTopology.Triangles,
                        3,
                        1,
                        PropertyBlock);
                });
            }
        }

        private static void SetAmbientProbe(ref RenderPassData passData)
        {
            SphericalHarmonicsL2 ambientProbe = RenderSettings.ambientProbe;
            passData.shAr = new Vector4(ambientProbe[0, 3], ambientProbe[0, 1], ambientProbe[0, 2], ambientProbe[0, 0] - ambientProbe[0, 6]);
            passData.shAg = new Vector4(ambientProbe[1, 3], ambientProbe[1, 1], ambientProbe[1, 2], ambientProbe[1, 0] - ambientProbe[1, 6]);
            passData.shAb = new Vector4(ambientProbe[2, 3], ambientProbe[2, 1], ambientProbe[2, 2], ambientProbe[2, 0] - ambientProbe[2, 6]);
            passData.shBr = new Vector4(ambientProbe[0, 4], ambientProbe[0, 5], ambientProbe[0, 6] * 3.0f, ambientProbe[0, 7]);
            passData.shBg = new Vector4(ambientProbe[1, 4], ambientProbe[1, 5], ambientProbe[1, 6] * 3.0f, ambientProbe[1, 7]);
            passData.shBb = new Vector4(ambientProbe[2, 4], ambientProbe[2, 5], ambientProbe[2, 6] * 3.0f, ambientProbe[2, 7]);
            passData.shC = new Vector4(ambientProbe[0, 8], ambientProbe[1, 8], ambientProbe[2, 8], 1.0f);
        }

        private sealed class RenderPassData
        {
            internal Material material;
            internal TextureHandle cloudTextureHandle;
            internal TextureHandle flowmapAHandle;
            internal TextureHandle flowmapBHandle;
            internal RTHandle cloudTexture;
            internal Texture flowmapA;
            internal Texture flowmapB;
            internal Vector4 flowParametersA;
            internal Vector4 flowParametersB;
            internal Vector4 layerModes;
            internal Vector4 layerEnabled;
            internal Vector4 layerParametersA;
            internal Vector4 layerParametersB;
            internal Vector4 ambientParameters;
            internal Vector4 sunDirection;
            internal float planetRadius;
            internal Vector4 shAr;
            internal Vector4 shAg;
            internal Vector4 shAb;
            internal Vector4 shBr;
            internal Vector4 shBg;
            internal Vector4 shBb;
            internal Vector4 shC;
            internal Vector3 viewRayBottomLeft;
            internal Vector3 viewRayTopLeft;
            internal Vector3 viewRayTopRight;
            internal Vector3 viewRayBottomRight;
        }
    }
}
#endif
