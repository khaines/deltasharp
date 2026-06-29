using Apache.Arrow;
using DeltaSharp.Engine.Columnar.Arrow;
using Xunit;
using ArrowInt32Type = Apache.Arrow.Types.Int32Type;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// Field metadata is deliberately dropped at the v1 boundary (council columnar F5). v1 maps only
/// name/type/nullability; Arrow field metadata (Delta column-mapping IDs, Spark field comments) is NOT
/// round-tripped. <c>StructField.Equals</c> includes metadata, so round-tripping it would otherwise be
/// required for schema identity — this pins the deliberate drop in both directions so it can't silently
/// change.
/// </summary>
public class ArrowBatchConverterMetadataTests
{
    [Fact]
    public void RoundTrip_FieldMetadata_IsDroppedInBothDirections()
    {
        var metadata = new Dictionary<string, string>
        {
            ["delta.columnMapping.id"] = "7",
            ["comment"] = "a field comment",
        };
        Int32Array values = new Int32Array.Builder().Append(1).AppendNull().Append(3).Build();
        var field = new Field("c", ArrowInt32Type.Default, nullable: true, metadata: metadata);
        using var source = new RecordBatch(
            new Schema(new[] { field }, metadata: null), new IArrowArray[] { values }, length: 3);

        // Precondition: the source field really carries metadata.
        Assert.True(source.Schema.GetFieldByIndex(0).HasMetadata);

        using ArrowColumnBatch imported = ArrowBatchConverter.FromArrow(source);

        // Import drops it: the DeltaSharp field carries no metadata.
        Assert.True(imported.Schema[0].Metadata.IsEmpty);

        using RecordBatch exported = ArrowBatchConverter.ToArrow(imported);

        // Export does not re-introduce it: the round-tripped Arrow field carries no metadata.
        Assert.False(exported.Schema.GetFieldByIndex(0).HasMetadata);
    }
}
