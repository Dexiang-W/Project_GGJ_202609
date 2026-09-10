// ---------------------------------------------------------------------------
//  水珠粒子 Shader（URP）
//  用 UV 反推半球法线，在 billboard 方片上算出菲涅尔边缘光 + 镜面高光。
//  要点：整条链子必须能「变暗」（珠体压暗 / 背光侧暗边 / 内圈折光暗带），
//        只有加法项的话水珠只会发光，看起来就是气团而不是水。
//  屏幕空间折射需先在 URP Asset 勾上 Opaque Texture。
// ---------------------------------------------------------------------------

Shader "Hongjia/WaterDropletParticle"
{
    Properties
    {
        [MainTexture] _MainTex ("Droplet Mask (alpha)", 2D) = "white" {}
        // RGB 保持白色，水珠颜色由粒子顶点色带进来；这里只用 A 通道当整体不透明度
        [MainColor]   _Color   ("Global Tint (RGB) / Opacity (A)", Color) = (1, 1, 1, 1)

        [Header(Body)]
        _BodyBrightness ("Body Brightness", Range(0, 2)) = 0.85
        // 中心不透明度上限。它乘在 alpha 链最后，拉低的话 _Color.a 给满也还是半透明
        _CoreOpacity ("Centre Opacity", Range(0, 1)) = 1

        [Header(Rim)]
        _RimColor ("Rim Colour", Color) = (0.80, 0.95, 1.0, 1)
        _RimBoost ("Lit Rim Brightness", Range(0, 4)) = 2.4
        _RimPower ("Rim Tightness", Range(0.5, 8)) = 2.4
        _RimShadow ("Shadow-side Rim Darkness", Range(0, 1)) = 0.62

        [Header(Refraction Band)]
        _BandStrength ("Inner Band Strength", Range(0, 1)) = 0.45
        _BandWidth ("Inner Band Width", Range(0.05, 1)) = 0.5
        _BandTint ("Inner Band Colour", Color) = (0.06, 0.13, 0.22, 1)

        [Header(Highlight)]
        _SpecIntensity ("Highlight Intensity", Range(0, 4)) = 1.2
        _SpecPower ("Highlight Sharpness", Range(2, 200)) = 80
        _LightDir ("Highlight Direction (view space)", Vector) = (0.35, 0.55, 0.76, 0)

        [Header(Screen Refraction)]
        // 需先在 URP Asset 勾上 Opaque Texture，否则 _CameraOpaqueTexture 是全黑的
        [Toggle(_REFRACTION_ON)] _RefractionOn ("Use Screen Refraction (needs Opaque Texture)", Float) = 0
        _RefractionStrength ("Refraction Bend", Range(0, 0.25)) = 0.08
        _RefractionAmount ("Refraction Amount", Range(0, 1)) = 0.7

        [Header(Compat)]
        _TextureColorInfluence ("Texture RGB Influence", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "WaterDroplet"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual        // 保留深度测试：球背面的水珠被球挡住，是「贴附表面」最重要的线索
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma shader_feature_local _REFRACTION_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #ifdef _REFRACTION_ON
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
                float2 screenUV   : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4  _Color;
                half4  _RimColor;
                half4  _BandTint;
                float4 _LightDir;
                half   _BodyBrightness;
                half   _CoreOpacity;
                half   _RimBoost;
                half   _RimPower;
                half   _RimShadow;
                half   _BandStrength;
                half   _BandWidth;
                half   _SpecIntensity;
                half   _SpecPower;
                half   _RefractionOn;
                half   _RefractionStrength;
                half   _RefractionAmount;
                half   _TextureColorInfluence;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);

                // 粒子方片正对镜头，4 个顶点 w 相同，所以直接除 w 得到屏幕 UV
                output.screenUV = posInputs.positionNDC.xy / max(posInputs.positionNDC.w, 1e-5);

                // 只用 alpha 做生灭淡入淡出，RGB 由 Shader 负责
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 mask = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);

                // ---------- 用 UV 反推半球法线 ----------
                float2 p = input.uv * 2.0 - 1.0;
                float  r2 = saturate(dot(p, p));
                float  z = sqrt(saturate(1.0 - r2));
                float3 n = float3(p, z);            // 圆心 z=1（正对镜头），边缘 z=0

                const float3 viewDir = float3(0.0, 0.0, 1.0);

                half edge = 1.0 - z;                // 0 = 圆心，1 = 轮廓

                // 圆心指向本像素的平面方向；圆心处无定义，但那里 rimRing = 0，不影响结果
                float2 pn = p / max(length(p), 1e-4);

                float2 lightXY = normalize(_LightDir.xy + 1e-5);
                half   side = (half)dot(pn, lightXY);   // >0 迎光侧，<0 背光侧

                // ---------- 边缘：迎光侧亮、背光侧暗 ----------
                half rimRing = pow(edge, _RimPower);

                // 两侧用互补遮罩，避免中段同时叠一层亮和一层暗
                half litMask  = smoothstep(-0.35, 0.65, side);
                half darkMask = 1.0 - litMask;

                half rimLit  = rimRing * litMask;
                half rimDark = rimRing * darkMask;

                // ---------- 镜面高光 ----------
                float3 h1 = normalize(normalize(_LightDir.xyz) + viewDir);
                half spec = pow(saturate(dot(n, h1)), _SpecPower) * _SpecIntensity;

                // 副高光：珠子内部二次反射的小亮点
                float3 h2 = normalize(normalize(float3(-0.45, -0.40, 0.80)) + viewDir);
                spec += pow(saturate(dot(n, h2)), _SpecPower * 1.6) * _SpecIntensity * 0.25;

                // ---------- 珠体 ----------
                half3 tint = _Color.rgb * input.color.rgb;
                half3 base = lerp(tint, tint * mask.rgb * 1.6, _TextureColorInfluence);
                half3 body = base * _BodyBrightness;

                // ---------- 内圈折光暗带 ----------
                half band = smoothstep(0.5 - _BandWidth * 0.5, 0.5 + _BandWidth * 0.5, edge)
                          * (1.0 - smoothstep(0.90, 1.0, edge));
                band *= lerp(0.55, 1.0, darkMask);      // 背光侧的折射更明显

                half3 bandCol = _BandTint.rgb * base * 1.2;
                body = lerp(body, bandCol, band * _BandStrength);

                // ---------- 可选：屏幕空间折射 ----------
                #ifdef _REFRACTION_ON
                    // 越靠边把背景推得越远，这就是珠子的放大倍率
                    half2 bend = n.xy * _RefractionStrength * (0.35 + 0.65 * edge);
                    half3 bg = (half3)SampleSceneColor(saturate(input.screenUV + bend));   // 内部已含 XR stereo transform
                    bg *= lerp(half3(1, 1, 1), base * 1.5, 0.5);                          // 带一点水色，否则像干净玻璃
                    // 中心保持实心，只在靠外区域露折射
                    half refrMask = smoothstep(0.15, 0.95, edge) * _RefractionAmount;
                    body = lerp(body, bg, refrMask);
                #endif

                // ---------- 合成 ----------
                half3 col = body;
                col += _RimColor.rgb * rimLit * _RimBoost;
                // 背光侧暗边用乘法压暗（减法在暗背景上会求出负色）
                col *= lerp(1.0, 1.0 - _RimShadow, rimDark);
                col += spec;

                // 轮廓 × 生灭淡入淡出 × 整体不透明度 × 中心上限
                half alpha = mask.a * input.color.a * _Color.a * lerp(_CoreOpacity, 1.0, rimRing);

                return half4(col, saturate(alpha));
            }
            ENDHLSL
        }
    }

    Fallback Off
}
