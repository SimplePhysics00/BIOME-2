using Biome2.Diagnostics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Biome2.FileLoading.Models;

public class RulesModel(
	string layerName,
	string originSpeciesName,
	ReactantModel[] reactants,
	string newSpeciesName,
	double probability,
	string verboseRule
) {
	// Names are stored (layer and species names) so the loader can map to indices later
	public string LayerName { get; } = layerName ?? string.Empty;
	public string OriginSpeciesName { get; } = originSpeciesName ?? string.Empty;
	public ReactantModel[] Reactants { get; } = reactants ?? [];
	public string NewSpeciesName { get; } = newSpeciesName ?? string.Empty;
	public double Probability { get; } = probability;

	// Logging purposes
	public string VerboseRule = verboseRule ?? string.Empty; // verbose description of the rule, basically the pre-parsed line, possibly the line number as well in the file

}
