using System.Text.Json.Nodes;

/// <summary>
/// Implemented by any system that owns campaign state which must survive a
/// save/load round-trip.
///
/// The previous save system hardcoded knowledge of every system into
/// <c>SaveLoadSystem</c>, collected staff/rooms/policies, and then never
/// re-applied them — the restore path only wrote back five scalars. Worse,
/// the pressure and narrative layer (heat internals, political favor,
/// blackmail inventory, syndicate grudges, union risk, arc progress) was
/// never captured at all, so reloading silently reset it.
///
/// Inverting the dependency fixes both problems: each system states its own
/// state, and <c>SaveLoadSystem</c> only moves opaque blobs around. Adding a
/// system to the save file is now a local change to that system.
///
/// Implementations must be symmetric — anything written by
/// <see cref="CaptureState"/> must be read by <see cref="RestoreState"/>,
/// and <see cref="RestoreState"/> must tolerate missing keys so that saves
/// written by older builds still load.
/// </summary>
public interface ISaveableSystem
{
    /// <summary>
    /// Stable key identifying this system's slice of the save file.
    /// Must not change once shipped — renaming it orphans existing saves.
    /// </summary>
    string SaveKey { get; }

    /// <summary>Serialize this system's campaign state.</summary>
    JsonObject CaptureState();

    /// <summary>
    /// Restore campaign state previously produced by <see cref="CaptureState"/>.
    /// Must handle absent or unexpected keys without throwing — a save from an
    /// older build is a normal case, not an error.
    /// </summary>
    void RestoreState(JsonObject state);
}
