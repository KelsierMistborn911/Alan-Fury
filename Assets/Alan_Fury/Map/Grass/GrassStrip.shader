Shader "Nature/GrassStrip"
{
    Properties
    {
        _Color ("Color", Color) = (0.16, 0.34, 0.12, 1)
        _TipColor ("Tip Color", Color) = (0.22, 0.42, 0.16, 1)
        _Brightness ("Brightness", Range(0.2, 2.5)) = 0.62
        _WindStrength ("Wind Strength", Range(0, 1)) = 0.12
        _WindSpeed ("Wind Speed", Range(0, 4)) = 0.4
        _Width ("Width", Range(0.02, 0.4)) = 0.06
        _PlayerPos ("Player Pos", Vector) = (0, 0, 0, 0)
        _PushRadius ("Push Radius", Range(0.2, 6)) = 1.2
        _PushStrength ("Push Strength", Range(0, 2)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            Name "GrassStrip"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _TipColor;
                float _Brightness;
                float _WindStrength;
                float _WindSpeed;
                float _Width;
                float4 _PlayerPos;
                float _PushRadius;
                float _PushStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float2 tuftCenterWS : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float fogCoord : TEXCOORD1;
                float shade : TEXCOORD2;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 pos = TransformObjectToWorld(v.positionOS.xyz);
                float tip = v.uv.y;

                float3 toCam = GetCameraPositionWS() - pos;
                toCam.y = 0;
                float lenSq = dot(toCam, toCam);
                float3 right = lenSq > 0.000001 ? cross(float3(0, 1, 0), toCam * rsqrt(lenSq)) : float3(1, 0, 0);
                pos += right * ((v.uv.x - 0.5) * _Width);

                float phase = dot(v.tuftCenterWS, float2(0.17, 0.13));
                float wind = sin(_Time.y * _WindSpeed + phase) * _WindStrength * tip;
                pos += right * wind;

                float2 d = v.tuftCenterWS - _PlayerPos.xz;
                float pdSq = dot(d, d);
                float r = max(_PushRadius, 0.05);
                if (pdSq < r * r && pdSq > 0.000001)
                {
                    float pd = sqrt(pdSq);
                    float k = (1.0 - pd / r);
                    k = k * k * tip * _PushStrength;
                    pos.xz += (d / pd) * k;
                }

                o.positionCS = TransformWorldToHClip(pos);
                o.uv = v.uv;
                o.fogCoord = ComputeFogFactor(o.positionCS.z);
                o.shade = v.color.r;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float band = step(0.55, i.uv.y);
                float3 col = lerp(_Color.rgb, _TipColor.rgb, band) * _Brightness * i.shade;
                col = floor(col * 8.0 + 0.5) / 8.0;
                col = MixFog(col, i.fogCoord);
                return half4(col, 1);
            }
            ENDHLSL
        }

        UsePass "ProPixelizer/SRP/PixelizedWithOutline/PROPIXELIZERPASS"
    }
    FallBack Off
}