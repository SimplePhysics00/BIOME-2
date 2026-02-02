using static Biome2.World.CellGrid.GridTopologies;

namespace Biome2.FileLoading.Models;

/// <summary>
/// A safe transport object representing a loaded world definition from a rules file.
/// This carries parsed species, layer names, rules, and basic settings but does not
/// contain any resolved indexes. It is intended for file-loading only.
/// </summary>
public sealed class WorldModel {
    // Parsed settings
    public int Width { get; init; }
    public int Height { get; init; }
	// Hex-specific parameter: third dimension (z-depth) for hex layouts.
	// Interpreted by world creation when GridType == Hexagonal.
	public int HexDepth { get; init; } = 0;

	public bool Paused { get; init; }

	// Topology: optional, defaults to rectangular for backward compatibility.
	public GridTopology GridTopology { get; init; } = GridTopology.RECT;


	// Species definitions (name -> color)
	public IReadOnlyList<SpeciesModel> Species { get; init; } = [];

    // Layer names in order
    public IReadOnlyList<string> Layers { get; init; } = [];

    // Parsed rules
    public IReadOnlyList<RulesModel> Rules { get; init; } = [];
    
    // Edge handling mode for neighbor queries
    public EdgeMode Edges { get; init; } = EdgeMode.BORDER;

    public WorldModel(
        int width,
        int height,
        bool paused,
        IReadOnlyList<SpeciesModel> species,
        IReadOnlyList<string> layers,
        IReadOnlyList<RulesModel> rules,
        EdgeMode edges = EdgeMode.BORDER
    ) {
        Width = width;
        Height = height;
        Paused = paused;
        Species = species ?? [];
        Layers = layers ?? [];
        Rules = rules ?? [];
        Edges = edges;
    }
}
