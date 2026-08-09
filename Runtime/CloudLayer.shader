Shader "Hidden/Sky/CloudLayerURP"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Cloud Layer"
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local_fragment _ _PHYSICALLY_BASED_SUN

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.jiaozi158.unity-volumetric-clouds-urp/Runtime/CloudLayerCommon.hlsl"

            float3 _CloudLayerViewRayBottomLeft;
            float3 _CloudLayerViewRayTopLeft;
            float3 _CloudLayerViewRayTopRight;
            float3 _CloudLayerViewRayBottomRight;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID, UNITY_RAW_FAR_CLIP_VALUE);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
                float rawDepth = SampleSceneDepth(uv);
                clip(Linear01Depth(rawDepth, _ZBufferParams) - 0.9999);

#if defined(UNITY_SINGLE_PASS_STEREO)
                float3 positionWS = ComputeWorldSpacePosition(uv, UNITY_RAW_FAR_CLIP_VALUE, UNITY_MATRIX_I_VP);
                float3 directionWS = normalize(positionWS - _WorldSpaceCameraPos);
#else
                float3 bottomRay = lerp(_CloudLayerViewRayBottomLeft, _CloudLayerViewRayBottomRight, uv.x);
                float3 topRay = lerp(_CloudLayerViewRayTopLeft, _CloudLayerViewRayTopRight, uv.x);
                float3 directionWS = normalize(lerp(bottomRay, topRay, uv.y));
#endif
                return CloudLayerRender(directionWS);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
