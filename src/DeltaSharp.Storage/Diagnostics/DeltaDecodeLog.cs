using Microsoft.Extensions.Logging;

namespace DeltaSharp.Storage.Diagnostics;

/// <summary>
/// Source-generated, allocation-free structured log messages for the bounded-decode admission control
/// (<c>BoundedDecode</c>, design §5.4 C-DECODE / EPIC-05 DoS-hardening). Event ids are in the storage-owned
/// <b>4500–4599</b> decode sub-range (commit 4000, vacuum 4100, optimize 4200, delete 4300, checkpoint 4400).
/// No message ever carries a file path, byte content, or any table/credential value — only the low-cardinality
/// door label and the door's OWN derived sizing (all process-derived integers, safe) — so no untrusted content
/// can leak (§7.2.2 redaction-by-omission) and the site takes no <see cref="System.Exception"/> object.
/// </summary>
internal static partial class DeltaDecodeLog
{
    /// <summary>
    /// The <b>one-shot startup Warning</b> (Round-13) emitted at most once per under-provisioned door: a door
    /// whose one-maximal-footprint floor exceeds the process-memory residual cap, so on this pod it can admit at
    /// most one maximal decode before saturating (fail-fast, retryable). This is the operator-valued half of the
    /// under-provisioned signal — the sibling <c>deltasharp.storage.decode.door_under_provisioned{door}</c> gauge
    /// is the machine-readable half. Because the process-global door fields have no <see cref="ILogger"/> in scope
    /// at static-field init, this fires from the first <see cref="DeltaLog"/> construction that reaches a logger,
    /// guarded by a per-door <c>Interlocked</c>-once flag so it emits at most once per door. Renders only the door
    /// label and the door's derived sizing (residual budget, max footprint, process memory — all safe integers)
    /// and takes no <see cref="System.Exception"/> object. Logged at <c>Warning</c>: it is a SIZING signal, not
    /// an error — the read still works, but the residual headroom on this pod is structurally thin, which is an
    /// actionable operator hint (increase pod memory) that is otherwise invisible.
    /// </summary>
    [LoggerMessage(EventId = 4500, EventName = "DeltaDecodeDoorUnderProvisioned", Level = LogLevel.Warning,
        Message = "The bounded-decode {Door} door is under-provisioned on this pod: its one-maximal-footprint "
            + "floor ({MaxFootprintBytes} bytes) exceeds the process-memory residual cap (residual budget "
            + "{ResidualBudgetBytes} bytes, process memory {ProcessMemoryBytes} bytes), so it can admit at most "
            + "one maximal decode before saturating (fail-fast, retryable). This is a sizing signal, not an "
            + "error; increase pod memory to restore residual headroom.")]
    internal static partial void DoorUnderProvisioned(
        ILogger logger, string door, long maxFootprintBytes, long residualBudgetBytes, long processMemoryBytes);
}
