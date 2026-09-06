Shader "Combat/TargetMarker"
{
    Properties
    {
        [HDR] _Color ("Color", Color) = (2.2, 0.12, 0.08, 1)
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Overlay"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend One One

        Pass
        {
            Name "Marker"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

            struct Attr
            {
                float4 positionOS : POSITION;
            };

            struct V2F
            {
                float4 positionCS : SV_POSITION;
            };

            V2F vert(Attr v)
            {
                V2F o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half4 frag(V2F i) : SV_Target
            {
                return half4(_Color.rgb, 1);
            }
            ENDHLSL
        }
    }
}