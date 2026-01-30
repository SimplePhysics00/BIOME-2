using Biome2.Diagnostics;
using OpenTK.Mathematics;
using System;

namespace Biome2.FileLoading.Models;

public sealed class SpeciesModel {
	private string _name = string.Empty;
	public string Name => _name;

	// Store color as RGBA8 bytes (0-255) to match file and renderer formats.
	private byte[] _color = new byte[4];
    public ReadOnlySpan<byte> Color => _color;

	private string _speciesDefinition = string.Empty; // verbose species definition, basically the pre-parsed line, including any comments

	// future attribute system goes here

	// Construct from a span/array of 4 bytes (RGBA)
	public SpeciesModel(string name, ReadOnlySpan<byte> color, string definition) {
		_name = name;
		_speciesDefinition = definition;
		_color = new byte[4];
		
		switch (color.Length) {
			case > 4:
				Logger.Info($"Species \"{name}\" given color array >= 4, copying first 4 bytes.");
				color[..4].CopyTo(_color);

				break;
			case 3:
				Logger.Info($"Species \"{name}\" given color array == 3, assuming RGB and setting A=255.");
				color.CopyTo(_color);
				_color[3] = 255;

				break;
			case < 3:
				Logger.Warn($"Species \"{name}\" given color array < 3, defaulting to black");
				_color[0] = 0;
				_color[1] = 0;
				_color[2] = 0;
				_color[3] = 255;

				break;
			default:
				color.CopyTo(_color);
				break;
		}
	}
}
