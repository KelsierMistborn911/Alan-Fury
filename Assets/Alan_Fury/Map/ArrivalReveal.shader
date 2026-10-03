Shader "VFX/ArrivalReveal"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _EdgeColor ("Edge", Color) = (1.6, 1.5, 1.2, 1)
        _CutY ("Cut Y", Float) = 0
        _Edge ("Edge Width", Range(0.02, 0.6)) = 0.18
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Cull Off
        ZWrite On

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            Texture2D _BaseMap;
            SamplerState sampler_BaseMap;
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float4 _EdgeColor;
            float _CutY;
            float _Edge;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.uv = v.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                if (i.positionWS.y > _CutY)
                    discard;
                float band = saturate((_CutY - i.positionWS.y) / max(0.02, _Edge));
                half4 tex = _BaseMap.Sample(sampler_BaseMap, i.uv) * _BaseColor;
                float edge = 1.0 - band;
                half3 col = lerp(tex.rgb, _EdgeColor.rgb, edge * edge);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
