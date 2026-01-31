using System.Collections.Generic;
using Biome2.Diagnostics;
using Biome2.FileLoading;

namespace Biome2.Simulation.Models;

/// <summary>
/// Simulation-side rule with resolved indices and ready-to-run reactants.
/// </summary>
public sealed class SimulationRuleModel(
	int layerIndex,
	int originSpeciesIndex,
	List<SimulationReactantModel> reactants,
	int newSpeciesIndex,
	double probability,
	string verboseRule
) {
	public int LayerIndex { get; } = layerIndex;
	public int OriginSpeciesIndex { get; } = originSpeciesIndex;
	public List<SimulationReactantModel> Reactants { get; } = reactants ?? [];
	public int NewSpeciesIndex { get; } = newSpeciesIndex;
	public double Probability { get; } = probability;

	private uint _opCount;
	public string VerboseRule { get; set; } = verboseRule;

	public void IncrementOpCount() => ++_opCount;

	public void ReportRuleDetails() {
		Logger.Info($"{VerboseRule}\t\t - operation count: {_opCount}");
	}
}
