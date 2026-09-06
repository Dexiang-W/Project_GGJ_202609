// GGJ2026 可交互物体描边壳（URP 兼容）
// 原理：把原网格的背面沿法线方向向外挤出一点点再上色，从而在物体边缘勾勒出一圈高亮。
// 由 InteractableObject 在运行时自动创建壳子并用 Shader.Find 找到本 Shader。
Shader "GGJ2026/OutlineShell"
{
    Properties
    {
        _Color ("Outline Color", Color) = (0.2, 1, 0.6, 1)
        _Width ("Outline Width (Object Space)", Float) = 0.06
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
                return _Color;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
