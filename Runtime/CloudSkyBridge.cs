using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using Unity.Mathematics;

/// <summary>Optional sky state consumed by clouds without a sky assembly dependency.</summary>
public struct CloudSkyEnvironment
{
    public bool active;
    public bool hasSky;
    public bool worldSpace;
    public bool hasPhysicallyBasedScattering;
    public bool physicallyBasedSkyActive;
    public bool atmosphericScattering;
    public bool hasFog;
    public float4 planetCenterRadius;
}

/// <summary>Sky textures belonging to the current camera's Render Graph.</summary>
public struct CloudSkyResources
{
    public TextureHandle airSingleScattering;
    public TextureHandle aerosolSingleScattering;
    public TextureHandle multipleScattering;
    public TextureHandle fogSky;
}

/// <summary>Registered by the optional PBSky integration assembly.</summary>
public static class CloudSkyBridge
{
    public static Func<VolumeStack, Vector3, CloudSkyEnvironment> EnvironmentProvider;
    public static Func<ContextContainer, CloudSkyResources> ResourcesProvider;

    /// <summary>Reads sky settings for the specified camera position.</summary>
    public static CloudSkyEnvironment ReadEnvironment(VolumeStack stack, Vector3 position)
        => EnvironmentProvider != null ? EnvironmentProvider(stack, position) : default;

    /// <summary>Reads resources only from the current camera frame.</summary>
    public static CloudSkyResources ReadResources(ContextContainer frame)
        => ResourcesProvider != null ? ResourcesProvider(frame) : new CloudSkyResources
        {
            airSingleScattering = TextureHandle.nullHandle,
            aerosolSingleScattering = TextureHandle.nullHandle,
            multipleScattering = TextureHandle.nullHandle,
            fogSky = TextureHandle.nullHandle
        };
}
