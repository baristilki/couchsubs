// SPDX-License-Identifier: AGPL-3.0-only
// Fork: oyun modunda altyazı ve ek alan kutularının arkası (DirectX, ekran kartında). Kare mip zinciriyle küçültülür;
// kutunun içi uygun küçüklükteki seviyeden 5x5 Gauss örnekle okunur (yumuşak, ucuz), hafifçe karartılır. Köşeler
// yuvarlak (işaretli uzaklık), kutunun dışı çizilmez.
// Derleme (Windows SDK fxc):
//   fxc /nologo /T vs_4_0 /E vs /Fo GameBlur.vs.cso GameBlur.hlsl
//   fxc /nologo /T ps_4_0 /E ps /Fo GameBlur.ps.cso GameBlur.hlsl
// Sonra scripts/shader-bytes.py ile Views/GameModeShaders.cs yeniden üretilir.

struct VsOut
{
    float4 pos : SV_Position;
};

VsOut vs(uint id : SV_VertexID)
{
    VsOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

Texture2D source : register(t0);
SamplerState linearClamp : register(s0);

cbuffer Blur : register(b0)
{
    float4 rect;    // sol, üst, sağ, alt (piksel)
    float4 shape;   // x: bulanıklık yarıçapı (piksel), y: köşe yarıçapı, z: karartma (0..1)
    float4 texel;   // x, y: 1/en, 1/boy
};

float4 ps(VsOut i) : SV_Target
{
    float2 p = i.pos.xy;
    float2 center = (rect.xy + rect.zw) * 0.5;
    float2 half = max((rect.zw - rect.xy) * 0.5 - shape.y, 0);
    float2 d = abs(p - center) - half;
    float dist = length(max(d, 0)) + min(max(d.x, d.y), 0) - shape.y;
    if (dist > 0.5) discard;

    // Yarıçapın yaklaşık üçte biri kadar küçültülmüş seviyeden örnek: 5x5 örnek geniş bir Gauss gibi davranır.
    float lod = max(0, log2(max(shape.x, 1) / 3.0));
    float step = exp2(lod);
    float3 sum = 0;
    float weight = 0;
    [unroll] for (int y = -2; y <= 2; y++)
    {
        [unroll] for (int x = -2; x <= 2; x++)
        {
            float w = exp(-(x * x + y * y) / 4.5);
            sum += source.SampleLevel(linearClamp, (p + float2(x, y) * step) * texel.xy, lod).rgb * w;
            weight += w;
        }
    }
    float3 color = sum / weight * (1 - shape.z);
    // Kenar yumuşatma (yarım piksel).
    float alpha = saturate(0.5 - dist);
    return float4(color, alpha);
}
