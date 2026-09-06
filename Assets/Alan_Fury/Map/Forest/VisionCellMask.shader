Shader "Hidden/VisionCellMask"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }
        ZWrite On
        ZTest LEqual
        Cull Off
        ColorMask RGBA

        Pass
        {
            Name "Mask"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float eye : TEXCOORD0;
                float groundY : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(ws);
                float3 vs = TransformWorldToView(ws);
                o.eye = -vs.z;
                o.groundY = v.uv.x;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                return float4(1, i.eye, i.groundY, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}