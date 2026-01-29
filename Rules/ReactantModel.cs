using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Biome2.Rules;
internal class ReactantModel {
	private byte _species;
	private byte _layer; // layer where reactant is located. (NOTE: original BIOME did not support both Layer and Count modes simultaneously)
	private int _count; // number of cells of species in layer to check for.
	private int _sign; // +1 for greater than or equal to _count, -1 for less than or equal to _count
	
	public ReactantModel() {
		_species = 0;
		_layer = 0;
		_count = 0;
		_sign = 0;
	}

	public ReactantModel(byte species, byte layerName, int count, int sign) {
		_species = species;
		_layer = layerName;
		_count = count;
		_sign = sign;
	}

	public bool Check(byte[] neighbors) {
		int speciesCount = neighbors.Count(b => b == _species);
		if (_sign == 1) {
			return speciesCount >= _count;
		} else if (_sign == -1) {
			return speciesCount <= _count;
		} else {
			return speciesCount == _count;
		}
	}
}
