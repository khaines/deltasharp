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
    /// <c>mode</c> (<c>"Append"</c> / <c>"Overwrite"</c>), <c>partitionBy</c> (the partition-column names),
    /// and <c>isBlindAppend</c> — the shape delta-spark emits for a write.
    ///
    /// <para><b>Value encoding (delta-spark <c>DeltaOperations.jsonEncodedValues</c> parity).</b>
    /// operationParameters is a <c>Map&lt;String,String&gt;</c>, so EVERY value is a JSON <b>string</b>. A
    /// scalar is a plain JSON string (<c>mode</c> ⇒ <c>"Append"</c>). A list/bool is <b>double-encoded</b>:
    /// the JSON array/bool is itself serialized into a JSON string, so <c>partitionBy</c> ⇒
    /// <c>"[\"region\"]"</c> (a quoted string whose content parses to the array) and <c>isBlindAppend</c> ⇒
    /// <c>"true"</c>. Strict readers (delta-standalone, legacy Spark) deserialize the map as
    /// <c>Map&lt;String,String&gt;</c> and reject a bare array — hence the outer string-encoding.</para></summary>
    internal static CommitInfoAction Write(
        bool overwrite, IReadOnlyList<string> partitionColumns, bool isBlindAppend) =>
        WithOperation(WriteOperation, Parameters(
            ("mode", JsonString(overwrite ? "Overwrite" : "Append")),
            ("partitionBy", JsonEncodedArray(partitionColumns)),
            ("isBlindAppend", JsonBoolString(isBlindAppend))));

    /// <summary>Provenance for a table create: <c>operation="CREATE TABLE"</c> with a double-encoded
    /// <c>partitionBy</c> JSON-string token (minimal + Delta-shaped; richer <c>isManaged</c>/<c>properties</c>
    /// keys are deferred — emitting a correct minimal shape beats guessing a wrong one, per the issue).</summary>
    internal static CommitInfoAction CreateTable(IReadOnlyList<string> partitionColumns) =>
        WithOperation(CreateTableOperation, Parameters(
            ("partitionBy", JsonEncodedArray(partitionColumns))));

    /// <summary>Provenance for a DELETE: <c>operation="DELETE"</c> only. The Delta <c>predicate</c> parameter
    /// (a JSON array of SQL predicate strings) is non-trivial to render faithfully from the internal delete
    /// predicate, so it is omitted rather than emitted in a wrong shape (the issue sanctions this) — the
    /// operation itself is what <c>DESCRIBE HISTORY</c> needs.</summary>
    internal static CommitInfoAction Delete() => WithOperation(DeleteOperation, EmptyParameters);

    /// <summary>Provenance for an OPTIMIZE: <c>operation="OPTIMIZE"</c> with an empty <c>predicate</c> — a
    /// double-encoded JSON-string token whose content is the empty array (<c>"[]"</c>), matching delta-spark's
    /// whole-table compaction. <c>operationMetrics</c> is deliberately NOT added here (deferred to #506).</summary>
    internal static CommitInfoAction Optimize() =>
        WithOperation(OptimizeOperation, Parameters(("predicate", JsonEncodedArray(Array.Empty<string>()))));

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

    /// <summary>Double-encodes <paramref name="values"/> into an operationParameters value token: the JSON
    /// array is itself JSON-string-encoded, e.g. <c>["region"]</c> ⇒ <c>"[\"region\"]"</c> and
    /// <c>[]</c> ⇒ <c>"[]"</c>. This is the <c>Map&lt;String,String&gt;</c> shape delta-spark emits (a bare
    /// array would break strict readers). The result is a quoted JSON string, still a valid raw JSON value
    /// for the writer's <c>WriteRawValue</c>.</summary>
    internal static string JsonEncodedArray(IReadOnlyList<string> values) => JsonString(JsonStringArray(values));

    /// <summary>Encodes <paramref name="value"/> as a JSON <b>string</b> token whose content is the JSON
    /// boolean literal, e.g. <c>true</c> ⇒ <c>"true"</c> — the double-encoded shape delta-spark uses for a
    /// boolean operationParameter (still a <c>Map&lt;String,String&gt;</c> value).</summary>
    internal static string JsonBoolString(bool value) => JsonString(value ? "true" : "false");
}
