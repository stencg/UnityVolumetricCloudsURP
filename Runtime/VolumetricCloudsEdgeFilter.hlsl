float _DepthAwareCloudEdgeFilterWidth;

half4 FilterCloudsAtOpaqueDepthEdges(half4 cloudsColor, float2 screenUV, out bool opaqueEdge)
{
    opaqueEdge = false;
    float2 depthTexelSize = _CameraDepthTexture_TexelSize.xy * _DepthAwareCloudEdgeFilterWidth;
    float centerDepth = SampleSceneDepth(screenUV);
    bool centerIsSky = abs(centerDepth - UNITY_RAW_FAR_CLIP_VALUE) <= 1e-6;

    float2 offsets[4] =
    {
        float2(-depthTexelSize.x, 0.0),
        float2(depthTexelSize.x, 0.0),
        float2(0.0, -depthTexelSize.y),
        float2(0.0, depthTexelSize.y)
    };

    half4 filteredClouds = cloudsColor;
    for (int i = 0; i < 4; i++)
    {
        float2 neighborUV = screenUV + offsets[i];
        float neighborDepth = SampleSceneDepth(neighborUV);
        bool neighborIsSky = abs(neighborDepth - UNITY_RAW_FAR_CLIP_VALUE) <= 1e-6;
        if (centerIsSky != neighborIsSky)
            opaqueEdge = true;
        filteredClouds += SAMPLE_TEXTURE2D_X_LOD(_VolumetricCloudsLightingTexture, s_linear_clamp_sampler, neighborUV, 0).rgba;
    }

    return opaqueEdge ? filteredClouds * 0.2h : cloudsColor;
}
