using Biome2.Diagnostics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Biome2.FileLoading.Models;

public class RulesModel {
    // Names are stored (layer and species names) so the loader can map to indices later
    public string LayerName { get; }
    public string OriginSpeciesName { get; }
    public ReactantModel[] Reactants { get; }
    public string NewSpeciesName { get; }
    public double Probability { get; }

    // Logging purposes
    public string VerboseRule = string.Empty; // verbose description of the rule, basically the pre-parsed line, possibly the line number as well in the file

    public RulesModel(
        string layerName,
        string originSpeciesName,
        ReactantModel[] reactants,
        string newSpeciesName,
        double probability,
        string verboseRule
    ) {
        LayerName = layerName ?? string.Empty;
        OriginSpeciesName = originSpeciesName ?? string.Empty;
        Reactants = reactants ?? [];
        NewSpeciesName = newSpeciesName ?? string.Empty;
        Probability = probability;
        VerboseRule = verboseRule ?? string.Empty;
    }

}
