using System;
using System.IO;
using System.Collections.Generic;
using Biome2.Diagnostics;
using Biome2.FileLoading.Models;

namespace Biome2.FileLoading;

/// <summary>
/// Placeholder for loading a rules file.
/// The UI will call into this service later (open file dialog, recent files, drag drop).
/// </summary>
public sealed class RulesLoader {
    public static WorldModel Load(string path) {
		var fileName = Path.GetFileName(path);
		Logger.Info($"===> Loading rules from file: {fileName} <===");

		var lines = File.ReadAllLines(path);

		// Track current scanning position for improved error reporting.
		int _currentLineNo = 0;
		string _currentRawLine = string.Empty;

		int GetPosition(string? substring) {
			if (string.IsNullOrEmpty(_currentRawLine) || string.IsNullOrEmpty(substring))
				return 1;
			int idx = _currentRawLine.IndexOf(substring, StringComparison.Ordinal);
			return Math.Max(1, idx + 1);
		}

		void LogLineParseError(string reason, string? substringForPos = null) {
			var lineInfo = string.IsNullOrEmpty(_currentRawLine) ? string.Empty : $"Line=\"{_currentRawLine}\"";
			int pos = GetPosition(substringForPos);
			Logger.Error($"Parse error, line #{_currentLineNo}, pos {pos}: {reason}. {lineInfo}");
		}

		void LogLineParseWarning(string reason, string? substringForPos = null) {
			var lineInfo = string.IsNullOrEmpty(_currentRawLine) ? string.Empty : $"Line=\"{_currentRawLine}\"";
			int pos = GetPosition(substringForPos);
			Logger.Warn($"Parse warning, line #{_currentLineNo}, pos {pos}: {reason}. {lineInfo}");
		}

		void LogLineParseInfo(string message, string? substringForPos = null) {
			var lineInfo = string.IsNullOrEmpty(_currentRawLine) ? string.Empty : $"Line=\"{_currentRawLine}\"";
			int pos = GetPosition(substringForPos);
			Logger.Info($"Line #{_currentLineNo}, pos {pos}: {message}. {lineInfo}");
		}

		var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var species = new List<SpeciesModel>();
		var layers = new List<string>();
		var rules = new List<RulesModel>();

		int section = 0; // 0=settings,1=species,2=layers,3=rules

		for (int i = 0; i < lines.Length; i++) {
			_currentLineNo =  i + 1;
			_currentRawLine = (string?) lines[i] ?? string.Empty;
			var line = _currentRawLine;

			// strip comments
			var semi = line.IndexOf(';');
			if (semi >= 0)
				line = line[..semi];

			var readLine = line.Trim();

			if (readLine.Length == 0)
				continue;
			if (readLine == "%%") {
				section++;
				Logger.Info($"===> Now parsing {"settings,species,layers,rules".Split(',')[Math.Min(section, 3)]} section. <===");
				continue;
			}

			if (section == 0) {
				// settings like "WIDTH = 150"
				var eq = readLine.IndexOf('=');
				if (eq > 0) {
					var k = readLine[..eq].Trim();
					var v = readLine[(eq + 1)..].Trim();
					settings[k] = v;
				} else {
					LogLineParseError("Invalid setting line (missing '=')", readLine);
				}

			} else if (section == 1) {
				// species lines like "NAME = {r,g,b}"
				var eq = readLine.IndexOf('=');

				if (eq <= 0) {
					LogLineParseError("Invalid species line (missing '=')", readLine);
					continue;
				}

				var name = readLine[..eq].Trim();
				var definition = readLine[(eq + 1)..].Trim();

				// find braces
				var l = definition.IndexOf('{');
				var r = definition.IndexOf('}');

				if (l >= 0 && r > l) {
					var content = definition.Substring(l + 1, r - l - 1);
					var parts = content.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
					byte[] colors = [0, 0, 0, 255];
					for (int j = 0; j < Math.Min(parts.Length, 4); j++) {
						if (byte.TryParse(parts[j], out var b))
							colors[j] = b;
						else {
							LogLineParseError($"Invalid color component '{parts[j]}'", parts[j]);
						}
					}
					species.Add(new SpeciesModel(name, colors, _currentRawLine));

				} else {
					LogLineParseError("Invalid species definition (missing '{' or '}')", definition);
				}

			} else if (section == 2) {
				// layers: currently expect lines like "DISCRETE NAME"
				var parts = readLine.Split((char[]) null, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

				if (parts.Length >= 2 && string.Equals(parts[0], "DISCRETE", StringComparison.OrdinalIgnoreCase)) {
					layers.Add(parts[1]);
				} else {
					LogLineParseError("Invalid layer definition (expected 'DISCRETE NAME')", readLine);
				}

			} else {
				// rules section. two possible forms:
				// - standard: layer:origin [reactants] -> NEW*prob
				// - move: layer:origin [reactants including a single-count mover] >> NEW*prob
				// Example move: LEVEL:FIELD + 1RED >> RED*0.125
				int moveOp = readLine.IndexOf(">>", StringComparison.Ordinal);
				bool isMoveLine = moveOp >= 0;
				string left;
				string right;

				if (isMoveLine) {
					left = readLine[..moveOp].Trim();
					right = readLine[(moveOp + 2)..].Trim();

				} else {
					var arrow = readLine.IndexOf("->", StringComparison.Ordinal);

					if (arrow <= 0) {
						LogLineParseError("Invalid rule (missing '->' or '>>')", readLine);
						continue;
					}

					left = readLine[..arrow].Trim();
					right = readLine[(arrow + 2)..].Trim();
				}

				// left: optional coordinate limits then layer:origin [reactants]
				// Coordinate limits may be parenthesized like "(0:60,40:90)" or as a simple prefix "0:30"
				int? xMin = null, xMax = null, yMin = null, yMax = null;

				// Helper to parse a single axis spec like "0:60" or ":30" or "40:"
				static bool TryParseAxis(string spec, out int? aMin, out int? aMax) {
					aMin = null;
					aMax = null;
					if (string.IsNullOrWhiteSpace(spec))
						return true;
					var parts = spec.Split(':', 2);
					if (parts.Length != 2)
						return false;
					var smin = parts[0].Trim();
					var smax = parts[1].Trim();
					if (smin.Length > 0) {
						if (int.TryParse(smin, out var vmin))
							aMin = vmin;
						else
							return false;
					}
					if (smax.Length > 0) {
						if (int.TryParse(smax, out var vmax))
							aMax = vmax;
						else
							return false;
					}
					return true;
				}

				// Detect parenthesized coords at start
				var leftTrim = left.TrimStart();
				if (leftTrim.StartsWith("(")) {
					int end = leftTrim.IndexOf(')');
					if (end > 0) {
						var coords = leftTrim.Substring(1, end - 1).Trim();
						left = leftTrim.Substring(end + 1).TrimStart();
						// split into x,y by comma
						var parts = coords.Split(',', 2);
						if (parts.Length >= 1) {
							var xspec = parts[0].Trim();
							if (!string.IsNullOrEmpty(xspec)) {
								if (!TryParseAxis(xspec, out xMin, out xMax)) {
									LogLineParseError($"Invalid coordinate spec '{xspec}'", xspec);
									xMin = xMax = null;
								}
							}
						}
						if (parts.Length == 2) {
							var yspec = parts[1].Trim();
							if (!string.IsNullOrEmpty(yspec)) {
								if (!TryParseAxis(yspec, out yMin, out yMax)) {
									LogLineParseError($"Invalid coordinate spec '{yspec}'", yspec);
									yMin = yMax = null;
								}
							}
						}
					}
				} else {
					// Check for simple prefix token before first space
					var firstSpace = left.IndexOf(' ');
					string firstTok = firstSpace >= 0 ? left[..firstSpace] : left;
					// Heuristic: treat as coord token when it contains ':' and has no letters
					bool hasColon = firstTok.Contains(':');
					bool hasLetter = false;
					foreach (var ch in firstTok)
						if (char.IsLetter(ch)) { hasLetter = true; break; }
					if (hasColon && !hasLetter) {
						// consume token
						left = (firstSpace >= 0) ? left.Substring(firstTok.Length).TrimStart() : string.Empty;
						// token may contain comma separating x and y
						var parts = firstTok.Split(',', 2);
						if (parts.Length >= 1) {
							if (!TryParseAxis(parts[0].Trim(), out xMin, out xMax)) {
								LogLineParseError($"Invalid coordinate spec '{parts[0]}'", parts[0]);
								xMin = xMax = null;
							}
						}
						if (parts.Length == 2) {
							if (!TryParseAxis(parts[1].Trim(), out yMin, out yMax)) {
								LogLineParseError($"Invalid coordinate spec '{parts[1]}'", parts[1]);
								yMin = yMax = null;
							}
						}
					}
				}

				// Detect optional move operator '>>' on the left side (between origin/reactants and right side)
				// Format: DEST [reactants] >> MOVERSPEC*prob -> SRCRESULT
				// Parse robustly by tokenizing so '>>' is not accidentally treated as a reactant token.
				var moveSpeciesName = string.Empty;

				var leftTokens = left.Split((char[]) null, StringSplitOptions.RemoveEmptyEntries);
				int moveTokIndex = -1;
				for (int ti = 0; ti < leftTokens.Length; ti++) {
					var t = leftTokens[ti];
					if (t == ">>") { moveTokIndex = ti; break; }
					int p = t.IndexOf(">>", StringComparison.Ordinal);
					if (p >= 0) { moveTokIndex = ti; break; }
				}

				if (moveTokIndex >= 0) {
					// Build destPart from tokens before the operator and movePart from the remainder
					string destPart;
					string movePart;
					var opTok = leftTokens[moveTokIndex];
					if (opTok == ">>") {
						destPart = string.Join(' ', leftTokens, 0, moveTokIndex).Trim();
						movePart = string.Join(' ', leftTokens, moveTokIndex + 1, leftTokens.Length - (moveTokIndex + 1)).Trim();
					} else {
						// operator is embedded in token like "1RED>>RED*0.1"
						var tok = opTok;
						var p = tok.IndexOf(">>", StringComparison.Ordinal);
						var before = tok.Substring(0, p);
						var after = tok.Substring(p + 2);
						var beforeList = new List<string>();
						if (moveTokIndex > 0)
							beforeList.AddRange(leftTokens[..moveTokIndex]);
						if (!string.IsNullOrEmpty(before))
							beforeList.Add(before);
						destPart = string.Join(' ', beforeList).Trim();

						var afterList = new List<string>();
						if (!string.IsNullOrEmpty(after))
							afterList.Add(after);
						if (moveTokIndex + 1 < leftTokens.Length)
							afterList.AddRange(leftTokens[(moveTokIndex + 1)..]);
						movePart = string.Join(' ', afterList).Trim();
					}

					left = destPart;

					// movePart may include a mover species and optional '*prob' suffix
					var starIdx = movePart.IndexOf('*');
					if (starIdx >= 0) {
						moveSpeciesName = movePart[..starIdx].Trim();
						// probability parsing will be handled from the right side (rightSide -> prob)
					} else {
						moveSpeciesName = movePart;
					}
				}

				// left: layer:origin [reactants]
				var colon = left.IndexOf(':');
				string layerName;
				string remainder;
				if (colon > 0) {
					layerName = left[..colon].Trim();
					remainder = left[(colon + 1)..].Trim();

				} else {
					// No explicit layer provided. Use the first defined layer if available.
					remainder = left;
					if (layers.Count > 0)
						layerName = layers[0];
					else {
						LogLineParseError("No layer specified and no layers were defined. Rule will not apply", left);
						layerName = String.Empty;
					}
				}

				// origin species is first token until whitespace or operator
				var originParts = remainder.Split((char[]) null, 2, StringSplitOptions.TrimEntries);
				if (originParts.Length == 0 || string.IsNullOrEmpty(originParts[0])) {
					LogLineParseError("Missing origin species", remainder);
					continue;
				}
				var originSpecies = originParts[0];

				var reactants = Array.Empty<ReactantModel>();
				if (originParts.Length > 1) {
					// parse reactants string like "+ 1FIRE2+"
					var reactStr = originParts[1].Trim();
					var list = new List<ReactantModel>();

					// reactants are separated by whitespace (space, tabs, etc.)
					var tokens = reactStr.Split((char[]) null, StringSplitOptions.RemoveEmptyEntries);
					// e.g. +, -, 1FIRE2+ or SIMULATOR:COLOR1
					bool pendingExclusion = false;
					foreach (var tok in tokens) {
						if (tok == "+") { pendingExclusion = false; continue; }
						if (tok == "-") { pendingExclusion = true; continue; }

						// determine sign from trailing + or - (this is different from a leading '-' token)
						int sign = 0;
						if (tok.EndsWith('+'))
							sign = 1;
						else if (tok.EndsWith('-'))
							sign = -1;

						var core = tok.TrimEnd('+', '-');

						// core might start with a number (count) or be a layer-prefixed name
						int idx = 0;
						while (idx < core.Length && char.IsDigit(core[idx]))
							idx++;

						int count = 1; // default of 1 is applied if no explicit count is provided
						if (idx > 0) {
							if (!int.TryParse(core.AsSpan(0, idx), out count)) {
								LogLineParseError($"Invalid reactant count '{core[..idx]}'", core[..idx]);
							}
							// validate count
							if (count < 0) {
								LogLineParseError($"Reactant count must be positive, got '{count}'", core[..idx]);
							}
						} else {
							LogLineParseInfo($"No explicit count found for reactant '{tok}'. Defaulting to 1");
						}

						// species part may include an explicit layer like LAYER:SPECIES
						var speciesPart = core[idx..];
						string reactLayer = string.Empty;
						string speciesName = speciesPart;
						var colonPos = speciesPart.IndexOf(':');
						if (colonPos >= 0) {
							reactLayer = speciesPart[..colonPos];
							speciesName = speciesPart[(colonPos + 1)..];
						}

						list.Add(new ReactantModel(speciesName, reactLayer, count, sign, pendingExclusion));
						pendingExclusion = false;
					}

					// Alert & merge compatible reactants of the same species and layer to simplify simulation checks.
					// Group by (layer, species)
					var groups = new Dictionary<(string layer, string species), List<ReactantModel>>();
					foreach (var r in list) {
						var key = (layer: r.LayerName ?? string.Empty, species: r.SpeciesName ?? string.Empty);
						if (!groups.TryGetValue(key, out var g)) { g = new List<ReactantModel>(); groups[key] = g; }
						g.Add(r);
					}

					var merged = new List<ReactantModel>();
					foreach (var kv in groups) {
						var entry = kv.Key;
						var group = kv.Value;

						if (group.Count == 1) { merged.Add(group[0]); continue; }

						// Check for exclusion conflicts: if any exclusion and others present, warn and keep originals
						bool hasExclusion = group.Exists(r => r.Exclusion);
						if (hasExclusion) {
							Logger.Warn($"MERGE WARNING: {_currentRawLine}\t\t - conflicting exclusion reactants for species '{entry.species}' in layer '{entry.layer}'; entries not merged.");
							merged.AddRange(group);
							continue;
						}

						// Check for conflicting non-zero signs (+ vs -)
						bool hasPlus = group.Exists(r => r.Sign > 0);
						bool hasMinus = group.Exists(r => r.Sign < 0);
						if (hasPlus && hasMinus) {
							Logger.Warn($"MERGE WARNING: {_currentRawLine}\t\t - conflicting reactant signs for species '{entry.species}' in layer '{entry.layer}' (both '+' and '-') ; entries not merged.");
							merged.AddRange(group);
							continue;
						}

						// Determine resulting sign: prefer any non-zero sign; neutral (0) merges into the non-zero sign if present
						int resultSign = 0;
						foreach (var r in group) { if (r.Sign != 0) { resultSign = r.Sign; break; } }

						// Sum counts
						int totalCount = 0;
						foreach (var r in group)
							totalCount += r.Count;

						merged.Add(new ReactantModel(entry.species, entry.layer, totalCount, resultSign, false));

						Logger.Info($"Merged {group.Count} reactants for species '{entry.species}' in layer '{entry.layer}' into '{totalCount}{(resultSign>0 ? "+" : resultSign<0 ? "-" : "")}' for rule: {_currentRawLine}. Cleanup is possible.");
					}

					reactants = merged.ToArray();
				}
				var rightSide = right;
				// right side: newSpecies * probability (standard replacement behavior)
				var probability = 1.0; // default probability is 1.0 (certain) if not specified by '*(probability)' suffix
				var newSpec = rightSide;
				var star = rightSide.IndexOf('*');
				if (star >= 0) {
					newSpec = rightSide[..star].Trim();
					var probStr = rightSide[(star + 1)..].Trim();
					if (!double.TryParse(probStr, out probability)) {
						LogLineParseError($"Invalid probability '{probStr}'. Using 0.0", probStr);
						probability = 0.0;
					}
				}

				// If this was a move-style line (uses '>>'), the move species must be provided
				// as a single-count reactant on the left. Extract and remove it from reactants
				// and treat the parsed probability as the move probability. The backfill
				// (source result) will not have its own probability.
				if (isMoveLine) {
					string found = string.Empty;
					// Prefer the last single-count non-exclusion reactant when multiple are present
					int foundIndex = -1;
					for (int ri = reactants.Length - 1; ri >= 0; ri--) {
						var r = reactants[ri];
						if (!string.IsNullOrEmpty(r.SpeciesName) && r.Count == 1 && !r.Exclusion) {
							found = r.SpeciesName;
							foundIndex = ri;
							break;
						}
					}
					if (foundIndex >= 0) {
						var tmp = new List<ReactantModel>(reactants);
						tmp.RemoveAt(foundIndex);
						reactants = tmp.ToArray();
					}
					if (string.IsNullOrEmpty(found)) {
						LogLineParseError($"Invalid move rule (expected a single-count mover reactant before '>>')", left);
					} else {
						moveSpeciesName = found;
						// For move-style rules, `prob` parsed from the right side is the move probability.
						// The backfill species (newSpec) does not have a probability.
					}
				}

				rules.Add(new RulesModel(
					layerName,
					originSpecies,
					reactants,
					newSpec,
					probability,
					_currentRawLine,
					xMin,
					xMax,
					yMin,
					yMax,
					moveSpeciesName
				));
			}
		}

		// Validate parsed rules against parsed species and layers. Validator will log warnings and
		// return only those parsed rules that reference known species/layers.
		// Build final RulesModel list from validated parsed rules
		rules = RulesValidator.ValidateRules(rules, species, layers);

		var world = RulesValidator.ValidateWorld(settings, species, layers, rules);

		Logger.Info("===> Finished loading rules file. <===");
		
		return world;
	}
}
