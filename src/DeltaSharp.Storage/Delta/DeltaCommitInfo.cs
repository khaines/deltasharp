using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Builds the per-operation <see cref="CommitInfoAction"/> provenance the Delta ecosystem
/// (<c>DESCRIBE HISTORY</c>, delta-standalone/-rs/-spark) reads: the <c>operation</c> string and its
/// <c>operationParameters</c>. The commit choke point (<see cref="DeltaCommitter"/>) stamps the remaining
/// fields it alone owns — the epoch-ms <c>timestamp</c> (from the injected <see cref="TimeProvider"/>, never
/// a wall clock), the <c>engineInfo</c> constant, and the idempotency <c>txnId</c> — so each write site only
/// declares WHAT it did, not the ambient clock/version stamps.
///
/// <para><b>operationParameters encoding.</b> Per the Delta spec each value is a <b>JSON-encoded string</b>
/// (a scalar is a quoted JSON string literal; a list is a JSON array). Every value this factory produces is
/// therefore already-encoded via <see cref="JsonString"/> / <see cref="JsonStringArray"/>, matching the
/// raw-token contract documented on <see cref="CommitInfoAction.OperationParameters"/>. Callers must not
/// hand-concatenate JSON.</para>
/// </summary>
internal static class DeltaCommitInfo
{
    /// <summary>Delta's <c>operation</c> string for an append/overwrite data write.</summary>
    internal const string WriteOperation = "WRITE";

    /// <summary>Delta's <c>operation</c> string for a table create (we use the plain form; DeltaSharp does
    /// not currently distinguish CREATE TABLE AS SELECT — the create commit carries data adds but the plain
    /// <c>CREATE TABLE</c> label is the safe, always-correct choice noted in the issue).</summary>
    internal const string CreateTableOperation = "CREATE TABLE";

    /// <summary>Delta's <c>operation</c> string for a DELETE.</summary>
    internal const string DeleteOperation = "DELETE";

    /// <summary>Delta's <c>operation</c> string for an OPTIMIZE (bin-packing compaction).</summary>
    internal const string OptimizeOperation = "OPTIMIZE";

    private static readonly ImmutableSortedDictionary<string, string> EmptyParameters =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };

    /// <summary>Provenance for an append/overwrite data write: <c>operation="WRITE"</c> with
    /// <c>mode</c> (<c>"Append"</c> / <c>"Overwrite"</c>) and <c>partitionBy</c> (a JSON array of the
    /// partition-column names, <c>[]</c> when unpartitioned) — the shape delta-spark emits for a write.</summary>
    internal static CommitInfoAction Write(bool overwrite, IReadOnlyList<string> partitionColumns) =>
        WithOperation(WriteOperation, Parameters(
            ("mode", JsonString(overwrite ? "Overwrite" : "Append")),
            ("partitionBy", JsonStringArray(partitionColumns))));

    /// <summary>Provenance for a table create: <c>operation="CREATE TABLE"</c> with a <c>partitionBy</c>
    /// JSON array (minimal + Delta-shaped; richer <c>isManaged</c>/<c>properties</c> keys are deferred —
    /// emitting a correct minimal shape beats guessing a wrong one, per the issue).</summary>
    internal static CommitInfoAction CreateTable(IReadOnlyList<string> partitionColumns) =>
        WithOperation(CreateTableOperation, Parameters(
            ("partitionBy", JsonStringArray(partitionColumns))));

    /// <summary>Provenance for a DELETE: <c>operation="DELETE"</c> only. The Delta <c>predicate</c> parameter
    /// (a JSON array of SQL predicate strings) is non-trivial to render faithfully from the internal delete
    /// predicate, so it is omitted rather than emitted in a wrong shape (the issue sanctions this) — the
    /// operation itself is what <c>DESCRIBE HISTORY</c> needs.</summary>
    internal static CommitInfoAction Delete() => WithOperation(DeleteOperation, EmptyParameters);

    /// <summary>Provenance for an OPTIMIZE: <c>operation="OPTIMIZE"</c> with an empty <c>predicate</c> JSON
    /// array (<c>[]</c>), matching delta-spark's whole-table compaction. <c>operationMetrics</c> is
    /// deliberately NOT added here (deferred to #506).</summary>
    internal static CommitInfoAction Optimize() =>
        WithOperation(OptimizeOperation, Parameters(("predicate", "[]")));

    private static CommitInfoAction WithOperation(
        string operation, ImmutableSortedDictionary<string, string> parameters) =>
        new(
            ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
            Operation: operation,
            OperationParameters: parameters);

    private static ImmutableSortedDictionary<string, string> Parameters(
        params (string Key, string Value)[] pairs)
    {
        ImmutableSortedDictionary<string, string>.Builder builder =
            ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in pairs)
        {
            builder[key] = value;
        }

        return builder.ToImmutable();
    }

    /// <summary>Encodes <paramref name="value"/> as a JSON string literal (with quotes), e.g.
    /// <c>Append</c> ⇒ <c>"Append"</c>.</summary>
    internal static string JsonString(string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStringValue(value);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Encodes <paramref name="values"/> as a JSON array of strings, e.g.
    /// <c>["region","year"]</c> (<c>[]</c> when empty).</summary>
    internal static string JsonStringArray(IReadOnlyList<string> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (string value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
