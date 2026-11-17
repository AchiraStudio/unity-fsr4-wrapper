// Synthetic GPU load for the FSR showcase. A full-screen pass whose cost grows with the number of pixels it runs on, so it makes the
// scene GPU-bound the way a heavy game is. It runs at render resolution (before the upscaler), which is the point: FSR's quality modes
// shrink that resolution and therefore this cost. The colour change it makes is far below one 8-bit step.
Shader "FSRShowcase/GpuLoad"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "GpuLoad"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _LoadIterations;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 c = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);

                float acc = 0.0;
                float2 p = uv * 37.0;
                int n = (int)_LoadIterations;
                [loop]
                for (int i = 0; i < n; i++)
                {
                    p = float2(sin(p.y * 1.7 + acc), cos(p.x * 1.3 - acc)) * 3.1 + p * 0.5;
                    acc += sin(p.x * p.y + i) * 0.0001;
                }
                c.rgb += (half)(acc * 1e-4);
                return c;
            }
            ENDHLSL
        }
    }
}
