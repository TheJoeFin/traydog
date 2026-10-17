using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

// The situations file's shape. Version is bumped on a breaking change so an older traydog can tell.
sealed record SituationFile(int Version, IReadOnlyList<Situation> Situations);

// Reading and writing situations.json through a source-generated context (trim-safe, no reflection).
// No WinRT here, so the test project links this file and round-trips every condition kind.
static class SituationJson
{
    public const int Version = 1;

    public static string Write(IReadOnlyList<Situation> situations) =>
        JsonSerializer.Serialize(new SituationFile(Version, situations), SituationJsonContext.Default.SituationFile);

    // Throws JsonException (or NotSupportedException for an unknown condition kind) on a file it
    // can't read; the caller keeps that file aside instead of overwriting it.
    public static IReadOnlyList<Situation> Read(string json)
    {
        var file = JsonSerializer.Deserialize(json, SituationJsonContext.Default.SituationFile)
            ?? throw new JsonException("Empty situations file.");
        if (file.Version > Version)
            throw new NotSupportedException($"Situations file version {file.Version} is newer than {Version}.");

        return file.Situations ?? [];
    }
}

// IgnoreReadOnlyProperties leaves out computed members (Situation.Sensors, Condition.Sensor).
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(SituationFile))]
partial class SituationJsonContext : JsonSerializerContext;
