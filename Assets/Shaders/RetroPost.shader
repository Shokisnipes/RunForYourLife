// Retro look, step 2 of 2 (step 1 is the low render resolution with hard pixel upscaling, set by RetroLook):
// fewer colour steps with ordered (Bayer) dithering, so gradients band in a crunchy, deliberate way.
// Runs as a full-screen pass after post-processing. Does nothing unless RetroLook sets _RetroAmount to 1.
Shader "RunForYourLife/RetroPost"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "RetroPost"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Globals set by RetroLook
            float _RetroAmount;   // 0 = off (pass the picture through), 1 = on
            float _RetroSteps;    // colour steps per channel
            float _RetroDither;   // 0 = hard bands, 1 = fully dithered
            float _RetroShadowLift; // gamma on the picture before stepping: 1 = unchanged, lower = brighter darks

            static const float Bayer4[16] =
            {
                0.0,  8.0,  2.0, 10.0,
               12.0,  4.0, 14.0,  6.0,
                3.0, 11.0,  1.0,  9.0,
               15.0,  7.0, 13.0,  5.0
            };

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, input.texcoord);
                if (_RetroAmount <= 0.0) return c;

                // Work in display (gamma) space so the steps are even to the eye
                float3 g = LinearToSRGB(saturate(c.rgb));
                // Lift the darks before stepping, or the darkest night colours all round down to black
                g = pow(g, _RetroShadowLift > 0.0 ? _RetroShadowLift : 1.0);   // 0 = never set: leave it alone
                uint2 px = uint2(input.positionCS.xy) % 4;
                float threshold = (Bayer4[px.y * 4 + px.x] + 0.5) / 16.0;     // 0..1
                float offset = lerp(0.5, threshold, _RetroDither);
                float steps = max(2.0, _RetroSteps);
                g = floor(g * steps + offset) / steps;
                c.rgb = lerp(c.rgb, SRGBToLinear(saturate(g)), _RetroAmount);
                return c;
            }
            ENDHLSL
        }
    }
}
