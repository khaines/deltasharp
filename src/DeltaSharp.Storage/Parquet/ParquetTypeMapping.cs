using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Parquet.Schema;
using PqListField = Parquet.Schema.ListField;
using PqMapField = Parquet.Schema.MapField;
using PqStructField = Parquet.Schema.StructField;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Maps each ADR-0008 <b>atomic</b> <see cref="DataType"/> to a Parquet.Net <see cref="DataField"/>
/// with Spark-compatible physical semantics, and converts values between the engine's
/// <see cref="ColumnVector"/> physical layout and the CLR values Parquet.Net reads/writes
/// (design §2.9.1 "Spark-compatible physical semantics"). It mirrors
/// <c>LocalRelationBatches</c>/<c>RowMaterializer</c> for the temporal/decimal conversions, so a
/// value written and read through here is byte-for-byte the engine's physical representation.
/// </summary>
/// <remarks>
/// Supported (all round-trip): boolean, byte (Spark signed <c>tinyint</c>), short, integer, long,
/// float, double, string (UTF-8), binary, date (INT32 epoch-day), timestamp (INT64 epoch-micros,
/// <c>isAdjustedToUTC</c>), and decimal with precision ≤ 28. Deferred with a deterministic
/// <see cref="StorageErrorKind.UnsupportedFeature"/>: nested types (array/map/struct), the void
/// (null) type, and decimal with precision &gt; 28 (beyond the <see cref="decimal"/> range).
/// </remarks>
internal static class ParquetTypeMapping
{
    /// <summary>The largest decimal precision whose unscaled value fits in <see cref="decimal"/>'s
    /// 96-bit magnitude, so it round-trips through Parquet.Net's <see cref="decimal"/>-typed field.</summary>
    internal const int MaxSupportedDecimalPrecision = 28;

    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    // System.Decimal supports at most 28 fractional digits (mirrors RowMaterializer.MaxDecimalScale).
    private const int MaxDecimalScale = 28;
    private static readonly UInt128 MaxDecimalMagnitude = UInt128.MaxValue >> 32;

    /// <summary>
    /// Builds the Parquet <see cref="Field"/> for <paramref name="field"/> — a <see cref="DataField"/> for a
    /// scalar column, or the recursively-built nested container for the three single-level nested shapes the
    /// writer supports (design §2.9: <c>struct&lt;scalars&gt;</c>, <c>array&lt;scalar&gt;</c>,
    /// <c>map&lt;scalar,scalar&gt;</c>). This is the <b>WRITE door</b>; the read path uses the
    /// <see cref="DataField"/>-returning <see cref="CreateScalarField"/> so its raw-echo bound (a scalar
    /// <c>SimpleString</c> is a bounded literal) is retained by construction.
    /// </summary>
    /// <remarks>
    /// Fail-closed boundaries (design §2.4a/§2.6), all <b>before any byte is written</b>: a nested type
    /// nested WITHIN one of the three shapes (→ #585); a <c>nullable:false</c> nested container (Parquet.Net
    /// 6.1.0 exposes no public <c>Field.IsNullable</c> setter, so every container is OPTIONAL on the wire and
    /// honoring the declaration is impossible — #730 divergence refused); a zero-field struct; and a nested
    /// column carrying column-mapping metadata (nested leaf <c>field_id</c> stamping is deferred to #676,
    /// design §2.5/§2.7 — this PR is NONE-mode only).
    /// </remarks>
    /// <param name="field">The engine field to map.</param>
    /// <param name="honorReferenceNullability">
    /// When <see langword="true"/>, string/binary (reference-typed) columns follow the declared
    /// <see cref="StructField.Nullable"/> flag rather than Parquet.Net's reference-type default of
    /// always-nullable (#730). The WRITE path sets this so the footer's physical repetition matches
    /// the declared <c>schemaString</c>; the READ path passes <see langword="false"/> because it uses
    /// the result as the <b>always-nullable expected shape</b> its physical-vs-requested guard is
    /// written around — a foreign/legacy file may store a log-required string/binary column as
    /// OPTIONAL, and rejecting that would break reads of files DeltaSharp itself wrote before #730.
    /// <para>
    /// There is deliberately NO default. The two semantics are not interchangeable: the read value
    /// (<see langword="false"/>) is fail-OPEN on the write path — it re-creates exactly the #730
    /// footer↔log divergence this parameter exists to remove — so a defaulted parameter would let a
    /// future write call site pick the wrong one by saying nothing. Making it required puts the
    /// choice in front of the compiler at every present and future call site.
    /// </para>
    /// <para>
    /// The read path's asymmetry (its SCHEMA-level nullability guard cannot bite on a string/binary column
    /// while the expected shape is built always-nullable) is dispositioned (issue #807) by a VALUE-level
    /// required-lane guard at materialization (<c>ParquetFileReader.RejectNullInRequiredLane</c>): a
    /// physically-OPTIONAL string/binary column with no nulls still reads (foreign / pre-#730 tolerance), but
    /// the first actual null read into a requested non-nullable lane fails closed.
    /// </para>
    /// </param>
    /// <exception cref="DeltaStorageException">
    /// The field's type has no supported Parquet mapping
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>): a nested-within-nested type, a
    /// <c>nullable:false</c> nested container, a zero-field struct, the void type, or a decimal
    /// with precision &gt; 28.
    /// </exception>
    public static Field CreateField(StructField field, bool honorReferenceNullability)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.DataType switch
        {
            ArrayType or MapType or StructType => CreateNestedField(field, honorReferenceNullability),
            _ => CreateScalarField(field, honorReferenceNullability),
        };
    }

    // Builds one of the three single-level nested shapes (design §2.9). Every reject here is fail-closed
    // BEFORE any row group is written (§2.6), and every raw Parquet.Net Field-construction fault is wrapped
    // into the typed contract (§2.4, Security note 4) so no library exception escapes the write door.
    private static Field CreateNestedField(StructField field, bool honorReferenceNullability)
    {
        string label = DiagnosticText.Sanitize(field.Name);

        // #676/#839: column-mapping id mode is supported for a STRUCT container (its scalar children each
        // carry a delta.columnMapping.id stamped as the leaf field_id below) AND — since #839 — for an
        // array<scalar>/map<scalar,scalar> container whose interior element/key/value field_id comes from the
        // container field's delta.columnMapping.nested.ids. The container GROUP node still carries no field_id
        // (Parquet.Net 6.1.0 has no public group-node field_id setter, so the container binds by physical
        // name); only the interior LEAF field_id is stamped. A nested-within-nested interior is rejected first
        // (below / CreateNestedLeaf).
        bool idMode = ColumnMapping.TryGetId(field, out _);

        // §2.4a — Field.IsNullable has no public setter in Parquet.Net 6.1.0, so a nested container is always
        // OPTIONAL on the wire. Emitting a declared-REQUIRED container as OPTIONAL is exactly the footer↔log
        // divergence #730 exists to remove, so refuse it rather than write a file whose repetition contradicts
        // the committed schemaString.
        if (!field.Nullable)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for nested column '{label}': a non-nullable ('nullable':false) nested "
                + $"container ('{field.DataType.TypeName}') cannot be written — Parquet.Net emits every nested "
                + "container as OPTIONAL, which would diverge from the declared schema (#730).");
        }

        try
        {
            switch (field.DataType)
            {
                case ArrayType array:
                    {
                        long? elementId = ResolveArrayElementId(field, idMode, label);
                        return new PqListField(
                            field.Name,
                            CreateNestedLeaf(array.ElementType, array.ContainsNull, "element", $"array column '{label}' element", honorReferenceNullability, elementId));
                    }

                case MapType map:
                    {
                        (long? keyId, long? valueId) = ResolveMapInteriorIds(field, idMode, label);
                        return new PqMapField(
                            field.Name,
                            CreateNestedLeaf(map.KeyType, nullable: false, "key", $"map column '{label}' key", honorReferenceNullability, keyId),
                            CreateNestedLeaf(map.ValueType, map.ValueContainsNull, "value", $"map column '{label}' value", honorReferenceNullability, valueId));
                    }

                case StructType structType:
                    if (structType.Count == 0)
                    {
                        // NEW-5: Parquet.Net's StructField ctor raises a raw ArgumentException for this; reject
                        // it on the typed contract instead, matching EnsureReadSupported's read-side reject.
                        throw DeltaStorageException.UnsupportedFeature(
                            $"Parquet mapping for struct column '{label}': a zero-field struct is not supported.");
                    }

                    var children = new Field[structType.Count];
                    for (int i = 0; i < structType.Count; i++)
                    {
                        StructField child = structType[i];
                        // §2.8: identify the offending child by ORDINAL, never by name — the child name is
                        // foreign schema text and a struct can carry thousands of them.
                        children[i] = CreateStructChildLeaf(
                            child, idMode, $"struct column '{label}' field {i}", honorReferenceNullability);
                    }

                    return new PqStructField(field.Name, children);
                default:
                    // Unreachable: CreateField routes only Array/Map/Struct here.
                    throw DeltaStorageException.UnsupportedFeature(
                        $"Parquet mapping for column '{label}' of type '{field.DataType.TypeName}' is not supported.");
            }
        }
        catch (ArgumentException ex)
        {
            // A4/Security note 4: Parquet.Net's own Field constructors raise raw ArgumentExceptions (a map's
            // key cannot be nullable; a struct requires at least one element; an empty field name). Map them
            // onto the typed contract with a bounded message that echoes no library text. A2: the raw cause is
            // retained as the INNER exception (never surfaced by ToString, #664) so a genuine defect here is
            // still diagnosable rather than being flattened into "the shape could not be constructed".
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for nested column '{label}' of kind '{field.DataType.TypeName}': the nested "
                + "Parquet shape could not be constructed.",
                ex);
        }
    }

    // Builds a STRUCT CHILD leaf (#676). In NAME/none mode the child carries no mapping metadata, so the leaf
    // gets no field_id (a name-mode physical file is field_id-free). In ID mode every mapped struct child MUST
    // carry a stampable delta.columnMapping.id — an unstamped leaf would commit a permanently-unreadable file,
    // so a missing id fails closed at the write door (the "every mapped struct-leaf stamped + range-guarded"
    // assertion; the range guard itself lives in CreateScalarField). Passing the child through with its id
    // metadata makes CreateScalarField stamp the leaf field_id = the child's id.
    private static DataField CreateStructChildLeaf(
        StructField child, bool idMode, string context, bool honorReferenceNullability)
    {
        if (child.DataType is ArrayType or MapType or StructType)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for {context}: a nested type within a nested type ('{child.DataType.TypeName}') is not "
                + "supported (deferred, #585).");
        }

        if (idMode && !ColumnMapping.TryGetId(child, out _))
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for {context}: a mapped struct child under id mode has no "
                + "'delta.columnMapping.id' to stamp as its Parquet field_id; an unstamped leaf would be unreadable.");
        }

        try
        {
            // In id mode carry the child's mapping metadata through so CreateScalarField stamps + range-guards
            // the leaf field_id; in name/none mode strip it (a synthesized StructField with no mapping metadata).
            StructField leafField = idMode
                ? child
                : new StructField(child.Name, child.DataType, child.Nullable);
            return CreateScalarField(leafField, honorReferenceNullability);
        }
        catch (DeltaStorageException ex)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for {context}: the leaf type '{DiagnosticText.DescribeType(child.DataType)}' has no "
                + "supported Parquet mapping.",
                ex);
        }
    }

    // Builds one nested LEAF field (an array element, a map key/value). A nested child that
    // is itself nested is the #585 boundary and fails closed here — before any bytes. In name/none mode the
    // leaf carries NO field_id (design §2.5/F9 / C1): array/map interiors are not StructFields. In id mode
    // (#839) the interior <paramref name="fieldId"/> — derived from the container's delta.columnMapping.nested.ids
    // — is stamped onto the LEAF (range-guarded [1, int.MaxValue]) so an id-mode reader binds the interior by
    // field_id within the container subtree.
    private static DataField CreateNestedLeaf(
        DataType type, bool nullable, string name, string context, bool honorReferenceNullability, long? fieldId)
    {
        if (type is ArrayType or MapType or StructType)
        {
            // #683/#686: `type` is statically nested here, so SimpleString would recurse into every nested
            // field name verbatim; echo the bounded KIND instead (`context` already carries the sanitized
            // column label identifying WHICH column is at fault).
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for {context}: a nested type within a nested type ('{type.TypeName}') is not "
                + "supported (deferred, #585).");
        }

        DataField leaf;
        try
        {
            leaf = CreateScalarField(new StructField(name, type, nullable), honorReferenceNullability);
        }
        catch (DeltaStorageException ex)
        {
            // §2.8 (Security #3): CreateScalarField identifies the offending field BY NAME, and here that name
            // is a nested CHILD name — foreign schema text that must never reach a diagnostic, and which
            // mis-attributes the failure to a "column" that does not exist at top level. Re-raise on the
            // ORDINAL `context` (which already carries the sanitized top-level column label) plus the bounded
            // leaf KIND, so the failing COLUMN is correctly attributed and no child name is echoed.
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for {context}: the leaf type '{DiagnosticText.DescribeType(type)}' has no "
                + "supported Parquet mapping.",
                ex);
        }

        if (fieldId is long id)
        {
            // Write-door assertion: every id-mode interior leaf MUST be stamped + range-guarded — an unstamped
            // interior leaf commits a permanently-unreadable file (design §2.4). The synthesized StructField
            // above carries no id metadata, so CreateScalarField never stamped it; stamp the nested.ids-derived
            // interior field_id here under the same [1, int.MaxValue] range guard.
            if (id is <= 0 or > int.MaxValue)
            {
                throw DeltaStorageException.UnsupportedFeature(
                    $"Parquet mapping for {context}: the nested.ids interior field_id ({id}) is outside the Parquet "
                    + "field_id range [1, int.MaxValue].");
            }

            leaf.FieldId = (int)id;
        }

        return leaf;
    }

    // #839 write door: resolves the array<scalar> interior element field_id from the container field's
    // delta.columnMapping.nested.ids (keyed by <physicalName>.element). In id mode a missing/malformed interior
    // id fails closed — an unstamped interior leaf would commit a permanently-unreadable file. Name/none mode
    // stamps no interior field_id (returns null).
    private static long? ResolveArrayElementId(StructField field, bool idMode, string label)
    {
        if (!idMode)
        {
            return null;
        }

        if (!ColumnMapping.TryGetArrayElementId(field, field.Name, out long elementId))
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for array column '{label}': an id-mode array has no "
                + "'delta.columnMapping.nested.ids' element id to stamp as its interior Parquet field_id; an "
                + "unstamped interior leaf would be unreadable.");
        }

        return elementId;
    }

    // #839 write door: resolves the map<scalar,scalar> interior key/value field_ids from the container field's
    // delta.columnMapping.nested.ids (keyed by <physicalName>.key / .value). See ResolveArrayElementId.
    private static (long? KeyId, long? ValueId) ResolveMapInteriorIds(StructField field, bool idMode, string label)
    {
        if (!idMode)
        {
            return (null, null);
        }

        if (!ColumnMapping.TryGetMapKeyValueIds(field, field.Name, out long keyId, out long valueId))
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for map column '{label}': an id-mode map has no "
                + "'delta.columnMapping.nested.ids' key/value ids to stamp as its interior Parquet field_ids; an "
                + "unstamped interior leaf would be unreadable.");
        }

        return (keyId, valueId);
    }

    /// <summary>
    /// Builds the Parquet <see cref="DataField"/> for a <b>scalar</b> <paramref name="field"/>, choosing the
    /// nullable Parquet field when <see cref="StructField.Nullable"/> is set, and rejecting every nested type.
    /// This is the entry point the READ path uses: because it never returns a nested field, a caller may echo
    /// a requested scalar's <c>SimpleString</c> raw (a bounded literal), which the nested
    /// <see cref="CreateField"/> would not permit.
    /// </summary>
    /// <param name="field">The engine field to map.</param>
    /// <param name="honorReferenceNullability">See <see cref="CreateField"/>.</param>
    /// <exception cref="DeltaStorageException">
    /// The field's type has no supported Parquet mapping
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>): a nested type, the void type, or a decimal
    /// with precision &gt; 28.
    /// </exception>
    public static DataField CreateScalarField(StructField field, bool honorReferenceNullability)
    {
        ArgumentNullException.ThrowIfNull(field);
        bool nullable = field.Nullable;

        // #730: for reference-typed columns, DataField<T>(name) defaults IsNullable=true regardless
        // of the declared schema. On the WRITE path we pass the declared flag so a "nullable":false
        // string/binary column is emitted as a REQUIRED Parquet column (its footer repetition then
        // matches the log). Passing null keeps Parquet.Net's always-nullable default, which the READ
        // path's nullability guard is deliberately written around.
        bool? referenceNullable = honorReferenceNullability ? nullable : null;
        DataField dataField = field.DataType switch
        {
            BooleanType => Value<bool>(field.Name, nullable),
            ByteType => Value<sbyte>(field.Name, nullable),
            ShortType => Value<short>(field.Name, nullable),
            IntegerType => Value<int>(field.Name, nullable),
            LongType => Value<long>(field.Name, nullable),
            FloatType => Value<float>(field.Name, nullable),
            DoubleType => Value<double>(field.Name, nullable),
            // #730: string/binary are reference-typed in Parquet.Net, whose DataField<T>(name)
            // ctor defaults IsNullable=true regardless of the declared schema. On the write path
            // `referenceNullable` carries the declared flag so a "nullable":false column is emitted
            // REQUIRED (its footer repetition then matches the log); on the read path it is null,
            // preserving the always-nullable default the read guard is written around.
            StringType => new DataField<string>(field.Name, referenceNullable),
            BinaryType => new DataField<byte[]>(field.Name, referenceNullable),
            DateType => new DateTimeDataField(field.Name, DateTimeFormat.Date, isNullable: nullable),
            // Both timestamp lanes use DateTimeFormat.Timestamp + Micros, which emits the modern
            // LogicalType.TIMESTAMP{isAdjustedToUTC, unit=MICROS} (Parquet.Net's Timestamp format writes ONLY
            // the LogicalType, not a companion legacy ConvertedType). LogicalType is authoritative per the
            // Parquet spec and read by Spark/delta-rs/pyarrow/DuckDB; the only interop floor is a pre-2018
            // ConvertedType-only reader, which would see a bare INT64 (and for which no timestamp_ntz encoding
            // exists anyway). Older DeltaSharp files written with the legacy DateAndTimeMicros ConvertedType
            // still read back correctly (isAdjustedToUTC defaults true → TimestampType).
            TimestampType => new DateTimeDataField(
                field.Name, DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Micros, isNullable: nullable),
            // timestamp_ntz (#533): DateTimeFormat.Timestamp + Micros emits a conformant modern
            // LogicalType.TIMESTAMP{isAdjustedToUTC=false, unit=MICROS}. (The legacy DateAndTimeMicros format
            // instead hard-codes ConvertedType.TIMESTAMP_MICROS and drops isAdjustedToUTC — which is why it
            // could only ever express UTC.) The stored INT64 micros are byte-identical to the LTZ timestamp.
            TimestampNtzType => new DateTimeDataField(
                field.Name, DateTimeFormat.Timestamp, isAdjustedToUTC: false, unit: DateTimeTimeUnit.Micros, isNullable: nullable),
            DecimalType decimalType => CreateDecimalField(field.Name, decimalType, nullable),
            // #683/#686: `SimpleString` is NOT a bounded type name for a nested type — StructType.SimpleString
            // appends each field's Name VERBATIM and recurses, so it is simultaneously a raw-name echo and an
            // unbounded aggregate (a 5,000-field struct renders ~124,000 chars). This arm matches ONLY
            // Array/Map/Struct, so the bounded KIND ("array"/"map"/"struct") carries the same diagnosis
            // alongside the already-sanitized column label, with no unbounded foreign content.
            // A nested type is unmappable HERE by design: this is the SCALAR entry point (the read path's, and
            // the nested builder's leaf lane). The write door's nested shapes are built by CreateField.
            ArrayType or MapType or StructType => throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for column '{DiagnosticText.Sanitize(field.Name)}': a nested type is not a scalar "
                + $"Parquet field — '{field.DataType.TypeName}'."),
            _ => throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for column '{DiagnosticText.Sanitize(field.Name)}' of type '{DiagnosticText.Sanitize(field.DataType.SimpleString)}' "
                + "is not supported."),
        };

        // Column-mapping id mode (#523/#572): stamp the Parquet field_id = delta.columnMapping.id so an
        // id-mode reader can resolve this column by field_id (Parquet.Net persists DataField.FieldId to the
        // Thrift footer field 9). Only reached when the (physical) StructField carries the id metadata:
        // ColumnMapping.ToPhysicalSchema PRESERVES the id in id mode (so the id-mode create/append writer
        // stamps field_id) but DROPS it in name mode, so name/none-mode Parquet output is byte-unchanged
        // (issue #523 AC3). Delta ids are longs while Parquet field_id is an int32 — a table with an id
        // outside the int range is not a real scenario, but guard the cast so a malformed id fails loud rather
        // than silently truncating. Delta column-mapping ids start at 1 (AssignFreshMapping mints 1, 2, …), so
        // 0 (and any non-positive id) is out of range too — reject it rather than stamp a field_id the spec
        // never assigns (#572, deltaspec N1).
        if (ColumnMapping.TryGetId(field, out long fieldId))
        {
            if (fieldId is <= 0 or > int.MaxValue)
            {
                throw DeltaStorageException.UnsupportedFeature(
                    $"Column '{DiagnosticText.Sanitize(field.Name)}' has a delta.columnMapping.id ({fieldId}) outside the Parquet "
                    + "field_id range [1, int.MaxValue].");
            }

            dataField.FieldId = (int)fieldId;
        }

        return dataField;
    }

    /// <summary>
    /// Validates that a requested read column is a shape the <see cref="ParquetFileReader"/> can decode,
    /// throwing <see cref="StorageErrorKind.UnsupportedFeature"/> otherwise — BEFORE any row group is
    /// decoded, so an unsupported projection fails deterministically without materializing a partial batch.
    /// Beyond the scalar mappings <see cref="CreateScalarField"/> accepts, the reader also decodes the three
    /// single-level nested shapes (#571): a <b>struct of scalars</b>, an <b>array of a scalar</b>, and a
    /// <b>map of scalar→scalar</b>. Any nested type nested WITHIN one of those (array-of-struct, struct-of-
    /// list, map-of-map, …) is deliberately <b>not</b> in this increment and fails closed here rather than
    /// producing a partial/wrong read — Spark-parity fail-closed behavior. This does not widen the
    /// <b>writer</b>'s scope: the write door (<see cref="CreateField"/>) accepts exactly these three shapes
    /// (design §2.9), and <see cref="CreateScalarField"/> — the entry point used here — still rejects every
    /// nested type.
    /// </summary>
    /// <exception cref="DeltaStorageException">The requested column's type (or a nested leaf type) has no
    /// supported Parquet read mapping (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
    public static void EnsureReadSupported(StructField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        switch (field.DataType)
        {
            case ArrayType array:
                EnsureScalarReadable(array.ElementType, $"array column '{DiagnosticText.Sanitize(field.Name)}' element");
                break;
            case MapType map:
                EnsureScalarReadable(map.KeyType, $"map column '{DiagnosticText.Sanitize(field.Name)}' key");
                EnsureScalarReadable(map.ValueType, $"map column '{DiagnosticText.Sanitize(field.Name)}' value");
                break;
            case StructType structType:
                if (structType.Count == 0)
                {
                    // A zero-field struct has no leaf to drive the row count, so it would reconstruct a
                    // length-0 vector for a non-empty row group and trip a raw ArgumentException in the batch
                    // ctor — fail closed on the DeltaStorageException contract instead (parity with the prior
                    // CreateField reject of all nested types).
                    throw DeltaStorageException.UnsupportedFeature(
                        $"Parquet read for struct column '{DiagnosticText.Sanitize(field.Name)}': a zero-field struct is not supported.");
                }

                foreach (StructField nested in structType)
                {
                    EnsureScalarReadable(nested.DataType, $"struct column '{DiagnosticText.Sanitize(field.Name)}' field '{DiagnosticText.Sanitize(nested.Name)}'");
                }

                break;
            default:
                // Scalar (or unsupported scalar/void/decimal>28): the exact same validation the write path
                // uses. A nested type never reaches here (handled above), so this only rejects unsupported
                // scalars. Also stamps/validates any column-mapping id, preserving the prior read behavior.
                // honorReferenceNullability: false — this is a READ-path validation that only asks "does this
                // type map at all"; the returned field is discarded, so the repetition it carries is
                // immaterial and the read default is kept for continuity with ValidateFileField.
                _ = CreateScalarField(field, honorReferenceNullability: false);
                break;
        }
    }

    // A requested nested LEAF type (array element, map key/value, or struct field) must itself be a supported
    // SCALAR — a nested-within-nested leaf fails closed (#571 scopes only single-level nesting). Reuses
    // CreateScalarField's scalar validation (rejecting void and decimal precision > 28) so the read path accepts
    // exactly the scalars the write path can round-trip.
    private static void EnsureScalarReadable(DataType type, string context)
    {
        if (type is ArrayType or MapType or StructType)
        {
            // #683/#686: inside this guard `type` is Array/Map/Struct, so `SimpleString` would recursively
            // embed every nested field NAME verbatim and unbounded. Echo the bounded KIND instead; `context`
            // already carries the (sanitized) column label that identifies WHICH column is at fault.
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet read for {context}: a nested type within a nested type ('{type.TypeName}') is "
                + "not supported.");
        }

        // honorReferenceNullability: false — a discarded probe of the LEAF type's mappability; the
        // returned field's repetition is never observed.
        _ = CreateScalarField(new StructField("_leaf", type, nullable: true), honorReferenceNullability: false);
    }

    /// <summary>
    /// Reconstructs the DeltaSharp <see cref="DataType"/> a Parquet footer <see cref="DataField"/> encodes —
    /// the inverse of <see cref="CreateField"/>. Used by the write-door to derive the <b>actual physical data
    /// schema a staged file was written with</b> (read back from its footer) so schema enforcement gates the
    /// real bytes, not the caller's declaration (#497). Nullability is deliberately <b>not</b> reconstructed
    /// as authoritative here: a footer carries a column's physical REPETITION, not Spark nullability, and the
    /// two can legitimately disagree on a file this reader did not write — a foreign producer, or a DeltaSharp
    /// file written before #730, may store a log-<i>required</i> string/binary column as OPTIONAL. So a
    /// footer-derived schema is compared by name + type only.
    /// </summary>
    /// <exception cref="DeltaStorageException">The footer field's physical type has no supported DeltaSharp
    /// mapping (<see cref="StorageErrorKind.UnsupportedFeature"/>) — the inverse of the deferrals in
    /// <see cref="CreateField"/>.</exception>
    public static DataType ToDataType(DataField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (TryToDataType(field, out DataType? type))
        {
            return type;
        }

        // Message hygiene (#653): `field` is a Parquet FOOTER DataField read back from the file, so
        // `field.Name` is attacker-authored on a foreign file and is not echoed; the bounded physical type
        // description (a fixed Parquet vocabulary) is sufficient to diagnose the unsupported mapping.
        throw DeltaStorageException.UnsupportedFeature(
            $"A Parquet footer column has physical type '{DescribePhysical(field)}', which "
            + "has no supported DeltaSharp type mapping.");
    }

    /// <summary>
    /// The non-throwing form of <see cref="ToDataType"/>: reconstructs the DeltaSharp <see cref="DataType"/> a
    /// footer <see cref="DataField"/> encodes, returning <see langword="false"/> (rather than throwing) when
    /// the physical type has no supported mapping. Used by the read path's type-widening promotion to probe a
    /// file column's <b>physical</b> type without forcing an exception for an unmappable column, and by both
    /// read doors as the fail-closed mappability gate.
    /// <para>This method is <b>TOTAL</b>: it must answer for EVERY footer field a hostile or foreign file can
    /// carry, and must never throw. It runs on the hot read path, so any escaping exception would be
    /// unclassified — bypassing the <see cref="DeltaStorageException"/> handlers that implement the
    /// fail-closed contract. An annotation DeltaSharp cannot represent (an out-of-range DECIMAL, a TIME of any
    /// encoding) therefore returns <see langword="false"/> rather than propagating a validation failure.</para>
    /// </summary>
    public static bool TryToDataType(DataField field, [NotNullWhen(true)] out DataType? type)
    {
        ArgumentNullException.ThrowIfNull(field);

        // The annotated subtypes must be matched BEFORE the raw CLR switch: a DateTimeDataField's ClrType is
        // DateTime and a DecimalDataField's is decimal, so the temporal/decimal annotation carries the real
        // logical type.
        switch (field)
        {
            case DateTimeDataField dateTime:
                // With DateTimeFormat.Timestamp the footer now carries a faithful LogicalType.TIMESTAMP
                // (isAdjustedToUTC preserved), so a micros column maps back to its true logical type:
                // isAdjustedToUTC=true → timestamp (LTZ), false → timestamp_ntz (#533/#557). A DATE-format
                // column maps to DateType. (Legacy files written with the old DateAndTimeMicros ConvertedType
                // read back as isAdjustedToUTC=true → timestamp, which is correct for those UTC files.)
                type = dateTime.DateTimeFormat == DateTimeFormat.Date
                    ? DataTypes.DateType
                    : dateTime.IsAdjustedToUTC
                        ? DataTypes.TimestampType
                        : DataTypes.TimestampNtzType;
                return true;
            case DecimalDataField decimalField:
                // FAIL CLOSED on an out-of-range DECIMAL annotation rather than letting DecimalType's ctor
                // throw. DeltaSharp caps precision at 38 (Spark parity), but a Parquet footer can legally
                // declare more — Arrow's `decimal256` emits up to 76, and a hostile footer can declare
                // anything at all. TryToDataType is on the HOT read path (ParquetFileReader.ValidateFileField
                // calls it for every column of every file), so a raw SchemaValidationException here would
                // escape ReadAsync UNCLASSIFIED, sailing past every `catch (DeltaStorageException)` in the
                // read stack and past the fail-closed contract those handlers implement. Returning false
                // instead routes it through the SAME unmappable-type rejection as any other unsupported
                // physical type — a typed, fail-closed DeltaStorageException. This is what makes
                // TryToDataType TOTAL: it must answer for EVERY footer field, never throw.
                if (!IsRepresentableDecimal(decimalField))
                {
                    type = null;
                    return false;
                }

                type = DataTypes.CreateDecimalType(decimalField.Precision, decimalField.Scale);
                return true;
        }

        // TIME must FAIL CLOSED, across EVERY footer encoding of it (see IsTimeColumn). Checked AFTER the
        // annotated subtypes (a TIME column is neither DATE/TIMESTAMP nor DECIMAL) and BEFORE the raw CLR
        // switch below — which would otherwise reinterpret the sub-day units as IntegerType/LongType, a
        // SILENT data corruption rather than an error. This preserves the fail-closed contract that held
        // under Parquet.Net 6.0.3, where TIME surfaced as an unmapped TimeSpan.
        if (IsTimeColumn(field))
        {
            type = null;
            return false;
        }

        Type clr = field.ClrType;
        type =
            clr == typeof(bool) ? DataTypes.BooleanType
            : clr == typeof(sbyte) ? DataTypes.ByteType
            : clr == typeof(short) ? DataTypes.ShortType
            : clr == typeof(int) ? DataTypes.IntegerType
            : clr == typeof(long) ? DataTypes.LongType
            : clr == typeof(float) ? DataTypes.FloatType
            : clr == typeof(double) ? DataTypes.DoubleType
            : IsStringPhysicalClrType(clr) ? DataTypes.StringType
            : IsBinaryPhysicalClrType(clr) ? DataTypes.BinaryType
            : null;
        return type is not null;
    }

    /// <summary>
    /// Returns whether <paramref name="clr"/> is a Parquet.Net physical CLR shape for a UTF-8 string column.
    /// Parquet.Net ≥6.1 normalizes every string <see cref="DataField"/> to <see cref="ReadOnlyMemory{T}"/> of
    /// <see cref="char"/>; the <see cref="string"/> arm is a defensive shim for a downgraded/pinned older
    /// Parquet.Net and is unreachable at the version this project pins. Both encode the same logical type.
    /// </summary>
    internal static bool IsStringPhysicalClrType(Type clr) =>
        clr == typeof(string) || clr == typeof(ReadOnlyMemory<char>);

    /// <summary>
    /// Returns whether <paramref name="clr"/> is a Parquet.Net physical CLR shape for a binary column.
    /// Parquet.Net ≥6.1 normalizes every binary <see cref="DataField"/> to <see cref="ReadOnlyMemory{T}"/> of
    /// <see cref="byte"/>; the <see cref="byte"/>-array arm is a defensive shim for a downgraded/pinned older
    /// Parquet.Net and is unreachable at the version this project pins. Both encode the same logical type.
    /// </summary>
    internal static bool IsBinaryPhysicalClrType(Type clr) =>
        clr == typeof(byte[]) || clr == typeof(ReadOnlyMemory<byte>);

    /// <summary>
    /// Compares Parquet.Net footer physical CLR shapes, treating the pre-6.1 and 6.1 string/binary
    /// representations as equivalent while leaving every other type exact-match and fail-closed. Takes the
    /// two CLR types rather than the owning <see cref="DataField"/>s so a caller that only has a leaf's
    /// physical type (the nested reader) can reuse the same equivalence.
    /// </summary>
    internal static bool PhysicalClrTypesMatch(Type file, Type requested) =>
        file == requested
        || (IsStringPhysicalClrType(file) && IsStringPhysicalClrType(requested))
        || (IsBinaryPhysicalClrType(file) && IsBinaryPhysicalClrType(requested));

    /// <summary>
    /// Renders a Parquet.Net physical CLR type as an ACTIONABLE, bounded diagnostic token for an error
    /// message. <see cref="Type.Name"/> alone regressed to the opaque, self-contradictory
    /// <c>ReadOnlyMemory`1</c> under Parquet.Net 6.1 (which reports BOTH string and binary columns as a
    /// <see cref="ReadOnlyMemory{T}"/>), so a reader could not tell a UTF-8 column from a BYTE_ARRAY one — or
    /// from the type it actually asked for. Collapse both string shapes and both binary shapes onto the
    /// PARQUET vocabulary instead, and fall back to the type name for everything else.
    /// <para>Message-hygiene safe (#653): the output is drawn from a fixed vocabulary or from a Parquet.Net
    /// type name — never from file-derived, attacker-authored text — and is inherently short.</para>
    /// </summary>
    internal static string DescribePhysicalClrType(Type clr)
    {
        ArgumentNullException.ThrowIfNull(clr);
        return IsStringPhysicalClrType(clr) ? "string (BYTE_ARRAY/UTF8)"
            : IsBinaryPhysicalClrType(clr) ? "binary (BYTE_ARRAY)"
            : clr.Name;
    }

    /// <summary>
    /// The ANNOTATION-AWARE form of <see cref="DescribePhysicalClrType(Type)"/>, for a message that describes
    /// a whole footer <see cref="DataField"/> rather than a bare CLR type. A TIME column's CLR type is a raw
    /// <see cref="int"/>/<see cref="long"/> and a DECIMAL column's is a bare <see cref="decimal"/>, so
    /// describing either by CLR type alone produced a self-contradictory message — "physical type 'Int64'
    /// does not match the requested engine type 'bigint'", or "physical type 'Decimal' … cannot be read as
    /// 'decimal(10,2)'" — from which an operator could not see that the file column is a TIME, or WHICH
    /// decimal shape the file actually declares. Name the annotation instead; everything else keeps the
    /// CLR-shape rendering.
    /// <para>Message-hygiene safe (#653): a fixed vocabulary plus, for DECIMAL, the footer's own two small
    /// integers — never file-derived, attacker-authored TEXT.</para>
    /// </summary>
    internal static string DescribePhysical(DataField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (IsTimeColumn(field))
        {
            return $"Parquet TIME column ({DescribeTimeEncoding(field)})";
        }

        if (field is DecimalDataField decimalField)
        {
            // Name the footer's declared precision/scale, and — when they are the very reason the column was
            // rejected — the supported range, so the message is ACTIONABLE. The cause clause is conditional
            // because this method also renders MAPPABLE decimals: the CLR-shape gate in
            // ParquetFileReader.ValidateFileField describes an in-range decimal column that was requested as
            // some other engine type, and claiming "unsupported" there would be a lie.
            string cause = IsRepresentableDecimal(decimalField)
                ? string.Empty
                : $" (unsupported: precision must be in [{DecimalType.MinPrecision}, "
                    + $"{DecimalType.MaxPrecision}] and scale in [0, precision])";
            return $"Parquet DECIMAL(precision {decimalField.Precision}, scale {decimalField.Scale}) "
                + $"column{cause}";
        }

        return DescribePhysicalClrType(field.ClrType);
    }

    /// <summary>
    /// Returns whether a footer DECIMAL annotation is within the range DeltaSharp's <see cref="DecimalType"/>
    /// can represent. A Parquet footer may legally declare more than DeltaSharp's Spark-parity cap of 38
    /// (Arrow's <c>decimal256</c> emits up to 76) and a hostile footer can declare anything at all, so this is
    /// the single source of truth shared by the fail-closed check in <see cref="TryToDataType"/> and the
    /// message rendered by <see cref="DescribePhysical(DataField)"/> — the two must never disagree about
    /// whether a column is representable.
    /// </summary>
    private static bool IsRepresentableDecimal(DecimalDataField field) =>
        field.Precision is >= DecimalType.MinPrecision and <= DecimalType.MaxPrecision
        && field.Scale >= 0
        && field.Scale <= field.Precision;

    /// <summary>
    /// Returns whether <paramref name="field"/> is a Parquet <b>TIME</b> (time-of-day) column under ANY of the
    /// encodings a footer can carry. DeltaSharp has no time-of-day logical type, so every one of them must
    /// fail closed:
    /// <list type="bullet">
    /// <item><description>Parquet.Net's own <see cref="TimeDataField"/>, which it materializes when — and
    /// ONLY when — the footer carries <c>LogicalType.TIME</c>.</description></item>
    /// <item><description><c>LogicalType.TIME</c> read straight off the footer's
    /// <c>SchemaElement</c>. At the pinned Parquet.Net every well-formed logical TIME already specializes to
    /// <see cref="TimeDataField"/>, so this arm is currently REDUNDANT with the first — it is forward-compat
    /// defense, catching a future Parquet.Net that stops specializing the field, or a TIME shape it does not
    /// specialize.</description></item>
    /// <item><description>The LEGACY <c>ConvertedType</c>-only encoding (<c>TIME_MILLIS</c>/
    /// <c>TIME_MICROS</c>, no <c>LogicalType</c> at all) that parquet-mr ≤1.10, Hive, Impala and older Spark
    /// emit — and that is trivially forgeable. Parquet.Net 6.1 surfaces THOSE as a PLAIN
    /// <see cref="DataField"/> whose <c>ClrType</c> is a raw <see cref="int"/>/<see cref="long"/>, so a
    /// <see cref="TimeDataField"/> test alone lets a whole class of real-world TIME columns through and reads
    /// their sub-day units as int/bigint.</description></item>
    /// </list>
    /// <para>The first and third arms are the ones exercised at the pinned Parquet.Net version; both are
    /// mutation-pinned by tests, at the flat and nested read doors and at the schema door.</para>
    /// <para><c>DataField.SchemaElement</c> is <see langword="null"/> on a field this process CONSTRUCTED (it
    /// is populated only for a field read back from a real footer), hence the null-conditional access.</para>
    /// </summary>
    internal static bool IsTimeColumn(DataField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field is TimeDataField
            || field.SchemaElement?.LogicalType?.TIME is not null
            || field.SchemaElement?.ConvertedType is global::Parquet.Meta.ConvertedType.TIME_MILLIS
                or global::Parquet.Meta.ConvertedType.TIME_MICROS;
    }

    /// <summary>Names the TIME unit a footer field encodes, drawn from whichever annotation carries it. The
    /// legacy <c>ConvertedType</c> vocabulary has no NANOS member, so a ConvertedType-only column is always
    /// millis or micros.</summary>
    private static string DescribeTimeEncoding(DataField field)
    {
        if (field is TimeDataField time)
        {
            return time.Precision switch
            {
                TimeUnitPrecision.Millis => "TIME_MILLIS",
                TimeUnitPrecision.Micros => "TIME_MICROS",
                _ => "TIME_NANOS",
            };
        }

        global::Parquet.Meta.TimeType? logical = field.SchemaElement?.LogicalType?.TIME;
        if (logical is not null)
        {
            return logical.Unit?.MILLIS is not null ? "TIME_MILLIS"
                : logical.Unit?.MICROS is not null ? "TIME_MICROS"
                : "TIME_NANOS";
        }

        return field.SchemaElement?.ConvertedType == global::Parquet.Meta.ConvertedType.TIME_MILLIS
            ? "TIME_MILLIS"
            : "TIME_MICROS";
    }

    /// <summary>
    /// Reconstructs the DeltaSharp data <see cref="StructType"/> a written Parquet footer encodes, walking
    /// <see cref="ParquetSchema.Fields"/> <b>recursively</b> — the inverse of <see cref="CreateField"/>, so a
    /// nested (<c>list</c>/<c>map</c>/<c>struct</c>) footer column maps back to its DECLARED LOGICAL shape
    /// rather than to its flattened leaves. This is the ACTUAL physical schema of the bytes on disk, used by
    /// the write-door for #497 physical write-schema validation. Compared by name + logical type only: the
    /// reconstructed <see cref="StructField.Nullable"/> is the footer's physical REPETITION, which since #730
    /// matches the declared schema for files DeltaSharp writes but need not on a foreign or pre-#730 file, and
    /// field metadata is not carried in a footer at all — neither is footer-faithful.
    /// </summary>
    /// <remarks>
    /// <para><b>Consumer scope (design §2.4, Decision 5).</b> Recursion is confined to the #497 write door
    /// (<c>ParquetFileReader.ReadDataSchemaAsync</c>), which reads DeltaSharp's OWN just-written bytes. The
    /// one foreign, attacker-controlled footer-schema consumer — CDF-EE-08 (#662) — keeps its historical
    /// leaf-flattening shape via <see cref="ToDataLeafSchema"/>, so its accept/reject acceptance set is
    /// unchanged by this recursion.</para>
    /// <para><b>Depth bound (N2).</b> The walk is depth-capped at <see cref="MaxFooterTypeDepth"/> type levels
    /// (mirroring <c>DeltaWriteSchemaEligibility.MaxDepth</c>) and the cap is enforced on ENTRY to each level,
    /// so the recursion can never exceed that many frames — an uncatchable <see cref="StackOverflowException"/>
    /// is a pod abort the caller's fail-closed boundary could not catch, whereas a footer deeper than the cap
    /// fails closed on the typed <see cref="StorageErrorKind.UnsupportedFeature"/> contract.</para>
    /// </remarks>
    /// <exception cref="DeltaStorageException">A footer field has no supported DeltaSharp mapping, or the
    /// footer's type tree is deeper than <see cref="MaxFooterTypeDepth"/>
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
    public static StructType ToDataSchema(ParquetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var fields = new List<StructField>(schema.Fields.Count);
        foreach (Field field in schema.Fields)
        {
            fields.Add(new StructField(field.Name, ToDataType(field, depth: 1), IsFieldNullable(field)));
        }

        return new StructType(fields);
    }

    /// <summary>
    /// Reconstructs the DeltaSharp data <see cref="StructType"/> a Parquet footer encodes by <b>flattening it
    /// to leaves</b> (each <see cref="ParquetSchema.DataFields"/> mapped via <see cref="ToDataType(DataField)"/>,
    /// in footer order, named by its leaf-local name) — the historical <c>ToDataSchema</c> behavior, retained
    /// verbatim for CDF-EE-08 (#662).
    /// </summary>
    /// <remarks>
    /// Design §2.4 / Decision 5: <c>ParquetFileReader.ReadDataLeafColumnsAsync</c> is the ONLY foreign,
    /// attacker-controlled footer-schema consumer, and it interpolates a leaf's reconstructed type into a
    /// user-visible message. Keeping it on this leaf-flattening shaping function makes its accept/reject
    /// acceptance set <b>invariant</b> across the nested-write change and keeps every type it can see atomic.
    /// </remarks>
    /// <exception cref="DeltaStorageException">A footer leaf has no supported DeltaSharp mapping
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
    public static StructType ToDataLeafSchema(ParquetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var fields = new List<StructField>();
        foreach (DataField field in schema.DataFields)
        {
            fields.Add(new StructField(field.Name, ToDataType(field), field.IsNullable));
        }

        return new StructType(fields);
    }

    /// <summary>The hard cap on a footer type tree's depth for the recursive <see cref="ToDataSchema"/>,
    /// mirroring <c>DeltaWriteSchemaEligibility.MaxDepth</c>. A deeper footer fails closed (N2).</summary>
    internal const int MaxFooterTypeDepth = 64;

    // Maps one footer Field (leaf or container) at nesting `depth` (1 = a top-level column). The cap is
    // checked BEFORE recursing, so the call depth is bounded by MaxFooterTypeDepth + 1 frames.
    private static DataType ToDataType(Field field, int depth)
    {
        if (depth > MaxFooterTypeDepth)
        {
            // Message hygiene (#653): the footer is foreign on some paths, so no field name is echoed — the
            // bound itself is the whole diagnosis.
            throw DeltaStorageException.UnsupportedFeature(
                $"A Parquet footer declares a type tree deeper than the supported limit of {MaxFooterTypeDepth} "
                + "type levels.");
        }

        switch (field)
        {
            case DataField leaf:
                return ToDataType(leaf);
            case PqListField list:
                return DataTypes.CreateArrayType(
                    ToDataType(list.Item, depth + 1), containsNull: IsFieldNullable(list.Item));
            case PqMapField map:
                return DataTypes.CreateMapType(
                    ToDataType(map.Key, depth + 1),
                    ToDataType(map.Value, depth + 1),
                    valueContainsNull: IsFieldNullable(map.Value));
            case PqStructField structField:
                var children = new List<StructField>(structField.Fields.Count);
                foreach (Field child in structField.Fields)
                {
                    children.Add(new StructField(
                        child.Name, ToDataType(child, depth + 1), IsFieldNullable(child)));
                }

                return DataTypes.CreateStructType(children);
            default:
                // Message hygiene (#653): a foreign footer's field name is never echoed; the bounded Parquet
                // schema-kind vocabulary is sufficient to diagnose the unmappable shape.
                throw DeltaStorageException.UnsupportedFeature(
                    $"A Parquet footer column has schema kind '{field.SchemaType}', which has no supported "
                    + "DeltaSharp type mapping.");
        }
    }

    // A container's own repetition. Parquet.Net models a top-level nested container as OPTIONAL (it exposes no
    // public IsNullable setter), so this is the footer's physical repetition — never re-derived.
    private static bool IsFieldNullable(Field field) => field.IsNullable;

    private static DataField Value<T>(string name, bool nullable)
        where T : unmanaged =>
        nullable ? new DataField<T?>(name) : new DataField<T>(name);

    private static DataField CreateDecimalField(string name, DecimalType type, bool nullable)
    {
        if (type.Precision > MaxSupportedDecimalPrecision)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet mapping for column '{DiagnosticText.Sanitize(name)}': decimal precision {type.Precision} exceeds the "
                + $"System.Decimal limit of {MaxSupportedDecimalPrecision} (phased, design §2.9).");
        }

        return new DecimalDataField(name, type.Precision, type.Scale, isNullable: nullable);
    }

    // ----- Temporal conversions (mirror LocalRelationBatches / RowMaterializer) -----

    /// <summary>Converts a DeltaSharp epoch-day (days since 1970-01-01) to the UTC-midnight
    /// <see cref="DateTime"/> Parquet.Net writes for a DATE column.</summary>
    /// <exception cref="DeltaStorageException">The epoch-day is outside the representable
    /// <see cref="DateTime"/> range (<see cref="StorageErrorKind.CorruptData"/>) — mapped
    /// deterministically so no raw <see cref="ArgumentOutOfRangeException"/> escapes the codec contract,
    /// mirroring <see cref="EpochMicrosToDateTime"/>.</exception>
    public static DateTime EpochDayToDateTime(int epochDay)
    {
        try
        {
            return DateTime.UnixEpoch.AddDays(epochDay);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            throw DeltaStorageException.CorruptData(
                "a date epoch-day value is outside the representable DateTime range.", ex);
        }
    }

    /// <summary>Converts a Parquet DATE <see cref="DateTime"/> back to the DeltaSharp epoch-day.</summary>
    public static int DateTimeToEpochDay(DateTime value) =>
        DateOnly.FromDateTime(value).DayNumber - UnixEpochDate.DayNumber;

    /// <summary>Converts a DeltaSharp epoch-microsecond instant to the UTC <see cref="DateTime"/>
    /// Parquet.Net writes for a micros TIMESTAMP column.</summary>
    /// <exception cref="DeltaStorageException">The value is outside the representable
    /// <see cref="DateTime"/> range (<see cref="StorageErrorKind.CorruptData"/>) — mapped
    /// deterministically so no raw <see cref="OverflowException"/> escapes the codec contract.</exception>
    public static DateTime EpochMicrosToDateTime(long micros)
    {
        try
        {
            long ticks = checked(DateTime.UnixEpoch.Ticks + (micros * TimeSpan.TicksPerMicrosecond));
            return new DateTime(ticks, DateTimeKind.Utc);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            throw DeltaStorageException.CorruptData(
                "a timestamp epoch-microsecond value is outside the representable DateTime range.", ex);
        }
    }

    /// <summary>Converts a Parquet micros TIMESTAMP <see cref="DateTime"/> (as decoded by Parquet.Net) back to
    /// the DeltaSharp epoch-microsecond instant. Reads the raw ticks <b>Kind-agnostically</b> — Parquet.Net's
    /// reader labels a decoded value <see cref="DateTimeKind.Utc"/> for an <c>isAdjustedToUTC=true</c> column
    /// and <see cref="DateTimeKind.Local"/> for an <c>isAdjustedToUTC=false</c> (<c>timestamp_ntz</c>) column,
    /// but that <see cref="DateTimeKind"/> is a <i>semantic label</i>, NOT an instruction to shift: the stored
    /// micros are already the value DeltaSharp wants (the UTC instant for LTZ, the wall-clock for ntz). A
    /// <see cref="DateTime.ToUniversalTime"/> here would wrongly offset an ntz value by the host time zone
    /// (#533/#557).</summary>
    public static long DateTimeToEpochMicros(DateTime value) =>
        (value.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;

    // ----- Decimal conversions -----

    /// <summary>Reads the unscaled decimal at <paramref name="index"/> from <paramref name="column"/>
    /// and reconstructs the <see cref="decimal"/> at the declared scale (mirrors
    /// <c>RowMaterializer.ReadDecimal</c>).</summary>
    public static decimal ReadDecimal(ColumnVector column, DecimalType type, int index)
    {
        if (type.Scale > MaxDecimalScale)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"decimal scale {type.Scale} exceeds the System.Decimal maximum of {MaxDecimalScale}.");
        }

        Int128 unscaled = type.IsCompact ? column.GetValue<long>(index) : column.GetValue<Int128>(index);
        bool isNegative = unscaled < 0;
        UInt128 magnitude = isNegative ? (UInt128)(-unscaled) : (UInt128)unscaled;
        if (magnitude > MaxDecimalMagnitude)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"a '{type.SimpleString}' value's unscaled magnitude exceeds the 96-bit System.Decimal range.");
        }

        int lo = unchecked((int)(uint)magnitude);
        int mid = unchecked((int)(uint)(magnitude >> 32));
        int hi = unchecked((int)(uint)(magnitude >> 64));
        return new decimal(lo, mid, hi, isNegative, (byte)type.Scale);
    }

    /// <summary>Encodes a <see cref="decimal"/> read from Parquet as the unscaled integer lane value and
    /// appends it to <paramref name="vector"/> (mirrors <c>LocalRelationBatches.AppendDecimal</c>).</summary>
    /// <exception cref="DeltaStorageException">The value cannot be represented at the declared
    /// scale/precision, or scaling overflows <see cref="decimal"/>/<see cref="Int128"/>
    /// (<see cref="StorageErrorKind.CorruptData"/>); the scale is unsupported
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
    public static void AppendDecimal(MutableColumnVector vector, DecimalType type, decimal value) =>
        AppendDecimal(vector, type, value, DecimalScaleFactors.For(type));

    /// <summary>The two loop-invariant powers <see cref="AppendDecimal(MutableColumnVector, DecimalType,
    /// decimal, DecimalScaleFactors)"/> needs — the decimal scaling factor <c>10^scale</c> and the
    /// <see cref="Int128"/> over-precision ceiling <c>10^precision</c>. Hoisted once per column chunk (L1)
    /// so the O(exponent) power loops run once per chunk, not once per value.</summary>
    internal readonly struct DecimalScaleFactors
    {
        private DecimalScaleFactors(decimal scaleFactor, Int128 precisionCeiling)
        {
            ScaleFactor = scaleFactor;
            PrecisionCeiling = precisionCeiling;
        }

        internal decimal ScaleFactor { get; }

        internal Int128 PrecisionCeiling { get; }

        /// <summary>Validates the scale and precomputes the powers for <paramref name="type"/>.</summary>
        /// <exception cref="DeltaStorageException">The scale exceeds the <see cref="decimal"/> maximum
        /// (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
        internal static DecimalScaleFactors For(DecimalType type)
        {
            if (type.Scale > MaxDecimalScale)
            {
                throw DeltaStorageException.UnsupportedFeature(
                    $"decimal scale {type.Scale} exceeds the System.Decimal maximum of {MaxDecimalScale}.");
            }

            return new DecimalScaleFactors(Pow10Decimal(type.Scale), Pow10Int128(type.Precision));
        }
    }

    /// <summary>Encodes <paramref name="value"/> using the pre-hoisted <paramref name="factors"/>,
    /// avoiding the per-value <c>Pow10</c> recompute (L1). Overflow of the scale multiply or the
    /// <see cref="Int128"/> conversion maps to <see cref="StorageErrorKind.CorruptData"/> rather than
    /// letting a raw <see cref="OverflowException"/> escape the codec contract (mirrors
    /// <c>LocalRelationBatches.AppendDecimal</c>).</summary>
    internal static void AppendDecimal(
        MutableColumnVector vector, DecimalType type, decimal value, in DecimalScaleFactors factors)
    {
        decimal scaled;
        try
        {
            scaled = value * factors.ScaleFactor;
        }
        catch (OverflowException ex)
        {
            throw DeltaStorageException.CorruptData(
                $"decimal value is out of range for type '{type.SimpleString}'.", ex);
        }

        if (scaled != decimal.Truncate(scaled))
        {
            throw DeltaStorageException.CorruptData(
                $"decimal value cannot be represented at scale {type.Scale} without loss of precision.");
        }

        Int128 unscaled;
        try
        {
            unscaled = Int128.CreateChecked(scaled);
        }
        catch (OverflowException ex)
        {
            throw DeltaStorageException.CorruptData(
                $"decimal value is out of range for type '{type.SimpleString}'.", ex);
        }

        // Over-precision guard (§2.9.1 mandates it on the read path too): a value whose unscaled
        // magnitude reaches 10^precision does not fit the declared decimal(P,S) and is corrupt. Mirrors
        // LocalRelationBatches.AppendDecimal.
        Int128 magnitude = unscaled < 0 ? -unscaled : unscaled;
        if (magnitude >= factors.PrecisionCeiling)
        {
            throw DeltaStorageException.CorruptData(
                $"decimal value does not fit in precision {type.Precision} (type '{type.SimpleString}').");
        }

        if (type.IsCompact)
        {
            vector.AppendValue((long)unscaled);
        }
        else
        {
            vector.AppendValue(unscaled);
        }
    }

    private static decimal Pow10Decimal(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }

    private static Int128 Pow10Int128(int exponent)
    {
        Int128 result = Int128.One;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }
}
