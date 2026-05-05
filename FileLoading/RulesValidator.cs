using Biome2.Diagnostics;
using Biome2.FileLoading.Models;
using Biome2.World.CellGrid;

namespace Biome2.FileLoading;

/// <summary>
/// Validate a parsed WorldModel for topology-specific constraints.
/// </summary>
public static class RulesValidator
{
    // Validate parsed rules against the parsed species and layers (file-level names).
    // Returns a tuple of (valid rules, warnings). Invalid rules are filtered out and
    // warnings are returned so the caller may log them.
    public static List<RulesModel> ValidateRules(
        IReadOnlyList<RulesModel> parsedRules,
        IReadOnlyList<SpeciesModel> species,
        IReadOnlyList<string> layers
    ) {
        var valid = new List<RulesModel>();

        if (species == null || species.Count == 0) {
            Logger.Error("Rule Validation Failed: No species found in file.");
            return valid;
        }

		if (layers == null ||  layers.Count == 0) {
			Logger.Error("Rule Validation Failed: No layers found in file.");
			return valid;
		}

		if (parsedRules == null || parsedRules.Count == 0) {
            Logger.Error("Rule Validation Failed: No rules found in file.");
            return valid;
        }

		// Build lookup sets for names
		var speciesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in species) if (s != null && !string.IsNullOrEmpty(s.Name)) speciesSet.Add(s.Name);
        
        var layerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in layers) if (!string.IsNullOrEmpty(l)) layerSet.Add(l);

        for (int i = 0; i < parsedRules.Count; i++) {
            var r = parsedRules[i];
            bool ok = true;

            if (string.IsNullOrEmpty(r.LayerName) || !layerSet.Contains(r.LayerName)) {
                Logger.Error($"{r.VerboseRule}: unknown layer '{r.LayerName}'");
                ok = false;
            }
            if (string.IsNullOrEmpty(r.OriginSpeciesName) || !speciesSet.Contains(r.OriginSpeciesName)) {
                Logger.Error($"{r.VerboseRule}: unknown origin species '{r.OriginSpeciesName}'");
                ok = false;
            }
            if (string.IsNullOrEmpty(r.NewSpeciesName) || !speciesSet.Contains(r.NewSpeciesName)) {
                Logger.Error($"{r.VerboseRule}: unknown new species '{r.NewSpeciesName}'");
                ok = false;
            }

            foreach (var react in r.Reactants ?? System.Array.Empty<ReactantModel>()) {
				if (!string.IsNullOrEmpty(react.LayerName) && !layerSet.Contains(react.LayerName)) {
					Logger.Error($"{r.VerboseRule}: reactant contains unknown layer '{react.LayerName}'");
					ok = false;
				}
				if (string.IsNullOrEmpty(react.SpeciesName) || !speciesSet.Contains(react.SpeciesName)) {
					Logger.Error($"{r.VerboseRule}: reactant contains unknown species '{react.SpeciesName}'");
                    ok = false;
                }
				if (react.LayerName == r.LayerName) {
					Logger.Error($"{r.VerboseRule}: a reactant cannot reference the same layer as the rule's target layer ({r.LayerName}).");
					ok = false;
				}
				if (react.Exclusion || react.LayerName != string.Empty) {
					if (react.Count > 0 || react.Sign != 0) {
						Logger.Warn($"{r.VerboseRule}: reactant '{react.SpeciesName}' is either a layer-target or exclusionary reactant. It cannot have either a signed or exact count value.");
						ok = false;
					}
				} else {
					if (react.Count > 8) {
						Logger.Info($"{r.VerboseRule}: reactant '{react.SpeciesName}' has a count value of '{react.Count}'. A cell can usually have no more than 8 neighbors. Rule has NOT been ignored.");
					} else if (react.Count <= 0 && react.Sign < 0) {
						Logger.Info($"{r.VerboseRule}: reactant '{react.SpeciesName}' has a count value of '{react.Count}' and a sign of '-'. Suggest removing the '-' to clarify. Rule has NOT been ignored.");
					}
				}
			}
			if (r.OriginSpeciesName.Equals(r.NewSpeciesName, StringComparison.OrdinalIgnoreCase) && r.MoveSpeciesName == string.Empty) {
				Logger.Warn($"{r.VerboseRule}: origin and new species cannot be the same ('{r.OriginSpeciesName}').");
				ok = false;
			}
			if (r.Probability <= 0 || r.Probability > 1) {
				Logger.Warn($"{r.VerboseRule}: probability value of '{r.Probability}' is out of expected range (0,1].");
				ok = false;
			}

			if (ok) valid.Add(r);
        }

        return valid;
    }

	public static WorldModel ValidateWorld(Dictionary<string, string> settings, List<SpeciesModel> species, List<string> layers, List<RulesModel> rules) {
		var config = ValidateConfig(settings, rules.Count == 0);

		// produce final world file model with parsed data (file-loading WorldModel)
		var final = new WorldModel(
			config: config,
			species: species,
			layers: layers,
			rules: rules
		);

		return final;
	}

	private static WorldConfigModel ValidateConfig(Dictionary<string, string> settings, bool rulesMissing) {
		int w = 0, h = 0, d = 0;
		bool paused = false;
		var edgeMode = EdgeMode.BORDER;
		var gridTopology = GridTopology.RECT;

		if (settings.TryGetValue("WIDTH", out var ws))
			_=int.TryParse(ws, out w);

		if (settings.TryGetValue("HEIGHT", out var hs))
			_=int.TryParse(hs, out h);

		if (settings.TryGetValue("DEPTH", out var ds))
			_=int.TryParse(ds, out d);

		if (settings.TryGetValue("SHAPE", out var shape)) {
			if (!string.IsNullOrEmpty(shape) && string.Equals(shape.Trim(), "SPIRAL", StringComparison.OrdinalIgnoreCase)) {
				gridTopology = GridTopology.SPIRAL;
				Logger.Info("Using 'SPIRAL' grid topology as per SHAPE setting.");

			} else if (!string.IsNullOrEmpty(shape) && string.Equals(shape.Trim(), "HEX", StringComparison.OrdinalIgnoreCase)) {
				gridTopology = GridTopology.HEX;
				Logger.Info("Using 'HEX' grid topology as per SHAPE setting.");
			} else {
				Logger.Warn($"Unknown SHAPE value '{shape}', defaulting to Rectangular ('RECT'). Other valid entries are 'HEX' and 'SPIRAL'.");
				gridTopology = GridTopology.RECT;
			}
		}

		if (settings.TryGetValue("PAUSE", out var ps))
			paused = ps != "0";

		if (settings.TryGetValue("EDGES", out var es)) {
			if (!Enum.TryParse<EdgeMode>(es.Trim(), true, out edgeMode)) {
				Logger.Warn($"Unknown EDGES value '{es}', defaulting to BORDER.");
			}
		}

		if (rulesMissing) {
			paused = true;
			Logger.Warn("Caught error, see above; no rules passed from validator. Defaulting to PAUSE=1 to prevent simulation of nothing.");
		}

		// config constructor handles additional validation
		var config = new WorldConfigModel(
			width: w,
			height: h,
			depth: d,
			gridTopology: gridTopology,
			edgeMode: edgeMode,
			paused: paused
		);

		return config;
	}
}
