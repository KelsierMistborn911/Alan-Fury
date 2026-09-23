Shader "Nature/VisionFade"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "Fade"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_VisionCellMaskTex);
            SAMPLER(sampler_VisionCellMaskTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
            CBUFFER_END

            float _VisionFadeAlpha;
            float _VisionFadeSoft;
            float _VisionKeepBottom;
            float _VisionKeepFraction;
            float _VisionTreeLocalHeight;
            float4x4 _VisionPartInv;
            float4 _VisionMapOrigin;
            float _VisionTileSize;
            float4 _VisionMaskOrigin;
            float _VisionMaskDim;
            float _VisionMaskOn;
            float4 _VisionPlayerXZ;
            float4 _VisionCursorXZ;
            float4 _VisionCamFwd;
            float _VisionGroundY;
            float _VisionKeepRadius;
            float _VisionNearFade;
            float _VisionNearFalloff;
            float _VisionFadeWeight;
            float _VisionFadePale;
            float _VisionFadeFromFrac;
            float _VisionCorridorHalf;
            float _VisionPlayerRadius;
            float _VisionCursorRadius;
            float _VisionEdgeMeters;
            float _VisionKeepHeight;

            float3 ViewGround(float3 wp)
            {
                float3 dir = _VisionCamFwd.xyz;
                float gy = _VisionGroundY;
                if (abs(dir.y) < 1e-4)
                    return float3(wp.x, gy, wp.z);
                float t = (gy - wp.y) / dir.y;
                return wp + dir * t;
            }

            float VisionInside(float2 xz)
            {
                if (_VisionMaskOn < 0.5 || _VisionTileSize < 0.001)
                    return 0.0;
                float2 cell = (xz - _VisionMapOrigin.xy) / _VisionTileSize;
                float dim = max(_VisionMaskDim, 1.0);
                float2 uv = (cell - _VisionMaskOrigin.xy + 0.5) / dim;
                if (uv.x <= 0.0 || uv.x >= 1.0 || uv.y <= 0.0 || uv.y >= 1.0)
                    return 0.0;
                return SAMPLE_TEXTURE2D(_VisionCellMaskTex, sampler_VisionCellMaskTex, uv).r;
            }

            float DistToSeg(float2 p, float2 a, float2 b)
            {
                float2 ab = b - a;
                float den = dot(ab, ab);
                float t = den < 1e-6 ? 0.0 : saturate(dot(p - a, ab) / den);
                return length(p - (a + ab * t));
            }

            float LookWeight(float2 xz)
            {
                float2 p = _VisionPlayerXZ.xy;
                float2 c = _VisionCursorXZ.xy;
                float soft = max(_VisionEdgeMeters, 0.75);
                float cap = DistToSeg(xz, p, c);
                float wCap = 1.0 - saturate((cap - max(_VisionCorridorHalf, 0.2)) / soft);
                float wP = 1.0 - saturate((length(xz - p) - max(_VisionPlayerRadius, 0.2)) / soft);
                float wC = 1.0 - saturate((length(xz - c) - max(_VisionCursorRadius, 0.15)) / soft);
                return max(wCap, max(wP, wC));
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(ws);
                o.uv = v.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                o.worldPos = ws;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * (half4)_Color;
                if (col.a < 0.02)
                    discard;

                float4x4 root = mul(unity_ObjectToWorld, _VisionPartInv);
                float3 basePos = float3(root._m03, root._m13, root._m23);

                float3 ground = ViewGround(i.worldPos);
                float vis = VisionInside(ground.xz);
                float visEdge = smoothstep(0.06, 0.72, vis);

                float2 radial = i.worldPos.xz - basePos.xz;
                float keepR = max(_VisionKeepRadius, 0.0);
                float keepW = max(_VisionNearFalloff, 0.5);
                float stem = 1.0 - saturate((length(radial) - keepR) / keepW);
                float stump = 1.0 - saturate((i.worldPos.y - basePos.y - max(_VisionKeepHeight, 0.0)) / 0.7);

                float zone = visEdge * (1.0 - stem) * (1.0 - stump);
                float w = saturate(_VisionFadeWeight) * saturate(zone);
                float targetA = lerp(1.0, saturate(_VisionFadeAlpha), saturate(_VisionFadeSoft));
                float aMul = lerp(1.0, targetA, w);
                col.a *= aMul;
                float seeThrough = saturate((1.0 - aMul) / max(1.0 - targetA, 0.001));
                col.rgb = lerp(col.rgb, col.rgb * (1.0 - _VisionFadePale) + _VisionFadePale, seeThrough);
                return col;
            }
            ENDHLSL
        }
    }
    FallBack Off
}