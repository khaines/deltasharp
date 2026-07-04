namespace DeltaSharp.Plans.Logical;

/// <summary>
/// An immutable, purely <b>logical</b> description of a write target — a format, save mode,
/// optional path or table identifier, partition columns (by name), and options. It holds
/// <b>no</b> open writer, stream, file handle, task, or backend object, which is what makes a
/// <see cref="WriteToSource"/> node a descriptor only (AC3).
/// </summary>
internal sealed class SinkDescriptor : IEquatable<SinkDescriptor>
{
    /// <summary>Creates a logical sink descriptor.</summary>
    /// <param name="format">The data source format name (for example <c>"parquet"</c>).</param>
    /// <param name="mode">How the write behaves when the target exists.</param>
    /// <param name="path">An optional output path.</param>
    /// <param name="tableIdentifier">An optional multipart table identifier.</param>
    /// <param name="partitionColumns">Partition column names, in order.</param>
    /// <param name="options">Writer options.</param>
    public SinkDescriptor(
        string format,
        SaveMode mode = SaveMode.ErrorIfExists,
        string? path = null,
        IEnumerable<string>? tableIdentifier = null,
        IEnumerable<string>? partitionColumns = null,
        IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        Format = format;
        Mode = mode;
        Path = path;
        TableIdentifier = tableIdentifier is null
            ? null
            : PlanCollections.ToIdentifier(tableIdentifier, nameof(tableIdentifier));
        PartitionColumns = partitionColumns is null
            ? PlanCollections.Empty<string>()
            : PlanCollections.ToImmutable(partitionColumns, nameof(partitionColumns));
        Options = PlanCollections.ToOptions(options);
    }

    /// <summary>The data source format name.</summary>
    public string Format { get; }

    /// <summary>How the write behaves when the target exists.</summary>
    public SaveMode Mode { get; }

    /// <summary>The optional output path.</summary>
    public string? Path { get; }

    /// <summary>The optional multipart table identifier.</summary>
    public IReadOnlyList<string>? TableIdentifier { get; }

    /// <summary>The partition column names, in order.</summary>
    public IReadOnlyList<string> PartitionColumns { get; }

    /// <summary>The writer options.</summary>
    public IReadOnlyDictionary<string, string> Options { get; }

    /// <summary>A one-line description used by the plan renderer.</summary>
    public string SimpleString
    {
        get
        {
            var parts = new List<string> { $"format={Format}", $"mode={Mode}" };
            if (Path is not null)
            {
                // Redact credential-bearing fragments (SAS ?sig=, presigned-URL signatures, userinfo)
                // so stringifying a write node — Explain (#179), a log line, or a diagnostic — never
                // leaks a secret embedded in the sink path (mirrors UnresolvedFileRelation, #424/#432).
                parts.Add($"path={SecretRedaction.RedactPath(Path)}");
            }

            if (TableIdentifier is not null)
            {
                parts.Add($"table={string.Join(".", TableIdentifier)}");
            }

            if (PartitionColumns.Count > 0)
            {
                parts.Add($"partitionBy=[{string.Join(", ", PartitionColumns)}]");
            }

            return "[" + string.Join(", ", parts) + "]";
        }
    }

    /// <inheritdoc/>
    public bool Equals(SinkDescriptor? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (!string.Equals(Format, other.Format, StringComparison.Ordinal)
            || Mode != other.Mode
            || !string.Equals(Path, other.Path, StringComparison.Ordinal))
        {
            return false;
        }

        bool tableEqual = (TableIdentifier, other.TableIdentifier) switch
        {
            (null, null) => true,
            (not null, not null) =>
                PlanCollections.StringSequenceEquals(TableIdentifier, other.TableIdentifier),
            _ => false,
        };

        return tableEqual
            && PlanCollections.StringSequenceEquals(PartitionColumns, other.PartitionColumns)
            && PlanCollections.OptionsEqual(Options, other.Options);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SinkDescriptor);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        int hash = PlanHash.Combine(PlanHash.Seed, PlanHash.OfString(Format));
        hash = PlanHash.Combine(hash, (int)Mode);
        hash = PlanHash.Combine(hash, Path is null ? 0 : PlanHash.OfString(Path));
        if (TableIdentifier is not null)
        {
            hash = PlanHash.CombineStrings(hash, TableIdentifier);
        }

        hash = PlanHash.CombineStrings(hash, PartitionColumns);
        return PlanHash.Combine(hash, PlanHash.OfStringMap(Options));
    }
}
