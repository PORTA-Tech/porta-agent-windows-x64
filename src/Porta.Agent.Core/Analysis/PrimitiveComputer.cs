using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Analysis;

internal readonly record struct PrimitiveComputationResult(
    double TypingRaw,
    double ScrollRaw,
    double FocusRaw,
    double TypingSmoothed,
    double ScrollSmoothed,
    double FocusSmoothed,
    bool TypingChannelAvailable,
    bool ScrollChannelAvailable,
    bool FocusChannelAvailable);

internal readonly record struct CaptorAvailability(
    bool Keyboard,
    bool Mouse,
    bool Focus,
    bool Idle)
{
    internal static readonly CaptorAvailability AllAvailable = new(
        Keyboard: true,
        Mouse: true,
        Focus: true,
        Idle: true);

    internal bool IsAvailableForChannel(string channelDependency)
    {
        return channelDependency switch
        {
            "keyboard" => Keyboard,
            "mouse" => Mouse,
            "focus" => Focus,
            "idle" => Idle,
            _ => true
        };
    }
}

internal static class PrimitiveComputer
{
    internal static void EnsurePrimitivesPrevStateInitialized(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        string updatedUtc = Time.toIsoUtcMsString(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        string? currentJson = ReadRuntimeStateValue(connection, key: PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey);
        if (TryParseCurrentValuesMap(currentJson, out Dictionary<string, double>? currentValues))
        {
            bool hasAllKeys = PrimitiveRegistry.Metrics.All(metric => currentValues.ContainsKey(metric.PrimitiveKey));
            Dictionary<string, double> normalized = BuildCompleteValuesMap(currentValues);
            if (!hasAllKeys)
            {
                UpsertPrimitivesPrevValues(connection, updatedUtc, normalized);
                Console.WriteLine(
                    $"PRIMITIVES_PREV_INIT reason=backfill_missing_keys neutral={PortaExecutionConstants.PrimitiveNormalization.InitialSPrevNeutral.ToString(CultureInfo.InvariantCulture)}");
            }

            return;
        }

        string? legacyJson = ReadRuntimeStateValue(connection, key: PortaExecutionConstants.PrimitiveNormalization.LegacySPrevRuntimeStateKey);
        if (TryParseLegacyValuesMap(legacyJson, out Dictionary<string, double>? legacyValues))
        {
            Dictionary<string, double> migrated = BuildCompleteValuesMap(legacyValues);
            UpsertPrimitivesPrevValues(connection, updatedUtc, migrated);
            Console.WriteLine("PRIMITIVES_PREV_INIT reason=legacy_migrated");
            return;
        }

        Dictionary<string, double> neutral = BuildCompleteValuesMap(source: null);
        UpsertPrimitivesPrevValues(connection, updatedUtc, neutral);
        Console.WriteLine(
            $"PRIMITIVES_PREV_INIT reason=missing_or_corrupt neutral={PortaExecutionConstants.PrimitiveNormalization.InitialSPrevNeutral.ToString(CultureInfo.InvariantCulture)}");
    }

    internal static PrimitiveComputationResult ComputeForFinalizedBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow bucket,
        RobustNormalizationSnapshot? robust)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tx);

        IReadOnlyDictionary<string, double> prevValues = ReadPreviousSPrevValues(connection, tx);
        HashSet<string> missingMajorChannels = ReadMissingMajorChannelsFromContributors(bucket);
        CaptorAvailability captorAvailability = ReadCaptorAvailability(connection, tx);
        var resultByKey = new Dictionary<string, (double raw, double smoothed, bool available)>(StringComparer.Ordinal);
        // Determinism contract: primitives are processed strictly in pinned registry order.
        foreach (PrimitiveMetricDefinition definition in PrimitiveRegistry.Metrics)
        {
            double fallbackRaw = ResolvePrimitiveFallbackRaw(bucket, definition.PrimitiveKey);
            double raw = ResolveRawComponent(robust, definition, fallbackRaw, out bool metricReadyForChannel);
            bool channelMissing = IsChannelMissing(
                definition,
                bucket.Partial != 0,
                missingMajorChannels,
                captorAvailability,
                metricReadyForChannel);
            double prev = prevValues[definition.PrimitiveKey];
            double smoothed = ApplySmoothing(raw, prev, channelMissing, definition);
            resultByKey[definition.PrimitiveKey] = (raw, smoothed, !channelMissing);
        }

        (double typingRaw, double typingSmoothed, bool typingAvailable) = resultByKey[PrimitiveRegistry.TypingBurstIntensity.PrimitiveKey];
        (double scrollRaw, double scrollSmoothed, bool scrollAvailable) = resultByKey[PrimitiveRegistry.ScrollVelocityIndex.PrimitiveKey];
        (double focusRaw, double focusSmoothed, bool focusAvailable) = resultByKey[PrimitiveRegistry.FocusSwitchRate.PrimitiveKey];
        return new PrimitiveComputationResult(
            TypingRaw: typingRaw,
            ScrollRaw: scrollRaw,
            FocusRaw: focusRaw,
            TypingSmoothed: typingSmoothed,
            ScrollSmoothed: scrollSmoothed,
            FocusSmoothed: focusSmoothed,
            TypingChannelAvailable: typingAvailable,
            ScrollChannelAvailable: scrollAvailable,
            FocusChannelAvailable: focusAvailable);
    }

    internal static void PersistSPrevForFinalizedBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        string updatedUtc,
        PrimitiveComputationResult primitive)
    {
        Dictionary<string, double> values = BuildValuesMap(primitive);
        string valueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            values
        });

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO runtime_state (key, value_json, updated_utc)
            VALUES ($key, $value_json, $updated_utc)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey);
        cmd.Parameters.AddWithValue("$value_json", valueJson);
        cmd.Parameters.AddWithValue("$updated_utc", updatedUtc);
        cmd.ExecuteNonQuery();
    }

    private static bool IsChannelMissing(
        PrimitiveMetricDefinition definition,
        bool bucketPartial,
        IReadOnlySet<string> missingMajorChannels,
        CaptorAvailability captorAvailability,
        bool metricReadyForChannel)
    {
        // Missing channel contract:
        // 1) metric not-ready for the primitive channel
        // 2) bucket partial + channel listed in missing_major_channels
        // 3) captor explicitly unavailable/disabled
        bool missingByMetricReadiness = !metricReadyForChannel;
        bool missingByBucketPartial = bucketPartial && missingMajorChannels.Contains(definition.ChannelDependency);
        bool missingByCaptor = !captorAvailability.IsAvailableForChannel(definition.ChannelDependency);
        return missingByMetricReadiness || missingByBucketPartial || missingByCaptor;
    }

    private static IReadOnlyDictionary<string, double> ReadPreviousSPrevValues(
        SqliteConnection connection,
        SqliteTransaction tx)
    {
        Dictionary<string, double> values = ReadPrimitivesPrevValues(connection, tx);
        return values;
    }

    private static CaptorAvailability ReadCaptorAvailability(SqliteConnection connection, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT value_json
            FROM runtime_state
            WHERE key IN ('captor_availability', 'channel_availability')
            ORDER BY CASE key
                WHEN 'captor_availability' THEN 0
                ELSE 1
            END
            LIMIT 1;
            """;
        object? scalar = cmd.ExecuteScalar();
        if (scalar == null || scalar == DBNull.Value)
        {
            return CaptorAvailability.AllAvailable;
        }

        string json = Convert.ToString(scalar, CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return CaptorAvailability.AllAvailable;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            JsonElement source = root;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("channels", out JsonElement channels)
                && channels.ValueKind == JsonValueKind.Object)
            {
                source = channels;
            }

            bool keyboard = TryReadBoolean(source, "keyboard", defaultValue: true);
            bool mouse = TryReadBoolean(source, "mouse", defaultValue: true);
            bool focus = TryReadBoolean(source, "focus", defaultValue: true);
            bool idle = TryReadBoolean(source, "idle", defaultValue: true);
            return new CaptorAvailability(keyboard, mouse, focus, idle);
        }
        catch
        {
            return CaptorAvailability.AllAvailable;
        }
    }

    private static HashSet<string> ReadMissingMajorChannelsFromContributors(InputBucketRow bucket)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (bucket.Partial == 0 || string.IsNullOrWhiteSpace(bucket.ContributorsJson))
        {
            return result;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(bucket.ContributorsJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("missing_major_channels", out JsonElement channels)
                || channels.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement element in channels.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string value = element.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }
            }
        }
        catch
        {
            return result;
        }

        return result;
    }

    private static double ResolveRawComponent(
        RobustNormalizationSnapshot? robust,
        PrimitiveMetricDefinition metricDefinition,
        double fallback,
        out bool channelAvailable)
    {
        channelAvailable = false;
        if (robust == null || robust.Metrics.Count == 0)
        {
            return Clamp01(fallback);
        }

        double sum = 0d;
        int count = 0;
        foreach (string metricKey in metricDefinition.SourceMetricKeys)
        {
            if (!robust.Metrics.TryGetValue(metricKey, out MetricNormalizationValue value))
            {
                continue;
            }

            if (!value.MetricReady)
            {
                continue;
            }

            // Step 9C contract: only consume clamped z-space for primitive transforms.
            // Clamp defensively here so transform behavior stays pinned even if upstream changes.
            double zClamped = Math.Clamp(
                value.Z,
                PortaExecutionConstants.RobustNormalization.ZClampMin,
                PortaExecutionConstants.RobustNormalization.ZClampMax);
            double normalized = Sigmoid(PortaExecutionConstants.PrimitiveNormalization.SigmoidScale * zClamped);
            sum += normalized;
            count++;
        }

        if (count == 0)
        {
            return Clamp01(fallback);
        }

        channelAvailable = true;
        return Clamp01(sum / count);
    }

    private static double ApplySmoothing(
        double rawValue,
        double previousSmoothed,
        bool channelMissing,
        PrimitiveMetricDefinition metricDefinition)
    {
        double raw = Clamp01(rawValue);
        double prev = Clamp01(previousSmoothed);
        if (channelMissing && metricDefinition.AppliesMissingChannelRule)
        {
            return Clamp01(PortaExecutionConstants.PrimitiveNormalization.MissingChannelDecay * prev);
        }

        double alpha = metricDefinition.EwmaAlpha;
        double smoothed = (alpha * raw) + ((1d - alpha) * prev);
        return Clamp01(smoothed);
    }

    private static double? TryReadDouble(JsonElement source, string propertyName)
    {
        if (source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double numeric))
        {
            return double.IsFinite(numeric) ? numeric : null;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return double.IsFinite(parsed) ? parsed : null;
        }

        return null;
    }

    private static Dictionary<string, double> ReadPrimitivesPrevValues(
        SqliteConnection connection,
        SqliteTransaction tx)
    {
        string? currentJson = ReadRuntimeStateValue(connection, tx, PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey);
        if (TryParseCurrentValuesMap(currentJson, out Dictionary<string, double>? currentValues))
        {
            return BuildCompleteValuesMap(currentValues);
        }

        string? legacyJson = ReadRuntimeStateValue(connection, tx, PortaExecutionConstants.PrimitiveNormalization.LegacySPrevRuntimeStateKey);
        if (TryParseLegacyValuesMap(legacyJson, out Dictionary<string, double>? legacyValues))
        {
            return BuildCompleteValuesMap(legacyValues);
        }

        return BuildCompleteValuesMap(source: null);
    }

    private static string? ReadRuntimeStateValue(SqliteConnection connection, SqliteTransaction tx, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT value_json
            FROM runtime_state
            WHERE key = $key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        object? scalar = cmd.ExecuteScalar();
        if (scalar == null || scalar == DBNull.Value)
        {
            return null;
        }

        return Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

    private static string? ReadRuntimeStateValue(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT value_json
            FROM runtime_state
            WHERE key = $key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        object? scalar = cmd.ExecuteScalar();
        if (scalar == null || scalar == DBNull.Value)
        {
            return null;
        }

        return Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

    private static bool TryParseCurrentValuesMap(string? json, out Dictionary<string, double> values)
    {
        values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("values", out JsonElement valuesElement)
                || valuesElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty property in valuesElement.EnumerateObject())
            {
                if (TryReadNumericValue(property.Value, out double parsed))
                {
                    values[property.Name] = Clamp01(parsed);
                }
            }

            return values.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseLegacyValuesMap(string? json, out Dictionary<string, double> values)
    {
        values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("s_prev", out JsonElement sPrev)
                || sPrev.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (TryReadDouble(sPrev, "typing_burst_intensity") is double typing)
            {
                values[PrimitiveRegistry.TypingBurstIntensity.PrimitiveKey] = Clamp01(typing);
            }

            if (TryReadDouble(sPrev, "scroll_velocity_index") is double scroll)
            {
                values[PrimitiveRegistry.ScrollVelocityIndex.PrimitiveKey] = Clamp01(scroll);
            }

            if (TryReadDouble(sPrev, "focus_switch_rate") is double focus)
            {
                values[PrimitiveRegistry.FocusSwitchRate.PrimitiveKey] = Clamp01(focus);
            }

            return values.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadNumericValue(JsonElement value, out double parsed)
    {
        parsed = 0d;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double numeric))
        {
            parsed = numeric;
            return double.IsFinite(parsed);
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fromString))
        {
            parsed = fromString;
            return double.IsFinite(parsed);
        }

        return false;
    }

    private static Dictionary<string, double> BuildValuesMap(PrimitiveComputationResult primitive)
    {
        return BuildCompleteValuesMap(
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [PrimitiveRegistry.TypingBurstIntensity.PrimitiveKey] = Clamp01(primitive.TypingSmoothed),
                [PrimitiveRegistry.ScrollVelocityIndex.PrimitiveKey] = Clamp01(primitive.ScrollSmoothed),
                [PrimitiveRegistry.FocusSwitchRate.PrimitiveKey] = Clamp01(primitive.FocusSmoothed)
            });
    }

    private static Dictionary<string, double> BuildCompleteValuesMap(IReadOnlyDictionary<string, double>? source)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (PrimitiveMetricDefinition metric in PrimitiveRegistry.Metrics)
        {
            double value = PortaExecutionConstants.PrimitiveNormalization.InitialSPrevNeutral;
            if (source != null && source.TryGetValue(metric.PrimitiveKey, out double existing))
            {
                value = existing;
            }

            values[metric.PrimitiveKey] = Clamp01(value);
        }

        return values;
    }

    private static void UpsertPrimitivesPrevValues(
        SqliteConnection connection,
        string updatedUtc,
        IReadOnlyDictionary<string, double> values)
    {
        string valueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            values
        });

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO runtime_state (key, value_json, updated_utc)
            VALUES ($key, $value_json, $updated_utc)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey);
        cmd.Parameters.AddWithValue("$value_json", valueJson);
        cmd.Parameters.AddWithValue("$updated_utc", updatedUtc);
        cmd.ExecuteNonQuery();
    }

    private static bool TryReadBoolean(JsonElement source, string propertyName, bool defaultValue)
    {
        if (source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty(propertyName, out JsonElement value))
        {
            return defaultValue;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric))
        {
            return numeric != 0;
        }

        if (value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out bool parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static double Sigmoid(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        // Pinned transform:
        // sigmoid(x) = 1 / (1 + exp(-x))
        return 1d / (1d + Math.Exp(-value));
    }

    private static double Clamp01(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private static double ResolvePrimitiveFallbackRaw(InputBucketRow bucket, string primitiveKey)
    {
        return primitiveKey switch
        {
            "typing_burst_intensity" => Clamp01(bucket.TypingBurstIntensity),
            "scroll_velocity_index" => Clamp01(bucket.ScrollVelocityIndex),
            "focus_switch_rate" => Clamp01(bucket.FocusSwitchRate),
            _ => PortaExecutionConstants.PrimitiveNormalization.InitialSPrevNeutral
        };
    }
}
