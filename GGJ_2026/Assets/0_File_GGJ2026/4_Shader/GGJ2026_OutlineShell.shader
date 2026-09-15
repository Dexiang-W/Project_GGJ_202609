// GGJ2026 可交互物体描边壳（URP 兼容）
//
// 两种模式（由材质上的 _Grow 决定，InteractableObject 会自动设置）：
//  1) 法线挤出（_Grow = 0，默认）：把原网格的背面沿法线向外挤出一点点再上色，
//     适合立方体、书本这类【封闭网格】；
//  2) 整体放大（_Grow > 0，薄片专用）：纸张 / 海报这类【只有一个面的开放网格】，
//     挤出方向只有一面，壳子会被自己挡住（P_Paper 不发光就是这个原因），
//     所以改成"以网格中心为基准整体放大一圈"，再沿视线方向往后推一点点，
//     让原网格挡住中间、只露出边缘的一圈高亮。
Shader "GGJ2026/OutlineShell"
{
    Properties
    {
        _Color ("Outline Color", Color) = (0.2, 1, 0.6, 1)
        _Width ("Outline Width (Object Space)", Float) = 0.06
        _Grow ("Flat Grow (薄片模式：整体放大比例，0 = 用法线挤出)", Float) = 0
        _Bias ("Flat Depth Bias (World, 薄片模式：沿视线往后推)", Float) = 0.01
        _Center ("Flat Center (Object Space, 网格中心)", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Overlay"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        // —— Pass 1：封闭网格用（背面挤出，只画背面）——
        Pass
        {
            Name "OUTLINE_SHELL"

            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Width;
                float _Grow;
                float _Bias;
                float4 _Center;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                float3 posOS = input.positionOS.xyz + normalize(input.normalOS) * _Width;
                o.positionCS = TransformObjectToHClip(posOS);
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // 薄片模式（_Grow > 0）交给下面的 Pass 2，这里直接丢弃
                if (_Grow > 0.0)
                    discard;

                return _Color;
            }
            ENDHLSL
        }

        // —— Pass 2：薄片 / 开放网格用（整体放大一圈，两面都画）——
        Pass
        {
            Name "FLAT_OUTLINE"

            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Width;
                float _Grow;
                float _Bias;
                float4 _Center;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;

                // 以网格中心为基准整体放大：薄片会在自己所在的平面上“胖一圈”
                float3 posOS = _Center.xyz + (input.positionOS.xyz - _Center.xyz) * (1.0 + _Grow);

                // 沿视线方向往远处推一点点，保证壳子永远在原网格后面，
                // 于是中间被原网格挡住，只在轮廓外露出一圈高亮
                float3 posWS = TransformObjectToWorld(posOS);
                float3 awayFromCamera = normalize(posWS - _WorldSpaceCameraPos);
                posWS += awayFromCamera * _Bias;

                o.positionCS = TransformWorldToHClip(posWS);
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // 非薄片模式（_Grow = 0）由 Pass 1 负责
                if (_Grow <= 0.0)
                    discard;

                return _Color;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
