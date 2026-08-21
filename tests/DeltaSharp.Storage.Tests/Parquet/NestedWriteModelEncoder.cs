namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// An <b>independent</b> Dremel level emitter for the three single-level nested shapes (#844, design §3.1).
/// Given a LOGICAL row model — the same <see cref="IReadOnlyList{T}"/> shapes <see cref="NestedVectors"/>
/// builds its <c>ColumnVector</c>s from — it computes, from FIRST PRINCIPLES (the §2.3 semantics restated in
/// each method's remarks), the per-leaf <c>(values, definition, repetition)</c> streams a correct writer must
/// produce.
/// </summary>
/// <remarks>
/// <para><b>Independence is the whole point.</b> This encoder shares NO code path with
/// <c>NestedColumnShredder</c> — it does not call it, reference its internals, nor read a schema-attached leaf
/// to derive a level. Its own correctness is anchored by reproducing the literal §2.3 tables the committed
/// <c>NestedParquetLevelStreamTests</c> hard-codes; if this encoder disagreed with those, the encoder would be
/// wrong. Once anchored, it becomes a third differential oracle for the shredder's WRITE path across the full
/// branch matrix and a seeded generative corpus.</para>
/// <para>The container lane (top-level <c>struct</c>/<c>array</c>/<c>map</c> field) is always OPTIONAL, matching
/// every schema the nested-write tests build (<c>nullable: true</c>). The child/element/value lanes vary
/// between OPTIONAL and REQUIRED and are passed explicitly.</para>
/// </remarks>
internal static class NestedWriteModelEncoder
{
    /// <summary>One leaf column's expected Dremel encoding. <see cref="Rep"/> is EMPTY when the leaf carries no
    /// repetition stream (an unrepeated struct child); <see cref="Values"/> lists the non-null cells in leaf
    /// order (the only cells a Parquet leaf physically stores).</summary>
    internal sealed class LeafExpectation
    {
        public required string[] Path { get; init; }

        public required int[] Def { get; init; }

        public required int[] Rep { get; init; }

        public required IReadOnlyList<object> Values { get; init; }
    }

    /// <summary>
    /// <c>struct&lt;a:int, b:string&gt;</c> under an OPTIONAL container (<c>structMaxDef == 1</c>), no
    /// repetition stream. Per row: a null struct drops BOTH children to <c>def 0</c> (cross-field parity); a
    /// present struct emits <c>def structMaxDef</c> for a null child and the child leaf's own max (
    /// <c>structMaxDef + (child nullable ? 1 : 0)</c>) for a present value.
    /// </summary>
    public static (LeafExpectation A, LeafExpectation B) EncodeStruct(
        string top, IReadOnlyList<(int? A, string? B)?> rows, bool aNullable, bool bNullable)
    {
        const int structMaxDef = 1; // OPTIONAL container.
        int aLeafMax = structMaxDef + (aNullable ? 1 : 0);
        int bLeafMax = structMaxDef + (bNullable ? 1 : 0);

        var aDef = new int[rows.Count];
        var bDef = new int[rows.Count];
        var aVals = new List<object>();
        var bVals = new List<object>();

        for (int i = 0; i < rows.Count; i++)
        {
            (int? A, string? B)? row = rows[i];
            if (row is null)
            {
                aDef[i] = 0;
                bDef[i] = 0;
                continue;
            }

            int? a = row.Value.A;
            if (a is null)
            {
                if (!aNullable)
                {
                    throw new InvalidOperationException(
                        $"struct row {i} field a is null but the field lane is REQUIRED.");
                }

                aDef[i] = structMaxDef;
            }
            else
            {
                aDef[i] = aLeafMax;
                aVals.Add(a.Value);
            }

            string? b = row.Value.B;
            if (b is null)
            {
                if (!bNullable)
                {
                    throw new InvalidOperationException(
                        $"struct row {i} field b is null but the field lane is REQUIRED.");
                }

                bDef[i] = structMaxDef;
            }
            else
            {
                bDef[i] = bLeafMax;
                bVals.Add(b);
            }
        }

        return (
            new LeafExpectation
            {
                Path = new[] { top, "a" },
                Def = aDef,
                Rep = Array.Empty<int>(),
                Values = aVals,
            },
            new LeafExpectation
            {
                Path = new[] { top, "b" },
                Def = bDef,
                Rep = Array.Empty<int>(),
                Values = bVals,
            });
    }

    /// <summary>
    /// <c>array&lt;int&gt;</c> under an OPTIONAL container (<c>containerMaxDef == 2</c>). Per row: a null list
    /// is one slot <c>def 0 / rep 0</c>; an empty list is one slot <c>def containerMaxDef - 1 / rep 0</c>; a
    /// non-empty list is one slot PER element with <c>rep 0</c> for the first element and <c>rep 1</c> for
    /// continuations, <c>def containerMaxDef</c> for a null element and the element leaf's own max (
    /// <c>containerMaxDef + (element nullable ? 1 : 0)</c>) for a present value.
    /// </summary>
    public static LeafExpectation EncodeArray(
        string top, IReadOnlyList<int?[]?> rows, bool elementNullable)
    {
        const int containerMaxDef = 2; // OPTIONAL container.
        int leafMax = containerMaxDef + (elementNullable ? 1 : 0);

        var def = new List<int>();
        var rep = new List<int>();
        var values = new List<object>();

        for (int i = 0; i < rows.Count; i++)
        {
            int?[]? row = rows[i];
            if (row is null)
            {
                def.Add(0);
                rep.Add(0);
                continue;
            }

            if (row.Length == 0)
            {
                def.Add(containerMaxDef - 1);
                rep.Add(0);
                continue;
            }

            for (int e = 0; e < row.Length; e++)
            {
                int? cell = row[e];
                if (cell is null)
                {
                    if (!elementNullable)
                    {
                        throw new InvalidOperationException(
                            $"array row {i} element {e} is null but the element lane is REQUIRED.");
                    }

                    def.Add(containerMaxDef);
                }
                else
                {
                    def.Add(leafMax);
                    values.Add(cell.Value);
                }

                rep.Add(e == 0 ? 0 : 1);
            }
        }

        return new LeafExpectation
        {
            Path = new[] { top, "list", "element" },
            Def = def.ToArray(),
            Rep = rep.ToArray(),
            Values = values,
        };
    }

    /// <summary>
    /// <c>map&lt;string,int&gt;</c> under an OPTIONAL container (<c>mapMaxDef == 2</c>), REQUIRED key. Key and
    /// value share ONE repetition stream. Per row: a null map is one slot <c>def 0 / rep 0</c> on both lanes;
    /// an empty map is one slot <c>def mapMaxDef - 1 / rep 0</c> on both lanes; a non-empty map is one slot per
    /// entry with <c>rep 0</c> then <c>1</c>, key <c>def mapMaxDef</c> (keys never null), value
    /// <c>def mapMaxDef</c> for a null value and the value leaf's own max (
    /// <c>mapMaxDef + (value nullable ? 1 : 0)</c>) for a present value.
    /// </summary>
    public static (LeafExpectation Key, LeafExpectation Value) EncodeMap(
        string top, IReadOnlyList<IReadOnlyList<(string Key, int? Value)>?> rows, bool valueNullable)
    {
        const int mapMaxDef = 2;   // OPTIONAL container.
        const int keyMax = 2;      // REQUIRED key ⇒ leaf max == container max.
        int valueMax = mapMaxDef + (valueNullable ? 1 : 0);

        var keyDef = new List<int>();
        var valueDef = new List<int>();
        var rep = new List<int>();
        var keyVals = new List<object>();
        var valueVals = new List<object>();

        for (int i = 0; i < rows.Count; i++)
        {
            IReadOnlyList<(string Key, int? Value)>? row = rows[i];
            if (row is null)
            {
                keyDef.Add(0);
                valueDef.Add(0);
                rep.Add(0);
                continue;
            }

            if (row.Count == 0)
            {
                keyDef.Add(mapMaxDef - 1);
                valueDef.Add(mapMaxDef - 1);
                rep.Add(0);
                continue;
            }

            for (int e = 0; e < row.Count; e++)
            {
                (string key, int? value) = row[e];
                keyDef.Add(keyMax);
                keyVals.Add(key);

                if (value is null)
                {
                    if (!valueNullable)
                    {
                        throw new InvalidOperationException(
                            $"map row {i} entry {e} value is null but the value lane is REQUIRED.");
                    }

                    valueDef.Add(mapMaxDef);
                }
                else
                {
                    valueDef.Add(valueMax);
                    valueVals.Add(value.Value);
                }

                rep.Add(e == 0 ? 0 : 1);
            }
        }

        int[] sharedRep = rep.ToArray();
        return (
            new LeafExpectation
            {
                Path = new[] { top, "key_value", "key" },
                Def = keyDef.ToArray(),
                Rep = sharedRep,
                Values = keyVals,
            },
            new LeafExpectation
            {
                Path = new[] { top, "key_value", "value" },
                Def = valueDef.ToArray(),
                Rep = (int[])sharedRep.Clone(),
                Values = valueVals,
            });
    }
}

/// <summary>
/// A general, greedy delta-debugging shrinker over a list-of-rows model (#844, design §3.1). Given a model, a
/// predicate that recognizes the failure, and a per-row simplifier, it minimizes FIRST by row count (drop a
/// row) and THEN by per-row complexity, accepting the first candidate that STILL reproduces the failure and
/// looping to a fixed point. It is deliberately shape-agnostic so it can be unit-tested on a synthetic injected
/// predicate independent of the writer.
/// </summary>
internal static class ModelShrinker
{
    public static IReadOnlyList<TRow> Shrink<TRow>(
        IReadOnlyList<TRow> initial,
        Func<IReadOnlyList<TRow>, bool> fails,
        Func<TRow, IEnumerable<TRow>> shrinkRow)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(fails);
        ArgumentNullException.ThrowIfNull(shrinkRow);

        var current = new List<TRow>(initial);
        bool progress = true;
        while (progress)
        {
            progress = false;

            // Phase 1: minimize row COUNT — remove any single row whose removal keeps the failure.
            for (int i = 0; i < current.Count; i++)
            {
                var candidate = new List<TRow>(current);
                candidate.RemoveAt(i);
                if (fails(candidate))
                {
                    current = candidate;
                    progress = true;
                    break;
                }
            }

            if (progress)
            {
                continue;
            }

            // Phase 2: minimize per-row COMPLEXITY — replace a row with a simpler variant that keeps failing.
            for (int i = 0; i < current.Count && !progress; i++)
            {
                foreach (TRow simpler in shrinkRow(current[i]))
                {
                    var candidate = new List<TRow>(current);
                    candidate[i] = simpler;
                    if (fails(candidate))
                    {
                        current = candidate;
                        progress = true;
                        break;
                    }
                }
            }
        }

        return current;
    }

    /// <summary>Simpler variants of one <c>array&lt;int&gt;</c> row, ordered simplest-first: a null list, an
    /// empty list, then each single-element removal, then each present value collapsed toward zero.</summary>
    public static IEnumerable<int?[]?> ShrinkArrayRow(int?[]? row)
    {
        if (row is null)
        {
            yield break;
        }

        yield return null;
        if (row.Length > 0)
        {
            yield return Array.Empty<int?>();
        }

        for (int drop = 0; drop < row.Length; drop++)
        {
            var reduced = new int?[row.Length - 1];
            int w = 0;
            for (int k = 0; k < row.Length; k++)
            {
                if (k != drop)
                {
                    reduced[w++] = row[k];
                }
            }

            yield return reduced;
        }

        for (int k = 0; k < row.Length; k++)
        {
            if (row[k] is int v && v != 0)
            {
                var collapsed = (int?[])row.Clone();
                collapsed[k] = 0;
                yield return collapsed;
            }
        }
    }
}
