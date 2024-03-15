using NetTopologySuite.IO;

using System.Collections.Immutable;

using TrainingServer.Extensibility;

using Route = TrainingServer.Extensibility.Route;

namespace TrainingServer.Hub.Data;

public class ManualDataProvider
{
	public ImmutableDictionary<char, ImmutableHashSet<Route>> Topologies { get; private set; } = ImmutableDictionary<char, ImmutableHashSet<Route>>.Empty;
	public string OsmGeoPath { get; private set; }
	public string BoundariesPath { get; private set; }

	public ManualDataProvider(IConfiguration config)
	{
		if (config["OsmPbf"] is not string pbfPath || !File.Exists(pbfPath))
			throw new ArgumentException("Invalid OSM PBF path.", nameof(config));

		if (config["BoundariesFile"] is not string boundariesPath || !File.Exists(boundariesPath))
			throw new ArgumentException("Invalid boundaries path.", nameof(config));

		if (config["Topologies"] is not string topoPath || !Directory.Exists(topoPath))
			throw new ArgumentException("Invalid topologies path.", nameof(config));

			OsmGeoPath = Path.GetFullPath(pbfPath);
		BoundariesPath = Path.GetFullPath(boundariesPath);
		LoadTopologies(topoPath);
	}

	private void LoadTopologies(string topoPath)
	{
		Dictionary<char, ImmutableHashSet<Route>> topos = [];

		foreach (string level in Directory.EnumerateFiles(topoPath, "*.shp"))
		{
			ShapefileReader reader = new(level);

			var geos = reader.ReadAll().Geometries;

			topos.Add(Path.GetFileNameWithoutExtension(level)[^1], [..geos.AsParallel().Select(g => new Route("COASTLINE", [.. g.Coordinates.Select(p => new Coordinate(p.Y, p.X))])).ToImmutableHashSet()]);
		}

		Topologies = topos.ToImmutableDictionary();
	}
}
