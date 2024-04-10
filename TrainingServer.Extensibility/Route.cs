using Simplify.NET;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("TrainingServer.Extensibility.Parallel")]

namespace TrainingServer.Extensibility;

[JsonConverter(typeof(RouteJsonConverter))]
public class Route : IEnumerable<Route.RouteSegment>
{
	public string Name { get; init; }

	public IEnumerable<Coordinate> Points => _segments.Select(rs => rs.Point);
	public IEnumerable<(Coordinate Point, string? PointLabel)> LabelledPoints => _segments.Select(rs => (rs.Point, rs.PointLabel));

	readonly List<RouteSegment> _segments = [];

	public Route(string name) => Name = name;

	public Route(string name, params (Coordinate start, string? pointLabel)[] points)
	{
		Name = name;
		_segments = new(points.Select(p => new StraightLineSegment(p.start, p.pointLabel)));
	}

	public Route(string name, params RouteSegment[] segments)
	{
		Name = name;
		_segments = new(segments);
	}

	public Route(string name, params Coordinate[] points)
	{
		Name = name;

		foreach (Coordinate i in points)
			_segments.Add(new StraightLineSegment(i, null));
	}

	public void Add(Coordinate point, string? pointLabel = null) => _segments.Add(new StraightLineSegment(point, pointLabel));
	public void AddArc(Coordinate controlPoint, Coordinate end, string? pointLabel = null) =>
		_segments.Add(new ArcSegment(controlPoint, end, pointLabel));

	public void AddArc(Coordinate from, Coordinate to, Coordinate origin, bool clockwise)
	{
		[DebuggerStepThrough]
		float getAngle(float degrees, float degrees2)
		{
			if (Math.Abs(degrees - degrees2) < 0.001)
				return 0;

			if (degrees > degrees2)
			{
				float num = degrees2 + 360 - degrees;
				float num2 = degrees - degrees2;
				if (num2 < num)
				{
					return -num2;
				}

				return num;
			}

			float num3 = degrees2 - degrees;
			float num4 = degrees + 360 - degrees2;

			return (num4 < num3) ? -num4 : num3;
		}

		float clampAngle(float angle)
		{
			while (angle < -360)
				angle += 360;

			while (angle > 360)
				angle -= 360;

			return angle;
		}

		void arcTo(Coordinate vertex, Coordinate next, Coordinate origin)
		{
#pragma warning disable IDE0042 // Variable declaration can be deconstructed
			var startData = origin.GetBearingDistance(vertex);
			var endData = origin.GetBearingDistance(next);
#pragma warning restore IDE0042 // Variable declaration can be deconstructed
			float startBearing = startData.bearing ?? (vertex.Latitude > origin.Latitude ? 0 : 180);
			float endBearing = endData.bearing ?? (next.Latitude > origin.Latitude ? 0 : 180);

			float guessBearing = getAngle(startBearing, endBearing);
			float realBearing = clampAngle(guessBearing / 2 + startBearing);

			if ((clockwise && guessBearing < 0) || (!clockwise && guessBearing > 0))
				realBearing = clampAngle(realBearing + 180);

			var midPoint = origin.FixRadialDistance(realBearing, startData.distance);

			if (midPoint != vertex && midPoint != next && Math.Abs(clampAngle(getAngle(startBearing, realBearing) + getAngle(realBearing, endBearing))) > 45)
			{
				arcTo(vertex, midPoint, origin);
				arcTo(midPoint, next, origin);
				return;
			}

			var controlLat = 2 * midPoint.Latitude - vertex.Latitude / 2 - next.Latitude / 2;
			var controlLon = 2 * midPoint.Longitude - vertex.Longitude / 2 - next.Longitude / 2;

			AddArc(new((float)controlLat, (float)controlLon), next);
		}

		arcTo(from, to, origin);
	}

	public void Jump(Coordinate point) => _segments.Add(new InvisibleSegment(point));

	public Coordinate Average() => _segments.Count != 0
		? new(
			_segments.Select(p => p.Point.Latitude).Average(),
			_segments.Select(p => p.Point.Longitude).Average()
		) : new(0, 0);

	public Route Filter(Func<Coordinate, bool> filter)
	{
		if (_segments.Count == 0 || (_segments.Count == 1 && filter(_segments[0].Point)))
			return this;
		else if (_segments.Count == 1)
			return new(Name, segments: []);

		return FilterInternal(this, [.. _segments.Select(s => filter(s.Point))]);
	}

	internal static Route FilterInternal(Route route, bool[] filterList)
	{
		List<RouteSegment> segments = [route._segments[0]];

		for (int cntr = 0; cntr < filterList.Length; ++cntr)
			if (filterList[cntr] || (cntr > 0 && filterList[cntr - 1]) || (cntr < filterList.Length - 1 && filterList[cntr + 1]))
			{
				if (segments.Count == 0)
					segments.Add(new StraightLineSegment(route._segments[cntr].Point, route._segments[cntr].PointLabel));
				else if (cntr > 0 && filterList[cntr - 1])
					segments.Add(route._segments[cntr]);
				else
				{
					if (segments[^1] is InvisibleSegment || segments.Count == 1)
						segments[^1] = new InvisibleSegment(route._segments[cntr].Point);
					else
						segments.Add(new InvisibleSegment(route._segments[cntr].Point));
				}
			}

		return new(route.Name, [.. segments]);
	}

	public string ToSpaceSeparated() => string.Join(" ", _segments.Select(p => $"{p.Point.Latitude:00.00000} {p.Point.Longitude:00.00000}"));

	public Route WithName(string name) => new(name, _segments.ToArray());

	public Route WithoutLabels() => new(Name, _segments.Select(s => s.Point).ToArray());

	public Route Simplified(double precision)
	{
		if (_segments.Any(s => s is not StraightLineSegment sls || !string.IsNullOrWhiteSpace(sls.PointLabel)))
			throw new Exception("Cannot simplify a curved, discontinuous, or labelled path.");

		var newPoints = SimplifyNet.Simplify(_segments.Select(seg => new Simplify.NET.Point(seg.Point.Longitude, seg.Point.Latitude)).ToList(), precision, true).Select(p => new Coordinate((float)p.Y, (float)p.X)).ToList();

		if (_segments.Last().Point == _segments.First().Point && newPoints.Last() != newPoints.First())
			newPoints.Add(newPoints.First());

		return new(Name, newPoints.ToArray());
	}

	public static Route operator +(Route first, Route second) => new(first.Name, first._segments.Concat(second._segments).ToArray());

	public override int GetHashCode() => _segments.Aggregate(0, (s, i) => HashCode.Combine(s, i));

	public IEnumerator<RouteSegment> GetEnumerator() => ((IEnumerable<RouteSegment>)_segments).GetEnumerator();
	System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => ((System.Collections.IEnumerable)_segments).GetEnumerator();

	public RouteSegment this[int idx]
	{
		get => _segments[idx];
		set => _segments[idx] = value;
	}

	public override string ToString() => Name;

	[JsonPolymorphic(TypeDiscriminatorPropertyName = "$", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor)]
	[JsonDerivedType(typeof(StraightLineSegment), "l")]
	[JsonDerivedType(typeof(ArcSegment), "a")]
	[JsonDerivedType(typeof(InvisibleSegment), "j")]
	public abstract record RouteSegment(Coordinate Point, string? PointLabel) { }

	[method: JsonConstructor()]
	public record StraightLineSegment(Coordinate Point, string? PointLabel) : RouteSegment(Point, PointLabel) { }
	[method: JsonConstructor()]
	public record ArcSegment(Coordinate ControlPoint, Coordinate End, string? PointLabel) : RouteSegment(End, PointLabel) { }
	[method: JsonConstructor()]
	public record InvisibleSegment(Coordinate Point) : RouteSegment(Point, null) { }

	internal class RouteJsonConverter : JsonConverter<Route>
	{
		[method: JsonConstructor()]
		private record JsonRoute(string Name, RouteSegment[] Segments)
		{
			public static implicit operator Route?([NotNullIfNotNull(nameof(me))] JsonRoute? me) => me is null ? null : new(me.Name, me.Segments);
			public static implicit operator JsonRoute(Route them) => new(them.Name, [.. them._segments]);
		}

		public override Route? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			JsonSerializer.Deserialize<JsonRoute>(ref reader, options);

		public override void Write(Utf8JsonWriter writer, Route value, JsonSerializerOptions options) =>
			JsonSerializer.Serialize(writer, (JsonRoute)value, options);
	}
}