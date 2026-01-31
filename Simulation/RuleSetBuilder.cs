using Biome2.Diagnostics;
using Biome2.FileLoading.Models;
using Biome2.Simulation.Models;
using Biome2.World;
using System.Collections.Generic;

namespace Biome2.Simulation;

/// <summary>
/// Converts file-loading models into simulation-ready structures.
/// Returns warnings encountered during conversion.
/// </summary>
public static class RuleSetBuilder {
    public static (List<SimulationRuleModel> rules, Dictionary<(int layer, int origin), List<SimulationRuleModel>> index, HashSet<int> layersWithRules)
        Build(IReadOnlyList<RulesModel> fileRules, WorldState world)
    {
        var warnings = new List<string>();
        var simRules = new List<SimulationRuleModel>();
        var index = new Dictionary<(int layer, int origin), List<SimulationRuleModel>>();
        var layersWithRules = new HashSet<int>();

        if (fileRules == null) return (simRules, index, layersWithRules);

        for (int i = 0; i < fileRules.Count; i++) {
            var fr = fileRules[i];

            int layerIdx = world.GetLayerIndex(fr.LayerName);
            if (layerIdx < 0) { warnings.Add($"RULE WARNING: {fr.VerboseRule}\t\t - unknown layer '{fr.LayerName}'"); continue; }

            int originIdx = world.GetSpeciesIndex(fr.OriginSpeciesName);
            if (originIdx < 0) { warnings.Add($"RULE WARNING: {fr.VerboseRule}\t\t - unknown origin species '{fr.OriginSpeciesName}'"); continue; }

            int newIdx = world.GetSpeciesIndex(fr.NewSpeciesName);
            if (newIdx < 0) { warnings.Add($"RULE WARNING: {fr.VerboseRule}\t\t - unknown new species '{fr.NewSpeciesName}'"); continue; }

            if (newIdx == originIdx) {
                warnings.Add($"RULE WARNING: {fr.VerboseRule}\t\t - new species is the same as origin species '{fr.NewSpeciesName}'");
                continue;
            }

			var simReactants = new List<SimulationReactantModel>();
            foreach (var r in fr.Reactants) {
                int sidx = world.GetSpeciesIndex(r.SpeciesName);
                if (sidx < 0) { warnings.Add($"RULE WARNING: {fr.VerboseRule}\t\t - reactant unknown species '{r.SpeciesName}'"); continue; }

                int lidx;
                if (string.IsNullOrEmpty(r.LayerName)) {
                    // No explicit layer specified in the reactant: use -1 to indicate "use neighborhood on the rule's layer".
                    lidx = -1;
                } else {
                    lidx = world.GetLayerIndex(r.LayerName);
                    if (lidx < 0) { warnings.Add($"RULE WARNING: {fr.VerboseRule}\t\t - reactant unknown layer '{r.LayerName}'"); continue; }
                }

                if (r.Count == 0 && r.Sign < 0) {
                    warnings.Add($"RULE WARNING: {fr.VerboseRule}\t\t - reactant '{r.SpeciesName}' has zero count and negative sign");
                    continue;
                }

				simReactants.Add(new SimulationReactantModel(sidx, lidx, r.Count, r.Sign));
            }

            var sr = new SimulationRuleModel(layerIdx, originIdx, simReactants, newIdx, fr.Probability, fr.VerboseRule) {
                VerboseRule = fr.VerboseRule
            };
            simRules.Add(sr);

            var key = (layerIdx, originIdx);
            if (!index.TryGetValue(key, out var list)) { list = []; index[key] = list; }
            list.Add(sr);
            layersWithRules.Add(layerIdx);
        }

		foreach (var w in warnings)
			Logger.Warn(w);

		return (simRules, index, layersWithRules);
    }
}
