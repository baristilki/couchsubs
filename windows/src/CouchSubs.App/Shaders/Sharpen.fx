// SPDX-License-Identifier: AGPL-3.0-only
// Fork: yayın penceresinde oyun görüntüsünün kontrasta duyarlı keskinleştirmesi (AMD FidelityFX CAS fikri, sade hâli).
// 1080p görüntü 1440p ekrana büyütülünce yumuşuyor; komşu piksellerin farkı kadar keskinleştirilir, zaten keskin
// kenarlar ve düz alanlar fazla sertleşmez (hale ve kumlanma olmaz).
// Derleme: fxc /T ps_3_0 /E main /Fo Sharpen.ps Sharpen.fx
sampler2D input : register(s0);
// x, y: kaynak görüntünün bir pikseli (1/en, 1/boy); z: güç (0..1).
float4 settings : register(c0);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 texel = settings.xy;
    float4 center = tex2D(input, uv);
    float3 c = center.rgb;
    float3 n = tex2D(input, uv + float2(0, -texel.y)).rgb;
    float3 s = tex2D(input, uv + float2(0, texel.y)).rgb;
    float3 w = tex2D(input, uv + float2(-texel.x, 0)).rgb;
    float3 e = tex2D(input, uv + float2(texel.x, 0)).rgb;
    float3 lo = min(c, min(min(n, s), min(w, e)));
    float3 hi = max(c, max(max(n, s), max(w, e)));
    // Kenar ne kadar az kontrastlıysa o kadar keskinleştirilir.
    float3 amp = sqrt(saturate(min(lo, 1.0 - hi) / max(hi, 0.0001)));
    float peak = -1.0 / lerp(8.0, 4.5, settings.z);
    float3 weight = amp * peak;
    float3 result = (c + (n + s + w + e) * weight) / (1.0 + 4.0 * weight);
    return float4(saturate(result), center.a);
}
