using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Biome2.Graphics;
internal static class Shaders {
	public const string GridVertex = @"
#version 450 core

layout(location = 0) in vec2 aLocalPos;
layout(location = 1) in vec2 aInstancePos;

uniform mat4 uViewProj;
uniform float uCellSize;
uniform vec2 uGridSize;

out vec2 vCellUv;
flat out ivec2 vCellCoord;

void main()
{
    // Local quad scaled to cell size.
    vec2 worldPos = aInstancePos + (aLocalPos * uCellSize);

    // vCellUv is used to draw border lines in fragment shader.
    vCellUv = aLocalPos;
    // Compute integer cell coordinate and pass to fragment shader without interpolation.
    ivec2 cell = ivec2(floor(aInstancePos / uCellSize + vec2(0.5)));
    vCellCoord = cell;

    gl_Position = uViewProj * vec4(worldPos.xy, 0.0, 1.0);
}";

	public const string GridFragment = @"
#version 450 core

in vec2 vCellUv;
flat in ivec2 vCellCoord;
out vec4 fragColor;

uniform int uShowGrid;
uniform float uPixelsPerUnit;
uniform float uGridThicknessPx;
uniform float uCellSize;
uniform sampler2D uCellIndices; // R8 texture containing species indices per cell (normalized)
uniform sampler2D uPalette;     // 1xN RGBA palette texture
uniform int uSpeciesCount;

void main()
{
    // Fetch the per-cell species index from the index texture. The index texture
    // is stored as R8 (normalized) so multiply back to 0..255 and clamp to species count.
    ivec2 coord = vCellCoord;
    float idxNorm = texelFetch(uCellIndices, coord, 0).r;
    int idx = int(idxNorm * 255.0 + 0.5);
    if (uSpeciesCount > 0) idx = clamp(idx, 0, uSpeciesCount - 1);

    // Lookup color in the palette (palette is a 1xN texture, sample by texelFetch for exact lookup).
    ivec2 pcoord = ivec2(idx, 0);
    vec4 texColor = texelFetch(uPalette, pcoord, 0);

    // If grid is disabled, output the texel color directly so adjacent
    // cells with the same color render contiguously (no seams).
    if (uShowGrid == 0) {
        fragColor = texColor;
        return;
    }

    // Otherwise compute an inset and anti-aliased interior mask to show grid gaps.
    float worldThickness = uGridThicknessPx / max(uPixelsPerUnit, 0.01);
    float inset = worldThickness / (2.0 * max(uCellSize, 0.0001));
    inset = clamp(inset, 0.0, 0.1);

    // Anti-aliasing fade expressed in UV coordinates (approx. half a screen
    // pixel converted into cell-local UV space).
    float uvPixel = (1.0 / max(uPixelsPerUnit, 0.0001)) / max(uCellSize, 0.0005);
    float fade = clamp(uvPixel * 0.5, 1e-6, 0.5);

    float left = smoothstep(inset - fade, inset + fade, vCellUv.x);
    float right = smoothstep(inset - fade, inset + fade, 1.0 - vCellUv.x);
    float down = smoothstep(inset - fade, inset + fade, vCellUv.y);
    float up = smoothstep(inset - fade, inset + fade, 1.0 - vCellUv.y);

    float interior = left * right * down * up;

    vec3 borderColor = vec3(0.0, 0.0, 0.0);
    vec3 color = mix(borderColor, texColor.rgb, interior);

    fragColor = vec4(color, texColor.a);
}
";

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

	public const string HighlightVertex = @"
#version 450 core

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

    // vRegionUv in cell units
    vRegionUv = aLocalPos * aInstanceSize;
    vRegionNorm = aLocalPos; // since aLocalPos goes 0..1 across region
    vWorldPos = worldPos;
    vInstanceSize = aInstanceSize;

    gl_Position = uViewProj * vec4(worldPos.xy, 0.0, 1.0);
}
";

	public const string HighlightFragment = @"
#version 450 core

in vec2 vRegionUv;
in vec2 vRegionNorm;
in vec2 vWorldPos;
in vec2 vInstanceSize;
out vec4 fragColor;

uniform float uTime; // seconds, for animation
uniform float uPixelsPerUnit;
uniform float uBorderThicknessPx; // thickness in pixels
uniform float uCellSize; // world units per cell
uniform float uDotFrequency; // dots per cell along border
uniform vec3 uColorA; // dot color A (e.g., black)
uniform vec3 uColorB; // dot color B (e.g., white)
uniform float uAlpha;

float aa(float x, float w) {
    // simple linear AA over w
    return clamp(x / w, 0.0, 1.0);
}

void main()
{
    // Border thickness in world units and normalized relative to instance size
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

    // Determine which edge we're on and compute along coordinate in cell units
    float along;
    float length;
    if (dLeft <= bn || dRight <= bn) {
        along = vRegionUv.y;
        length = vInstanceSize.y;
    } else {
        along = vRegionUv.x;
        length = vInstanceSize.x;
    }

    // Dotted pattern along edge; animate shift with time
    float p = along * uDotFrequency;
    float phase = fract(p + uTime * 4.0);
    float choice = step(0.5, phase);
    vec3 col = mix(uColorA, uColorB, choice);
    fragColor = vec4(col, uAlpha);
}
";
}
