using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

/// <summary>Connects clouds to PBSky only when both packages are installed.</summary>
public static class CloudSkyIntegration
{
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoadMethod]
#endif
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Register()
    {
        CloudSkyBridge.EnvironmentProvider = ReadEnvironment;
        CloudSkyBridge.ResourcesProvider = ReadResources;
        PhysicallyBasedSkyURP.CloudEnvironmentMaterialSource = VolumetricCloudsURP.GetEnvironmentMaterialSource;
        PhysicallyBasedSkyURP.PrepareCloudEnvironmentMaterial = VolumetricCloudsURP.PrepareEnvironmentMaterial;
    }

    private static CloudSkyEnvironment ReadEnvironment(VolumeStack stack, Vector3 position)
    {
        var environment = stack?.GetComponent<VisualEnvironment>();
        var sky = stack?.GetComponent<PhysicallyBasedSky>();
        var fog = stack?.GetComponent<Fog>();
        bool active = environment != null && environment.IsActive();
        bool scattering = sky != null && sky.atmosphericScattering.value;
        return new CloudSkyEnvironment
        {
            active = active,
            hasSky = active && environment.skyType.value != 0,
            worldSpace = active && environment.renderingSpace.value == VisualEnvironment.RenderingSpace.World,
            hasPhysicallyBasedScattering = active && environment.skyType.value == (int)VisualEnvironment.SkyType.PhysicallyBased && scattering,
            physicallyBasedSkyActive = sky != null && sky.IsActive(),
            atmosphericScattering = scattering,
            hasFog = fog != null && fog.IsActive(),
            planetCenterRadius = environment != null ? environment.GetPlanetCenterRadius(position) : default
        };
    }

    private static CloudSkyResources ReadResources(ContextContainer frame)
    {
        var resources = frame.GetOrCreate<PBSkyFrameResources>();
        return new CloudSkyResources
        {
            airSingleScattering = resources.airSingleScattering,
            aerosolSingleScattering = resources.aerosolSingleScattering,
            multipleScattering = resources.multipleScattering,
            fogSky = resources.fogSky
        };
    }
}
