using Biome2.Diagnostics;
using System;
using System.Linq;
using Biome2.Rules;
using OpenTK.Mathematics;

namespace Biome2.World;

/// <summary>
/// The root world container.
/// Keeps layers, dimensions, metadata, and hooks for history and statistics.
/// </summary>
public sealed class WorldModel {
	// World defaults for first launch.
	private const int DefaultWorldWidthCells = 256;
	private const int DefaultWorldHeightCells = 256;
	private const int DefaultWorldLayerCount = 1;

	public int WidthCells { get; }
	public int HeightCells { get; }
	public int LayerCount { get; }

	private readonly List<WorldLayer> _layers = new();
	public IReadOnlyList<WorldLayer> Layers => _layers;

	// Layer viewing can swap which layer is currently visible.
	public int ActiveLayerIndex { get; set; } = 0;

	public WorldLayer ActiveLayer => _layers[ActiveLayerIndex];

	// Species registry for this world. Each species index maps directly to the byte value
	// stored in CellGrid cells. The renderer will query this model for RGBA bytes.
	private List<SpeciesModel> _species = new();
	public IReadOnlyList<SpeciesModel> Species => _species;

    // Flattened RGBA8 palette (4 bytes per species) rebuilt when species list changes.
    private byte[] _speciesPalette = Array.Empty<byte>();

    // Event raised when the flattened RGBA8 palette changes. The payload is a
    // byte[] with length = (speciesCount * 4) in RGBA order. If no species are
    // defined, an empty array is provided.
    public event Action<byte[]>? SpeciesPaletteChanged;

	private WorldModel(int widthCells, int heightCells, int layerCount) {
		// bound checking
		var _widthCells = widthCells;
		var _heightCells = heightCells;
		var _layerCount = layerCount;

		if (widthCells <= 0) {
			Logger.Error("WidthCells must be positive.");
			_widthCells = 1;
		}
		if (heightCells <= 0) {
			Logger.Error("HeightCells must be positive.");
			_heightCells = 1;
		}
		if (layerCount <= 0) {
			Logger.Error("LayerCount must be positive.");
			_layerCount = 1;
		}

		WidthCells = _widthCells;
		HeightCells = _heightCells;
		LayerCount = _layerCount;

		CreateLayers();
	}

	private void CreateLayers() {
		for (int i = 0; i < LayerCount; i++) {
			_layers.Add(new WorldLayer($"Layer {i}", WidthCells, HeightCells));
		}
	}

	public static WorldModel CreateBlank() {
		return new WorldModel(
			DefaultWorldWidthCells,
			DefaultWorldHeightCells,
			DefaultWorldLayerCount
		);
	}

	/// <summary>
	/// Replace the species list for this world. The species index is the byte stored in CellGrid.
	/// Rebuilds an internal flattened RGBA8 palette for fast lookups by renderer.
	/// </summary>
	public void SetSpeciesList(IEnumerable<SpeciesModel> species) {
		if (species is null) throw new ArgumentNullException(nameof(species));
		_species = species.ToList();
		BuildSpeciesPalette();
	}

	/// <summary>
	/// Return a copy of the flattened RGBA8 palette. Caller owns the array.
	/// </summary>
	public byte[] GetSpeciesPalette() => (byte[])_speciesPalette.Clone();

	/// <summary>
	/// Get the display name for a species index; returns empty string if out of range.
	/// </summary>
	public string GetSpeciesName(int index) =>
		(index >= 0 && index < _species.Count) ? _species[index].Name : string.Empty;

	/// <summary>
	/// Find the index for a species name; returns -1 when not found.
	/// </summary>
	public int GetSpeciesIndex(string name) {
		if (string.IsNullOrEmpty(name)) return -1;
		for (int i = 0; i < _species.Count; i++) {
			if (string.Equals(_species[i].Name, name, StringComparison.Ordinal))
				return i;
		}
		return -1;
	}

	/// <summary>
	/// Returns a ReadOnlySpan of 4 bytes containing RGBA8 for the given cell byte value.
	/// If the species list is empty returns a fallback color. Out-of-range indices are clamped
	/// to the last species.
	/// </summary>
	public ReadOnlySpan<byte> GetSpeciesColorBytes(byte value) {
		if (_species.Count == 0) {
			Logger.Error("At least one species must be defined to get color bytes.");
			return new ReadOnlySpan<byte>();
		}

		int idx = value < _species.Count ? value : (_species.Count - 1);
		int off = idx * 4;
		return new ReadOnlySpan<byte>(_speciesPalette, off, 4);
	}

	private void BuildSpeciesPalette() {
		if (_species.Count == 0) {
			_speciesPalette = Array.Empty<byte>();
			SpeciesPaletteChanged?.Invoke(Array.Empty<byte>());
			return;
		}

		_speciesPalette = new byte[_species.Count * 4];
		for (int i = 0; i < _species.Count; i++) {
			var c = _species[i].Color;

			static byte Conv(float v) =>
				(byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

			int off = i * 4;
			_speciesPalette[off + 0] = Conv(c.R);
			_speciesPalette[off + 1] = Conv(c.G);
			_speciesPalette[off + 2] = Conv(c.B);
			_speciesPalette[off + 3] = Conv(c.A);
		}

		// Publish new flattened palette for subscribers (e.g., renderer).
		SpeciesPaletteChanged?.Invoke(_speciesPalette);
	}
}
