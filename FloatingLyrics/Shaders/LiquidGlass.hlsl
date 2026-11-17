// Liquid glass pixel shader (Direct2D custom effect, used through Win2D PixelShaderEffect).
//
// Input 0: blurred + saturated backdrop (frosted interior)
// Input 1: sharp backdrop (seen refracted through the curved rim)
//
// The glass is a rounded rectangle slab whose edge is a quarter-circle bevel. Light passing
// through the bevel is bent toward the centre (lensing) and split by wavelength (dispersion),
// which produces the rainbow fringes on high-contrast edges behind the rim. Everything is
// analytic (signed distance field), so the shape can morph and the bevel can "grow" from the
// rim inward while the glass materialises.
//
// Build: Shaders/build-shaders.ps1 (fxc, ps_4_0, D2D_FULL_SHADER).

#define D2D_INPUT_COUNT 2
#define D2D_INPUT0_COMPLEX
#define D2D_INPUT1_COMPLEX
#define D2D_REQUIRES_SCENE_POSITION
#include "d2d1effecthelpers.hlsli"

// All lengths are in output pixels.
float2 shapeOrigin;   // top-left of the glass shape
float2 shapeSize;     // size of the glass shape
float  radius;        // corner radius
float  bevel;         // width of the curved rim (animated 0 -> full while appearing)
float  refraction;    // max inward bend at the rim
float  dispersion;    // 0..1 strength of the wavelength split
float  magnify;       // 0..~0.08 gentle magnification of the flat interior
float  rimClarity;    // 0..1 how sharp (unblurred) the refracted rim content is
float  specular;      // 0..1 bevel highlight intensity
float  glint;         // position of the sweeping glint along the diagonal (-0.3..1.3), <-1 = off
float  saturationBoost; // extra saturation applied to the refracted rim colour
float4 tint;          // rgb = tint colour, a = tint opacity
float2 lightDir;      // direction the light travels (normalised), e.g. (0.6, 0.8)

float sdRoundRect(float2 p, float2 halfSize, float r)
{
    float2 q = abs(p) - halfSize + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// Smooth spectral response for a normalised wavelength w (0 = violet, 1 = red).
float3 spectrum(float w)
{
    float r = saturate(1.0 - pow((w - 0.82) / 0.32, 2.0));
    float g = saturate(1.0 - pow((w - 0.50) / 0.28, 2.0));
    float b = saturate(1.0 - pow((w - 0.16) / 0.32, 2.0));
    return float3(r, g, b);
}

float3 sampleBackdrop(float2 offset, float clarity)
{
    float3 frosted = D2DSampleInputAtOffset(0, offset).rgb;
    float3 sharp = D2DSampleInputAtOffset(1, offset).rgb;
    return lerp(frosted, sharp, clarity);
}

D2D_PS_ENTRY(main)
{
    float2 scene = D2DGetScenePosition().xy;
    float2 halfSize = shapeSize * 0.5;
    float2 p = scene - shapeOrigin - halfSize;
    float r = min(radius, min(halfSize.x, halfSize.y));
    float d = sdRoundRect(p, halfSize, r);

    // 1px anti-aliased silhouette.
    float coverage = saturate(0.5 - d);
    if (coverage <= 0.0)
    {
        return float4(0, 0, 0, 0);
    }

    // Outward normal from the SDF gradient.
    const float e = 0.75;
    float2 grad = float2(
        sdRoundRect(p + float2(e, 0), halfSize, r) - sdRoundRect(p - float2(e, 0), halfSize, r),
        sdRoundRect(p + float2(0, e), halfSize, r) - sdRoundRect(p - float2(0, e), halfSize, r));
    float2 n = grad / max(length(grad), 1e-4);

    // t: 0 on the flat interior, 1 right at the rim (quarter-circle bevel profile).
    float b = max(bevel, 0.001);
    float t = saturate(1.0 + d / b);
    float lens = 1.0 - sqrt(saturate(1.0 - t * t));      // surface slope proxy, steep at the rim

    // Inward bend through the bevel + gentle magnification of the interior.
    float2 bend = -n * (refraction * lens) - p * magnify;
    float clarity = rimClarity * smoothstep(0.0, 0.85, t);

    float3 color;
    float strength = length(bend);
    if (dispersion < 0.01 || strength < 0.35)
    {
        color = sampleBackdrop(bend, clarity);
    }
    else
    {
        // Spectral dispersion: short wavelengths bend more than long ones.
        const int SAMPLES = 16;
        float spread = dispersion * (0.14 + 0.42 * lens);
        float3 acc = 0;
        float3 weight = 0;
        [unroll]
        for (int i = 0; i < SAMPLES; i++)
        {
            float w = (i + 0.5) / SAMPLES;
            float3 response = spectrum(w);
            float2 offset = bend * (1.0 + spread * (0.5 - w) * 2.0);
            acc += sampleBackdrop(offset, clarity) * response;
            weight += response;
        }
        color = acc / max(weight, 1e-4);
    }

    // The refracted rim is more vivid (like light concentrated by a lens).
    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
    color = lerp(luma.xxx, color, 1.0 + saturationBoost * lens);

    // Tint.
    color = lerp(color, tint.rgb, tint.a);

    // Lighting on the bevel: highlight on the side facing the light, a fainter reflection on
    // the opposite side, a soft inner shadow on the lower rim and a Fresnel brightening.
    float facing = dot(n, -lightDir);
    float highlight = pow(t, 3.0) * (0.22 + 0.78 * max(facing, -facing * 0.45));
    float fresnel = pow(t, 5.0) * 0.18;
    float innerShadow = pow(t, 1.6) * saturate(dot(n, float2(0.0, 1.0))) * 0.16;
    color = color * (1.0 - innerShadow) + (highlight * 0.55 + fresnel) * specular;

    // Glint sweeping diagonally across the rim while the glass materialises.
    if (glint > -1.0)
    {
        float2 uv = (scene - shapeOrigin) / max(shapeSize, 1.0);
        float diag = (uv.x * 0.65 + uv.y * 0.35);
        float band = exp(-pow((diag - glint) / 0.07, 2.0));
        color += band * pow(t, 1.5) * 0.85 * specular;
    }

    return float4(saturate(color) * coverage, coverage);
}
