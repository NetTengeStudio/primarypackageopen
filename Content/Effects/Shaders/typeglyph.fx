#if OPENGL
#define SV_POSITION POSITION
#define VS_PROFILE vs_3_0
#define PS_PROFILE ps_3_0
#else
#define VS_PROFILE vs_4_0
#define PS_PROFILE ps_4_0
#endif

#ifndef TYPE_SAMPLES
#error TYPE_SAMPLES must be supplied by the shader builder
#endif

struct VertexInput {
    float2 Position : POSITION0;
    float4 Color : COLOR0;
    float2 Uv : TEXCOORD0;
    float4 Bounds : TEXCOORD1;
    float4 Motion : TEXCOORD2;
};

struct VertexOutput {
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 Uv : TEXCOORD0;
    float4 Bounds : TEXCOORD1;
    float4 Motion : TEXCOORD2;
};

texture xGlyph;
float2 xAtlasSize;
sampler GlyphSampler = sampler_state {
    Texture = <xGlyph>;
    MinFilter = LINEAR;
    MagFilter = LINEAR;
    MipFilter = NONE;
    AddressU = CLAMP;
    AddressV = CLAMP;
};

VertexOutput GlyphVS(VertexInput input) {
    VertexOutput output;
    output.Position = float4(input.Position, 0.0, 1.0);
    output.Color = input.Color;
    output.Uv = input.Uv;
    output.Bounds = input.Bounds;
    output.Motion = input.Motion;
    return output;
}

float Ease(float phase) {
    float t = saturate(phase);
    float v = min(t, 1.0 - t);
    float eased = v * v * v * (v * (v * 6.0 - 15.0) + 10.0);
    return t <= 0.5 ? eased : 1.0 - eased;
}

float4 GlyphTexel(float2 uv, float4 bounds) {
    
    
    if (uv.x < bounds.x || uv.y < bounds.y || uv.x >= bounds.z || uv.y >= bounds.w) {
        return 0.0;
    }
    float2 low = (uv - bounds.xy) * xAtlasSize + 0.5;
    float2 high = (bounds.zw - uv) * xAtlasSize + 0.5;
    float mask = saturate(low.x) * saturate(low.y) * saturate(high.x) * saturate(high.y);
    float2 halfTexel = 0.5 / xAtlasSize;
    float2 safeUv = clamp(uv, bounds.xy + halfTexel, bounds.zw - halfTexel);
    return tex2D(GlyphSampler, safeUv) * mask;
}

float4 GlyphPS(VertexOutput input) : COLOR0 {
    if (input.Motion.w <= 0.0) {
        float4 texel = tex2D(GlyphSampler, input.Uv);
        return texel * input.Color;
    }
    float3 radiance = 0.0;
    float coverage = 0.0;
    [unroll]
    for (int sampleIndex = 0; sampleIndex < TYPE_SAMPLES; sampleIndex++) {
        float phase = input.Motion.z - input.Motion.w + input.Motion.w * ((sampleIndex + 0.5) / TYPE_SAMPLES);
        float opacity = Ease(phase);
        float2 uv = input.Uv - input.Motion.xy * (1.0 - opacity);
        float4 texel = GlyphTexel(uv, input.Bounds);
        float alpha = texel.a * opacity;
        
        
        
        radiance += texel.rgb * alpha;
        coverage += alpha;
    }
    float3 rgb = coverage > 0.0 ? radiance / coverage : 0.0;
    return float4(rgb * input.Color.rgb, saturate(coverage * input.Color.a / TYPE_SAMPLES));
}

technique GlyphMotion {
    pass P0 {
        VertexShader = compile VS_PROFILE GlyphVS();
        PixelShader = compile PS_PROFILE GlyphPS();
    }
}
