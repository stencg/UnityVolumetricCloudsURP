#ifndef URP_CLOUD_LAYER_COMMON_INCLUDED
#define URP_CLOUD_LAYER_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SphericalHarmonics.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/VolumeRendering.hlsl"

#define CLOUD_LAYER_OPACITY_EPSILON 1e-4

TEXTURE2D_ARRAY(_CloudTexture);
SAMPLER(sampler_CloudTexture);

TEXTURE2D(_FlowmapA);
SAMPLER(sampler_FlowmapA);

TEXTURE2D(_FlowmapB);
SAMPLER(sampler_FlowmapB);

TEXTURECUBE(_VolumetricCloudsAmbientProbe);
SAMPLER(sampler_VolumetricCloudsAmbientProbe);

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
float _CloudLayerPlanetaryRadius;

#ifdef _PHYSICALLY_BASED_SUN
float CloudLayerComputeCosineOfHorizonAngle(float radialDistance)
{
    float sinHorizon = _CloudLayerPlanetaryRadius / radialDistance;
    return -sqrt(saturate(1.0 - sinHorizon * sinHorizon));
}

float CloudLayerChapmanUpperApprox(float z, float cosTheta)
{
    float cosineSquared = cosTheta * cosTheta;
    float numerator = 0.761643 * ((1.0 + 2.0 * z) - cosineSquared * z);
    float denominator = cosTheta * z + sqrt(z * (1.47721 + 0.273828 * cosineSquared * z));
    return 0.5 * cosTheta + numerator / denominator;
}

float CloudLayerChapmanHorizontal(float z)
{
    float inverseSqrtZ = rsqrt(z);
    return 0.626657 * (inverseSqrtZ + 2.0 * z * inverseSqrtZ);
}

float3 CloudLayerComputeAtmosphericOpticalDepth(float radialDistance, float cosTheta)
{
    const float2 scaleHeight = float2(8000.0, 1200.0);
    const float2 densityFalloff = rcp(scaleHeight);
    float2 z = densityFalloff * radialDistance;
    float2 planetZ = densityFalloff * _CloudLayerPlanetaryRadius;
    float sinTheta = sqrt(saturate(1.0 - cosTheta * cosTheta));
    float2 chapman;
    chapman.x = CloudLayerChapmanUpperApprox(z.x, abs(cosTheta)) * exp(planetZ.x - z.x);
    chapman.y = CloudLayerChapmanUpperApprox(z.y, abs(cosTheta)) * exp(planetZ.y - z.y);

    if (cosTheta < 0.0)
    {
        float2 horizonZ = z * sinTheta;
        float2 horizontalChapman;
        horizontalChapman.x = 2.0 * CloudLayerChapmanHorizontal(horizonZ.x);
        horizontalChapman.y = 2.0 * CloudLayerChapmanHorizontal(horizonZ.y);
        chapman = horizontalChapman * exp(planetZ - horizonZ) - chapman;
    }

    float2 opticalDepth = chapman * scaleHeight;
    const float3 airSeaLevelExtinction = float3(5.8, 13.5, 33.1) / 1000000.0;
    const float aerosolSeaLevelExtinction = 0.00001;
    return opticalDepth.x * airSeaLevelExtinction + opticalDepth.y * aerosolSeaLevelExtinction;
}

float3 CloudLayerEvaluateSunColorAttenuation(float3 positionPS, float3 sunDirection)
{
    float radialDistance = length(positionPS);
    float cosTheta = dot(positionPS, sunDirection) / radialDistance;
    radialDistance = max(radialDistance, _CloudLayerPlanetaryRadius);
    float cosHorizon = CloudLayerComputeCosineOfHorizonAngle(radialDistance);

    if (cosTheta < cosHorizon)
        return 0.0;

    float3 attenuation = TransmittanceFromOpticalDepth(
        CloudLayerComputeAtmosphericOpticalDepth(radialDistance, cosTheta));
    float penumbra = saturate((cosTheta - cosHorizon) / 0.0019);
    return attenuation * penumbra;
}
#endif

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
#ifdef _CLOUDS_AMBIENT_PROBE
    float3 result = SAMPLE_TEXTURECUBE_LOD(
        _VolumetricCloudsAmbientProbe,
        sampler_VolumetricCloudsAmbientProbe,
        normalWS,
        4.0).rgb;
#else
    float3 result = SHEvalLinearL0L1(normalWS, _CloudLayerSHAr, _CloudLayerSHAg, _CloudLayerSHAb);
    result += SHEvalLinearL2(normalWS, _CloudLayerSHBr, _CloudLayerSHBg, _CloudLayerSHBb, _CloudLayerSHC);
#endif

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

float4 CloudLayerRenderSingle(float3 direction, int layerIndex, out float distanceToLayer)
{
    float4 flowParameters = layerIndex == 0 ? _FlowmapParamA : _FlowmapParamB;
    float4 layerParameters = layerIndex == 0 ? _CloudLayerParamsA : _CloudLayerParamsB;
    float ambientDimmer = layerIndex == 0 ? _AmbientParams.x : _AmbientParams.y;
    int distortionMode = layerIndex == 0 ? (int)_LayerModes.x : (int)_LayerModes.y;
    float altitude = layerParameters.w;

    distanceToLayer = CloudLayerIntersectSphere(
        altitude + _CloudLayerPlanetaryRadius,
        direction.y,
        _CloudLayerPlanetaryRadius).y;
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

    float3 directLighting = cloud.x * layerParameters.xyz;
#ifdef _PHYSICALLY_BASED_SUN
    float3 positionPS = position + float3(0.0, _CloudLayerPlanetaryRadius, 0.0);
    directLighting *= CloudLayerEvaluateSunColorAttenuation(positionPS, _SunDirection);
#endif
    float3 ambient = max(CloudLayerEvaluateAmbient(float3(0.0, -1.0, 0.0)), 0.0) * ambientDimmer;
    return float4(directLighting + ambient * cloud.y, cloud.y) * _FlowmapParamB.w;
}

float4 CloudLayerRender(float3 direction, out float effectiveDistance)
{
    float4 clouds = 0.0;
    float weightedDistance = 0.0;
    bool upperHemisphereOnly = _FlowmapParamA.w != 0.0;

    if (direction.y >= 0.0 || !upperHemisphereOnly)
    {
        if (_LayerEnabled.x != 0.0)
        {
            float firstDistance;
            clouds = CloudLayerRenderSingle(direction, 0, firstDistance);
            weightedDistance = clouds.a * firstDistance;
        }

        if (_LayerEnabled.y != 0.0)
        {
            float secondDistance;
            float4 secondLayer = CloudLayerRenderSingle(direction, 1, secondDistance);
            float secondWeight = (1.0 - clouds.a) * secondLayer.a;
            clouds += secondLayer * (1.0 - clouds.a);
            weightedDistance += secondWeight * secondDistance;
        }
    }

    effectiveDistance = clouds.a > CLOUD_LAYER_OPACITY_EPSILON
        ? weightedDistance * rcp(clouds.a)
        : 0.0;
    return clouds;
}

float4 CloudLayerRender(float3 direction)
{
    float effectiveDistance;
    return CloudLayerRender(direction, effectiveDistance);
}

#endif
