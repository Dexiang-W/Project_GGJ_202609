// GGJ2026 NPR 能量球着色器（URP，参考视频：绿色“史莱姆/能量球”质感）
//
// 重点还原参考视频里的两件事 —— 颜色 和 质感：
//   【颜色】橄榄绿三段式斑块：暗部斑块 / 中间调 / 亮部斑块（取色自参考视频：
//           暗部 ≈ #4a6831，中间调 ≈ #5a7a35，亮斑 ≈ #85a260，高光 ≈ #b2c59e）。
//   【质感】
//     · 斑块（Marble）：程序化 3D 噪声（FBM）按【物体空间坐标】算，不用贴图、不用 UV，
//       斑块“长在模型表面”上，球上的碎粒（小水滴）也能自动获得同款花纹；
//     · 柔光包裹（Wrap Lighting）：明暗交界被“包”过去，模拟半透明的次表面散射，
//       得到参考里那种蜡质/果冻一样的软软的暗面；
//     · 透光（Backlight SSS）：光从球背面打过来时，边缘会透出亮绿——参考里逆光侧的那圈绿光；
//     · 蜡质高光：宽而柔（阈值量化过），不是塑料那种小亮点；
//     · 边缘光（Fresnel Rim）：轮廓处轻微提亮，能量球的“呼吸感”；
//     · 呼吸脉动：整体亮度随时间轻微起伏（能量球活着的感觉）。
//   所有噪声随时间缓慢漂移，表面花纹会有“内部在流动”的感觉。
//
// 用法：新建/选中材质 → Shader 选 GGJ2026/NPR_EnergyBall → 拖给 P_EnergyBall（或任何球体）。
// 调色入口都在材质面板上（均有中文标签），颜色按“想要的目标观感”直接调即可。
Shader "GGJ2026/NPR_EnergyBall"
{
    Properties
    {
        // ———————— 颜色 ————————
        _BaseColorMid    ("中间调 Mid Color", Color) = (0.1229, 0.2224, 0.0434, 1)
        _BaseColorDark   ("暗部斑块 Dark Blotch", Color) = (0.0722, 0.1468, 0.0256, 1)
        _BaseColorLight  ("亮部斑块 Light Blotch", Color) = (0.2897, 0.4175, 0.1165, 1)
        _ShadowTint      ("暗面倍增 Shadow Tint", Color) = (0.50, 0.55, 0.45, 1)
        _HighlightColor  ("高光 Specular Color", Color) = (0.4795, 0.5827, 0.3515, 1)
        _SSSColor        ("透光 SSS Color", Color) = (0.30, 0.45, 0.15, 1)
        _RimColor        ("边缘光 Rim Color", Color) = (0.50, 0.60, 0.30, 1)

        // ———————— 斑块质感（程序化噪声） ————————
        _BlotchScale         ("斑块大小 Blotch Scale", Range(0.5, 12)) = 3.2
        _BlotchLow           ("暗斑阈值 Blotch Low", Range(0, 1)) = 0.42
        _BlotchHigh          ("亮斑阈值 Blotch High", Range(0, 1)) = 0.60
        _BlotchSoftness      ("斑块边缘柔度 Blotch Softness", Range(0.01, 0.5)) = 0.12
        _LightBlotchStrength ("亮斑强度 Light Blotch Strength", Range(0, 1)) = 0.65
        _MottleStrength      ("细碎杂色强度 Mottle Strength", Range(0, 0.5)) = 0.18
        _NoiseDriftSpeed     ("花纹漂移速度 Noise Drift", Range(0, 1)) = 0.15

        // ———————— 光照 / 质感 ————————
        _Wrap            ("柔光包裹 Wrap (次表面感)", Range(0, 1)) = 0.35
        _StepMid         ("明暗分界 Shadow Step", Range(0, 1)) = 0.42
        _StepSmooth      ("明暗过渡宽 Step Smooth", Range(0.01, 0.5)) = 0.16
        _SSSPower        ("透光衰减 SSS Power", Range(1, 8)) = 2.5
        _SSSStrength     ("透光强度 SSS Strength", Range(0, 2)) = 0.6
        _SpecPower       ("高光锐度 Spec Power", Range(4, 128)) = 28
        _SpecThreshold   ("高光阈值 Spec Threshold", Range(0, 0.9)) = 0.25
        _SpecStrength    ("高光强度 Spec Strength", Range(0, 2)) = 0.35
        _RimPower        ("边缘光衰减 Rim Power", Range(0.5, 8)) = 3
        _RimStrength     ("边缘光强度 Rim Strength", Range(0, 2)) = 0.5
        _AmbientStrength ("环境光强度 Ambient", Range(0, 1)) = 0.35

        // ———————— VAT 顶点动画（读 VAT_Position.exr + 模型上的 VAT_ID[UV1]）————————
        // 说明：这张 VAT 存的是【每个顶点相对静止姿态的位移偏移】(RGB, 物体空间)，
        //      布局是“每帧占 4 行、行内反向存 4 个顶点段”，下面的参数是按实测填好的，别乱改。
        _VatEnable       ("VAT 开关 VAT Enable", Range(0, 1)) = 0
        _VatPosMap       ("VAT 位置贴图 Position Map", 2D) = "black" {}
        _VatOffsetScale  ("位移缩放 Offset Scale", Float) = 1
        _VatFrameStart   ("起始帧 Frame Start", Float) = 0
        _VatFrameEnd     ("结束帧 Frame End", Float) = 240
        _VatTotalFrames  ("贴图总帧数 Total Frames", Float) = 250
        _VatRowsPerFrame ("每帧行数 Rows Per Frame", Float) = 4
        _VatTexHeight    ("贴图高度 Tex Height", Float) = 1000
        _VatFps          ("播放帧率 FPS", Float) = 24
        _VatAutoPlay     ("自动播放 Auto Play", Range(0, 1)) = 1
        _VatManualFrame  ("手动帧 Manual Frame", Float) = 0
        _VatPingPong     ("乒乓循环 PingPong", Range(0, 1)) = 0
        _VatFlipV        ("上下翻转 Flip V (画面乱跳时试)", Range(0, 1)) = 0
        _VatNormalBlend  ("形变法线强度 Normal Blend", Range(0, 1)) = 1

        // ———————— 轻微顶点起伏（不用贴图，纯数学，开销极小）————————
        // 几个不同频率的正弦波叠在一起，把顶点沿“离心方向”推出去/拉回来一点点。
        // 位移只由【顶点位置】决定（与法线无关），接缝处同位置的顶点位移一定相同，绝不会裂面。
        _WobbleEnable   ("起伏开关 Wobble Enable", Range(0, 1)) = 1
        _WobbleAmp      ("起伏幅度 Wobble Amplitude", Range(0, 0.1)) = 0.02
        _WobbleFreq     ("起伏密度 Wobble Frequency", Range(0.2, 10)) = 2.2
        _WobbleSpeed    ("起伏速度 Wobble Speed", Range(0, 4)) = 0.8

        // ———————— 呼吸脉动 ————————
        _PulseSpeed      ("呼吸速度 Pulse Speed", Range(0, 8)) = 1.6
        _PulseStrength   ("呼吸幅度 Pulse Strength", Range(0, 0.5)) = 0.08
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "NPR_FORWARD"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5                  // 顶点着色器要采样贴图（顶点纹理读取）
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColorMid;
                half4 _BaseColorDark;
                half4 _BaseColorLight;
                half4 _ShadowTint;
                half4 _HighlightColor;
                half4 _SSSColor;
                half4 _RimColor;

                float _BlotchScale;
                float _BlotchLow;
                float _BlotchHigh;
                float _BlotchSoftness;
                float _LightBlotchStrength;
                float _MottleStrength;
                float _NoiseDriftSpeed;

                float _Wrap;
                float _StepMid;
                float _StepSmooth;
                float _SSSPower;
                float _SSSStrength;
                float _SpecPower;
                float _SpecThreshold;
                float _SpecStrength;
                float _RimPower;
                float _RimStrength;
                float _AmbientStrength;

                float _PulseSpeed;
                float _PulseStrength;

                float _WobbleEnable;
                float _WobbleAmp;
                float _WobbleFreq;
                float _WobbleSpeed;

                float _VatEnable;
                float _VatOffsetScale;
                float _VatFrameStart;
                float _VatFrameEnd;
                float _VatTotalFrames;
                float _VatRowsPerFrame;
                float _VatTexHeight;
                float _VatFps;
                float _VatAutoPlay;
                float _VatManualFrame;
                float _VatPingPong;
                float _VatFlipV;
                float _VatNormalBlend;
            CBUFFER_END

            TEXTURE2D(_VatPosMap);
            SAMPLER(sampler_VatPosMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv1        : TEXCOORD1;   // VAT_ID：x=顶点索引/U宽度, y=所属顶点段
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;   // 物体空间坐标：噪声采样用（斑块贴着模型走）
                float3 normalWS   : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
            };

            // ———————— 程序化噪声（值噪声 + FBM，不需要任何贴图） ————————

            float Hash13(float3 p)
            {
                p = frac(p * 0.3183099 + float3(0.1, 0.17, 0.13));
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float Noise3(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(lerp(Hash13(i + float3(0, 0, 0)), Hash13(i + float3(1, 0, 0)), f.x),
                         lerp(Hash13(i + float3(0, 1, 0)), Hash13(i + float3(1, 1, 0)), f.x), f.y),
                    lerp(lerp(Hash13(i + float3(0, 0, 1)), Hash13(i + float3(1, 0, 1)), f.x),
                         lerp(Hash13(i + float3(0, 1, 1)), Hash13(i + float3(1, 1, 1)), f.x), f.y), f.z);
            }

            float Fbm3(float3 p)
            {
                float v = 0.0;
                float a = 0.5;
                for (int k = 0; k < 4; k++)
                {
                    v += a * Noise3(p);
                    p = p * 2.03 + 11.7;
                    a *= 0.5;
                }
                return v;
            }

            // ———————— VAT 顶点动画 ————————
            // 贴图布局（实测 VAT_Position.exr）：8192(宽) × 1000(高)，RGBA Float
            //   宽 = 每段 8192 个顶点；每帧占 4 行（30722 顶点 / 8192 = 4 段），共 250 帧
            //   帧内行序是【倒着放】的：band0(顶点0~8191)→行3, band1→行2, band2→行1, band3→行0
            //   VAT_ID(UV1): x = (顶点在本段内序号 + 0.5) / 8192,  y = (段号 + 0.5) / 4
            //   → 采样 v = 1 - (RowsPerFrame / TexHeight) * (frame + 1 - uv1.y)
            float3 SampleVatOffset(float2 vatID, float frame)
            {
                float v = 1.0 - (_VatRowsPerFrame / _VatTexHeight) * (frame + 1.0 - vatID.y);
                v = _VatFlipV > 0.5 ? (1.0 - v) : v;
                return SAMPLE_TEXTURE2D_LOD(_VatPosMap, sampler_VatPosMap, float2(vatID.x, v), 0).rgb;
            }

            Varyings vert(Attributes input)
            {
                Varyings o;

                // 花纹用【形变前】的物体空间坐标 → 斑块像“画在表面上”，跟着顶点一起动
                o.positionOS = input.positionOS.xyz;

                float3 pOS = input.positionOS.xyz;
                float3 nOS = normalize(input.normalOS);

                // —— 轻微顶点起伏：位移只看【位置】，同位置顶点位移相同，绝不裂面 ——
                if (_WobbleEnable > 0.5)
                {
                    float t  = _Time.y * _WobbleSpeed;
                    float3 f = float3(_WobbleFreq, _WobbleFreq * 1.31, _WobbleFreq * 0.83);
                    float3 q = pOS * f + float3(t, t * 1.17 + 1.7, t * 0.86 + 3.4);
                    float  w = (sin(q.x) + sin(q.y) + sin(q.z)) * 0.33333;

                    // 球心在原点时这就是“离心方向”，整张网格的拓扑不会被撕开
                    pOS += normalize(pOS + 1e-4) * (w * _WobbleAmp);
                }

                if (_VatEnable > 0.5)
                {
                    float span = max(_VatFrameEnd - _VatFrameStart, 1.0);
                    float ft   = _VatAutoPlay > 0.5 ? (_Time.y * _VatFps) : _VatManualFrame;
                    float k    = ft / span;
                    k = _VatPingPong > 0.5 ? abs(frac(k) * 2.0 - 1.0) : frac(k);
                    float frame = floor(_VatFrameStart + k * span);   // 取整帧，正好落在行中心
                    frame = min(frame, max(_VatTotalFrames - 1.0, 0.0));

                    pOS += SampleVatOffset(input.uv1, frame) * _VatOffsetScale;
                }

                o.positionWS = TransformObjectToWorld(pOS);
                o.normalWS   = normalize(TransformObjectToWorldNormal(nOS));
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.fogFactor  = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 N = normalize(input.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(input.positionWS));

                // VAT 形变后：用屏幕导数重算真实法线，明暗/高光/边缘光才会跟着形变走
                if (_VatEnable > 0.5 && _VatNormalBlend > 0.001)
                {
                    float3 faceN = normalize(cross(ddx(input.positionWS), ddy(input.positionWS)));
                    if (dot(faceN, N) < 0.0) faceN = -faceN;
                    N = normalize(lerp(N, faceN, _VatNormalBlend));
                }

                // —— 1) 颜色斑块：FBM 按物体空间坐标混合 暗斑 / 中间调 / 亮斑 ——
                float drift = _Time.y * _NoiseDriftSpeed;
                float3 noiseP = input.positionOS * _BlotchScale + drift * float3(0.7, 0.45, 0.6);

                float blotch = Fbm3(noiseP);                       // 大块斑块（暗 / 亮）
                float mottle = Fbm3(noiseP * 2.7 + 5.2);           // 细碎杂色

                float tDark = smoothstep(_BlotchLow, _BlotchLow + _BlotchSoftness, blotch);
                float tHi   = smoothstep(_BlotchHigh, _BlotchHigh + _BlotchSoftness, blotch);

                half3 baseCol = lerp(_BaseColorDark.rgb, _BaseColorMid.rgb, tDark);
                baseCol = lerp(baseCol, _BaseColorLight.rgb, tHi * _LightBlotchStrength);
                baseCol *= 1.0 + (mottle - 0.5) * 2.0 * _MottleStrength;   // 细碎色斑起伏

                // —— 2) 主光（没有实时光时给一个固定方向兜底，保证室内/关闭实时光也有体积感）——
                float3 L = normalize(_MainLightPosition.xyz);
                half3 lightColor = _MainLightColor.rgb;
                if (dot(lightColor, half3(0.299, 0.587, 0.114)) < 0.05)
                {
                    L = normalize(float3(0.4, 0.8, 0.45));
                    lightColor = half3(1, 1, 1);
                }

                // —— 3) 柔光包裹的明暗分级（wrap lighting：次表面散射的“软”）——
                float ndl  = dot(N, L);
                float wrap = saturate((ndl + _Wrap) / (1.0 + _Wrap));
                float shade = smoothstep(_StepMid - _StepSmooth, _StepMid + _StepSmooth, wrap);

                half3 col = lerp(baseCol * _ShadowTint.rgb, baseCol, shade);

                // —— 4) 透光（背光侧透出亮绿）——
                float sss = pow(saturate(dot(-N, L) * 0.5 + 0.5), _SSSPower) * _SSSStrength;
                col += _SSSColor.rgb * lightColor * sss;

                // —— 5) 蜡质高光：宽而柔，阈值量化一下更有手绘感 ——
                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), _SpecPower);
                spec = smoothstep(_SpecThreshold, _SpecThreshold + 0.25, spec) * _SpecStrength;
                col += _HighlightColor.rgb * lightColor * spec;

                // —— 6) 边缘光 ——
                float fres = pow(1.0 - saturate(dot(N, V)), _RimPower);
                col += _RimColor.rgb * lightColor * fres * _RimStrength;

                // —— 7) 环境光补底（球底 / 背面不至于死黑）——
                col += baseCol * SampleSH(N) * _AmbientStrength;

                // —— 8) 呼吸脉动 ——
                col *= 1.0 + _PulseStrength * sin(_Time.y * _PulseSpeed);

                col = MixFog(col, input.fogFactor);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
