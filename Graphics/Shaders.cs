namespace Biome2.Graphics;
internal static class Shaders {
	public const string GridVertexRect = @"#version 450 core

layout(location = 0) in vec2 aLocalPos;
layout(location = 1) in vec4 aInstance; // xy = origin, z = angle (unused), w = pad
layout(location = 2) in vec2 aCellCoord;

uniform mat4 uViewProj;
uniform float uCellSize;
uniform vec2 uGridSize;

out vec2 vCellUv;
flat out ivec2 vCellCoord;

void main()
{
    vec2 worldPos = aInstance.xy + (aLocalPos * uCellSize);
    vCellUv = aLocalPos;
    vCellCoord = ivec2(int(aCellCoord.x + 0.5), int(aCellCoord.y + 0.5));
    gl_Position = uViewProj * vec4(worldPos.xy, 0.0, 1.0);
}";

	public const string GridVertexHex = @"#version 450 core

layout(location = 0) in vec2 aLocalPos;
layout(location = 1) in vec4 aInstance; // xy = origin (top-left)
layout(location = 2) in vec2 aCellCoord;

uniform mat4 uViewProj;
uniform float uCellSize;
uniform int uShowGrid;
uniform vec2 uGridSize;

out vec2 vCellUv;
flat out ivec2 vCellCoord;

void main()
{
    float hexH = uCellSize * 0.86602540378;
    if (uShowGrid == 1) {
        hexH *= 0.975;
    }
    vec2 worldPos = aInstance.xy + aLocalPos * vec2(uCellSize, hexH);
    vCellUv = aLocalPos;
    vCellCoord = ivec2(int(aCellCoord.x + 0.5), int(aCellCoord.y + 0.5));
    gl_Position = uViewProj * vec4(worldPos.xy, 0.0, 1.0);
}";

    public const string GridVertexDisk = @"#version 450 core

    layout(location = 0) in vec2 aLocalPos;
    layout(location = 1) in vec4 aInstance; // xy = origin, z = angle, w = ring count
    layout(location = 2) in vec2 aCellCoord;

    uniform mat4 uViewProj;
    uniform float uCellSize;
    uniform vec2 uGridSize;
    uniform vec2 uDiskCenter;

    out vec2 vCellUv;
    flat out ivec2 vCellCoord;

    const float TWOPI = 2.0 * 3.14159265359;

    void main()
    {
        float angle = aInstance.z - 1.57079632679; // -PI/2
        float t = aLocalPos.y;
        vec2 centered = aInstance.xy - uDiskCenter;
        float radius = length(centered);
        float cnt = aInstance.w;

        // Desired radius for this ring so that arc length = uCellSize
        float desiredRadius = uCellSize * (1 + cnt / TWOPI);

        // radialPad positions the cell's center at the desired radius
        float radialPad = desiredRadius;
        float radiusForArc = radius + radialPad;

        // Compute arc lengths for outer and inner edges of the cell band
        float arcOuter = TWOPI * max(radiusForArc, 1e-6) / cnt;
        float arcInner = TWOPI * max(radiusForArc - uCellSize, 1e-6) / cnt;

        // Slightly shrink the computed arc extents to reduce visual overlap at seams
        arcOuter *= 0.98;
        arcInner *= 0.98;

        float halfOuter = max(0.5, 0.5 * arcOuter);
        float halfInner = clamp(0.5 * arcInner, 0.0, halfOuter);
        float halfWidth = mix(halfInner, halfOuter, t);

        float xLocal = (aLocalPos.x - 0.5) * 2.0 * halfWidth;
        float yLocal = (aLocalPos.y - 0.5) * uCellSize + radialPad;
        float ca = cos(angle);
        float sa = sin(angle);
        vec2 worldPos = aInstance.xy + vec2(ca * xLocal - sa * yLocal, sa * xLocal + ca * yLocal);
        vCellUv = aLocalPos;
        vCellCoord = ivec2(int(aCellCoord.x + 0.5), int(aCellCoord.y + 0.5));
        gl_Position = uViewProj * vec4(worldPos.xy, 0.0, 1.0);
    }";

	public const string GridFragmentRect = @"#version 450 core

in vec2 vCellUv;
flat in ivec2 vCellCoord;
out vec4 fragColor;

uniform int uShowGrid;
uniform float uPixelsPerUnit;
uniform float uGridThicknessPx;
uniform float uCellSize;
uniform vec2 uGridSize;
uniform sampler2D uCellIndices;
uniform sampler2D uPalette;
uniform int uSpeciesCount;

void main()
{
    ivec2 coord = vCellCoord;
    float idxNorm = texelFetch(uCellIndices, coord, 0).r;
    int idx = int(idxNorm * 255.0 + 0.5);
    if (uSpeciesCount > 0) idx = clamp(idx, 0, uSpeciesCount - 1);
    ivec2 pcoord = ivec2(idx, 0);
    vec4 texColor = texelFetch(uPalette, pcoord, 0);
    float uvPixel = (1.0 / max(uPixelsPerUnit, 0.0001)) / max(uCellSize, 0.0005);
    float edgeFade = clamp(uvPixel * 0.5, 1e-6, 0.5);
    float edgeWorldUnits = (1.0 / max(uPixelsPerUnit, 0.01));
    float edgeNormalized = edgeWorldUnits / max(uCellSize, 0.0001);
    int gridW = int(uGridSize.x + 0.5);
    int gridH = int(uGridSize.y + 0.5);
    float leftMask = 0.0;
    float rightMask = 0.0;
    float downMask = 0.0;
    float upMask = 0.0;
    if (gridW > 0) {
        if (coord.x == 0) leftMask = 1.0 - smoothstep(edgeNormalized - edgeFade, edgeNormalized + edgeFade, vCellUv.x);
        if (coord.x == gridW - 1) rightMask = 1.0 - smoothstep(edgeNormalized - edgeFade, edgeNormalized + edgeFade, 1.0 - vCellUv.x);
    }
    if (gridH > 0) {
        if (coord.y == 0) downMask = 1.0 - smoothstep(edgeNormalized - edgeFade, edgeNormalized + edgeFade, vCellUv.y);
        if (coord.y == gridH - 1) upMask = 1.0 - smoothstep(edgeNormalized - edgeFade, edgeNormalized + edgeFade, 1.0 - vCellUv.y);
    }
    float borderMask = max(max(leftMask, rightMask), max(downMask, upMask));
    vec3 worldBorderColor = vec3(0.5, 0.5, 0.5);
    if (uShowGrid == 0) {
        vec3 base = texColor.rgb;
        vec3 outColor = mix(worldBorderColor, base, 1.0 - borderMask);
        fragColor = vec4(outColor, texColor.a);
        return;
    }
    float worldThickness = uGridThicknessPx / max(uPixelsPerUnit, 0.01);
    float inset = worldThickness / (2.0 * max(uCellSize, 0.0001));
    inset = clamp(inset, 0.0, 0.1);
    float fade = edgeFade;
    float left = smoothstep(inset - fade, inset + fade, vCellUv.x);
    float right = smoothstep(inset - fade, inset + fade, 1.0 - vCellUv.x);
    float down = smoothstep(inset - fade, inset + fade, vCellUv.y);
    float up = smoothstep(inset - fade, inset + fade, 1.0 - vCellUv.y);
    float interior = left * right * down * up;
    vec3 borderColor = vec3(0.0, 0.0, 0.0);
    vec3 color = mix(borderColor, texColor.rgb, interior);
    vec3 finalColor = mix(worldBorderColor, color, 1.0 - borderMask);
    fragColor = vec4(finalColor, texColor.a);
}";

	public const string GridFragmentHex = @"#version 450 core

in vec2 vCellUv;
flat in ivec2 vCellCoord;
out vec4 fragColor;

uniform int uShowGrid;
uniform float uPixelsPerUnit;
uniform float uGridThicknessPx;
uniform float uCellSize;
uniform vec2 uGridSize;
uniform sampler2D uCellIndices;
uniform sampler2D uPalette;
uniform int uSpeciesCount;

float hexInteriorMask(vec2 uv, float cellSize, float pixelsPerUnit, float thicknessWorld, out float edgeDist)
{
    // If uShowGrid is off, the caller uploaded unscaled vCellUv so we should not apply uHexScale here.
    float hexH = cellSize * 0.865;
    float aspect = hexH / cellSize;
    vec2 p = uv - vec2(0.5);
    // To match the vertex scaling, the fragment receives vCellUv already scaled when grid on.
    p.y *= aspect;
    p = abs(p);
    float k = 0.57735026919; // tan(30deg)
    float t = 0.5 - (p.x + p.y * k);
    edgeDist = t;
    // Map edge distance into an interior blend factor with a small transition.
    return clamp(t * 10.0 + 0.5, 0.0, 1.0);
}

void main()
{
    ivec2 coord = vCellCoord;
    float idxNorm = texelFetch(uCellIndices, coord, 0).r;
    int idx = int(idxNorm * 255.0 + 0.5);
    if (uSpeciesCount > 0) idx = clamp(idx, 0, uSpeciesCount - 1);
    ivec2 pcoord = ivec2(idx, 0);
    vec4 texColor = texelFetch(uPalette, pcoord, 0);
    float uvPixel = (1.0 / max(uPixelsPerUnit, 0.0001)) / max(uCellSize, 0.0005);
    float edgeFade = clamp(uvPixel * 0.5, 0.0, 0.001);
    float edgeWorldUnits = (1.0 / max(uPixelsPerUnit, 0.001));
    // Compute hex interior distance (positive inside, negative outside)
    float edgeDist;
    float interior = hexInteriorMask(vCellUv, uCellSize, uPixelsPerUnit, edgeWorldUnits, edgeDist);
    float aaW = edgeFade;

    // If grid lines are disabled, render only the hex interior and discard fragments outside the hex.
    // Use a small AA guard (aaW) to avoid visible seams between adjacent hexes.
    if (uShowGrid == 0) {
        if (edgeDist <= aaW) discard;
        fragColor = vec4(texColor.rgb, texColor.a);
        return;
    }

    // Thickness of grid border expressed in normalized cell units
    float worldThickness = uGridThicknessPx / max(uPixelsPerUnit, 0.001);
    float thicknessNorm = worldThickness / max(uCellSize, 0.0001);

    // Grid enabled: discard fragments well outside the hex.
    //if (edgeDist <= -aaW) discard;

    // Border width (normalized to cell units). Make it thin by default: use 0.75x thicknessNorm
    float borderW = max(thicknessNorm, 0.0001);
    // Smooth blend region for anti-aliasing
    float blendW = aaW;

    // interiorFactor = 0 at border edge, 1 inside cell beyond borderW+blendW
    float interiorFactor = smoothstep(borderW, borderW + blendW, edgeDist);
    vec3 borderColor = vec3(0.0, 0.0, 0.0);
    vec3 color = mix(borderColor, texColor.rgb, interiorFactor);
    // If fragment is outside hex (edgeDist <= 0), discard to avoid rectangular artifacts
    if (edgeDist <= 0.0) discard;
    fragColor = vec4(color, texColor.a);
}";

	public const string AxisVertex = @"
#version 450 core

layout(location = 0) in vec2 aPos;
uniform mat4 uViewProj;

void main()
{
    gl_Position = uViewProj * vec4(aPos.xy, 0.0, 1.0);
}";

	public const string AxisFragment = @"
#version 450 core

uniform vec3 uColor;
out vec4 fragColor;

void main()
{
    fragColor = vec4(uColor, 1.0);
}";

	public const string HighlightVertexRect = @"#version 450 core

layout(location = 0) in vec2 aLocalPos;    // 0..1 local quad
layout(location = 1) in vec4 aInstance; // origin.xy, size.xy

uniform mat4 uViewProj;
uniform float uCellSize;

out vec2 vRegionUv;    // coordinates in cell units (0..size)
out vec2 vRegionNorm;  // normalized coordinates across region (0..1)
out vec2 vWorldPos;
out vec2 vInstanceSize;

void main()
{
    vec2 aInstancePos = aInstance.xy;
    vec2 aInstanceSize = aInstance.zw;
    vec2 regionSizeWorld = aInstanceSize * uCellSize;
    vec2 worldPos = aInstancePos + aLocalPos * regionSizeWorld;
    vRegionUv = aLocalPos * aInstanceSize;
    vRegionNorm = aLocalPos;
    vWorldPos = worldPos;
    vInstanceSize = aInstanceSize;
    gl_Position = uViewProj * vec4(worldPos.xy, 0.0, 1.0);
}";

	public const string HighlightVertexHex = @"#version 450 core

layout(location = 0) in vec2 aLocalPos;
layout(location = 1) in vec4 aInstance; // origin.xy, size unused

uniform mat4 uViewProj;
uniform float uCellSize;

out vec2 vRegionUv;
out vec2 vRegionNorm;
out vec2 vWorldPos;
out vec2 vInstanceSize;

void main()
{
    float hexH = uCellSize * 0.86602540378;
    vec2 worldPos = aInstance.xy + aLocalPos * vec2(uCellSize, hexH);
    vRegionUv = aLocalPos;
    vRegionNorm = aLocalPos;
    vWorldPos = worldPos;
    vInstanceSize = vec2(1.0, 1.0);
    gl_Position = uViewProj * vec4(worldPos.xy, 0.0, 1.0);
}";

   public const string HighlightVertexDisk = @"#version 450 core

    layout(location = 0) in vec2 aLocalPos;
    layout(location = 1) in vec4 aInstance; // origin.xy, z=angle, w=ringCount

    uniform mat4 uViewProj;
    uniform float uCellSize;
    uniform vec2 uDiskCenter;

    out vec2 vRegionUv;
    out vec2 vRegionNorm;
    out vec2 vWorldPos;
    out vec2 vInstanceSize;

    const float TWOPI = 2.0 * 3.14159265359;

    void main()
    {
        // Mirror the grid vertex math so highlights align with the disk cells
        float angle = aInstance.z - 1.57079632679; // -PI/2
        float t = aLocalPos.y;
        vec2 centered = aInstance.xy - uDiskCenter;
        float radius = length(centered);
        float cnt = aInstance.w;

        // Compute desired radius so arc length ~ uCellSize, matching GridVertexDisk
        float desiredRadius = uCellSize * (1 + cnt / TWOPI);
        float radialPad = desiredRadius;
        float radiusForArc = radius + radialPad;

        float arcOuter = TWOPI * max(radiusForArc, 1e-6) / cnt;
        float arcInner = TWOPI * max(radiusForArc - uCellSize, 1e-6) / cnt;

        arcOuter *= 0.98;
        arcInner *= 0.98;

        float halfOuter = max(0.5, 0.5 * arcOuter);
        float halfInner = clamp(0.5 * arcInner, 0.0, halfOuter);
        float halfWidth = mix(halfInner, halfOuter, t);

        float xLocal = (aLocalPos.x - 0.5) * 2.0 * halfWidth;
        float yLocal = (aLocalPos.y - 0.5) * uCellSize + radialPad;
        float ca = cos(angle);
        float sa = sin(angle);
        vec2 worldPos = aInstance.xy + vec2(ca * xLocal - sa * yLocal, sa * xLocal + ca * yLocal);

        vRegionUv = aLocalPos * vec2(1.0, 1.0);
        vRegionNorm = aLocalPos;
        vWorldPos = worldPos;
        vInstanceSize = vec2(1.0, 1.0);
        gl_Position = uViewProj * vec4(worldPos.xy, 0.0, 1.0);
    }";

	public const string HighlightFragmentRect = @"#version 450 core

in vec2 vRegionUv;
in vec2 vRegionNorm;
in vec2 vWorldPos;
in vec2 vInstanceSize;
out vec4 fragColor;

uniform float uTime;
uniform float uPixelsPerUnit;
uniform float uBorderThicknessPx;
uniform float uCellSize;
uniform float uDotFrequency;
uniform vec3 uColorA;
uniform vec3 uColorB;
uniform float uAlpha;

void main()
{
    float borderWorld = uBorderThicknessPx / max(uPixelsPerUnit, 0.01);
    vec2 regionWorld = vInstanceSize * uCellSize;
    float bn = min(borderWorld / max(regionWorld.x, 1e-6), borderWorld / max(regionWorld.y, 1e-6));
    bn = clamp(bn, 0.001, 0.5);
    float dLeft = vRegionNorm.x;
    float dRight = 1.0 - vRegionNorm.x;
    float dDown = vRegionNorm.y;
    float dUp = 1.0 - vRegionNorm.y;
    float distToEdge = min(min(dLeft, dRight), min(dDown, dUp));
    if (distToEdge > bn) discard;
    float along;
    float length;
    if (dLeft <= bn || dRight <= bn) { along = vRegionUv.y; length = vInstanceSize.y; } else { along = vRegionUv.x; length = vInstanceSize.x; }
    float p = along * uDotFrequency;
    float phase = fract(p + uTime * 4.0);
    float choice = step(0.5, phase);
    vec3 col = mix(uColorA, uColorB, choice);
    fragColor = vec4(col, uAlpha);
}";

	public const string HighlightFragmentHex = @"#version 450 core

in vec2 vRegionUv;
in vec2 vRegionNorm;
in vec2 vWorldPos;
in vec2 vInstanceSize;
out vec4 fragColor;

uniform float uTime;
uniform float uPixelsPerUnit;
uniform float uBorderThicknessPx;
uniform float uCellSize;
uniform float uDotFrequency;
uniform vec3 uColorA;
uniform vec3 uColorB;
uniform float uAlpha;
uniform int uUseRect;

void main()
{
    float borderWorld = uBorderThicknessPx / max(uPixelsPerUnit, 0.01);
    vec2 regionWorld = vInstanceSize * uCellSize;
    float bn = min(borderWorld / max(regionWorld.x, 1e-6), borderWorld / max(regionWorld.y, 1e-6));
    bn = clamp(bn, 0.001, 0.5);

    // If caller requested a rectangular highlight (zone select), use simple rect logic.
    if (uUseRect == 1) {
        float dLeft = vRegionNorm.x;
        float dRight = 1.0 - vRegionNorm.x;
        float dDown = vRegionNorm.y;
        float dUp = 1.0 - vRegionNorm.y;
        float distToEdge = min(min(dLeft, dRight), min(dDown, dUp));
        if (distToEdge > bn) discard;
        float along;
        float length;
        if (dLeft <= bn || dRight <= bn) { along = vRegionUv.y; length = vInstanceSize.y; } else { along = vRegionUv.x; length = vInstanceSize.x; }
        float pval = along * uDotFrequency;
        float phase = fract(pval + uTime * 4.0);
        float choice = step(0.5, phase);
        vec3 col = mix(uColorA, uColorB, choice);
        fragColor = vec4(col, uAlpha);
        return;
    }

    // Hex highlight: match GridFragmentHex's scaling/math so the highlight matches hex shape.
    float hexH = uCellSize * 0.865;
    float aspect = hexH / uCellSize;
    vec2 p = vRegionNorm - vec2(0.5);
    // Apply the same vertical scaling used by the grid shader
    p.y *= aspect;
    p = abs(p);
    float k = 0.57735026919;
    float edgeDist = 0.5 - (p.x + p.y * k);
    if (edgeDist < -bn) discard;
    if (edgeDist > bn) discard;
    float along = vRegionUv.x * (vInstanceSize.x);
    float pval = along * uDotFrequency;
    float phase = fract(pval + uTime * 4.0);
    float choice = step(0.5, phase);
    vec3 col = mix(uColorA, uColorB, choice);
    fragColor = vec4(col, uAlpha);
}";
}
