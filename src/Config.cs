using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

public class Config
{
    [JsonPropertyName("SteamApiKey")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string SteamApiKey { get; set; } = "REPLACE_ME";

    [JsonPropertyName("Language")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Language { get; set; } = "en";

    [JsonPropertyName("RequestTimeoutSeconds")]
    public int RequestTimeoutSeconds { get; set; } = 5;

    [JsonPropertyName("CacheDurationSeconds")]
    public int CacheDurationSeconds { get; set; } = 120;

    [JsonPropertyName("NonMemberAdIntervalMinutes")]
    public int NonMemberAdIntervalMinutes { get; set; } = 5;

    [JsonPropertyName("Groups")]
    public List<SteamGroupConfig> Groups { get; set; } = new()
    {
        new SteamGroupConfig()
    };

    [JsonPropertyName("EnableDebugLogging")]
    public bool EnableDebugLogging { get; set; } = false;

    // Ignore unknown properties without failing
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class SteamGroupConfig
{
    [JsonPropertyName("GroupId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string GroupId { get; set; } = "REPLACE_ME";

    [JsonPropertyName("GrantedFlag")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string GrantedFlag { get; set; } = "@abs/membre";

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Priority")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int Priority { get; set; } = 0;
}

public class MessageConfig
{
    [JsonPropertyName("Prefix")]
    public string Prefix { get; set; } = "{red}[Group]{reset} ";

    [JsonPropertyName("MemberDetected")]
    public string MemberDetected { get; set; } = "You are detected as a member of: {MemberGroups}";

    [JsonPropertyName("SteamIdUnavailable")]
    public string SteamIdUnavailable { get; set; } = "Your SteamID isn't available yet.";

    [JsonPropertyName("GroupCheckMember")]
    public string GroupCheckMember { get; set; } = "You are a member of: {MemberGroups}";

    [JsonPropertyName("GroupCheckNotMember")]
    public string GroupCheckNotMember { get; set; } = "You are not a member of our groups. Visit {GroupUrls}";

    [JsonPropertyName("NonMemberAdvertisement")]
    public string NonMemberAdvertisement { get; set; } = "Join our Steam groups to unlock rewards! Visit {GroupUrls}";

    [JsonPropertyName("GroupCheckUnknown")]
    public string GroupCheckUnknown { get; set; } = "Failed to determine group membership right now.";

    public bool ApplyDefaultsFrom(MessageConfig defaults)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(Prefix))
        {
            Prefix = defaults.Prefix;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(MemberDetected))
        {
            MemberDetected = defaults.MemberDetected;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(SteamIdUnavailable))
        {
            SteamIdUnavailable = defaults.SteamIdUnavailable;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(GroupCheckMember))
        {
            GroupCheckMember = defaults.GroupCheckMember;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(GroupCheckNotMember))
        {
            GroupCheckNotMember = defaults.GroupCheckNotMember;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(GroupCheckUnknown))
        {
            GroupCheckUnknown = defaults.GroupCheckUnknown;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(NonMemberAdvertisement))
        {
            NonMemberAdvertisement = defaults.NonMemberAdvertisement;
            changed = true;
        }

        return changed;
    }
}

public sealed class FlexibleStringConverter : JsonConverter<string>
{
    private static string ReadElementAsString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.StartArray => ReadArray(ref reader),
            _ => null
        } ?? string.Empty;
    }

    private static string ReadArray(ref Utf8JsonReader reader)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var value = ReadElementAsString(el);
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value);
            }
        }

        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

public sealed class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => reader.TryGetInt32(out var i)
                    ? i
                    : Convert.ToInt32(reader.GetDouble()),
                JsonTokenType.String => int.TryParse(reader.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0,
                JsonTokenType.True => 1,
                JsonTokenType.False => 0,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}
