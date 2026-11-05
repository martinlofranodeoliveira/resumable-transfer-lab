using System.Text.Json;

namespace ResumableTransferLab;

public static class StateSerializer
{
    public static byte[] SerializeToUtf8Bytes(TransferState state)
    {
        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", state.SchemaVersion);
            writer.WriteString("artifactId", state.ArtifactId);
            writer.WriteString("sourceUrl", state.SourceUrl);
            writer.WriteString("strongETag", state.StrongETag);
            writer.WriteNumber("expectedLength", state.ExpectedLength);
            writer.WriteString("expectedSha256", state.ExpectedSha256);
            writer.WriteNumber("committedBytes", state.CommittedBytes);
            writer.WriteString("committedPrefixSha256", state.CommittedPrefixSha256);
            writer.WriteEndObject();
        }

        byte[] bytes = ms.ToArray();
        if (bytes.Length > TransferLimits.MaxStateFileBytes)
        {
            throw new InvalidOperationException("Serialized transfer state exceeds 64 KiB cap.");
        }

        return bytes;
    }

    public static bool TryParse(ReadOnlySpan<byte> utf8Bytes, out TransferState? state, out TransferError? error)
    {
        state = null;
        if (utf8Bytes.Length == 0)
        {
            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "Transfer state file is empty.");
            return false;
        }

        if (utf8Bytes.Length > TransferLimits.MaxStateFileBytes)
        {
            error = TransferError.Create(TransferErrorCode.LimitExceeded, "Transfer state file exceeds 64 KiB limit.");
            return false;
        }

        Utf8JsonReader reader = new(utf8Bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4
        });

        HashSet<string> seenKeys = new(StringComparer.Ordinal);
        int? schemaVersion = null;
        string? artifactId = null;
        string? sourceUrl = null;
        string? strongETag = null;
        long? expectedLength = null;
        string? expectedSha256 = null;
        long? committedBytes = null;
        string? committedPrefixSha256 = null;

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "Transfer state root must be a JSON object.");
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "Expected property name token in state JSON.");
                    return false;
                }

                string propName = reader.GetString() ?? string.Empty;
                if (!seenKeys.Add(propName))
                {
                    error = TransferError.Create(TransferErrorCode.DuplicateStateKey, "Transfer state JSON contains duplicate property keys.");
                    return false;
                }

                if (!reader.Read())
                {
                    error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "Unexpected end of state JSON after property name.");
                    return false;
                }

                switch (propName)
                {
                    case "schemaVersion":
                        if (reader.TokenType != JsonTokenType.Number)
                        {
                            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "schemaVersion must be an integer.");
                            return false;
                        }
                        if (!reader.TryGetInt32(out int ver))
                        {
                            error = TransferError.Create(TransferErrorCode.NumericOverflow, "schemaVersion overflows 32-bit integer bounds.");
                            return false;
                        }
                        schemaVersion = ver;
                        break;

                    case "artifactId":
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "artifactId must be a string.");
                            return false;
                        }
                        artifactId = reader.GetString();
                        break;

                    case "sourceUrl":
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "sourceUrl must be a string.");
                            return false;
                        }
                        sourceUrl = reader.GetString();
                        break;

                    case "strongETag":
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "strongETag must be a string.");
                            return false;
                        }
                        strongETag = reader.GetString();
                        break;

                    case "expectedLength":
                        if (reader.TokenType != JsonTokenType.Number)
                        {
                            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "expectedLength must be an integer.");
                            return false;
                        }
                        if (!reader.TryGetInt64(out long expLen))
                        {
                            error = TransferError.Create(TransferErrorCode.NumericOverflow, "expectedLength overflows 64-bit integer bounds.");
                            return false;
                        }
                        expectedLength = expLen;
                        break;

                    case "expectedSha256":
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "expectedSha256 must be a string.");
                            return false;
                        }
                        expectedSha256 = reader.GetString();
                        break;

                    case "committedBytes":
                        if (reader.TokenType != JsonTokenType.Number)
                        {
                            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "committedBytes must be an integer.");
                            return false;
                        }
                        if (!reader.TryGetInt64(out long commLen))
                        {
                            error = TransferError.Create(TransferErrorCode.NumericOverflow, "committedBytes overflows 64-bit integer bounds.");
                            return false;
                        }
                        committedBytes = commLen;
                        break;

                    case "committedPrefixSha256":
                        if (reader.TokenType != JsonTokenType.String)
                        {
                            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "committedPrefixSha256 must be a string.");
                            return false;
                        }
                        committedPrefixSha256 = reader.GetString();
                        break;

                    default:
                        error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "Transfer state JSON contains an unknown property.");
                        return false;
                }
            }

            if (reader.Read())
            {
                error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "Transfer state JSON contains trailing tokens.");
                return false;
            }
        }
        // GetString also rejects invalid UTF-8 and unpaired escaped surrogates
        // with InvalidOperationException. Those are malformed persisted input,
        // not programming failures that may escape the TryParse boundary.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "Transfer state file is not valid JSON.");
            return false;
        }

        if (!schemaVersion.HasValue)
        {
            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "schemaVersion is required in transfer state.");
            return false;
        }

        if (schemaVersion.Value != TransferLimits.CurrentStateSchemaVersion)
        {
            error = TransferError.Create(TransferErrorCode.UnsupportedStateVersion, "Transfer state schemaVersion is not supported.");
            return false;
        }

        if (artifactId is null || sourceUrl is null || strongETag is null ||
            !expectedLength.HasValue || expectedSha256 is null ||
            !committedBytes.HasValue || committedPrefixSha256 is null)
        {
            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "Transfer state is missing required properties.");
            return false;
        }

        if (expectedLength.Value < 0 || committedBytes.Value < 0)
        {
            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "Byte lengths in transfer state cannot be negative.");
            return false;
        }

        if (expectedLength.Value > TransferLimits.MaxArtifactBytes)
        {
            error = TransferError.Create(TransferErrorCode.LimitExceeded, "expectedLength in transfer state exceeds 64 MiB limit.");
            return false;
        }

        if (committedBytes.Value > expectedLength.Value)
        {
            error = TransferError.Create(TransferErrorCode.AmbiguousLocalState, "committedBytes exceeds expectedLength in transfer state.");
            return false;
        }

        if (!TransferGuards.ValidateArtifactId(artifactId, out _))
        {
            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "artifactId in transfer state is invalid.");
            return false;
        }

        if (!TransferGuards.ValidateLoopbackHttpUrl(sourceUrl, out Uri? parsedUri, out _) || parsedUri is null)
        {
            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "sourceUrl in transfer state is invalid.");
            return false;
        }

        if (!TransferGuards.ValidateStrongETag(strongETag, out string normalizedETag, out _))
        {
            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "strongETag in transfer state is invalid or weak.");
            return false;
        }

        if (!TransferGuards.ValidateSha256Hex(expectedSha256, out string normExpectedSha, out _) ||
            !TransferGuards.ValidateSha256Hex(committedPrefixSha256, out string normPrefixSha, out _))
        {
            error = TransferError.Create(TransferErrorCode.InvalidStateFormat, "SHA-256 digest in transfer state is invalid.");
            return false;
        }

        state = new TransferState
        {
            SchemaVersion = schemaVersion.Value,
            ArtifactId = artifactId,
            SourceUrl = parsedUri.AbsoluteUri,
            StrongETag = normalizedETag,
            ExpectedLength = expectedLength.Value,
            ExpectedSha256 = normExpectedSha,
            CommittedBytes = committedBytes.Value,
            CommittedPrefixSha256 = normPrefixSha
        };
        error = null;
        return true;
    }
}
