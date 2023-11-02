using System.Text.Json.Serialization;

namespace TrainingServer.Networking;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "geo", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor)]
[JsonDerivedType(typeof(NetworkNode), 0)]
[JsonDerivedType(typeof(NetworkWay), 1)]
[JsonDerivedType(typeof(NetworkRelation), 2)]
public record NetworkGeo(long Id, Dictionary<string, string>? Tags) { }

[method: JsonConstructor()]
public record NetworkNode(long Id, float Latitude, float Longitude, Dictionary<string, string>? Tags) : NetworkGeo(Id, Tags) { }

[method: JsonConstructor()]
public record NetworkWay(long Id, long[] Nodes, Dictionary<string, string>? Tags) : NetworkGeo(Id, Tags) { }

[method: JsonConstructor()]
public record NetworkRelation(long Id, NetworkRelationMember[] Members, Dictionary<string, string>? Tags) : NetworkGeo(Id, Tags) { }

[method: JsonConstructor()]
public record struct NetworkRelationMember(int MemberType, long Member, string Role) { }