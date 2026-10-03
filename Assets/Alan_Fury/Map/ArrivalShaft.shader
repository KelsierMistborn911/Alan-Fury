Shader "VFX/ArrivalShaft"
{
    Properties
    {
        _BaseColor ("Color (HDR)", Color) = (1.6, 1.55, 1.4, 1)
        _CoreColor ("Core Color (HDR)", Color) = (2.6, 2.5, 2.3, 1)
        _Softness ("Core Softness", Range(0.2, 6)) = 1.4
        _RimPower ("Rim Power", Range(0.5, 4)) = 1.4
        _TopFade ("Top Fade", Range(0.2, 2)) = 0.8
        _BottomFade ("Bottom Fade", Range(0.01, 1)) = 0.05
        _NoiseStrength ("Noise", Range(0, 1)) = 0.22
        _NoiseScale ("Noise Scale", Range(0.5, 10)) = 3.5
        _NoiseSpeed ("Noise Speed", Range(0, 5)) = 1.0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha One
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _BaseColor;
            float4 _CoreColor;
            float _Softness;
            float _RimPower;
            float _TopFade;
            float _BottomFade;
            float _NoiseStrength;
            float _NoiseScale;
            float _NoiseSpeed;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.positionOS = v.positionOS.xyz;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            float hash(float3 p)
            {
                p = frac(p * 0.3183099 + float3(0.1, 0.2, 0.3));
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float noise3(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = hash(i + float3(0,0,0));
                float n100 = hash(i + float3(1,0,0));
                float n010 = hash(i + float3(0,1,0));
                float n110 = hash(i + float3(1,1,0));
                float n001 = hash(i + float3(0,0,1));
                float n101 = hash(i + float3(1,0,1));
                float n011 = hash(i + float3(0,1,1));
                float n111 = hash(i + float3(1,1,1));
                float nx00 = lerp(n000, n100, f.x);
                float nx10 = lerp(n010, n110, f.x);
                float nx01 = lerp(n001, n101, f.x);
                float nx11 = lerp(n011, n111, f.x);
                float nxy0 = lerp(nx00, nx10, f.y);
                float nxy1 = lerp(nx01, nx11, f.y);
                return lerp(nxy0, nxy1, f.z);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 viewDir = normalize(GetWorldSpaceViewDir(i.positionWS));
                float3 n = normalize(i.normalWS);

                // Unity-цилиндр: y от -1 (низ) до 1 (верх)
                float h = saturate((i.positionOS.y + 1.0) * 0.5);

                // Rim по нормали (€рче у силуэта)
                float rim = pow(1.0 - saturate(dot(n, viewDir)), _RimPower);

                // ядро по оси
                float radial = length(i.positionOS.xz) / 0.5;
                float core = pow(saturate(1.0 - radial), _Softness);

                // «атухание к небу Ч столб раствор€етс€ вверху
                float topFade = pow(1.0 - h, _TopFade);

                // ћ€гкий вход у земли
                float bottomFade = smoothstep(0.0, _BottomFade + 0.001, h);

                // Ўум
                float3 np = i.positionWS * _NoiseScale;
                np.y -= _Time.y * _NoiseSpeed * 2.0;
                float nz = noise3(np);
                float noiseMask = lerp(1.0, nz, _NoiseStrength);

                float a = (rim * 0.7 + core) * topFade * bottomFade * noiseMask * _BaseColor.a;

                float3 col = lerp(_BaseColor.rgb, _CoreColor.rgb, core * bottomFade);
                return half4(col * a, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}