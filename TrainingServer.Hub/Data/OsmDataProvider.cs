using OsmSharp;
using OsmSharp.Complete;
using OsmSharp.Streams;

using System.Collections.Concurrent;
using System.Collections.Immutable;

using TrainingServer.Networking;

namespace TrainingServer.Hub.Data;

public class OsmDataProvider
{
	public OsmDataProvider() => Task.Run(LoadGeos);

	public readonly ConcurrentDictionary<long, NetworkNode> Nodes = new();
	public readonly ConcurrentDictionary<long, NetworkWay> Ways = new();
	public readonly ConcurrentDictionary<long, NetworkRelation> Relations = new();

	private void LoadGeos()
	{
		using PBFOsmStreamSource osmStream = new(File.OpenRead(@"C:\Users\westo\Downloads\aw.osm.pbf"));
		Parallel.ForEach(osmStream.ToComplete(), i => _ = Create(i));
		Console.WriteLine($"{Nodes.Count} nodes, {Ways.Count} ways, and {Relations.Count} relations loaded.");
	}

	private long Create(Node n)
	{
		long id = n.Id ?? throw new ArgumentException();

		if (!Nodes.ContainsKey(id))
			Nodes[id] = new(id,
				(float)(n.Latitude ?? 0), (float)(n.Longitude ?? 0),
				n.Tags.Count == 0 ? null : n.Tags.Select(t => new KeyValuePair<string, string>(t.Key, t.Value)).ToDictionary()
			);

		return id;
	}

	private long Create(CompleteWay w)
	{
		if (Ways.ContainsKey(w.Id))
			return w.Id;

		List<long> subNodes = [];
		foreach (Node n in w.Nodes)
		{
			Create(n);
			subNodes.Add(n.Id ?? throw new ArgumentException());
		}

		Ways[w.Id] = new(w.Id,
			[..subNodes],
			w.Tags.Count == 0 ? null : w.Tags.Select(t => new KeyValuePair<string, string>(t.Key, t.Value)).ToDictionary()
		);

		return w.Id;
	}

	private long Create(CompleteRelation r)
	{
		if (Relations.ContainsKey(r.Id))
			return r.Id;

		Relations[r.Id] = new(r.Id,
			[..r.Members.Select(m =>
				new NetworkRelationMember(
					m.Member switch { Node => 0, CompleteWay => 1, CompleteRelation => 2, _ => throw new NotImplementedException() },
					Create(m.Member),
					m.Role
				))],
			r.Tags.Count == 0 ? null : r.Tags.Select(t => new KeyValuePair<string, string>(t.Key, t.Value)).ToDictionary()
		);

		return r.Id;
	}

	private long Create(ICompleteOsmGeo g) => g switch {
		Node n => Create(n),
		CompleteWay w => Create(w),
		CompleteRelation r => Create(r),
		_ => throw new NotImplementedException()
	};
}
