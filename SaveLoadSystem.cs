using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

/// <summary>Save file header for versioning.</summary>
public class SaveHeader
{
    [JsonPropertyName("version")]     public int Version { get; set; } = 2;
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "0.1.0";
    [JsonPropertyName("timestamp")]   public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
    [JsonPropertyName("playtime")]    public double PlaytimeSeconds { get; set; }
}

/// <summary>Serializable snapshot of GameStateManager metrics.</summary>
public class SaveGameState
{
    [JsonPropertyName("cash")]            public double Cash { get; set; }
    [JsonPropertyName("reputation")]      public float Reputation { get; set; }
    [JsonPropertyName("heat")]            public float Heat { get; set; }
    [JsonPropertyName("publicSentiment")] public float PublicSentiment { get; set; }
    [JsonPropertyName("dayCount")]        public int DayCount { get; set; }
}

/// <summary>
/// Serializable staff member data. Staff get a typed DTO rather than the
/// generic system blob because the roster is the campaign's most important
/// state and benefits from explicit versioning.
/// </summary>
public class SaveStaffMember
{
    [JsonPropertyName("id")]           public string Id { get; set; }
    [JsonPropertyName("name")]         public string Name { get; set; }
    [JsonPropertyName("role")]         public string Role { get; set; }
    [JsonPropertyName("backstory")]    public string Backstory { get; set; }
    [JsonPropertyName("salary")]       public double Salary { get; set; }

    [JsonPropertyName("charisma")]     public float Charisma { get; set; }
    [JsonPropertyName("negotiation")]  public float Negotiation { get; set; }
    [JsonPropertyName("discretion")]   public float Discretion { get; set; }

    [JsonPropertyName("stress")]       public float Stress { get; set; }
    [JsonPropertyName("satisfaction")] public float Satisfaction { get; set; }
    [JsonPropertyName("trauma")]       public float Trauma { get; set; }
    [JsonPropertyName("loyalty")]      public float Loyalty { get; set; }

    [JsonPropertyName("specialization")]    public string Specialization { get; set; }
    [JsonPropertyName("ambition")]          public string Ambition { get; set; }
    [JsonPropertyName("ambitionProgress")]  public float AmbitionProgress { get; set; }
    [JsonPropertyName("ambitionFulfilled")] public bool AmbitionFulfilled { get; set; }
    [JsonPropertyName("origin")]            public string Origin { get; set; }
    [JsonPropertyName("faction")]           public string AssociatedFaction { get; set; }
    [JsonPropertyName("contractDebt")]      public double ContractDebt { get; set; }

    /// <summary>Affinity toward other staff, keyed by their Id.</summary>
    [JsonPropertyName("relationships")] public Dictionary<string, float> Relationships { get; set; } = new();
}

/// <summary>Complete save data container.</summary>
public class SaveData
{
    [JsonPropertyName("header")]    public SaveHeader Header { get; set; } = new();
    [JsonPropertyName("gameState")] public SaveGameState GameState { get; set; } = new();
    [JsonPropertyName("staff")]     public List<SaveStaffMember> Staff { get; set; } = new();

    /// <summary>
    /// One entry per <see cref="ISaveableSystem"/> found in the scene tree,
    /// keyed by its <c>SaveKey</c>. Opaque to this class by design.
    /// </summary>
    [JsonPropertyName("systems")]   public Dictionary<string, JsonObject> Systems { get; set; } = new();

    [JsonPropertyName("checksum")]  public string Checksum { get; set; }
}

/// <summary>
/// Campaign persistence. Serializes the roster and every
/// <see cref="ISaveableSystem"/> in the scene tree to an obfuscated local
/// file, and — unlike the previous implementation — actually restores them.
///
/// Design notes:
/// - The checksum is computed over the payload with the checksum field
///   cleared, on both save and load. The old code hashed the null-checksum
///   JSON on save but the checksum-bearing JSON on load, so every load
///   failed its own integrity check.
/// - Systems are discovered, not hardcoded. See <see cref="ISaveableSystem"/>.
/// - Load order matters: core scalars, then roster, then systems — systems
///   may reference staff by Id during restore.
/// </summary>
public partial class SaveLoadSystem : Node
{
    [Signal] public delegate void OnSaveCompletedEventHandler(string slotName);
    [Signal] public delegate void OnLoadCompletedEventHandler(string slotName, int dayLoaded);
    [Signal] public delegate void OnSaveErrorEventHandler(string message);
    [Signal] public delegate void OnLoadErrorEventHandler(string message);

    private const string SaveDir = "user://saves/";
    private const int CurrentSaveVersion = 2;

    /// <summary>
    /// Obfuscation key for save files. This is deliberately not a security
    /// boundary — it is a single-player save on the player's own machine, and
    /// anyone determined can read it. The goal is only to make casual
    /// save-scumming via a text editor inconvenient.
    /// </summary>
    private const string ObfuscationKey = "EstablishmentSimulator2026";

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private double _playtimeAccumulator;

    public bool AutoSaveEnabled { get; set; } = true;
    public int AutoSaveIntervalDays { get; set; } = 5;

    /// <summary>Playtime across this campaign in seconds, restored from save.</summary>
    public double PlaytimeSeconds => _playtimeAccumulator;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        DirAccess.MakeDirAbsolute(SaveDir);

        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;

        GD.Print("[SaveLoadSystem] Initialized. Save dir: user://saves/");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    public override void _Process(double delta) => _playtimeAccumulator += delta;

    private void OnDailyTick(double cash, float rep, float heat, float sent)
    {
        if (!AutoSaveEnabled) return;

        var day = GameStateManager.Instance?.DayCount ?? 0;
        if (day > 0 && day % AutoSaveIntervalDays == 0)
            SaveGame("autosave");
    }

    // ── Save ───────────────────────────────────────────────────────────

    /// <summary>Save the campaign to a named slot.</summary>
    public bool SaveGame(string slotName)
    {
        try
        {
            var data = CollectSaveData();

            // Checksum covers the payload with the field cleared, so that the
            // same computation is reproducible on load.
            data.Checksum = null;
            data.Checksum = ComputeChecksum(JsonSerializer.Serialize(data, _jsonOpts));

            var json = JsonSerializer.Serialize(data, _jsonOpts);
            var obfuscated = Obfuscate(json);
            var path = SaveDir + $"{slotName}.sav";

            using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
            if (file == null)
            {
                var reason = $"Could not open '{path}' for writing: {Godot.FileAccess.GetOpenError()}";
                EmitSignal(SignalName.OnSaveError, reason);
                GD.PrintErr($"[SaveLoadSystem] {reason}");
                return false;
            }

            file.StoreString(obfuscated);

            EmitSignal(SignalName.OnSaveCompleted, slotName);
            GD.Print($"[SaveLoadSystem] Saved '{slotName}' — day {data.GameState.DayCount}, " +
                     $"{data.Staff.Count} staff, {data.Systems.Count} systems, {obfuscated.Length} bytes.");
            return true;
        }
        catch (Exception ex)
        {
            EmitSignal(SignalName.OnSaveError, ex.Message);
            GD.PrintErr($"[SaveLoadSystem] Save error: {ex.Message}");
            return false;
        }
    }

    // ── Load ───────────────────────────────────────────────────────────

    /// <summary>Load a campaign from a named slot and apply it to all systems.</summary>
    public SaveData LoadGame(string slotName)
    {
        try
        {
            var path = SaveDir + $"{slotName}.sav";
            if (!Godot.FileAccess.FileExists(path))
            {
                EmitSignal(SignalName.OnLoadError, $"Save file '{slotName}' not found.");
                return null;
            }

            using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (file == null)
            {
                EmitSignal(SignalName.OnLoadError, $"Could not open '{path}': {Godot.FileAccess.GetOpenError()}");
                return null;
            }

            var json = Deobfuscate(file.GetAsText());
            var data = JsonSerializer.Deserialize<SaveData>(json, _jsonOpts);

            if (data == null)
            {
                EmitSignal(SignalName.OnLoadError, "Failed to deserialize save data.");
                return null;
            }

            if (data.Header.Version > CurrentSaveVersion)
            {
                EmitSignal(SignalName.OnLoadError,
                    $"Save version {data.Header.Version} is newer than this build ({CurrentSaveVersion}).");
                return null;
            }

            if (!VerifyChecksum(data))
            {
                EmitSignal(SignalName.OnLoadError, "Save file checksum mismatch — data may be corrupted.");
                return null;
            }

            ApplySaveData(data);

            _playtimeAccumulator = data.Header.PlaytimeSeconds;

            EmitSignal(SignalName.OnLoadCompleted, slotName, data.GameState.DayCount);
            GD.Print($"[SaveLoadSystem] Loaded '{slotName}' — day {data.GameState.DayCount}, " +
                     $"{data.Staff.Count} staff, {data.Systems.Count} systems.");
            return data;
        }
        catch (Exception ex)
        {
            EmitSignal(SignalName.OnLoadError, ex.Message);
            GD.PrintErr($"[SaveLoadSystem] Load error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Recompute the checksum the same way <see cref="SaveGame"/> did: clear
    /// the field, serialize, hash. Restores the original value afterward so
    /// the caller still sees intact data.
    /// </summary>
    private static bool VerifyChecksum(SaveData data)
    {
        var stored = data.Checksum;

        // Pre-v2 saves predate the fixed checksum scheme; their stored value
        // can never validate, so accept them rather than stranding the player.
        if (data.Header.Version < 2 || string.IsNullOrEmpty(stored)) return true;

        data.Checksum = null;
        var computed = ComputeChecksum(JsonSerializer.Serialize(data, _jsonOpts));
        data.Checksum = stored;

        return string.Equals(stored, computed, StringComparison.Ordinal);
    }

    // ── Data Collection ────────────────────────────────────────────────

    private SaveData CollectSaveData()
    {
        var gsm = GameStateManager.Instance;

        return new SaveData
        {
            Header = new SaveHeader
            {
                Version = CurrentSaveVersion,
                GameVersion = "0.1.0",
                Timestamp = DateTime.UtcNow.ToString("O"),
                PlaytimeSeconds = _playtimeAccumulator
            },
            GameState = new SaveGameState
            {
                Cash = gsm?.Cash ?? 0,
                Reputation = gsm?.Reputation ?? 0,
                Heat = gsm?.Heat ?? 0,
                PublicSentiment = gsm?.PublicSentiment ?? 0,
                DayCount = gsm?.DayCount ?? 0
            },
            Staff = CollectStaffData(),
            Systems = CollectSystemStates()
        };
    }

    /// <summary>
    /// Capture the full roster. Sourced from <see cref="StaffRoster"/> — the
    /// previous implementation read <c>GetStaffAtRisk()</c>, so any staff
    /// member below 80 stress was silently dropped from the save.
    /// </summary>
    private static List<SaveStaffMember> CollectStaffData()
    {
        var result = new List<SaveStaffMember>();
        var roster = StaffRoster.Instance;
        if (roster == null) return result;

        foreach (var s in roster.GetAll())
        {
            var relationships = new Dictionary<string, float>();
            foreach (var kvp in s.Relationships)
                relationships[kvp.Key] = kvp.Value;

            result.Add(new SaveStaffMember
            {
                Id = s.Id,
                Name = s.StaffName,
                Role = s.Role,
                Backstory = s.Backstory,
                Salary = s.Salary,

                Charisma = s.Charisma,
                Negotiation = s.Negotiation,
                Discretion = s.Discretion,

                Stress = s.Stress,
                Satisfaction = s.Satisfaction,
                Trauma = s.Trauma,
                Loyalty = s.Loyalty,

                Specialization = s.Specialization.ToString(),
                Ambition = s.Ambition.ToString(),
                AmbitionProgress = s.AmbitionProgress,
                AmbitionFulfilled = s.AmbitionFulfilled,
                Origin = s.Origin.ToString(),
                AssociatedFaction = s.AssociatedFaction,
                ContractDebt = s.ContractDebt,

                Relationships = relationships
            });
        }

        return result;
    }

    /// <summary>Ask every ISaveableSystem in the tree for its state.</summary>
    private Dictionary<string, JsonObject> CollectSystemStates()
    {
        var result = new Dictionary<string, JsonObject>();

        foreach (var system in FindSaveableSystems())
        {
            try
            {
                var key = system.SaveKey;
                if (string.IsNullOrEmpty(key))
                {
                    GD.PrintErr($"[SaveLoadSystem] {system.GetType().Name} has an empty SaveKey. Skipped.");
                    continue;
                }

                if (result.ContainsKey(key))
                {
                    GD.PrintErr($"[SaveLoadSystem] Duplicate SaveKey '{key}'. Skipped.");
                    continue;
                }

                result[key] = system.CaptureState() ?? new JsonObject();
            }
            catch (Exception ex)
            {
                // One misbehaving system must not cost the player their save.
                GD.PrintErr($"[SaveLoadSystem] {system.GetType().Name}.CaptureState failed: {ex.Message}");
            }
        }

        return result;
    }

    // ── Data Application ───────────────────────────────────────────────

    /// <summary>
    /// Apply loaded data. Order is deliberate: core scalars first, then the
    /// roster, then systems — systems may resolve staff by Id during restore.
    /// </summary>
    private void ApplySaveData(SaveData data)
    {
        ApplyGameState(data.GameState);
        ApplyStaffData(data.Staff);
        ApplySystemStates(data.Systems);
    }

    private static void ApplyGameState(SaveGameState state)
    {
        var gsm = GameStateManager.Instance;
        if (gsm == null || state == null) return;

        gsm.Cash = state.Cash;
        gsm.Reputation = state.Reputation;
        gsm.Heat = state.Heat;
        gsm.PublicSentiment = state.PublicSentiment;
        gsm.SetDayCount(state.DayCount);
    }

    /// <summary>
    /// Rebuild the roster from save. The previous implementation collected
    /// staff and then never applied them, so every load produced an empty house.
    /// </summary>
    private static void ApplyStaffData(List<SaveStaffMember> saved)
    {
        var roster = StaffRoster.Instance;
        if (roster == null || saved == null) return;

        var rebuilt = new List<StaffMember>();

        foreach (var s in saved)
        {
            var staff = new StaffMember
            {
                Id = string.IsNullOrEmpty(s.Id) ? Guid.NewGuid().ToString("N")[..12] : s.Id,
                StaffName = s.Name ?? "Unnamed",
                Role = s.Role ?? "Attendant",
                Backstory = s.Backstory ?? "",
                Salary = s.Salary,

                Charisma = s.Charisma,
                Negotiation = s.Negotiation,
                Discretion = s.Discretion,

                Specialization = ParseEnum(s.Specialization, StaffSpecialization.None),
                Ambition = ParseEnum(s.Ambition, StaffAmbition.Money),
                Origin = ParseEnum(s.Origin, StaffOrigin.OpenCall),
                AssociatedFaction = s.AssociatedFaction ?? "",
                ContractDebt = s.ContractDebt
            };

            // Agency variables are set after the stats so the trauma-adjusted
            // satisfaction ceiling is computed against the restored trauma.
            staff.Trauma = s.Trauma;
            staff.Stress = s.Stress;
            staff.Satisfaction = s.Satisfaction;
            staff.RestoreLoyalty(s.Loyalty);
            staff.RestoreAmbitionProgress(s.AmbitionProgress, s.AmbitionFulfilled);

            if (s.Relationships != null)
            {
                foreach (var kvp in s.Relationships)
                    staff.Relationships[kvp.Key] = kvp.Value;
            }

            rebuilt.Add(staff);
        }

        roster.ReplaceAll(rebuilt);
    }

    private void ApplySystemStates(Dictionary<string, JsonObject> states)
    {
        if (states == null) return;

        foreach (var system in FindSaveableSystems())
        {
            var key = system.SaveKey;
            if (string.IsNullOrEmpty(key)) continue;

            if (!states.TryGetValue(key, out var state) || state == null)
            {
                // Normal for saves written before this system existed.
                GD.Print($"[SaveLoadSystem] No saved state for '{key}'. Leaving at defaults.");
                continue;
            }

            try
            {
                system.RestoreState(state);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SaveLoadSystem] {system.GetType().Name}.RestoreState failed: {ex.Message}");
            }
        }
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    // ── System Discovery ───────────────────────────────────────────────

    /// <summary>
    /// Walk the scene tree for nodes implementing <see cref="ISaveableSystem"/>.
    /// Deliberately not cached: systems are added and removed across phase
    /// transitions, and a stale cache would silently drop state from the save.
    /// </summary>
    private List<ISaveableSystem> FindSaveableSystems()
    {
        var result = new List<ISaveableSystem>();
        var root = GetTree()?.Root;
        if (root != null) CollectSaveableSystems(root, result);
        return result;
    }

    private static void CollectSaveableSystems(Node node, List<ISaveableSystem> into)
    {
        if (node is ISaveableSystem saveable) into.Add(saveable);

        foreach (var child in node.GetChildren())
            CollectSaveableSystems(child, into);
    }

    // ── Obfuscation ────────────────────────────────────────────────────

    private static string Obfuscate(string plaintext)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(ObfuscationKey));

        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    private static string Deobfuscate(string ciphertext)
    {
        var fullCipher = Convert.FromBase64String(ciphertext);
        if (fullCipher.Length <= 16)
            throw new InvalidDataException("Save file is truncated.");

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(ObfuscationKey));
        var iv = new byte[16];
        var cipherBytes = new byte[fullCipher.Length - 16];

        Buffer.BlockCopy(fullCipher, 0, iv, 0, 16);
        Buffer.BlockCopy(fullCipher, 16, cipherBytes, 0, cipherBytes.Length);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static string ComputeChecksum(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    // ── Slot Management ────────────────────────────────────────────────

    /// <summary>List all save slot names.</summary>
    public string[] ListSaves()
    {
        using var dir = DirAccess.Open(SaveDir);
        if (dir == null) return Array.Empty<string>();

        var files = new List<string>();
        dir.ListDirBegin();

        string name;
        while ((name = dir.GetNext()) != "")
            if (name.EndsWith(".sav")) files.Add(name[..^4]);

        dir.ListDirEnd();
        return files.ToArray();
    }

    /// <summary>Delete a save slot.</summary>
    public bool DeleteSave(string slotName)
    {
        var path = SaveDir + $"{slotName}.sav";
        if (!Godot.FileAccess.FileExists(path)) return false;

        DirAccess.RemoveAbsolute(path);
        return true;
    }

    public override string ToString() =>
        $"[SaveLoadSystem] Saves={ListSaves().Length} " +
        $"AutoSave={(AutoSaveEnabled ? $"every {AutoSaveIntervalDays}d" : "off")}";
}
