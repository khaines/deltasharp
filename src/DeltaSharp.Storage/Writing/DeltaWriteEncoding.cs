using System.Globalization;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Writing;

/// <summary>
/// The narrow set of columnar encoding helpers the Delta write facade (<see cref="DeltaWriteTarget"/>,
/// #487) needs: copying a single logical value from a source <see cref="ColumnVector"/> onto a
/// <see cref="MutableColumnVector"/> lane (used to split a batch into per-partition sub-batches without
/// materializing to <c>Row</c>s), and formatting a partition-column value into its canonical Delta
/// partition-value string (the string stored in <c>add.partitionValues</c> and Hive-encoded into the file
/// directory path). Both are the exact inverse of the read/materialize side and honor the ADR-0008 lane
/// storage (Date is an epoch-day int, Timestamp an epoch-microsecond long, compact/wide decimals the
/// unscaled integer).
/// </summary>
internal static class DeltaWriteEncoding
{
    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    /// <summary>Sentinel directory-name value for a null partition value (Hive/Delta convention). The
    /// <c>add.partitionValues</c> map stores a real JSON null; only the physical directory path uses this.</summary>
    public const string HiveDefaultPartition = "__HIVE_DEFAULT_PARTITION__";

    /// <summary>Appends the logical value at <paramref name="row"/> of <paramref name="source"/> onto
    /// <paramref name="destination"/>, preserving nulls and the ADR-0008 lane encoding. The two vectors
    /// must share the same <see cref="ColumnVector.Type"/>.</summary>
    public static void AppendValue(MutableColumnVector destination, ColumnVector source, int row)
    {
        if (source.IsNull(row))
        {
            destination.AppendNull();
            return;
        }

        switch (source.Type)
        {
            case BooleanType:
                destination.AppendValue(source.GetValue<bool>(row));
                break;
            case ByteType:
                destination.AppendValue(source.GetValue<byte>(row));
                break;
            case ShortType:
                destination.AppendValue(source.GetValue<short>(row));
                break;
            case IntegerType:
                destination.AppendValue(source.GetValue<int>(row));
                break;
            case LongType:
                destination.AppendValue(source.GetValue<long>(row));
                break;
            case FloatType:
                destination.AppendValue(source.GetValue<float>(row));
                break;
            case DoubleType:
                destination.AppendValue(source.GetValue<double>(row));
                break;
            case DateType:
                destination.AppendValue(source.GetValue<int>(row));
                break;
            case TimestampType or TimestampNtzType:
                destination.AppendValue(source.GetValue<long>(row));
                break;
            case DecimalType decimalType:
                if (decimalType.IsCompact)
                {
                    destination.AppendValue(source.GetValue<long>(row));
                }
                else
                {
                    destination.AppendValue(source.GetValue<Int128>(row));
                }

                break;
            case StringType:
            case BinaryType:
                destination.AppendBytes(source.GetBytes(row));
                break;
            // TypeName, not SimpleString: ColumnVectors.Create builds StructColumnVector/ListColumnVector/
            // MapColumnVector, so `source` genuinely can be nested-typed here and SimpleString would recurse
            // through every foreign field name. Verified reachable at runtime with a committed struct row.
            default:
                throw new DeltaStorageException(
                    StorageErrorKind.UnsupportedFeature,
                    $"The Delta write facade has no columnar encoding for type '{source.Type.TypeName}'.");
        }
    }

    /// <summary>Whether <paramref name="type"/> is a supported Delta partition-column type: an atomic type —
    /// exactly the arms of <see cref="FormatPartitionValue"/>. Nested types (struct/array/map) and binary are
    /// NOT partition-encodable (a partition value must render to a single directory-segment string). Keep in
    /// sync with <see cref="FormatPartitionValue"/> — a new supported arm there must be added here too.</summary>
    public static bool IsSupportedPartitionType(DataType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            BooleanType or ByteType or ShortType or IntegerType or LongType or FloatType or DoubleType
                or StringType or DateType or TimestampType or TimestampNtzType or DecimalType => true,
            _ => false,
        };
    }

    /// <summary>
    /// Composes a single Hive-style partition directory <b>physical</b> segment, <c>column=value</c> (#806
    /// layer 1), using Apache Spark's <c>ExternalCatalogUtils.escapePathName</c> alphabet
    /// (<see cref="EscapePathName"/>) for both the column name and the value. A null <b>or empty</b> value uses
    /// the <see cref="HiveDefaultPartition"/> sentinel (Spark parity — both collide on disk, disambiguated by
    /// the authoritative <c>add.partitionValues</c>).</summary>
    /// <remarks>
    /// <para><b>#806 two-layer encoding — layer 1 (physical on disk).</b> This is the directory name written to
    /// storage; it is byte-for-byte what Spark/delta-rs write for the same <c>(name, value)</c>, so the on-disk
    /// layout is interoperable. The complementary layer 2 (<see cref="ToAddPath"/>) URI-encodes the assembled
    /// relative path into <c>add.path</c> per the Delta protocol; the read path recovers the physical key by a
    /// single <c>Uri.UnescapeDataString</c> (<c>PartitionPathResolver</c>, Inc-A). This replaces the pre-#806
    /// <c>Uri.EscapeDataString</c> scheme, whose alphabet diverged from Spark's (it escaped space and all
    /// non-ASCII) and which stored <c>add.path</c> literally.</para>
    /// <para><b>Injection safety is preserved.</b> <see cref="EscapePathName"/> escapes <c>/ \ = :</c> and
    /// control characters, so a hostile name or value cannot fabricate or escape a directory segment (the #708
    /// hardening). The write-door column-name validation (<c>ColumnMapping.FindUnsafePathSegmentReason</c>)
    /// remains as defense in depth.</para>
    /// <para><b>Encoded-length budget (#806 §2.6).</b> The composed physical segment must fit a filesystem path
    /// component (<see cref="MaxEncodedPathComponentBytes"/>). <see cref="EscapePathName"/>'s non-ASCII
    /// passthrough removes the old <c>Uri.EscapeDataString</c> blow-up; the residual is escape-heavy ASCII (each
    /// escaped char → 3 bytes). An over-budget segment fails <b>closed</b> here (pre-commit, orphan-Parquet-only)
    /// rather than a late filesystem <c>ENAMETOOLONG</c>.</para>
    /// <para><b>Partition truth is authoritative from <c>add.partitionValues</c></b>, never recovered by parsing
    /// the directory path, so the physical encoding never affects read correctness. Both the write path
    /// (<c>DeltaWriteTarget.DataFilePath</c>) and OPTIMIZE (<c>DeltaOptimize.BuildOutputPath</c>) call this, so
    /// the two cannot drift.</para>
    /// </remarks>
    /// <exception cref="DeltaStorageException">The composed segment exceeds
    /// <see cref="MaxEncodedPathComponentBytes"/> UTF-8 bytes (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
    public static string HivePartitionSegment(string column, string? value)
    {
        ArgumentNullException.ThrowIfNull(column);
        // Spark parity (ExternalCatalogUtils.getPartitionValueString): BOTH null and empty-string map to the
        // sentinel directory (they collide on disk, disambiguated by the authoritative add.partitionValues).
        string encodedValue = string.IsNullOrEmpty(value) ? HiveDefaultPartition : EscapePathName(value);
        string segment = EscapePathName(column) + "=" + encodedValue;

        int encodedBytes = Encoding.UTF8.GetByteCount(segment);
        if (encodedBytes > MaxEncodedPathComponentBytes)
        {
            // Message hygiene (#653/#806): name only the bounded byte counts, never the (potentially PII) value.
            throw DeltaStorageException.UnsupportedFeature(
                $"A Hive partition-directory component is {encodedBytes} bytes after encoding, exceeding the "
                + $"{MaxEncodedPathComponentBytes}-byte filesystem path-component limit; the partition value "
                + "cannot be written as a Hive partition path. The write fails closed before commit.");
        }

        return segment;
    }

    /// <summary>The conservative on-disk path-<b>component</b> byte budget (ext4/PVC <c>NAME_MAX</c>), enforced
    /// by <see cref="HivePartitionSegment"/> on the composed physical <c>name=value</c> directory segment
    /// (#806 §2.6).</summary>
    public const int MaxEncodedPathComponentBytes = 255;

    // The Apache Spark ExternalCatalogUtils.escapePathName charToEscape bitset (non-Windows alphabet, #806 D1):
    // ASCII controls 0x01–0x1F, DEL 0x7F, and " # % ' * / : = ? \ { [ ] ^. Every other code point — including
    // all non-ASCII and (non-Windows) the space — passes through unescaped.
    private static readonly bool[] PathNameEscape = BuildPathNameEscapeSet();

    private static readonly char[] UpperHex = "0123456789ABCDEF".ToCharArray();

    private static bool[] BuildPathNameEscapeSet()
    {
        var set = new bool[128];
        for (int c = 0x01; c <= 0x1F; c++)
        {
            set[c] = true;
        }

        set[0x7F] = true;
        foreach (char c in "\"#%'*/:=?\\{[]^")
        {
            set[c] = true;
        }

        return set;
    }

    /// <summary>The physical on-disk directory-name encoding — byte-for-byte Apache Spark
    /// <c>ExternalCatalogUtils.escapePathName</c> (#806 layer 1). Escapes only the Hive <c>charToEscape</c>
    /// bitset as uppercase <c>%XX</c>; every other code point (all non-ASCII, and the space) passes through
    /// unescaped, so DeltaSharp's on-disk layout matches Spark/delta-rs.</summary>
    public static string EscapePathName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int firstEscape = -1;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c < 0x80 && PathNameEscape[c])
            {
                firstEscape = i;
                break;
            }
        }

        if (firstEscape < 0)
        {
            return value; // fast path: nothing to escape (the common ASCII-unreserved / non-ASCII case)
        }

        var sb = new StringBuilder(value.Length + 8);
        sb.Append(value, 0, firstEscape);
        for (int i = firstEscape; i < value.Length; i++)
        {
            char c = value[i];
            if (c < 0x80 && PathNameEscape[c])
            {
                sb.Append('%').Append(UpperHex[(c >> 4) & 0xF]).Append(UpperHex[c & 0xF]);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>The Delta-protocol <c>add.path</c> encoding (#806 layer 2): URI-encodes the assembled physical
    /// relative path (<see cref="EscapePathName"/> segments joined by <c>/</c>, plus the
    /// <c>part-*.parquet</c> file name) per the Delta protocol URI-encoded-path rule, preserving the <c>/</c>
    /// separators. Each <c>/</c>-delimited segment's octets are percent-encoded, so a layer-1 <c>%2F</c> becomes
    /// <c>%252F</c>, the literal <c>=</c> separator becomes <c>%3D</c>, a space becomes <c>%20</c>, and non-ASCII
    /// becomes its UTF-8 <c>%</c>-triplets. The read path recovers the physical object key by a single
    /// <c>Uri.UnescapeDataString</c> (<c>PartitionPathResolver</c>, Inc-A) — <c>UnescapeDataString(ToAddPath(p)) == p</c>
    /// for every physical path <c>p</c>.</summary>
    public static string ToAddPath(string physicalRelativePath)
    {
        ArgumentNullException.ThrowIfNull(physicalRelativePath);

        string[] segments = physicalRelativePath.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }

        return string.Join('/', segments);
    }

    /// <summary>Formats the value at <paramref name="row"/> of a partition column <paramref name="source"/>
    /// into its canonical Delta partition-value string, or <see langword="null"/> for a null value.</summary>
    public static string? FormatPartitionValue(ColumnVector source, int row)
    {
        if (source.IsNull(row))
        {
            return null;
        }

        return source.Type switch
        {
            BooleanType => source.GetValue<bool>(row) ? "true" : "false",
            ByteType => ((sbyte)source.GetValue<byte>(row)).ToString(CultureInfo.InvariantCulture),
            ShortType => source.GetValue<short>(row).ToString(CultureInfo.InvariantCulture),
            IntegerType => source.GetValue<int>(row).ToString(CultureInfo.InvariantCulture),
            LongType => source.GetValue<long>(row).ToString(CultureInfo.InvariantCulture),
            FloatType => source.GetValue<float>(row).ToString("R", CultureInfo.InvariantCulture),
            DoubleType => source.GetValue<double>(row).ToString("R", CultureInfo.InvariantCulture),
            StringType => Encoding.UTF8.GetString(source.GetBytes(row)),
            DateType => UnixEpochDate.AddDays(source.GetValue<int>(row)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimestampType or TimestampNtzType => FormatTimestamp(source.GetValue<long>(row)),
            DecimalType decimalType => FormatDecimal(source, row, decimalType),
            // TypeName, not SimpleString -- same reachability as AppendValue's default arm above.
            _ => throw new DeltaStorageException(
                StorageErrorKind.UnsupportedFeature,
                $"Type '{source.Type.TypeName}' is not supported as a Delta partition column."),
        };
    }

    private static string FormatTimestamp(long epochMicros)
    {
        DateTime utc = DateTime.UnixEpoch.AddTicks(epochMicros * TimeSpan.TicksPerMicrosecond);
        return utc.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    // System.Decimal spans an unscaled magnitude of at most decimal.MaxValue (~7.9e28, 29 digits); an
    // Int128 unscaled value from a precision-38 Delta decimal can exceed that. Precompute the inclusive
    // bounds once so FormatDecimal can range-check instead of letting the `(decimal)` cast throw a raw
    // OverflowException (which would escape the storage layer's deterministic exception contract).
    private static readonly Int128 MaxDecimalUnscaled = (Int128)decimal.MaxValue;
    private static readonly Int128 MinDecimalUnscaled = (Int128)decimal.MinValue;

    private static string FormatDecimal(ColumnVector source, int row, DecimalType type)
    {
        Int128 unscaled = type.IsCompact ? source.GetValue<long>(row) : source.GetValue<Int128>(row);
        if (unscaled < MinDecimalUnscaled || unscaled > MaxDecimalUnscaled)
        {
            throw new DeltaStorageException(
                StorageErrorKind.UnsupportedFeature,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Decimal partition value (precision {type.Precision}, scale {type.Scale}) has an " +
                    $"unscaled magnitude that exceeds System.Decimal's range and cannot be formatted as a " +
                    $"Delta partition value."));
        }

        decimal value = (decimal)unscaled;
        for (int i = 0; i < type.Scale; i++)
        {
            value /= 10m;
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }
}
