using System;

namespace Biome2.Simulation.Models;

/// <summary>
/// Simulation-side reactant representation with resolved indices ready for fast checks.
/// </summary>
public sealed class SimulationReactantModel(
	int speciesIndex,
	int layerIndex,
	int count,
	int sign
) {
	public int SpeciesIndex { get; } = speciesIndex;
	public int LayerIndex { get; } = layerIndex;
	public int Count { get; } = count;
	public int Sign { get; } = sign;

	public bool Check(ReadOnlySpan<byte> neighbors) {
        if (SpeciesIndex < 0) return false;
        int speciesCount = 0;
        for (int i = 0; i < neighbors.Length; i++) {
            if (neighbors[i] == SpeciesIndex) speciesCount++;
        }
        if (Sign == 1) return speciesCount >= Count;
        if (Sign == -1) return speciesCount <= Count;
        return speciesCount == Count;
    }
}
