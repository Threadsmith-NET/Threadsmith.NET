namespace Threadsmith.Extensions.Neo4j.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using global::Neo4j.Driver;
using Xunit;

/// <summary>Checks spatial and temporal identity through ordinary record serialization.</summary>
public static class Neo4jValueMapperTests
{
    /// <summary>Retains dimensionality and original zones, including unresolved and offset-only zones.</summary>
    [Fact]
    public static void ValueMapper_PreservesSpatialAndZonedValues()
    {
        var mapper = new Neo4jValueMapper(16_384);
        const long instant = 1_704_067_200;
        var record = new JsonObject
        {
            ["point2d"] = mapper.Convert(new Point(7203, 1, 2)),
            ["point3d"] = mapper.Convert(new Point(9157, 1, 2, 3)),
            ["london"] = mapper.Convert(new ZonedDateTime(instant, 123, Zone.Of("Europe/London"))),
            ["abidjan"] = mapper.Convert(new ZonedDateTime(instant, 123, Zone.Of("Africa/Abidjan"))),
            ["offset"] = mapper.Convert(new ZonedDateTime(instant, 123, Zone.Of(3600))),
            ["unknown"] = mapper.Convert(new ZonedDateTime(instant, 123, Zone.Of("Threadsmith/Unknown"))),
        };

        // Exercise the same ordinary JSON serialization used to measure records before commit.
        using var json = JsonDocument.Parse(record.ToJsonString());
        var root = json.RootElement;
        var point = root.GetProperty("point2d");
        Assert.Equal(2, point.GetProperty("dimensions").GetInt32());
        Assert.Equal(7203, point.GetProperty("srid").GetInt32());
        Assert.Equal(1d, point.GetProperty("x").GetDouble());
        Assert.Equal(2d, point.GetProperty("y").GetDouble());
        Assert.False(point.TryGetProperty("z", out _));
        var point3d = root.GetProperty("point3d");
        Assert.Equal(3, point3d.GetProperty("dimensions").GetInt32());
        Assert.Equal(3d, point3d.GetProperty("z").GetDouble());
        var london = root.GetProperty("london");
        var abidjan = root.GetProperty("abidjan");
        Assert.Equal("named", london.GetProperty("zoneKind").GetString());
        Assert.Equal("Europe/London", london.GetProperty("zoneId").GetString());
        Assert.Equal("Africa/Abidjan", abidjan.GetProperty("zoneId").GetString());
        Assert.Equal(london.GetProperty("utcSeconds").GetInt64(), abidjan.GetProperty("utcSeconds").GetInt64());
        var offset = root.GetProperty("offset");
        Assert.Equal("offset", offset.GetProperty("zoneKind").GetString());
        Assert.Equal(3600, offset.GetProperty("offsetSeconds").GetInt32());
        Assert.False(offset.TryGetProperty("zoneId", out _));
        var unknown = root.GetProperty("unknown");
        Assert.Equal("Threadsmith/Unknown", unknown.GetProperty("zoneId").GetString());
        Assert.False(unknown.GetProperty("zoneResolved").GetBoolean());
        Assert.False(unknown.TryGetProperty("offsetSeconds", out _));
        Assert.Equal(instant, unknown.GetProperty("utcSeconds").GetInt64());
        Assert.Equal(123, unknown.GetProperty("nanosecond").GetInt32());
    }
}
