using Parquet.Schema;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Resolves the leaf <see cref="DataField"/>s of a Delta classic checkpoint's Parquet schema (design
/// §2.10.3) by navigating the field tree (top-level action <see cref="StructField"/>s → their
/// sub-fields → map key/value and list element leaves). Every column is optional: a checkpoint written by
/// a different engine may omit columns a baseline table never uses (e.g. <c>tags</c>), and unknown columns
/// are simply not resolved — matching <see cref="DeltaLogActionReader"/>'s forward-compatible tolerance.
///
/// <para>Maps and lists are bound through Parquet.Net's <b>logical</b> <see cref="MapField"/>/
/// <see cref="ListField"/> abstraction (via <c>.Key</c>/<c>.Value</c>/<c>.Item</c>), so the resolution is
/// agnostic to the physical group/element names — it works for the standard 3-level MAP
/// (<c>key_value</c>) and LIST (<c>list</c>/<c>element</c>) shapes Spark/delta-rs/parquet-mr emit
/// regardless of whether a writer names the list element <c>element</c> or <c>array_element</c>. A column
/// a writer encodes in a shape Parquet.Net does <b>not</b> surface as a logical MAP/LIST (e.g. a legacy
/// 2-level list from <c>writeLegacyFormat=true</c>, which no mainstream Delta writer emits) resolves to
/// null and is read as empty; for <c>partitionColumns</c> that would present a partitioned table as
/// unpartitioned — a v1-scope limitation tracked for a golden-file compatibility test.</para>
/// </summary>
internal sealed class CheckpointSchema
{
    private CheckpointSchema()
    {
    }

    // add
    public DataField? AddPath { get; private init; }
    public DataField? AddSize { get; private init; }
    public DataField? AddModificationTime { get; private init; }
    public DataField? AddDataChange { get; private init; }
    public DataField? AddStats { get; private init; }
    public MapLeaves? AddPartitionValues { get; private init; }
    public MapLeaves? AddTags { get; private init; }

    // remove
    public DataField? RemovePath { get; private init; }
    public DataField? RemoveDeletionTimestamp { get; private init; }
    public DataField? RemoveDataChange { get; private init; }
    public DataField? RemoveExtendedFileMetadata { get; private init; }
    public DataField? RemoveSize { get; private init; }
    public MapLeaves? RemovePartitionValues { get; private init; }

    // metaData
    public DataField? MetaId { get; private init; }
    public DataField? MetaName { get; private init; }
    public DataField? MetaDescription { get; private init; }
    public DataField? MetaSchemaString { get; private init; }
    public DataField? MetaCreatedTime { get; private init; }
    public DataField? FormatProvider { get; private init; }
    public MapLeaves? FormatOptions { get; private init; }
    public DataField? MetaPartitionColumns { get; private init; }
    public MapLeaves? MetaConfiguration { get; private init; }

    // protocol
    public DataField? ProtocolMinReaderVersion { get; private init; }
    public DataField? ProtocolMinWriterVersion { get; private init; }
    public DataField? ProtocolReaderFeatures { get; private init; }
    public DataField? ProtocolWriterFeatures { get; private init; }

    // txn
    public DataField? TxnAppId { get; private init; }
    public DataField? TxnVersion { get; private init; }
    public DataField? TxnLastUpdated { get; private init; }

    /// <summary>A map's required-key and optional-value leaf columns.</summary>
    public sealed record MapLeaves(DataField Key, DataField Value);

    public static CheckpointSchema Resolve(ParquetSchema schema)
    {
        StructField? add = Struct(schema, "add");
        StructField? remove = Struct(schema, "remove");
        StructField? metaData = Struct(schema, "metaData");
        StructField? format = metaData is null ? null : Child(metaData, "format") as StructField;
        StructField? protocol = Struct(schema, "protocol");
        StructField? txn = Struct(schema, "txn");

        return new CheckpointSchema
        {
            AddPath = Scalar(add, "path"),
            AddSize = Scalar(add, "size"),
            AddModificationTime = Scalar(add, "modificationTime"),
            AddDataChange = Scalar(add, "dataChange"),
            AddStats = Scalar(add, "stats"),
            AddPartitionValues = Map(add, "partitionValues"),
            AddTags = Map(add, "tags"),

            RemovePath = Scalar(remove, "path"),
            RemoveDeletionTimestamp = Scalar(remove, "deletionTimestamp"),
            RemoveDataChange = Scalar(remove, "dataChange"),
            RemoveExtendedFileMetadata = Scalar(remove, "extendedFileMetadata"),
            RemoveSize = Scalar(remove, "size"),
            RemovePartitionValues = Map(remove, "partitionValues"),

            MetaId = Scalar(metaData, "id"),
            MetaName = Scalar(metaData, "name"),
            MetaDescription = Scalar(metaData, "description"),
            MetaSchemaString = Scalar(metaData, "schemaString"),
            MetaCreatedTime = Scalar(metaData, "createdTime"),
            FormatProvider = Scalar(format, "provider"),
            FormatOptions = Map(format, "options"),
            MetaPartitionColumns = ListElement(metaData, "partitionColumns"),
            MetaConfiguration = Map(metaData, "configuration"),

            ProtocolMinReaderVersion = Scalar(protocol, "minReaderVersion"),
            ProtocolMinWriterVersion = Scalar(protocol, "minWriterVersion"),
            ProtocolReaderFeatures = ListElement(protocol, "readerFeatures"),
            ProtocolWriterFeatures = ListElement(protocol, "writerFeatures"),

            TxnAppId = Scalar(txn, "appId"),
            TxnVersion = Scalar(txn, "version"),
            TxnLastUpdated = Scalar(txn, "lastUpdated"),
        };
    }

    /// <summary>All resolved leaf <see cref="DataField"/>s this reader will decode for a row group — the
    /// inputs to the decode-ceiling guard (design §5.4 C-DECODE). Only columns actually read are included
    /// (unknown columns are neither resolved nor bounded).</summary>
    public IReadOnlyList<DataField> LeafFields()
    {
        var fields = new List<DataField>(24);
        void Add(DataField? f)
        {
            if (f is not null)
            {
                fields.Add(f);
            }
        }

        void AddMap(MapLeaves? m)
        {
            if (m is not null)
            {
                fields.Add(m.Key);
                fields.Add(m.Value);
            }
        }

        Add(AddPath); Add(AddSize); Add(AddModificationTime); Add(AddDataChange); Add(AddStats);
        AddMap(AddPartitionValues); AddMap(AddTags);
        Add(RemovePath); Add(RemoveDeletionTimestamp); Add(RemoveDataChange); Add(RemoveExtendedFileMetadata);
        Add(RemoveSize); AddMap(RemovePartitionValues);
        Add(MetaId); Add(MetaName); Add(MetaDescription); Add(MetaSchemaString); Add(MetaCreatedTime);
        Add(FormatProvider); AddMap(FormatOptions); Add(MetaPartitionColumns); AddMap(MetaConfiguration);
        Add(ProtocolMinReaderVersion); Add(ProtocolMinWriterVersion); Add(ProtocolReaderFeatures);
        Add(ProtocolWriterFeatures);
        Add(TxnAppId); Add(TxnVersion); Add(TxnLastUpdated);
        return fields;
    }

    private static StructField? Struct(ParquetSchema schema, string name)
    {
        foreach (Field field in schema.Fields)
        {
            if (field is StructField structField && string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                return structField;
            }
        }

        return null;
    }

    private static Field? Child(StructField parent, string name)
    {
        foreach (Field field in parent.Fields)
        {
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                return field;
            }
        }

        return null;
    }

    private static DataField? Scalar(StructField? parent, string name) =>
        parent is null ? null : Child(parent, name) as DataField;

    private static MapLeaves? Map(StructField? parent, string name)
    {
        if (parent is null || Child(parent, name) is not MapField map)
        {
            return null;
        }

        return map is { Key: DataField key, Value: DataField value } ? new MapLeaves(key, value) : null;
    }

    private static DataField? ListElement(StructField? parent, string name)
    {
        if (parent is null || Child(parent, name) is not ListField list)
        {
            return null;
        }

        return list.Item as DataField;
    }
}
