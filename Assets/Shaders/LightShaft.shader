// Fake volumetric light: a soft glowing cone under a street lamp, so you can see the beam in the foggy air.
// Additive, no depth writes. Brightest at the bulb, fading towards the ground, soft at the cone's edges (where you
// look along its surface), faded when the camera is inside it, faded into the fog with distance, with slow
// drifting dust. StreetLamp builds the cone and sets _ShaftLevel per lamp (flicker, broken).
Shader "RunForYourLife/LightShaft"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 0.68, 0.38, 1)
        _Intensity ("Intensity", Range(0, 2)) = 0.18
        _EdgeSoftness ("Edge Softness", Range(0.5, 6)) = 2
        _GroundFade ("Fade Towards Ground", Range(0.2, 4)) = 1.3
        _NearFade ("Fade Near Camera (m)", Range(0, 6)) = 2.5
        _Dust ("Dust", Range(0, 1)) = 0.35
        _ShaftLevel ("Level (set by StreetLamp)", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend One One
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Intensity, _EdgeSoftness, _GroundFade, _NearFade, _Dust;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float, _ShaftLevel)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;   // v: 0 at the bulb, 1 at the ground
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float v : TEXCOORD2;
                float fog : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_TRANSFER_INSTANCE_ID(i, o);
                VertexPositionInputs p = GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.v = i.uv.y;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            float Hash(float3 p) { return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453); }
            float Noise(float3 p)
            {
                float3 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(Hash(i), Hash(i + float3(1, 0, 0)), f.x), lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), f.x), f.y),
                            lerp(lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), f.x), lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), f.x), f.y), f.z);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float level = UNITY_ACCESS_INSTANCED_PROP(Props, _ShaftLevel);

                float3 view = normalize(GetCameraPositionWS() - i.positionWS);
                float facing = abs(dot(normalize(i.normalWS), view));
                float edge = pow(saturate(facing), _EdgeSoftness);               // soft where the cone curves away
                float down = pow(saturate(1.0 - i.v), _GroundFade);               // bright at the bulb
                float camDist = distance(GetCameraPositionWS(), i.positionWS);
                float near = saturate(camDist / max(0.01, _NearFade));            // don't blind someone standing in it
                float dust = lerp(1.0, 0.55 + 0.9 * Noise(i.positionWS * 1.6 + float3(0, -_Time.y * 0.25, _Time.y * 0.1)), _Dust);

                float a = _Intensity * level * edge * down * near * dust;
                float3 col = _Color.rgb * a;
                col *= ComputeFogIntensity(i.fog);   // far beams fade into the fog (additive: fade to black)
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
