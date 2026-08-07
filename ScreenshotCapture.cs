using Godot;
using System;

/// <summary>
/// Captures the rendered viewport to a PNG.
///
/// Exists so the dollhouse view can be verified from a headless-ish run
/// without a human watching the window: the scene can drive itself for a few
/// seconds, snap a frame, and quit. Also bound to a key for manual use while
/// tuning the look.
/// </summary>
public partial class ScreenshotCapture : Node
{
    /// <summary>Fired after a capture is written to disk.</summary>
    [Signal]
    public delegate void OnCapturedEventHandler(string path);

    /// <summary>Directory for captures. Created if absent.</summary>
    [Export] public string OutputDirectory { get; set; } = "user://screenshots/";

    /// <summary>Key that triggers a manual capture.</summary>
    [Export] public Key CaptureKey { get; set; } = Key.F12;

    /// <summary>
    /// Capture automatically this many seconds after the scene starts, then
    /// quit. Zero or less disables the automatic behaviour.
    /// </summary>
    [Export] public float AutoCaptureAfterSeconds { get; set; }

    /// <summary>Quit the game once an automatic capture completes.</summary>
    [Export] public bool QuitAfterAutoCapture { get; set; } = true;

    private float _elapsed;
    private bool _autoCaptureDone;

    public override void _Ready()
    {
        DirAccess.MakeDirRecursiveAbsolute(OutputDirectory);
        GD.Print($"[Screenshot] Ready. Press {CaptureKey} to capture. " +
                 $"Output: {ProjectSettings.GlobalizePath(OutputDirectory)}");
    }

    public override void _Process(double delta)
    {
        if (AutoCaptureAfterSeconds <= 0f || _autoCaptureDone) return;

        _elapsed += (float)delta;
        if (_elapsed < AutoCaptureAfterSeconds) return;

        _autoCaptureDone = true;
        Capture("auto");

        if (QuitAfterAutoCapture)
        {
            // One frame of grace so the capture is flushed before teardown.
            CallDeferred(nameof(QuitNow));
        }
    }

    private void QuitNow() => GetTree().Quit();

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key && key.Keycode == CaptureKey)
            Capture("manual");
    }

    /// <summary>
    /// Grab the current frame and write it as a PNG.
    /// </summary>
    /// <param name="tag">Short label folded into the filename.</param>
    /// <returns>The globalized path written, or null on failure.</returns>
    public string Capture(string tag = "shot")
    {
        var viewport = GetViewport();
        if (viewport == null)
        {
            GD.PrintErr("[Screenshot] No viewport available.");
            return null;
        }

        var image = viewport.GetTexture()?.GetImage();
        if (image == null)
        {
            GD.PrintErr("[Screenshot] Viewport produced no image.");
            return null;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = $"{OutputDirectory}{tag}-{stamp}.png";

        var error = image.SavePng(path);
        if (error != Error.Ok)
        {
            GD.PrintErr($"[Screenshot] Save failed: {error}");
            return null;
        }

        var globalPath = ProjectSettings.GlobalizePath(path);
        GD.Print($"[Screenshot] Wrote {globalPath} ({image.GetWidth()}x{image.GetHeight()})");

        EmitSignal(SignalName.OnCaptured, globalPath);
        return globalPath;
    }
}
