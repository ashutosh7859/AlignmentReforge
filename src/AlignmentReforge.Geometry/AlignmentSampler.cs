using System;
using System.Collections.Generic;
using System.Linq;
using AlignmentReforge.Domain;

namespace AlignmentReforge.Geometry;

public static class AlignmentSampler
{
    public static IReadOnlyList<Vertex> SampleCase2(
        SolvedAlignment solved,
        double interval,
        double firstSegmentLength = 0.0)
    {
        ArgumentNullException.ThrowIfNull(solved);
        if (interval <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        }

        var orderedCurves = solved.Curves.OrderBy(curve => curve.StaTS).ToArray();
        var totalLength = solved.BuildPlan.Count == 0
            ? Distance(solved.InputVertices[0].Position, solved.InputVertices[^1].Position)
            : solved.BuildPlan.Max(plan => plan.EndStation);

        var stations = BuildSampleStations(totalLength, interval, firstSegmentLength);
        var vertices = new List<Vertex>(stations.Count);

        for (var i = 0; i < stations.Count; i++)
        {
            var point = PointAtStation(solved, orderedCurves, stations[i]);
            vertices.Add(new Vertex(i, point, VertexHint.Unknown));
        }

        return vertices;
    }

    private static IReadOnlyList<double> BuildSampleStations(double totalLength, double interval, double firstSegmentLength)
    {
        var stations = new List<double> { 0.0 };
        if (totalLength <= 0.0)
        {
            return stations;
        }

        var next = firstSegmentLength > 0.0 && firstSegmentLength < interval
            ? firstSegmentLength
            : interval;

        while (next < totalLength)
        {
            stations.Add(next);
            next += interval;
        }

        if (stations[^1] < totalLength)
        {
            stations.Add(totalLength);
        }

        return stations;
    }

    private static Point2D PointAtStation(
        SolvedAlignment solved,
        IReadOnlyList<SolvedCurve> curves,
        double station)
    {
        if (curves.Count == 0)
        {
            return PointAlongLine(
                solved.InputVertices[0].Position,
                solved.InputVertices[^1].Position,
                station);
        }

        var start = solved.InputVertices[0].Position;
        var end = solved.InputVertices[^1].Position;
        var first = curves[0];
        if (station <= first.StaTS)
        {
            return PointAlongLine(start, first.TS.Position, station);
        }

        for (var i = 0; i < curves.Count; i++)
        {
            var curve = curves[i];
            if (station <= curve.StaSC)
            {
                return IntegrateEntrySpiral(curve, station - curve.StaTS);
            }

            if (station <= curve.StaCS)
            {
                return IntegrateArc(curve, station - curve.StaSC);
            }

            if (station <= curve.StaST)
            {
                return IntegrateExitSpiral(curve, station - curve.StaCS);
            }

            var next = i + 1 < curves.Count ? curves[i + 1] : null;
            if (next is not null && station <= next.StaTS)
            {
                return PointAlongLine(curve.ST.Position, next.TS.Position, station - curve.StaST);
            }
        }

        return PointAlongLine(curves[^1].ST.Position, end, station - curves[^1].StaST);
    }

    private static Point2D PointAlongLine(Point2D start, Point2D end, double distanceFromStart)
    {
        var length = Distance(start, end);
        if (length <= 1e-12)
        {
            return start;
        }

        var fraction = distanceFromStart / length;
        return new Point2D(
            start.X + ((end.X - start.X) * fraction),
            start.Y + ((end.Y - start.Y) * fraction));
    }

    private static Point2D IntegrateEntrySpiral(SolvedCurve curve, double distance)
    {
        var sign = (int)curve.Direction;
        var bearing = DegreesToRadians(curve.EntryBearing);
        var length = Math.Clamp(distance, 0.0, curve.SpiralLengthIn);
        return Integrate(
            curve.TS.Position,
            bearing,
            sign,
            length,
            s => (s * s) / (2.0 * curve.Radius * curve.SpiralLengthIn));
    }

    private static Point2D IntegrateArc(SolvedCurve curve, double distance)
    {
        var sign = (int)curve.Direction;
        var startBearing = DegreesToRadians(curve.EntryBearing) + (sign * curve.SpiralLengthIn / (2.0 * curve.Radius));
        var length = Math.Clamp(distance, 0.0, curve.ArcLength);
        return Integrate(
            curve.SC.Position,
            startBearing,
            sign,
            length,
            s => s / curve.Radius);
    }

    private static Point2D IntegrateExitSpiral(SolvedCurve curve, double distance)
    {
        var sign = (int)curve.Direction;
        var startBearing =
            DegreesToRadians(curve.EntryBearing) +
            (sign * curve.SpiralLengthIn / (2.0 * curve.Radius)) +
            (sign * curve.ArcLength / curve.Radius);
        var length = Math.Clamp(distance, 0.0, curve.SpiralLengthOut);
        return Integrate(
            curve.CS.Position,
            startBearing,
            sign,
            length,
            s => (s / curve.Radius) - ((s * s) / (2.0 * curve.Radius * curve.SpiralLengthOut)));
    }

    private static Point2D Integrate(
        Point2D start,
        double startBearingRadians,
        int sign,
        double length,
        Func<double, double> deltaFunction)
    {
        if (length <= 0.0)
        {
            return start;
        }

        var steps = Math.Max(8, (int)Math.Ceiling(length / 0.5));
        var ds = length / steps;
        var x = start.X;
        var y = start.Y;

        for (var i = 0; i < steps; i++)
        {
            var mid = (i + 0.5) * ds;
            var bearing = startBearingRadians + (sign * deltaFunction(mid));
            x += Math.Sin(bearing) * ds;
            y += Math.Cos(bearing) * ds;
        }

        return new Point2D(x, y);
    }

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static double DegreesToRadians(double degrees)
        => degrees * Math.PI / 180.0;
}
