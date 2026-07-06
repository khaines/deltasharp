using Parquet.Schema;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Resolves the leaf <see cref="DataField"/>s of a Delta classic checkpoint's Parquet schema (design
/// §2.10.3) by navigating the field tree (top-level action <see cref="StructField"/>s → their
/// sub-fields → map key/value and list element leaves). Every column is optional: a checkpoint written by
/// a different engine may omit columns a baseline table never uses (e.g. <c>tags</c>), and unknown columns
/// are simply not resolved — matching <see cref="DeltaLogActionReader"/>'s forward-compatible tolerance.
/// Resolution binds only to the standard 3-level MAP (<c>key_value/key</c>,<c>key_value/value</c>) and
/// LIST (<c>list/element</c>) shapes Parquet.Net and Spark/delta-rs emit.
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
