#ifndef URP_CLOUD_LAYER_COMMON_INCLUDED
#define URP_CLOUD_LAYER_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SphericalHarmonics.hlsl"

TEXTURE2D_ARRAY(_CloudTexture);
SAMPLER(sampler_CloudTexture);

TEXTURE2D(_FlowmapA);
SAMPLER(sampler_FlowmapA);

TEXTURE2D(_FlowmapB);
SAMPLER(sampler_FlowmapB);

float4 _FlowmapParamA;
float4 _FlowmapParamB;
float4 _LayerModes;
float4 _LayerEnabled;
float4 _CloudLayerParamsA;
float4 _CloudLayerParamsB;
float4 _AmbientParams;
float4 _CloudLayerSHAr;
float4 _CloudLayerSHAg;
float4 _CloudLayerSHAb;
float4 _CloudLayerSHBr;
float4 _CloudLayerSHBg;
float4 _CloudLayerSHBb;
float4 _CloudLayerSHC;
float3 _SunDirection;
float _PlanetaryRadius;

float2 CloudLayerIntersectSphere(float sphereRadius, float cosChi, float radialDistance)
{
    float radiusRatio = sphereRadius / radialDistance;
    float discriminant = radiusRatio * radiusRatio - saturate(1.0 - cosChi * cosChi);
    return discriminant < 0.0
        ? discriminant
        : radialDistance * float2(-cosChi - sqrt(discriminant), -cosChi + sqrt(discriminant));
}

float2 CloudLayerGetLatLongCoords(float3 direction, bool upperHemisphereOnly)
{
    const float2 inverseAtan = float2(0.1591, 0.3183);
    float2 uv = float2(
        atan2(direction.x, direction.z),
        asin(clamp(direction.y, -1.0, 1.0))) * inverseAtan + 0.5;
    uv.y = upperHemisphereOnly ? uv.y * 2.0 - 1.0 : uv.y;
    return uv;
}

float3 CloudLayerRotationUp(float3 direction, float2 cosineSine)
{
    float3 axisX = float3(cosineSine.x, 0.0, -cosineSine.y);
    float3 axisY = float3(cosineSine.y, 0.0, cosineSine.x);
    return float3(dot(axisX, direction), direction.y, dot(axisY, direction));
}

float3 CloudLayerEvaluateAmbient(float3 normalWS)
{
    float3 result = SHEvalLinearL0L1(normalWS, _CloudLayerSHAr, _CloudLayerSHAg, _CloudLayerSHAb);
    result += SHEvalLinearL2(normalWS, _CloudLayerSHBr, _CloudLayerSHBg, _CloudLayerSHBb, _CloudLayerSHC);

#ifdef UNITY_COLORSPACE_GAMMA
    result = LinearToSRGB(result);
#endif

    return result;
}

float2 CloudLayerSampleMap(float3 direction, int layerIndex)
{
    bool upperHemisphereOnly = _FlowmapParamA.w != 0.0;
    float2 uv = CloudLayerGetLatLongCoords(direction, upperHemisphereOnly);
    return SAMPLE_TEXTURE2D_ARRAY_LOD(_CloudTexture, sampler_CloudTexture, uv, layerIndex, 0).rg;
}

float2 CloudLayerSampleFlowmap(float3 direction, int layerIndex)
{
    bool upperHemisphereOnly = _FlowmapParamA.w != 0.0;
    float2 uv = CloudLayerGetLatLongCoords(direction, upperHemisphereOnly);

    if (layerIndex == 0)
        return SAMPLE_TEXTURE2D_LOD(_FlowmapA, sampler_FlowmapA, uv, 0).rg;

    return SAMPLE_TEXTURE2D_LOD(_FlowmapB, sampler_FlowmapB, uv, 0).rg;
}

float4 CloudLayerRenderSingle(float3 direction, int layerIndex)
{
    float4 flowParameters = layerIndex == 0 ? _FlowmapParamA : _FlowmapParamB;
    float4 layerParameters = layerIndex == 0 ? _CloudLayerParamsA : _CloudLayerParamsB;
    float ambientDimmer = layerIndex == 0 ? _AmbientParams.x : _AmbientParams.y;
    int distortionMode = layerIndex == 0 ? (int)_LayerModes.x : (int)_LayerModes.y;
    float altitude = layerParameters.w;

    float distanceToLayer = CloudLayerIntersectSphere(
        altitude + _PlanetaryRadius,
        direction.y,
        _PlanetaryRadius).y;
    float3 position = direction * max(distanceToLayer, 0.0);
    float2 cloud;

    if (distortionMode != 0)
    {
        float scrollDistance = max(2.0 * altitude, 0.001);
        float2 alpha = frac(flowParameters.z / scrollDistance + float2(0.0, 0.5)) - 0.5;
        float3 delta;

        if (distortionMode == 2)
        {
            float3 tangent = cross(direction, float3(0.0, 1.0, 0.0));
            float tangentLengthSquared = dot(tangent, tangent);
            tangent = tangentLengthSquared > 0.000001 ? tangent * rsqrt(tangentLengthSquared) : float3(1.0, 0.0, 0.0);
            float3 bitangent = cross(tangent, direction);
            float3 windDirection = CloudLayerRotationUp(direction, flowParameters.xy);
            float2 flow = CloudLayerSampleFlowmap(windDirection, layerIndex) * 2.0 - 1.0;
            delta = flow.x * tangent + flow.y * bitangent;
        }
        else
        {
            delta = float3(flowParameters.x, 0.0, flowParameters.y);
        }

        float2 cloudA = CloudLayerSampleMap(normalize(position + alpha.x * delta * scrollDistance), layerIndex);
        float2 cloudB = CloudLayerSampleMap(normalize(position + alpha.y * delta * scrollDistance), layerIndex);
        cloud = lerp(cloudA, cloudB, abs(2.0 * alpha.x));
    }
    else
    {
        cloud = CloudLayerSampleMap(direction, layerIndex);
    }

    float3 ambient = max(CloudLayerEvaluateAmbient(float3(0.0, -1.0, 0.0)), 0.0) * ambientDimmer;
    return float4(cloud.x * layerParameters.xyz + ambient * cloud.y, cloud.y) * _FlowmapParamB.w;
}

float4 CloudLayerRender(float3 direction)
{
    float4 clouds = 0.0;
    bool upperHemisphereOnly = _FlowmapParamA.w != 0.0;

    if (direction.y >= 0.0 || !upperHemisphereOnly)
    {
        if (_LayerEnabled.x != 0.0)
            clouds = CloudLayerRenderSingle(direction, 0);

        if (_LayerEnabled.y != 0.0)
        {
            float4 secondLayer = CloudLayerRenderSingle(direction, 1);
            clouds += secondLayer * (1.0 - clouds.a);
        }
    }

    return clouds;
}

#endif
