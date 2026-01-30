using System;
using System.Collections.Generic;
using Biome2.Diagnostics;
using Biome2.FileLoading.Models;
using Biome2.World;

namespace Biome2.FileLoading;

public static class RulesValidator
{
    // Validate rules against the given world (species and layer names).
    // This validator only inspects names and returns warnings. It does not mutate
    // file models; resolution of names to indices is performed by the simulation builder.
    // Return list of warning messages encountered during validation.
    public static List<string> Validate(IReadOnlyList<RulesModel> rules, WorldState world)
    {
        var warnings = new List<string>();
        if (rules == null) {
            warnings.Add("There were no rules found!");
			return warnings;
		}
		ArgumentNullException.ThrowIfNull(world);

		for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];

            // Report unknown names; do not mutate file models.
            if (world.GetLayerIndex(rule.LayerName) < 0) warnings.Add($"Rule #{i + 1}: unknown layer '{rule.LayerName}'");
            if (world.GetSpeciesIndex(rule.OriginSpeciesName) < 0) warnings.Add($"Rule #{i + 1}: unknown origin species '{rule.OriginSpeciesName}'");
            if (world.GetSpeciesIndex(rule.NewSpeciesName) < 0) warnings.Add($"Rule #{i + 1}: unknown new species '{rule.NewSpeciesName}'");
            foreach (var react in rule.Reactants) {
                if (world.GetSpeciesIndex(react.SpeciesName) < 0) warnings.Add($"Rule #{i + 1}: reactant unknown species '{react.SpeciesName}'");
                if (!string.IsNullOrEmpty(react.LayerName) && world.GetLayerIndex(react.LayerName) < 0) warnings.Add($"Rule #{i + 1}: reactant unknown layer '{react.LayerName}'");
            }
        }

        return warnings;
    }
}
