Shader "Nature/GrassField"
{
    Properties
    {
        [Header(Shading)]
        _TopColor ("Top Color", Color) = (0.42, 0.82, 0.28, 1)
        _BottomColor ("Bottom Color", Color) = (0.18, 0.42, 0.12, 1)
        _Brightness ("Brightness", Range(0.4, 2.5)) = 1.15

        [Header(Blades)]
        _BladeWidth ("Blade Width", Range(0.02, 0.4)) = 0.09
        _BladeWidthRandom ("Blade Width Random", Range(0, 0.25)) = 0.035
        _BladeHeight ("Blade Height", Range(0.2, 2.4)) = 0.85
        _BladeHeightRandom ("Blade Height Random", Range(0, 1.5)) = 0.22
        _Bend ("Bend", Range(0, 1)) = 0.22

        [Header(Density)]
        _CenterDensity ("Center Density", Range(1, 32)) = 22
        _EdgeDensity ("Edge Density", Range(0, 16)) = 3
        _ScreenPad ("Screen Pad", Range(1, 1.3)) = 1.05

        [Header(Wind)]
        _WindStrength ("Wind Strength", Range(0, 2)) = 0.14
        _WindSpeed ("Wind Speed", Range(0, 4)) = 0.35

        [Header(Player Push)]
        _PlayerPos ("Player Pos", Vector) = (0, 0, 0, 0)
        _DrawRadius ("Draw Radius", Range(4, 400)) = 252
        _PushRadius ("Push Radius", Range(0.2, 6)) = 1.35
        _PushStrength ("Push Strength", Range(0, 3)) = 1.05
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }
        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            Name "Grass"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma geometry Geom
            #pragma fragment Frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define STAMP_COUNT 8

            CBUFFER_START(UnityPerMaterial)
                float4 _TopColor;
                float4 _BottomColor;
                float _Brightness;
                float _BladeWidth;
                float _BladeWidthRandom;
                float _BladeHeight;
                float _BladeHeightRandom;
                float _Bend;
                float _CenterDensity;
                float _EdgeDensity;
                float _ScreenPad;
                float _WindStrength;
                float _WindSpeed;
                float4 _PlayerPos;
                float _DrawRadius;
                float _PushRadius;
                float _PushStrength;
                float4 _PushStamps[STAMP_COUNT];
                float4 _PushDirs[STAMP_COUNT];
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
            };

            struct v2g
            {
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float open : TEXCOORD2;
            };

            struct g2f
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float fogCoord : TEXCOORD1;
            };

            float3 Hash33(float3 p)
            {
                p = frac(p * float3(0.1031, 0.11369, 0.13787));
                p += dot(p, p.yzx + 19.19);
                return frac((p.xxy + p.yzz) * p.zyx);
            }

            float Hash31(float3 p)
            {
                return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453123);
            }

            v2g Vert(Attributes v)
            {
                v2g o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.open = v.color.r;
                return o;
            }

            g2f MakeVert(float3 posWS, float2 uv)
            {
                g2f o;
                o.positionCS = TransformWorldToHClip(posWS);
                o.uv = uv;
                o.fogCoord = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            void SamplePush(float3 root, out float push, out float3 pushDir)
            {
                push = 0;
                pushDir = float3(0, 0, 1);
                float r = max(_PushRadius, 0.05);
                [unroll]
                for (int s = 0; s < STAMP_COUNT; s++)
                {
                    float str = _PushStamps[s].w;
                    if (str <= 0.001) continue;
                    float2 dlt = root.xz - _PushStamps[s].xz;
                    float pd = length(dlt);
                    if (pd > r) continue;
                    float2 dir = _PushDirs[s].xz;
                    float ahead = pd > 0.001 ? dot(dlt / pd, dir) : 1;
                    if (ahead < 0.12) continue;
                    float local = saturate(1.0 - pd / r);
                    local *= local * str;
                    if (local > push)
                    {
                        push = local;
                        pushDir = pd > 0.001 ? float3(dlt.x / pd, 0, dlt.y / pd) : float3(dir.x, 0, dir.y);
                    }
                }
            }

            void EmitBlade(inout TriangleStream<g2f> stream, float3 root, float3 up, float3 seed)
            {
                float3 rnd = Hash33(seed);
                if (rnd.y < 0.18) return;

                float yaw = Hash31(seed + float3(root.z * 17.13, root.x * 31.57, 9.13)) * 6.2831853;
                float3 right = normalize(float3(cos(yaw), 0.0, sin(yaw)));
                float3 face = normalize(cross(up, right));

                float w = _BladeWidth + (rnd.y - 0.5) * 2.0 * _BladeWidthRandom;
                float h = _BladeHeight + rnd.z * _BladeHeightRandom;

                float push;
                float3 pushDir;
                SamplePush(root, push, pushDir);
                h *= lerp(1.0, 0.22, push);

                float phase = Hash31(seed + 41.7) * 6.2831853;
                float wind = sin(_Time.y * _WindSpeed + phase);
                float3 tipOff = face * (_Bend * h * 0.22)
                    + right * (wind * _WindStrength * h)
                    + pushDir * (_PushStrength * _BladeHeight * push);

                stream.Append(MakeVert(root - right * w, float2(0, 0)));
                stream.Append(MakeVert(root + right * w, float2(1, 0)));
                stream.Append(MakeVert(root + up * h + tipOff, float2(0.5, 1)));
                stream.RestartStrip();
            }

            [maxvertexcount(96)]
            void Geom(triangle v2g i[3], inout TriangleStream<g2f> stream)
            {
                float3 a = i[0].positionWS;
                float3 b = i[1].positionWS;
                float3 c = i[2].positionWS;
                float3 mid = (a + b + c) * 0.333333;
                float4 clip = TransformWorldToHClip(mid);
                float2 ndc = clip.xy / max(abs(clip.w), 1e-4);
                float pad = max(_ScreenPad, 1.0);
                float frame = max(abs(ndc.x), abs(ndc.y));
                if (frame > pad) return;

                float open = saturate((i[0].open + i[1].open + i[2].open) * 0.333333);
                float t = open * open * open;
                float dens = lerp(_EdgeDensity, _CenterDensity, t);
                if (dens < 0.15) return;

                float3 ab = b - a;
                float3 ac = c - a;
                float area = length(cross(ab, ac)) * 0.5;
                float3 up = normalize(i[0].normalWS + i[1].normalWS + i[2].normalWS);
                if (up.y < 0.35) return;

                float rim = saturate((pad - frame) / 0.08);
                int n = (int)clamp(area * dens * rim, 0, 32);
                if (n <= 0) return;

                [loop]
                for (int k = 0; k < n; k++)
                {
                    float2 hv = Hash33(mid + float3(k * 19.17, k * 7.31, k * 3.9)).xy;
                    if (hv.x + hv.y > 1.0) hv = 1.0 - hv;
                    float3 p = a + ab * hv.x + ac * hv.y;
                    EmitBlade(stream, p, up, p + k);
                }
            }

            half4 Frag(g2f i) : SV_Target
            {
                float3 col = lerp(_BottomColor.rgb, _TopColor.rgb, i.uv.y) * _Brightness;
                col = MixFog(col, i.fogCoord);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}