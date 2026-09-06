/// <summary>
/// Текст шейдера Nature/VisionFade. В Editor NatureRenderer пишет его в Assets/NatureVisionFade.shader.
/// </summary>
public static class NatureVisionFadeSrc
{
    public const string Code = @"Shader ""Nature/VisionFade""
{
    Properties
    {
        _Color (""Color"", Color) = (1,1,1,1)
        _MainTex (""Texture"", 2D) = ""white"" {}
    }
    SubShader
    {
        Tags
        {
            ""RenderPipeline"" = ""UniversalPipeline""
            ""Queue"" = ""Transparent""
            ""RenderType"" = ""Transparent""
            ""IgnoreProjector"" = ""True""
        }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name ""Fade""
            Tags { ""LightMode"" = ""SRPDefaultUnlit"" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_VisionCellMaskTex);
            SAMPLER(sampler_PointClamp);

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
            float4 _VisionCamFwd;
            float _VisionGroundY;
            float _VisionKeepRadius;
            float _VisionNearFade;
            float _VisionNearFalloff;
            float _VisionFadeWeight;
            float _VisionFadePale;
            float _VisionFadeFromFrac;

            float3 ViewGround(float3 wp)
            {
                float3 dir = _VisionCamFwd.xyz;
                float gy = _VisionGroundY;
                if (abs(dir.y) < 1e-4)
                    return float3(wp.x, gy, wp.z);
                float t = (gy - wp.y) / dir.y;
                return wp + dir * t;
            }

            bool OnVisibleCell(float3 wp)
            {
                if (_VisionMaskOn < 0.5 || _VisionTileSize < 0.001) return false;
                float cx = floor((wp.x - _VisionMapOrigin.x) / _VisionTileSize);
                float cz = floor((wp.z - _VisionMapOrigin.y) / _VisionTileSize);
                float dim = max(_VisionMaskDim, 1.0);
                float u = (cx - _VisionMaskOrigin.x + 0.5) / dim;
                float v = (cz - _VisionMaskOrigin.y + 0.5) / dim;
                if (u < 0.0 || u > 1.0 || v < 0.0 || v > 1.0) return false;
                return SAMPLE_TEXTURE2D(_VisionCellMaskTex, sampler_PointClamp, float2(u, v)).r > 0.5;
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
                float scaleY = length(float3(root._m01, root._m11, root._m21));
                float h = max(_VisionTreeLocalHeight * scaleY, 0.001);
                float keepH = max(h * saturate(_VisionKeepFraction), max(_VisionKeepBottom, 0.0));
                float cutH = max(keepH, h * saturate(_VisionFadeFromFrac));
                if (i.worldPos.y < basePos.y + cutH)
                    return col;

                float w = saturate(_VisionFadeWeight);
                float targetA = lerp(1.0, saturate(_VisionFadeAlpha), saturate(_VisionFadeSoft));
                float2 dp = i.worldPos.xz - _VisionPlayerXZ.xy;
                float dist = length(dp);
                float inner = max(_VisionNearFade, 0.0);
                float outer = inner + max(_VisionNearFalloff, 0.01);
                float nearT = saturate((dist - inner) / (outer - inner));
                targetA *= nearT;
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
";
}
