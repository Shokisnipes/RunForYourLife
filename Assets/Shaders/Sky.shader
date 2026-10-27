// Procedural sky for Run For Your Life: colour gradient that melts into the fog at the horizon, a sunset glow,
// stars, drifting clouds, and a moon that is shaded as a lit sphere (its phase comes from where the sun is).
// SkyCycle sets every property from the game clock; the values here are just the night look.
Shader "RunForYourLife/Sky"
{
    Properties
    {
        _ZenithColor ("Zenith", Color) = (0.02, 0.03, 0.06, 1)
        _HorizonColor ("Horizon (match the fog)", Color) = (0.098, 0.118, 0.157, 1)
        _GroundColor ("Below Horizon", Color) = (0.03, 0.03, 0.035, 1)
        _HorizonFade ("Horizon Fade Height", Range(0.01, 0.5)) = 0.12

        _SunDir ("Sun Direction (towards the sun)", Vector) = (-0.9, -0.2, -0.4, 0)
        _SunColor ("Sun Glow Colour", Color) = (1, 0.5, 0.25, 1)
        _SunGlow ("Sun Glow", Range(0, 4)) = 0

        _MoonDir ("Moon Direction (towards the moon)", Vector) = (0.45, 0.57, 0.68, 0)
        _MoonColor ("Moon Colour", Color) = (0.85, 0.88, 0.95, 1)
        _MoonSize ("Moon Radius (radians)", Range(0.005, 0.1)) = 0.035
        _MoonBrightness ("Moon Brightness", Range(0, 8)) = 2.5
        _MoonGlow ("Moon Halo", Range(0, 2)) = 0.6

        _Stars ("Stars", Range(0, 2)) = 1

        _CloudCover ("Cloud Cover", Range(0, 1)) = 0.5
        _CloudSoftness ("Cloud Softness", Range(0.02, 0.6)) = 0.25
        _CloudScale ("Cloud Scale", Range(0.1, 5)) = 1.2
        _CloudSpeed ("Cloud Speed", Range(0, 0.2)) = 0.012
        _CloudOpacity ("Cloud Opacity", Range(0, 1)) = 0.92
        _CloudLit ("Cloud Lit Side", Color) = (0.16, 0.17, 0.2, 1)
        _CloudDark ("Cloud Dark Core", Color) = (0.035, 0.04, 0.05, 1)
        _CloudMoonLight ("Moonlit Cloud Edges", Range(0, 4)) = 1.2
        _CloudSunLight ("Sunlit Cloud Edges", Range(0, 4)) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _ZenithColor, _HorizonColor, _GroundColor;
                float _HorizonFade;
                float4 _SunDir, _SunColor;
                float _SunGlow;
                float4 _MoonDir, _MoonColor;
                float _MoonSize, _MoonBrightness, _MoonGlow;
                float _Stars;
                float _CloudCover, _CloudSoftness, _CloudScale, _CloudSpeed, _CloudOpacity;
                float4 _CloudLit, _CloudDark;
                float _CloudMoonLight, _CloudSunLight;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.dir = v.positionOS.xyz;   // the sky mesh surrounds the camera, so its vertices are view directions
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float Hash3(float3 p) { return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453); }

            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x),
                            lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), f.x), f.y);
            }

            float Fbm(float2 p)
            {
                float v = 0.0, a = 0.5;
                for (int k = 0; k < 5; k++) { v += a * Noise(p); p = p * 2.03 + 17.1; a *= 0.5; }
                return v;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float up = d.y;
                float3 sunDir = normalize(_SunDir.xyz);
                float3 moonDir = normalize(_MoonDir.xyz);

                // Sky gradient; below the horizon it's ground colour
                float3 col = lerp(_HorizonColor.rgb, _ZenithColor.rgb, pow(saturate(up), 0.45));
                if (up < 0.0) col = lerp(_HorizonColor.rgb, _GroundColor.rgb, saturate(-up * 4.0));

                // Sunset glow around the (just set) sun, strongest at the horizon
                float sunDot = saturate(dot(d, sunDir));
                col += _SunColor.rgb * _SunGlow * (pow(sunDot, 6.0) * 0.5 + pow(sunDot, 48.0) * 0.8) * saturate(1.0 - up * 1.5);

                // Stars: one random star per little cell of the sky, twinkling
                if (_Stars > 0.0 && up > 0.0)
                {
                    float3 p = d * 220.0;
                    float3 cell = floor(p);
                    float r = Hash3(cell);
                    if (r > 0.9975)
                    {
                        float dist = length(frac(p) - 0.5);
                        float twinkle = 0.6 + 0.4 * sin(_Time.y * (2.0 + r * 40.0) + r * 100.0);
                        col += smoothstep(0.25, 0.0, dist) * twinkle * _Stars * saturate(up * 3.0) * 0.9;
                    }
                }

                // Moon: a sphere lit by the sun, so the lit part (phase) is real; dark seas from noise
                float cosA = dot(d, moonDir);
                float ang = acos(clamp(cosA, -1.0, 1.0));
                float3 right = normalize(cross(moonDir, float3(0, 1, 0.001)));
                float3 upv = cross(right, moonDir);
                if (ang < _MoonSize * 1.05)
                {
                    float2 q = float2(dot(d, right), dot(d, upv)) / sin(_MoonSize);
                    float rr = saturate(dot(q, q));
                    float z = sqrt(1.0 - rr);
                    float3 normal = right * q.x + upv * q.y - moonDir * z;   // the half facing us
                    float lit = saturate(dot(normal, sunDir) * 1.2 + 0.05);
                    float seas = 0.72 + 0.28 * smoothstep(0.35, 0.7, Fbm(q * 2.5 + 3.0));
                    float3 moon = _MoonColor.rgb * _MoonBrightness * lit * seas + _MoonColor.rgb * 0.02;   // + earthshine
                    float edge = smoothstep(_MoonSize, _MoonSize * 0.96, ang);
                    col = lerp(col, moon, edge);
                }
                // Halo around the moon (thin haze)
                float moonDot = saturate(cosA);
                col += _MoonColor.rgb * _MoonGlow * (pow(moonDot, 900.0) * 0.6 + pow(moonDot, 60.0) * 0.12 + pow(moonDot, 8.0) * 0.03);

                // Clouds: noise on a flat layer overhead, drifting with the wind; thin out towards the horizon
                if (up > 0.0)
                {
                    float2 uv = d.xz / (up + 0.12) * _CloudScale + _Time.y * _CloudSpeed * float2(1.0, 0.35);
                    float n = Fbm(uv);
                    float threshold = 1.0 - _CloudCover;
                    float cover = smoothstep(threshold - _CloudSoftness * 0.5, threshold + _CloudSoftness * 0.5, n);
                    cover *= smoothstep(0.0, 0.18, up);

                    float thick = saturate((n - threshold) * 2.5);
                    float3 cloud = lerp(_CloudLit.rgb, _CloudDark.rgb, thick);
                    // Thin edges glow where the moon or sun is behind them (silver lining)
                    float edges = 1.0 - thick;
                    cloud += _MoonColor.rgb * _CloudMoonLight * edges * (pow(moonDot, 12.0) * 0.8 + pow(moonDot, 3.0) * 0.15);
                    cloud += _SunColor.rgb * _CloudSunLight * edges * (pow(sunDot, 4.0) * 0.8 + 0.1) * saturate(1.0 - up);
                    col = lerp(col, cloud, cover * _CloudOpacity);
                }

                // Melt into the fog at the horizon, so foggy streets and the sky meet without a seam
                col = lerp(_HorizonColor.rgb, col, smoothstep(-0.02, _HorizonFade, up));
                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
