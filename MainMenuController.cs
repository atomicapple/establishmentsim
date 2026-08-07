using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

/// <summary>Parsed metadata from a save file header.</summary>
public class MainMenuSaveMeta
{
    [JsonPropertyName("version")]     public int Version { get; set; }
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; }
    [JsonPropertyName("timestamp")]   public string Timestamp { get; set; }
    [JsonPropertyName("playtime")]    public double PlaytimeSeconds { get; set; }
}

/// <summary>Parsed game state from save for menu display.</summary>
public class SaveMenuPreview
{
    [JsonPropertyName("cash")]      public double Cash { get; set; }
    [JsonPropertyName("dayCount")]  public int DayCount { get; set; }
    [JsonPropertyName("reputation")] public float Reputation { get; set; }
    [JsonPropertyName("heat")]      public float Heat { get; set; }
}

/// <summary>Container matching SaveLoadSystem's save format for metadata extraction.</summary>
public class SavePreviewContainer
{
    [JsonPropertyName("header")]    public MainMenuSaveMeta Header { get; set; }
    [JsonPropertyName("gameState")] public SaveMenuPreview GameState { get; set; }
}

/// <summary>
/// Primary main menu controller for Godot 4.3+. Binds UI button
/// signals (New Operation, Load Dossier, Settings, Exit). Scans
/// user://saves/ for existing JSON save files to enable Load
/// Dossier and display campaign metadata. All scene transitions
/// use async loading to prevent viewport freezing.
/// </summary>
public partial class MainMenuController : Control
{
    [Signal] public delegate void OnSceneLoadStartedEventHandler(string scenePath);
    [Signal] public delegate void OnSceneLoadProgressEventHandler(float progress);
    [Signal] public delegate void OnSceneLoadCompleteEventHandler();

    // ── Node References ────────────────────────────────────────────────
    private Button _newGameBtn, _loadGameBtn, _settingsBtn, _exitBtn;
    private Label _campaignLabel, _campaignDetailLabel;
    private ProgressBar _loadProgress;
    private Label _loadStatus;
    private Control _saveInfoPanel;
    private bool _hasSaves;
    private string _latestSavePath;
    private SaveMenuPreview _preview;

    // ── Art Deco Palette ───────────────────────────────────────────────
    private static readonly Color Gold    = new(0.85f, 0.7f, 0.25f);
    private static readonly Color GoldDim = new(0.5f, 0.4f, 0.15f);
    private static readonly Color DarkBg  = new(0.05f, 0.04f, 0.07f);

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Scene paths ────────────────────────────────────────────────────
    private const string MainGameScene  = "res://test_scene.tscn";
    private const string SettingsScene  = "res://settings_menu.tscn";
    private const string IntroScene     = "res://intro_cutscene.tscn";

    public bool HasSaves => _hasSaves;
    public string LatestSavePath => _latestSavePath;

    public override void _Ready()
    {
        BuildUI();
        ScanForSaves();
        WireSignals();
        GD.Print("[MainMenuController] Ready.");
    }

    // ── UI Construction ────────────────────────────────────────────────

    private void BuildUI()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);

        // Dark background
        var bg = new ColorRect();
        bg.Color = DarkBg;
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        // Center column
        var center = new VBoxContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.Center);
        center.Alignment = BoxContainer.AlignmentMode.Center;
        center.AddThemeConstantOverride("separation", 8);
        center.SetSize(new Vector2(360, 0));
        AddChild(center);

        // Gold line
        var line = new ColorRect { Color = Gold, CustomMinimumSize = new Vector2(300, 2) };
        center.AddChild(line);
        center.AddChild(Spacer(16));

        // Title
        var title = MakeLabel("ESTABLISHMENT SIMULATOR", 28, Gold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        center.AddChild(title);

        var subtitle = MakeLabel("MANAGEMENT IS A DIRTY BUSINESS", 12, GoldDim);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        center.AddChild(subtitle);

        center.AddChild(Spacer(24));

        var line2 = new ColorRect { Color = Gold, CustomMinimumSize = new Vector2(300, 1) };
        center.AddChild(line2);
        center.AddChild(Spacer(32));

        // Buttons
        _newGameBtn  = MakeMenuButton("NEW OPERATION", Gold);
        _loadGameBtn = MakeMenuButton("LOAD DOSSIER", GoldDim);
        _settingsBtn = MakeMenuButton("DIRECTIVES & SETTINGS", new Color(0.25f, 0.22f, 0.3f));
        _exitBtn     = MakeMenuButton("EXIT TO REALITY", new Color(0.4f, 0.3f, 0.25f));

        center.AddChild(_newGameBtn);
        center.AddChild(_loadGameBtn);
        center.AddChild(_settingsBtn);
        center.AddChild(Spacer(20));
        center.AddChild(_exitBtn);

        // Save info panel (hidden by default)
        center.AddChild(Spacer(16));
        _saveInfoPanel = new VBoxContainer();
        _saveInfoPanel.Visible = false;
        center.AddChild(_saveInfoPanel);

        _campaignLabel = MakeLabel("", 13, Gold);
        _campaignDetailLabel = MakeLabel("", 11, new Color(0.6f, 0.6f, 0.7f));
        _saveInfoPanel.AddChild(_campaignLabel);
        _saveInfoPanel.AddChild(_campaignDetailLabel);

        // Load progress (hidden by default)
        _loadProgress = new ProgressBar();
        _loadProgress.CustomMinimumSize = new Vector2(300, 8);
        _loadProgress.MinValue = 0; _loadProgress.MaxValue = 100;
        _loadProgress.Value = 0;
        _loadProgress.Visible = false;
        center.AddChild(_loadProgress);

        _loadStatus = MakeLabel("", 10, new Color(0.5f, 0.5f, 0.6f));
        _loadStatus.Visible = false;
        center.AddChild(_loadStatus);

        // Version
        center.AddChild(Spacer(40));
        var version = MakeLabel("v0.1.0 — Godot 4.7.1", 10, new Color(0.3f, 0.3f, 0.35f));
        version.HorizontalAlignment = HorizontalAlignment.Center;
        center.AddChild(version);
    }

    private Button MakeMenuButton(string text, Color color)
    {
        var btn = new Button();
        btn.Text = text;
        btn.AddThemeFontSizeOverride("font_size", 16);
        btn.CustomMinimumSize = new Vector2(300, 46);

        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderWidthBottom = 1, BorderWidthTop = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = color
        };
        btn.AddThemeStyleboxOverride("normal", normal);

        var hover = new StyleBoxFlat
        {
            BgColor = new Color(color.R, color.G, color.B, 0.15f),
            BorderWidthBottom = 1, BorderWidthTop = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(color.R * 1.2f, color.G * 1.2f, color.B * 1.2f)
        };
        btn.AddThemeStyleboxOverride("hover", hover);

        btn.AddThemeColorOverride("font_color", color);
        return btn;
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var l = new Label(); l.Text = text;
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    private static Control Spacer(int height) =>
        new Control { CustomMinimumSize = new Vector2(0, height) };

    // ── Signal Wiring ──────────────────────────────────────────────────

    private void WireSignals()
    {
        _newGameBtn.Pressed  += () => _ = LoadSceneAsync(IntroScene);
        _loadGameBtn.Pressed += () => _ = LoadGameAndStart();
        _settingsBtn.Pressed += () => _ = LoadSceneAsync(SettingsScene);
        _exitBtn.Pressed     += () => GetTree()?.Quit();
    }

    // ── Save File Discovery ────────────────────────────────────────────

    private void ScanForSaves()
    {
        string saveDir = ProjectSettings.GlobalizePath("user://saves/");
        if (!Directory.Exists(saveDir))
        {
            _loadGameBtn.Disabled = true;
            return;
        }

        var saveFiles = Directory.GetFiles(saveDir, "*.sav");
        if (saveFiles.Length == 0)
        {
            _loadGameBtn.Disabled = true;
            return;
        }

        // Find newest save by file modification time
        _latestSavePath = saveFiles[0];
        DateTime latestTime = File.GetLastWriteTime(_latestSavePath);
        foreach (var f in saveFiles)
        {
            var t = File.GetLastWriteTime(f);
            if (t > latestTime) { latestTime = t; _latestSavePath = f; }
        }

        _hasSaves = true;

        // Enable and style Load Dossier
        _loadGameBtn.Disabled = false;
        _loadGameBtn.AddThemeColorOverride("font_color", Gold);
        var hover = new StyleBoxFlat
        {
            BgColor = new Color(Gold.R, Gold.G, Gold.B, 0.15f),
            BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = Gold
        };
        _loadGameBtn.AddThemeStyleboxOverride("normal", hover);

        // Try to parse metadata
        ParseSaveMetadata(_latestSavePath);
    }

    private void ParseSaveMetadata(string path)
    {
        try
        {
            // Save files are AES-encrypted — try reading as Godot FileAccess
            if (!Godot.FileAccess.FileExists(path.Replace(ProjectSettings.GlobalizePath(""), "")))
                return;

            // For encrypted saves, we show minimal metadata from file stats
            var fi = new FileInfo(path);
            string timeAgo = GetTimeAgo(fi.LastWriteTime);

            _preview = new SaveMenuPreview
            {
                Cash = 0, // would be decrypted in production
                DayCount = 0,
                Reputation = 0,
                Heat = 0
            };

            _campaignLabel.Text = $"Continue Campaign";
            _campaignDetailLabel.Text = $"Last saved: {timeAgo}";
            _saveInfoPanel.Visible = true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MainMenuController] Metadata parse error: {ex.Message}");
        }
    }

    private static string GetTimeAgo(DateTime dt)
    {
        var span = DateTime.UtcNow - dt;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    // ── Async Scene Loading ────────────────────────────────────────────

    private async Task LoadSceneAsync(string scenePath)
    {
        _loadProgress.Visible = true;
        _loadProgress.Value = 0;
        _loadStatus.Visible = true;
        _loadStatus.Text = "Loading...";

        EmitSignal(SignalName.OnSceneLoadStarted, scenePath);

        // Disable all buttons during load
        SetButtonsEnabled(false);

        try
        {
            // Use ResourceLoader for non-blocking load
            var error = ResourceLoader.LoadThreadedRequest(scenePath);

            if (error != Error.Ok)
            {
                GD.PrintErr($"[MainMenuController] Failed to start async load: {error}");
                SetButtonsEnabled(true);
                return;
            }

            // Poll until loaded
            var status = ResourceLoader.ThreadLoadStatus.InProgress;
            while (status == ResourceLoader.ThreadLoadStatus.InProgress)
            {
                status = ResourceLoader.LoadThreadedGetStatus(scenePath);
                float progress = ResourceLoader.LoadThreadedGetStatus(scenePath) ==
                                 ResourceLoader.ThreadLoadStatus.Loaded ? 1f : 0.5f;

                // Simulate smooth progress
                _loadProgress.Value = Mathf.Lerp((float)_loadProgress.Value, progress * 100f, 0.1f);
                EmitSignal(SignalName.OnSceneLoadProgress, (float)_loadProgress.Value / 100f);

                await Task.Delay(50);
            }

            if (status == ResourceLoader.ThreadLoadStatus.Loaded)
            {
                _loadProgress.Value = 100;
                _loadStatus.Text = "Ready.";

                var packedScene = ResourceLoader.LoadThreadedGet(scenePath) as PackedScene;
                if (packedScene != null)
                {
                    GetTree().ChangeSceneToPacked(packedScene);
                }
            }
            else
            {
                _loadStatus.Text = "Load failed.";
                SetButtonsEnabled(true);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MainMenuController] Scene load error: {ex.Message}");
            _loadStatus.Text = $"Error: {ex.Message}";
            SetButtonsEnabled(true);
        }

        EmitSignal(SignalName.OnSceneLoadComplete);
    }

    private async Task LoadGameAndStart()
    {
        if (!_hasSaves) return;

        _loadStatus.Text = "Loading save...";
        _loadProgress.Visible = true;
        SetButtonsEnabled(false);

        // In production: call SaveLoadSystem.LoadGame(autosave) then transition
        // For now: just load the main game scene
        await LoadSceneAsync(MainGameScene);
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _newGameBtn.Disabled  = !enabled;
        _loadGameBtn.Disabled = !enabled || !_hasSaves;
        _settingsBtn.Disabled = !enabled;
        _exitBtn.Disabled     = !enabled;
    }

    public override string ToString() =>
        $"[MainMenuController] Saves={_hasSaves} Preview={_preview != null}";
}
