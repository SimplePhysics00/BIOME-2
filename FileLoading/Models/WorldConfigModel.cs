using Biome2.Diagnostics;
using Biome2.World.CellGrid;

namespace Biome2.FileLoading.Models;
public class WorldConfigModel {
	public int Width { get; init; }
	public int Height { get; init; }
	// Hex-specific parameter: third dimension (z-depth) for hex layouts.
	// Interpreted by world creation when GridType == Hexagonal.
	public int HexDepth { get; init; } = 0;

	// Topology: optional, defaults to rectangular for backward compatibility.
	public GridTopology GridTopology { get; init; } = GridTopology.RECT;

	public float InitSpeed { get; init; } = 1.0f;

	// Edge handling mode for neighbor queries
	public EdgeMode Edges { get; init; } = EdgeMode.BORDER;

	public bool Paused { get; init; }

	public WorldConfigModel(
		bool paused,
		int width,
		int height,
		int depth,
		float initSpeed,
		GridTopology gridTopology,
		EdgeMode edgeMode
	) {
		if (width <= 0) {
			Logger.Warn("WIDTH setting is missing or non-positive; defaulting to 1.");
			width = 1;
		}
		if (height <= 0) {
			Logger.Warn("HEIGHT setting is missing or non-positive; defaulting to 1.");
			height = 1;
		}

		if (gridTopology == GridTopology.HEX && depth <= 0) {
			Logger.Warn($"Hexagonal topology rules should specify a DEPTH setting; defaulting to {width}, the same value as WIDTH.");
			depth = width;
		}

		if (gridTopology == GridTopology.SPIRAL) {
			if (height < 3) {
				Logger.Warn("SPIRAL shape requires HEIGHT >= 3 to form a valid outer ring.");
				height = 3;
			}
		}

		if (initSpeed <= 0) {
			Logger.Warn("SPEED setting is non-positive; defaulting to 0.0.");
			initSpeed = 0.0f;
		} else if (initSpeed > 1.0f) {
			Logger.Warn("SPEED setting is invalid (too high); defaulting to 1.0.");
			initSpeed = 1.0f;
		}

		Width = width;
		Height = height;
		HexDepth = depth;
		GridTopology = gridTopology;
		InitSpeed = initSpeed;
		Edges = edgeMode;
		Paused = paused;
	}


}
