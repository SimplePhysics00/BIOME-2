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
    public WorldModel Load(string path) {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

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
            Logger.Error($"Parse error at line {_currentLineNo}, pos {pos}: {reason}. {lineInfo}");
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
                line = line.Substring(0, semi);

            var readLine = line.Trim();

            if (readLine.Length == 0)
                continue;
            if (readLine == "%%") { section++; continue; }

            if (section == 0) {
                // settings like "WIDTH = 150"
                var eq = readLine.IndexOf('=');
                if (eq > 0) {
                    var k = readLine.Substring(0, eq).Trim();
                    var v = readLine.Substring(eq + 1).Trim();
                    settings[k] = v;
                } else {
                    LogLineParseError("Invalid setting (missing '=')", readLine);
                }
            } else if (section == 1) {
                // species lines like "NAME = {r,g,b}"
                var eq = readLine.IndexOf('=');
                if (eq <= 0) {
                    LogLineParseError("Invalid species definition (missing '=')", readLine);
                    continue;
                }
                var name = readLine.Substring(0, eq).Trim();
                var rest = readLine.Substring(eq + 1).Trim();

                // find braces
                var l = rest.IndexOf('{');
                var r = rest.IndexOf('}');
                if (l >= 0 && r > l) {
                    var content = rest.Substring(l + 1, r - l - 1);
                    var parts = content.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    byte[] col = new byte[4] { 0, 0, 0, 255 };
                    for (int j = 0; j < Math.Min(parts.Length, 4); j++) {
                        if (byte.TryParse(parts[j], out var b))
                            col[j] = b;
                        else {
                            LogLineParseError($"Invalid color component '{parts[j]}'", parts[j]);
                        }
                    }
                    species.Add(new SpeciesModel(name, col, _currentRawLine));
                } else {
                    LogLineParseError("Invalid species definition (missing '{' or '}')", rest);
                }
            } else if (section == 2) {
                // layers: currently expect lines like "DISCRETE NAME"
                var parts = readLine.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && string.Equals(parts[0], "DISCRETE", StringComparison.OrdinalIgnoreCase)) {
                    layers.Add(parts[1]);
                } else {
                    LogLineParseError("Invalid layer definition (expected 'DISCRETE NAME')", readLine);
                }
            } else {
                // rules section. parse basic form layer:origin [reactants] -> new*prob
                // Example: FOREST:OLD + 1FIRE2+ -> FIRE1*0.1
                var arrow = readLine.IndexOf("->", StringComparison.Ordinal);
                if (arrow <= 0) { LogLineParseError("Invalid rule (missing '->')", readLine); continue; }
                var left = readLine.Substring(0, arrow).Trim();
                var right = readLine.Substring(arrow + 2).Trim();

                // left: layer:origin [reactants]
                var colon = left.IndexOf(':');
                string layerName;
                string remainder;
                if (colon > 0) {
                    layerName = left.Substring(0, colon).Trim();
                    remainder = left.Substring(colon + 1).Trim();

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
                var originParts = remainder.Split(' ', 2, StringSplitOptions.TrimEntries);
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

                    // reactants are separated by spaces
                    var tokens = reactStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    // e.g. +, 1FIRE2+ or SIMULATOR:COLOR1
                    foreach (var tok in tokens) {
                        if (tok == "+")
                            continue;

                        // determine sign from trailing + or -
                        int sign = 0;
                        if (tok.EndsWith("+")) sign = 1;
                        else if (tok.EndsWith("-")) sign = -1;

                        var core = tok.TrimEnd('+', '-');

                        // core might start with a number (count) or be a layer-prefixed name
                        int idx = 0;
                        while (idx < core.Length && char.IsDigit(core[idx])) idx++;

                        int count = 0; // default: no count specified
                        if (idx > 0) {
                            if (!int.TryParse(core.Substring(0, idx), out count)) {
                                LogLineParseError($"Invalid reactant count '{core.Substring(0, idx)}'", core.Substring(0, idx));
                                continue;
                            }
                            // validate count
                            if (count < 0) {
                                LogLineParseError($"Reactant count must be positive, got '{count}'", core.Substring(0, idx));
                                continue;
                            } else if (count > 8) {
                                LogLineParseError($"Reactant count too large for given RANGE, got '{count}'", core.Substring(0, idx)); // TODO: RANGE setting
                            }
                        }

                        // species part may include an explicit layer like LAYER:SPECIES
                        var speciesPart = core.Substring(idx);
                        string reactLayer = string.Empty;
                        string speciesName = speciesPart;
                        var cpos = speciesPart.IndexOf(':');
                        if (cpos >= 0) {
                            reactLayer = speciesPart.Substring(0, cpos);
                            speciesName = speciesPart.Substring(cpos + 1);
                        }

                        if (string.IsNullOrEmpty(speciesName)) {
                            LogLineParseError("Invalid reactant (missing species name)", core);
                            continue;
                        }

                        list.Add(new ReactantModel(speciesName, reactLayer, count, sign));
                    }
                    reactants = list.ToArray();
                }

                // right side: newSpecies * probability
                var prob = 1.0;
                var newSpec = right;
                var star = right.IndexOf('*');
                if (star >= 0) {
                    newSpec = right.Substring(0, star).Trim();
                    var probStr = right.Substring(star + 1).Trim();
                    if (!double.TryParse(probStr, out prob)) {
                        LogLineParseError($"Invalid probability '{probStr}'", probStr);
                        prob = 1.0;
                    }
                }

                rules.Add(new RulesModel(layerName, originSpecies, reactants, newSpec, prob, _currentRawLine));
            }
        }

        // apply settings
        int w = 0, h = 0;
        bool paused = false;
        var edgesMode = EdgeMode.BORDER;
        if (settings.TryGetValue("WIDTH", out var ws))
            int.TryParse(ws, out w);
        if (settings.TryGetValue("HEIGHT", out var hs))
            int.TryParse(hs, out h);
        if (settings.TryGetValue("PAUSE", out var ps))
            paused = ps != "0";
        if (settings.TryGetValue("EDGES", out var es)) {
            if (!Enum.TryParse<EdgeMode>(es.Trim(), true, out edgesMode)) {
                Logger.Warn($"Unknown EDGES value '{es}', defaulting to BORDER.");
            }
        }

        // produce final world file model with parsed data (file-loading WorldModel)
        var final = new WorldModel(
            width: w,
            height: h,
            paused: paused,
            species: species,
            layers: layers,
            rules: rules,
            edges: edgesMode
        );

        return final;
    }
}
