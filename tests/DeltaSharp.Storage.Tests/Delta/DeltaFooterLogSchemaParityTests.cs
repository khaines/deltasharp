using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Parquet;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// The <b>log-side</b> artifact layer for issue #679, and the only place anything compares a real
/// <c>_delta_log</c> <c>metaData.schemaString</c> to a real Parquet footer <c>delta.schema</c>.
/// </summary>
/// <remarks>
/// <para>
/// #679 consolidated the footer serializer onto the shared one, which closes <i>"two
/// implementations disagree"</i>. The artifact guards in <c>ParquetWriterTests</c> then close
/// <i>"one implementation, given less than it was told"</i> and <i>"what we wrote differs from
/// what we declared"</i>. Every one of those keys on the <b>footer</b> call site
/// (<c>ParquetFileWriter</c>), and each compares the footer to a <b>helper invocation</b> of
/// <c>SchemaJson.ToJson</c> made by the test itself.
/// </para>
/// <para>
/// That leaves the original defect's own shape unguarded. <c>DeltaTableWriter</c> calls
/// <c>SchemaJson.ToJson</c> at seven <b>sibling call sites</b> to produce
/// <c>metaData.schemaString</c>, and a transform applied at any of them — the shared serializer
/// left entirely alone, its <i>input</i> narrowed on the way in — reproduces #679's footer/log
/// divergence exactly, with every footer-side guard still green, because no footer-side guard ever
/// looks at the log. Comparing the footer to a helper cannot see it either: the helper is not the
/// log. The fourth guarantee is <i>"the two sides of the same commit disagree"</i>, and only an
/// end-to-end comparison of the two real artifacts can hold it.
/// </para>
/// <para>
/// So these tests go through the public write door and read both artifacts back off disk: the
/// committed log JSON, and the footer of the data file that commit points at.
/// </para>
/// <para>
/// Where the property is <b>equality</b> -- the unpartitioned, unmapped case -- nothing is
/// re-serialized in between: both sides are bytes read off disk and compared directly. That is
/// deliberate, because re-serializing either side would once again compare an artifact to a
/// helper, which is the mistake this PR exists to remove.
/// </para>
/// <para>
/// Where the two artifacts are <b>supposed to differ</b> -- partitioning drops the partition
/// columns from the footer, column mapping replaces logical names with physical ones -- equality
/// is the wrong assertion and would fail on correct output, so those tests necessarily compute an
/// expectation. What keeps that honest is not avoiding serialization but <b>where the expectation
/// comes from</b>: it is derived from the CALLER'S schema, which the test owns, and never from the
/// write path's own mapping helpers. A helper that turns test-owned input into an expectation sits
/// OUTSIDE both the prober and the probed; a helper that re-derives one of the two artifacts being
/// compared sits BETWEEN them. Only the second is a tautology.
/// </para>
/// </remarks>
public sealed class DeltaFooterLogSchemaParityTests : IDisposable
{
    private const string CreateSeed = "issue-679-footerlog-create";
    private const string EvolveSeed = "issue-679-footerlog-evolve";

    private readonly string _root;

    /// <summary>A fixed clock, so commit timestamps are not a source of variation.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Deterministic data-file names, so the mapping seeds are the only source of variation.</summary>
    private static Func<string> FileNames()
    {
        int counter = 0;
        return () => "file" + counter++.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public DeltaFooterLogSchemaParityTests() =>
        _root = Path.Combine(Path.GetTempPath(), "delta-footerlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <param name="useScalarCorpus">
    /// Which input schema to drive. The guard was originally keyed on one hand-written schema of
    /// three fields and two types, so a divergence conditioned on a type it did not contain was
    /// unreachable here while being well covered on the footer side. The corpus arm ranges over
    /// every atomic type the writer accepts plus the decimal family at its boundaries, and it is
    /// the SAME object the footer-side tests use, so a newly accepted type widens both at once.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryCommit_FooterSchemaString_IsByteIdenticalToTheLogInEffect(bool useScalarCorpus)
    {
        StructType initial = useScalarCorpus ? ScalarCorpus.Schema : HostileSchema();

        StructType widened = Widen(initial, "added_one");
        StructType final = Widen(widened, "added_two");

        StructType replaced = Widen(final, "replaced_by_overwrite");

        // The OPERATION set, not just the commit set. Schema-carrying commits are produced by
        // several different DeltaTableWriter call sites, and an append-only sequence reaches only
        // some of them: a transform installed at the OVERWRITE site is invisible to any number of
        // appends. ParityGuard_DrivesEveryWriteEntryPoint holds this honest.
        await AppendAsync(initial, Array.Empty<string>(), mergeSchema: false);
        await AppendAsync(widened, Array.Empty<string>(), mergeSchema: true);
        await AppendAsync(final, Array.Empty<string>(), mergeSchema: true);
        await OverwriteAsync(replaced);

        IReadOnlyList<(long Version, string Path, string Declared)> written = ReadCommittedFiles();

        // The sequence must actually have evolved, or this passes by writing the same schema four
        // times -- the shape of accidental coverage this file keeps finding elsewhere.
        Assert.Equal(4, written.Count);
        Assert.Equal(4, written.Select(x => x.Declared).Distinct(StringComparer.Ordinal).Count());

        // AND THE LOG MUST DECLARE WHAT THE CALLER HANDED IT. Footer-vs-log equality alone cannot
        // see a transform that narrows the schema on the way into an EVOLUTION commit and leaves
        // the two artifacts agreeing with each other about the wrong thing -- both sides derive
        // from the same narrowed input at some sites. The damage from such a transform is real and
        // compliance-visible: field metadata carries classification and PII tags, and losing them
        // from the on-disk schema is silent. So the final commit is also compared against the
        // schema this test asked for.
        Assert.Equal(SchemaJson.ToJson(replaced), written[^1].Declared);

        foreach ((long version, string path, string declared) in written)
        {
            string footer = await ReadFooterSchema(path);
            if (!string.Equals(footer, declared, StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"Commit {version} declares a schemaString its own data file does not carry -- "
                    + "issue #679's divergence, at the two REAL artifacts."
                    + $"{Environment.NewLine}  file: {path}"
                    + $"{Environment.NewLine}  log    ({declared.Length} chars): {Truncate(declared)}"
                    + $"{Environment.NewLine}  footer ({footer.Length} chars): {Truncate(footer)}"
                    + $"{Environment.NewLine}  first difference at char {FirstDifference(declared, footer)}");
            }
        }
    }

    /// <summary>
    /// Pins the one place the two artifacts are legitimately allowed to differ, across partition
    /// counts and POSITIONS rather than the single case it was first written for.
    /// </summary>
    /// <remarks>
    /// A partitioned table stores partition values in the <c>add</c> action's
    /// <c>partitionValues</c> rather than in the data file, so the footer legitimately declares
    /// FEWER columns than the log (measured, issue #724). It is stated as an exact relationship --
    /// footer equals the log's schema minus exactly the partition columns, same order, byte for
    /// byte -- rather than as a mere inequality.
    /// <para>
    /// THE INSTANTIATION IS THE GUARD. At a single LEADING partition column, "drop the named
    /// partition columns" and "drop the leading N columns" are indistinguishable, so a pruner that
    /// silently switched from name-based to position-based would satisfy this test while corrupting
    /// every other layout. That was measured, not imagined: such a transform is invisible at
    /// position 0 and caught at positions 1 and 2. Hence the theory varies POSITION (first, middle,
    /// last) and COUNT (one, two, and two NON-ADJACENT), because a relationship verified only where
    /// two different rules coincide is a coincidence of the case chosen, not a property.
    /// </para>
    /// <para>
    /// The partition VALUES also vary per row, so the commit genuinely produces more than one data
    /// file and every one of them is compared. An earlier version asserted a single file existed,
    /// which put multi-file commits outside the guard; removing that assertion was not enough on
    /// its own, because the batch still carried one distinct partition value and so still produced
    /// exactly one file. The file count is therefore ASSERTED here rather than described, since a
    /// claim about coverage that nothing checks is how the first version came to be wrong.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 1 })]
    [InlineData(new[] { 2 })]
    [InlineData(new[] { 0, 1 })]
    [InlineData(new[] { 1, 2 })]
    [InlineData(new[] { 0, 2 })]
    public async Task Partitioned_FooterOmitsExactlyThePartitionColumns_AndNothingElse(int[] positions)
    {
        StructType schema = HostileSchema();
        string[] partitionColumns = positions.Select(i => schema[i].Name).ToArray();

        await AppendPartitionedAsync(schema, partitionColumns);

        var remainder = new StructType(
            schema.Where(f => !partitionColumns.Contains(f.Name, StringComparer.Ordinal)).ToArray());
        string expected = SchemaJson.ToJson(remainder);

        IReadOnlyList<(long Version, string Path, string Declared)> written = ReadCommittedFiles();

        // Two rows carrying DIFFERENT partition values must land in two different partition
        // directories, hence two data files. Asserted, because the previous version of this test
        // described multi-file coverage it did not actually have.
        Assert.Equal(2, written.Count);

        foreach ((long _, string path, string declared) in written)
        {
            Assert.Equal(SchemaJson.ToJson(schema), declared);
            Assert.NotEqual(declared, await ReadFooterSchema(path));
            Assert.Equal(expected, await ReadFooterSchema(path));
        }
    }

    /// <summary>
    /// Writes one partitioned commit whose two rows differ in EVERY partition column, so the
    /// commit spans two partition directories and produces two data files.
    /// </summary>
    private async Task AppendPartitionedAsync(StructType schema, IReadOnlyList<string> partitionColumns)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);

        var vectors = new ColumnVector[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            StructField field = schema[i];
            MutableColumnVector vector = ColumnVectors.Create(field.DataType, 2);
            if (partitionColumns.Contains(field.Name, StringComparer.Ordinal))
            {
                AppendPartitionValue(vector, field.DataType, 0);
                AppendPartitionValue(vector, field.DataType, 1);
            }
            else
            {
                ScalarCorpus.AppendOne(vector, field.DataType);
                ScalarCorpus.AppendOne(vector, field.DataType);
            }

            vectors[i] = vector;
        }

        await WriteEntryPointRecorder.DriveAsync(
            target, t => t.AppendAsync(schema, partitionColumns, new[] { new ManagedColumnBatch(schema, vectors, 2) }));
    }

    /// <summary>Appends partition value number <paramref name="row"/>, distinct per row.</summary>
    /// <remarks>
    /// Fails closed: a partition column of a type with no arm here would otherwise silently get
    /// two IDENTICAL values, collapsing the commit back to a single file and quietly removing the
    /// multi-file coverage this test exists to provide.
    /// </remarks>
    private static void AppendPartitionValue(MutableColumnVector vector, DataType type, int row)
    {
        switch (type)
        {
            case StringType:
                vector.AppendBytes(Encoding.UTF8.GetBytes(row == 0 ? "west" : "east"));
                break;
            case LongType:
                vector.AppendValue(row == 0 ? 1L : 2L);
                break;
            default:
                Assert.Fail(
                    $"No DISTINCT partition value is constructible here for {type.SimpleString}, so "
                    + "partitioning on it would collapse to one file and silently narrow this test.");
                break;
        }
    }

    /// <summary>
    /// The <b>column-mapping</b> evolution commit, which is the one sibling call site the
    /// footer/log byte comparison structurally cannot reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under column mapping the two artifacts are <b>supposed</b> to differ: the log carries
    /// logical names plus <c>delta.columnMapping.physicalName</c>, while the footer carries the
    /// physical names with empty metadata. So byte equality is the wrong assertion there -- it
    /// would fail on correct output -- and the site stays dark if the only guard is the byte
    /// comparison. Measured: a metadata-narrowing transform installed at that site is green under
    /// the byte-parity test even after that test was widened to range over every commit.
    /// </para>
    /// <para>
    /// What still holds unconditionally is that the mapping commit may only <i>add</i> mapping
    /// keys. Everything the caller declared has to survive into the committed log verbatim, which
    /// is exactly what an input-narrowing transform at that site destroys. The expectation is the
    /// caller's own metadata -- test-owned ground truth -- so nothing derived from the write path
    /// sits between the assertion and the code it audits. In particular this does NOT call
    /// <c>ColumnMapping.MapWriteSchemaToPhysical</c> to build the expectation, which would put a
    /// production helper on both sides and re-create the tautology this PR exists to remove.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ColumnMappingEvolution_PreservesEveryMetadataEntryTheCallerDeclared()
    {
        StructType created = MappableSchema();
        Func<string> names = FileNames();

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await WriteEntryPointRecorder.DriveAsync(
                target, t => t.CreateNameMappedTableAsync(created, Array.Empty<string>(), new[] { BuildBatch(created, "west") },
                new SeededPhysicalNameSource(CreateSeed)));
        }

        // mergeSchema on a name-mapped table routes through the column-mapping evolution branch,
        // which is a DIFFERENT SchemaJson.ToJson call site from the plain-merge one.
        StructType evolved = Widen(created, "added");
        using (DeltaWriteTarget append = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names,
            new SeededPhysicalNameSource(EvolveSeed)))
        {
            await WriteEntryPointRecorder.DriveAsync(
                append, t => t.AppendAsync(
                    evolved, Array.Empty<string>(), new[] { BuildBatch(evolved, "east") }, mergeSchema: true));
        }

        IReadOnlyList<(long Version, string Path, string Declared)> written = ReadCommittedFiles();
        Assert.Equal(2, written.Count);

        // The evolution commit's log, parsed off disk. Every caller-declared entry must be present
        // verbatim; the commit is allowed to ADD delta.columnMapping.* and nothing else.
        var logged = (StructType)SchemaJson.FromJson(written[^1].Declared);
        Assert.Equal(evolved.Count, logged.Count);

        foreach (StructField expected in evolved)
        {
            StructField actual = Assert.Single(logged, f => f.Name == expected.Name);

            foreach (KeyValuePair<string, MetadataValue> entry in expected.Metadata)
            {
                Assert.True(
                    actual.Metadata.TryGetValue(entry.Key, out MetadataValue? value),
                    $"Field '{expected.Name}' lost metadata key '{entry.Key}' in the column-mapping "
                    + "evolution commit; the caller declared it and only mapping keys may be added.");
                Assert.Equal(entry.Value, value);
            }

            foreach (string key in actual.Metadata.Keys)
            {
                Assert.True(
                    expected.Metadata.ContainsKey(key) || key.StartsWith("delta.columnMapping.", StringComparison.Ordinal),
                    $"Field '{expected.Name}' gained metadata key '{key}', which the caller never "
                    + "declared and which is not a column-mapping key.");
            }
        }
    }

    /// <summary>
    /// The remaining fresh-create entry points, each of which commits a <c>metaData</c> from its
    /// own call site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are driven because <c>ParityGuard_DrivesEveryWriteEntryPoint</c> derives the required
    /// operation set from the product and named them as unreachable from this suite. They were not
    /// chosen; they were reported.
    /// </para>
    /// <para>
    /// The assertion is metadata PRESERVATION rather than footer/log byte equality, because the
    /// mapped seams legitimately rewrite names into the footer -- the same reason the column-mapping
    /// evolution test uses this invariant. It holds for every seam here: whatever the caller
    /// declared must appear in the committed log, and only <c>delta.columnMapping.*</c> may be
    /// added. That is exactly what an input-narrowing transform at any of these sites destroys.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(CreateSeam.IdMapped)]
    [InlineData(CreateSeam.NameMappedDeletionVector)]
    [InlineData(CreateSeam.IdMappedDeletionVector)]
    [InlineData(CreateSeam.DeletionVector)]
    public async Task EveryCreateSeam_PreservesEveryMetadataEntryTheCallerDeclared(CreateSeam seam)
    {
        StructType created = MappableSchema();
        var batches = new[] { BuildBatch(created, "west") };

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames(),
            new SeededPhysicalNameSource(CreateSeed)))
        {
            var source = new SeededPhysicalNameSource(CreateSeed);
            switch (seam)
            {
                case CreateSeam.IdMapped:
                    await WriteEntryPointRecorder.DriveAsync(
                        target, t => t.CreateIdMappedTableAsync(created, Array.Empty<string>(), batches, source));
                    break;
                case CreateSeam.NameMappedDeletionVector:
                    await WriteEntryPointRecorder.DriveAsync(
                        target, t => t.CreateNameMappedDeletionVectorTableAsync(created, Array.Empty<string>(), batches, source));
                    break;
                case CreateSeam.IdMappedDeletionVector:
                    await WriteEntryPointRecorder.DriveAsync(
                        target, t => t.CreateIdMappedDeletionVectorTableAsync(created, Array.Empty<string>(), batches, source));
                    break;
                case CreateSeam.DeletionVector:
                    await WriteEntryPointRecorder.DriveAsync(
                        target, t => t.CreateDeletionVectorTableAsync(created, Array.Empty<string>(), batches));
                    break;
                default:
                    Assert.Fail(
                        $"CreateSeam.{seam} was added but this test does not drive it, so its "
                        + "call site would have no footer/log guard.");
                    break;
            }
        }

        IReadOnlyList<(long Version, string Path, string Declared)> written = ReadCommittedFiles();
        Assert.NotEmpty(written);
        AssertDeclaredPreservesCallerMetadata(created, written[^1].Declared);
    }

    /// <summary>The fresh-create entry points this suite drives, one per <c>metaData</c> call site.</summary>
    public enum CreateSeam
    {
        IdMapped,
        NameMappedDeletionVector,
        IdMappedDeletionVector,
        DeletionVector,
    }

    /// <summary>
    /// Asserts the committed log declares everything the caller did, adding only mapping keys.
    /// </summary>
    /// <summary>
    /// ALTER RENAME/DROP COLUMN rewrites the committed <c>schemaString</c>, and must carry every
    /// metadata entry the caller declared through the rewrite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This drives <c>DeltaTableWriter.CommitSchemaChangeAsync</c>, a <c>SchemaJson.ToJson</c> call
    /// site that no other test in this suite could reach. It was invisible to the operation-set
    /// guard for a structural reason worth recording: that guard derived its required set from
    /// <c>typeof(DeltaWriteTarget)</c> methods returning <c>Task&lt;DeltaWriteResult&gt;</c>, and
    /// ALTER is on a different type returning a different result, so it was excluded BY
    /// CONSTRUCTION rather than by oversight. A transform installed at only this site left the
    /// entire suite green while an ALTER silently dropped a field's classification metadata.
    /// </para>
    /// <para>
    /// There is no footer to compare against here -- ALTER is metadata-only and writes no data
    /// file -- so the assertion is the one that holds without a sibling artifact: the caller's own
    /// declared metadata, which is test-owned ground truth, survives the rewrite. That is the same
    /// property the column-mapping evolution test pins, at the seam ALTER owns.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterColumn_PreservesEveryMetadataEntryTheCallerDeclared(bool drop)
    {
        StructType created = MappableSchema();

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames(),
            new SeededPhysicalNameSource(CreateSeed)))
        {
            await WriteEntryPointRecorder.DriveAsync(
                target, t => t.CreateNameMappedTableAsync(created, Array.Empty<string>(), new[] { BuildBatch(created, "west") },
                new SeededPhysicalNameSource(CreateSeed)));
        }

        var writer = new DeltaTableWriter(new LocalFileSystemBackend(_root));
        StructType expected;

        if (drop)
        {
            await WriteEntryPointRecorder.DriveAsync(
                writer, t => t.DropColumnAsync("naïve"));
            expected = new StructType(created.Where(f => f.Name != "naïve").ToArray());
        }
        else
        {
            await WriteEntryPointRecorder.DriveAsync(
                writer, t => t.RenameColumnAsync("région", "region"));
            expected = new StructType(created
                .Select(f => f.Name == "région"
                    ? new StructField("region", f.DataType, f.Nullable, f.Metadata)
                    : f)
                .ToArray());
        }

        IReadOnlyList<(long Version, string Declared)> committed = ReadCommittedSchemas();

        // The ALTER commit must be a NEW one at a LATER version: if the operation committed
        // nothing, the create's schemaString would satisfy the assertion below while the seam
        // under test never ran.
        Assert.True(
            committed.Count >= 2 && committed[^1].Version > committed[0].Version,
            "ALTER produced no new schema-carrying commit; versions seen: "
            + string.Join(", ", committed.Select(c => c.Version)));

        AssertDeclaredPreservesCallerMetadata(expected, committed[^1].Declared);
    }

    /// <summary>
    /// Drives the two PUBLIC staged-file write overloads that nothing else in this suite reaches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DeltaTableWriter.AppendAsync(StructType, IReadOnlyList&lt;StagedDataFile&gt;, ...)</c> and the
    /// matching <c>OverwriteAsync</c> take an ALREADY-STAGED file and load the snapshot themselves,
    /// so they are a different shape from the <c>DeltaWriteTarget</c> seams the rest of this suite
    /// drives -- and they were reached by nothing. They are public API that serializes a committed
    /// schemaString, so a transform in either reproduces issue #679's divergence with every other
    /// test in this file green. That is not hypothetical: they were found precisely because the
    /// coverage guard's required side was widened, and they were the first thing it reported.
    /// </para>
    /// <para>
    /// The oracle is metadata PRESERVATION rather than footer/log byte equality, because these
    /// overloads write no footer -- the caller stages the file. So the assertion is the one that
    /// holds without a sibling artifact, the same one ALTER and column-mapping evolution use: what
    /// the caller declared must survive into the committed log.
    /// </para>
    /// </remarks>
    /// <param name="overwrite">Drive <c>OverwriteAsync</c> rather than <c>AppendAsync</c>.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagedFileOverloads_PreserveEveryMetadataEntryTheCallerDeclared(bool overwrite)
    {
        StructType created = HostileSchema();

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames(),
            new SeededPhysicalNameSource(CreateSeed)))
        {
            await WriteEntryPointRecorder.DriveAsync(
                target, t => t.AppendAsync(
                    created, Array.Empty<string>(), new[] { BuildBatch(created, "west") }));
        }

        var writer = new DeltaTableWriter(new LocalFileSystemBackend(_root));
        var staged = new[]
        {
            new StagedDataFile(
                "part-00001-staged.parquet",
                ImmutableSortedDictionary<string, string?>.Empty,
                Size: 1L,
                ModificationTime: 1L,
                Stats: null),
        };

        // The schemaString is only re-serialized when the commit carries a metaData action, which
        // for these overloads means an ADDITIVE evolution. A same-schema write commits adds only and
        // would drive the overload without ever reaching the serializer -- coverage of the entry
        // point but not of the call site, which is the distinction this whole guard family exists
        // to keep. So widen, and evolve.
        StructType evolved = Widen(created, "staged_added");

        if (overwrite)
        {
            await WriteEntryPointRecorder.DriveAsync(
                writer, t => t.OverwriteAsync(
                    evolved, staged, PartitionOverwriteMode.Static, SchemaEvolutionMode.AddNewColumns));
        }
        else
        {
            await WriteEntryPointRecorder.DriveAsync(
                writer, t => t.AppendAsync(evolved, staged, SchemaEvolutionMode.AddNewColumns));
        }

        IReadOnlyList<(long Version, string Declared)> committed = ReadCommittedSchemas();

        // Fails closed: if the overload committed nothing, the create's schemaString would satisfy
        // the assertion below while the seam under test never ran.
        Assert.True(
            committed.Count >= 2 && committed[^1].Version > committed[0].Version,
            "the staged-file overload produced no new schema-carrying commit; versions seen: "
            + string.Join(", ", committed.Select(c => c.Version)));

        AssertDeclaredPreservesCallerMetadata(evolved, committed[^1].Declared);
    }

    private static void AssertDeclaredPreservesCallerMetadata(StructType expectedSchema, string declared)
    {
        var logged = (StructType)SchemaJson.FromJson(declared);
        Assert.Equal(expectedSchema.Count, logged.Count);

        foreach (StructField expected in expectedSchema)
        {
            StructField actual = Assert.Single(logged, f => f.Name == expected.Name);

            foreach (KeyValuePair<string, MetadataValue> entry in expected.Metadata)
            {
                Assert.True(
                    actual.Metadata.TryGetValue(entry.Key, out MetadataValue? value),
                    $"Field '{expected.Name}' lost metadata key '{entry.Key}'; the caller declared "
                    + "it and only column-mapping keys may be added.");
                Assert.Equal(entry.Value, value);
            }

            foreach (string key in actual.Metadata.Keys)
            {
                Assert.True(
                    expected.Metadata.ContainsKey(key)
                        || key.StartsWith("delta.columnMapping.", StringComparison.Ordinal),
                    $"Field '{expected.Name}' gained metadata key '{key}', which the caller never "
                    + "declared and which is not a column-mapping key.");
            }
        }
    }

    /// <summary>
    /// The hostile metadata of <see cref="HostileSchema"/> without the hand-written
    /// <c>delta.columnMapping.id</c>, which the mapping writer mints itself.
    /// </summary>
    private static StructType MappableSchema() =>
        new(HostileSchema()
            .Select(f => new StructField(
                f.Name,
                f.DataType,
                f.Nullable,
                FieldMetadata.FromValues(f.Metadata.Where(
                    e => !e.Key.StartsWith("delta.columnMapping.", StringComparison.Ordinal)))))
            .ToArray());

    /// <summary>Adds a nullable column, so a later append evolves the schema rather than matching it.</summary>
    private static StructType Widen(StructType schema, string name) =>
        new(schema.Append(new StructField(name, DataTypes.StringType, nullable: true)).ToArray());

    /// <summary>
    /// A schema built to make an input-narrowing transform at EITHER call site observable, since
    /// such a transform is the shape that survives every other guard in this PR.
    /// </summary>
    /// <remarks>
    /// The plausible forms are threshold-guarded fast paths — "if this field carries more than N
    /// metadata entries, take the bulk path" — and content filters that drop what the fast path
    /// cannot handle. So a field must carry enough metadata entries to trip such a threshold
    /// (well past the 64 that showed up in the rogues), and the entries must include the content a
    /// bulk path would choke on: non-ASCII keys and values, an astral pair, an empty key, an empty
    /// value, and characters requiring escapes.
    /// <para>
    /// The value kinds are enumerated from <see cref="MetadataValueKind"/> itself rather than
    /// listed, so a kind added later is carried here without anyone remembering to add it.
    /// </para>
    /// </remarks>
    private static StructType HostileSchema()
    {
        var kinds = Enum.GetValues<MetadataValueKind>();
        var bulky = new List<KeyValuePair<string, MetadataValue>>
        {
            new(string.Empty, MetadataValue.String("value under an empty key")),
            new("emptyValue", MetadataValue.String(string.Empty)),
            new("café", MetadataValue.String("caffè")),
            new("ключ", MetadataValue.String("значение")),
            new("astral\U0001F600", MetadataValue.String("\U0001F600")),
            new("quote\"tab\tnewline\n", MetadataValue.String("back\\slash")),
        };

        for (int i = 0; bulky.Count < 96; i++)
        {
            MetadataValueKind kind = kinds[i % kinds.Length];
            bulky.Add(new KeyValuePair<string, MetadataValue>($"pad.é{i:D3}", ValueOfKind(kind, i)));
        }

        return new StructType(new[]
        {
            new StructField("région", DataTypes.StringType, nullable: true, FieldMetadata.FromValues(bulky)),
            new StructField("id", DataTypes.LongType, nullable: false, FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(1)),
            })),
            new StructField("naïve", DataTypes.StringType, nullable: true),
        });
    }

    private static MetadataValue ValueOfKind(MetadataValueKind kind, int i) => kind switch
    {
        MetadataValueKind.Null => MetadataValue.Null,
        MetadataValueKind.String => MetadataValue.String($"é{i}"),
        MetadataValueKind.Long => MetadataValue.Long(i),
        MetadataValueKind.Double => MetadataValue.Double(i + 0.5),
        MetadataValueKind.Boolean => MetadataValue.Boolean(i % 2 == 0),
        MetadataValueKind.Array => MetadataValue.Array(new[] { MetadataValue.String("é"), MetadataValue.Long(i) }),
        MetadataValueKind.Nested => MetadataValue.Nested(FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("né", MetadataValue.String("sté")),
        })),
        _ => throw new NotSupportedException(
            $"MetadataValueKind.{kind} was added but this corpus does not build one, so the "
            + "footer/log parity guard would silently stop covering it."),
    };

    private async Task AppendAsync(
        StructType schema, IReadOnlyList<string> partitionColumns, bool mergeSchema)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);

        var batch = BuildBatch(schema, "west");
        await WriteEntryPointRecorder.DriveAsync(
            target, t => t.AppendAsync(schema, partitionColumns, new[] { batch }, mergeSchema));
    }

    /// <summary>
    /// Replaces the table schema wholesale via the overwrite door, which commits its
    /// <c>metaData</c> from a DIFFERENT call site than any append.
    /// </summary>
    private async Task OverwriteAsync(StructType schema)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);

        await WriteEntryPointRecorder.DriveAsync(
            target, t => t.OverwriteAsync(schema,
            Array.Empty<string>(),
            new[] { BuildBatch(schema, "north") },
            DeltaPartitionOverwriteMode.Static,
            overwriteSchema: true));
    }

    private static ManagedColumnBatch BuildBatch(StructType schema, string region)
    {
        var vectors = new ColumnVector[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            MutableColumnVector vector = ColumnVectors.Create(schema[i].DataType, 2);
            if (schema[i].DataType is StringType)
            {
                // Partition columns are string-typed here, and the partition VALUE has to vary to
                // produce a multi-file commit, so strings carry the caller's value rather than the
                // corpus placeholder.
                vector.AppendBytes(Encoding.UTF8.GetBytes(region));
                vector.AppendBytes(Encoding.UTF8.GetBytes(region));
            }
            else
            {
                // Fails closed on a type it cannot build, so a newly accepted writer type cannot
                // quietly drop out of the footer/log comparison.
                ScalarCorpus.AppendOne(vector, schema[i].DataType);
                ScalarCorpus.AppendOne(vector, schema[i].DataType);
            }

            vectors[i] = vector;
        }

        return new ManagedColumnBatch(schema, vectors, 2);
    }

    /// <summary>Reads the <c>delta.schema</c> footer metadata off one committed data file.</summary>
    private async Task<string> ReadFooterSchema(string relativePath)
    {
        await using FileStream stream = File.OpenRead(Path.Combine(_root, relativePath));
        await using ParquetReader reader =
            await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        return reader.CustomMetadata[FooterWireKeys.Schema];
    }

    /// <summary>
    /// Replays EVERY commit in the log and returns each added data file paired with the
    /// <c>schemaString</c> that was in effect when that file was committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An earlier version read only <c>00000000000000000000.json</c> and asserted a single data
    /// file existed. That guarded commit 0 of a one-file table and nothing else -- so the SCHEMA
    /// EVOLUTION call sites, which run on commits 1..n and are a different set of
    /// <c>SchemaJson.ToJson</c> calls, were never footer/log compared, and the multi-file commit
    /// was excluded by an assertion rather than covered. "One instance guarded, its siblings not"
    /// had bitten at the call-site level; this is the same shape at the COMMIT level.
    /// </para>
    /// <para>
    /// The schemaString is CARRIED FORWARD across commits because a commit that adds no
    /// <c>metaData</c> action still writes data files, and those files must match the schema still
    /// in effect. Reading the raw committed bytes rather than a snapshot object is deliberate:
    /// nothing between the writer and the assertion can normalize a difference away.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The <c>schemaString</c> of every commit that carries a <c>metaData</c> action.
    /// </summary>
    /// <remarks>
    /// <see cref="ReadCommittedFiles"/> pairs a schema with the data files committed under it, so a
    /// metadata-ONLY commit -- which is what ALTER produces -- yields no row there. This reader
    /// exists for those seams and deliberately keeps the version, so a test can require that the
    /// operation actually committed rather than silently re-reading the create.
    /// </remarks>
    private IReadOnlyList<(long Version, string Declared)> ReadCommittedSchemas()
    {
        var results = new List<(long, string)>();
        string[] commits = Directory.GetFiles(Path.Combine(_root, "_delta_log"), "*.json");
        Array.Sort(commits, StringComparer.Ordinal);

        foreach (string commit in commits)
        {
            long version = long.Parse(
                Path.GetFileNameWithoutExtension(commit), System.Globalization.CultureInfo.InvariantCulture);

            foreach (string line in File.ReadAllLines(commit))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("metaData", out JsonElement metadata)
                    && metadata.TryGetProperty("schemaString", out JsonElement schemaString))
                {
                    results.Add((version, schemaString.GetString()!));
                }
            }
        }

        return results;
    }

    private IReadOnlyList<(long Version, string Path, string Declared)> ReadCommittedFiles()
    {
        var results = new List<(long, string, string)>();
        string logDirectory = Path.Combine(_root, "_delta_log");
        string[] commits = Directory.GetFiles(logDirectory, "*.json");
        Array.Sort(commits, StringComparer.Ordinal);

        string? current = null;
        foreach (string commit in commits)
        {
            long version = long.Parse(
                Path.GetFileNameWithoutExtension(commit), System.Globalization.CultureInfo.InvariantCulture);
            var added = new List<string>();

            foreach (string line in File.ReadAllLines(commit))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("metaData", out JsonElement metadata)
                    && metadata.TryGetProperty("schemaString", out JsonElement schemaString))
                {
                    current = schemaString.GetString();
                }

                if (document.RootElement.TryGetProperty("add", out JsonElement add)
                    && add.TryGetProperty("path", out JsonElement path))
                {
                    added.Add(Uri.UnescapeDataString(path.GetString()!));
                }
            }

            if (current is null && added.Count > 0)
            {
                Assert.Fail($"Commit {version} added data files before any metaData action was committed.");
            }

            foreach (string path in added)
            {
                results.Add((version, path, current!));
            }
        }

        return results;
    }

    private static int FirstDifference(string left, string right)
    {
        int shared = Math.Min(left.Length, right.Length);
        for (int i = 0; i < shared; i++)
        {
            if (left[i] != right[i])
            {
                return i;
            }
        }

        return shared;
    }

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : value[..400] + $"... (+{value.Length - 400} chars)";
}
