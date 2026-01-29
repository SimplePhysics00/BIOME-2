using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Biome2.Rules;

internal class RulesModel {
	private byte _layer; // layer where rule applies
	private byte _originSpecies; // species to perform rule on
	private ReactantModel[] _reactants; // optional, variable length: conditions to check for
	private byte _newSpecies; // species to change to
	private double _probability; // probability of rule occurring on a given tick (0.0 to 1.0)

	// Logging purposes
	private uint _ruleOperationCount; // count the number of times this rule has executed
	private string _verboseRule = string.Empty; // verbose description of the rule, basically the pre-parsed line, possibly the line number as well in the file

	public RulesModel(
		byte originSpecies,
		ReactantModel[] reactants,
		byte newSpecies,
		double probability,
		string verboseRule
	) {
		_originSpecies = originSpecies;
		_reactants = reactants;
		_newSpecies = newSpecies;
		_probability = probability;
		_verboseRule = verboseRule;
		_ruleOperationCount = 0;
	}
}
