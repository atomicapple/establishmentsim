using Godot;
using System;

/// <summary>
/// Dynamic portrait controller that applies visual shader overlays
/// to character portraits based on Stress and Trauma values.
/// Effects include desaturation, fatigue vignetting, and agitated
/// color shifts. Smooth transitions when switching between staff views.
/// </summary>
public partial class DynamicPortraitController : Control
{
    private TextureRect _portraitDisplay;
    private ShaderMaterial _shaderMat;
    private Tween _transitionTween;
    private StaffMember _currentStaff;

    // Target values for smooth transitions
    private float _targetDesaturation;
    private float _targetVignette;
    private float _targetColorShift;
    private float _currentDesaturation;
    private float _currentVignette;
    private float _currentColorShift;

    public override void _Ready()
    {
        BuildUI();
        GD.Print("[PortraitController] Initialized.");
    }

    private void BuildUI()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);

        _portraitDisplay = new TextureRect();
        _portraitDisplay.SetAnchorsPreset(Control.LayoutPreset.Center);
        _portraitDisplay.SetSize(new Vector2(180, 220));
        _portraitDisplay.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _portraitDisplay.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;

        // Create shader material with dynamic properties
        _shaderMat = new ShaderMaterial();
        var shader = new Shader();
        shader.Code = @"
shader_type canvas_item;

uniform float desaturation : hint_range(0, 1) = 0;
uniform float vignette_strength : hint_range(0, 1) = 0;
uniform float color_shift_r : hint_range(-1, 1) = 0;
uniform float color_shift_g : hint_range(-1, 1) = 0;
uniform float color_shift_b : hint_range(-1, 1) = 0;
uniform float scanline_intensity : hint_range(0, 1) = 0;

void fragment() {
    vec4 tex = texture(TEXTURE, UV);

    // Desaturation
    float gray = dot(tex.rgb, vec3(0.299, 0.587, 0.114));
    tex.rgb = mix(tex.rgb, vec3(gray), desaturation);

    // Color shift (agitation → red tint, fatigue → blue shift)
    tex.r += color_shift_r;
    tex.g += color_shift_g;
    tex.b += color_shift_b;

    // Vignette
    vec2 uv_centered = UV - 0.5;
    float dist = length(uv_centered) * 1.5;
    float vignette = 1.0 - smoothstep(0.3, 1.0, dist) * vignette_strength;
    tex.rgb *= vignette;

    // Scanlines (high stress = more visible)
    float scanline = sin(UV.y * 200.0) * 0.05 * scanline_intensity;
    tex.rgb -= scanline;

    COLOR = tex;
}";
        _shaderMat.Shader = shader;
        _shaderMat.SetShaderParameter("desaturation", 0f);
        _shaderMat.SetShaderParameter("vignette_strength", 0f);
        _shaderMat.SetShaderParameter("color_shift_r", 0f);
        _shaderMat.SetShaderParameter("color_shift_g", 0f);
        _shaderMat.SetShaderParameter("color_shift_b", 0f);
        _shaderMat.SetShaderParameter("scanline_intensity", 0f);

        _portraitDisplay.Material = _shaderMat;

        // Placeholder texture
        var placeholder = new ColorRect();
        placeholder.Color = new Color(0.3f, 0.25f, 0.35f);
        placeholder.SetSize(new Vector2(180, 220));
        AddChild(placeholder);
        _portraitDisplay.Texture = placeholder.GetTexture();

        AddChild(_portraitDisplay);
    }

    public override void _Process(double delta)
    {
        // Smooth interpolation toward target values
        float speed = 3f * (float)delta;
        _currentDesaturation = Mathf.Lerp(_currentDesaturation, _targetDesaturation, speed);
        _currentVignette = Mathf.Lerp(_currentVignette, _targetVignette, speed);
        _currentColorShift = Mathf.Lerp(_currentColorShift, _targetColorShift, speed);

        _shaderMat.SetShaderParameter("desaturation", _currentDesaturation);
        _shaderMat.SetShaderParameter("vignette_strength", _currentVignette);
        _shaderMat.SetShaderParameter("scanline_intensity", _currentDesaturation * 0.5f);

        // Color shift: high stress → red tint, high trauma → blue/desaturated
        float redShift = _currentColorShift * 0.15f;
        float blueShift = -_currentColorShift * 0.1f;
        _shaderMat.SetShaderParameter("color_shift_r", redShift);
        _shaderMat.SetShaderParameter("color_shift_b", blueShift);
    }

    /// <summary>Update portrait for a staff member with smooth transition.</summary>
    public void SetStaff(StaffMember staff)
    {
        if (staff == null) return;

        // Store current values as starting point for transition
        float startDesat = _targetDesaturation;
        float startVignette = _targetVignette;
        float startColor = _targetColorShift;

        // Calculate target values from staff stats
        _targetDesaturation = staff.Trauma / 100f;                    // Trauma → desaturation
        _targetVignette = Mathf.Clamp(staff.Stress / 120f, 0f, 0.85f); // Stress → vignette
        _targetColorShift = (staff.Stress / 100f) * 0.7f + (staff.Trauma / 100f) * 0.3f;

        _currentDesaturation = startDesat;
        _currentVignette = startVignette;
        _currentColorShift = startColor;

        if (_currentStaff != null)
            _currentStaff.OnAgencyChanged -= OnStaffAgencyChanged;

        _currentStaff = staff;
        _currentStaff.OnAgencyChanged += OnStaffAgencyChanged;

        GD.Print($"[PortraitController] Displaying {staff.StaffName}: " +
                 $"Desat={_targetDesaturation:F2} Vig={_targetVignette:F2} Shift={_targetColorShift:F2}");
    }

    private void OnStaffAgencyChanged(string name, float newValue, float delta)
    {
        if (_currentStaff == null) return;

        // Update targets in real time
        _targetDesaturation = _currentStaff.Trauma / 100f;
        _targetVignette = Mathf.Clamp(_currentStaff.Stress / 120f, 0f, 0.85f);
        _targetColorShift = (_currentStaff.Stress / 100f) * 0.7f + (_currentStaff.Trauma / 100f) * 0.3f;
    }

    /// <summary>Get a human-readable description of the current visual state.</summary>
    public string GetVisualStateDescription()
    {
        if (_targetDesaturation > 0.7f) return "Severely drained — trauma is overwhelming.";
        if (_targetVignette > 0.6f) return "Visibly stressed — fatigue is apparent.";
        if (_targetColorShift > 0.5f) return "Agitated — stress is showing.";
        if (_targetDesaturation < 0.2f && _targetVignette < 0.2f) return "Healthy and composed.";
        return "Managing, but showing signs of strain.";
    }
}

// Extension to get texture from ColorRect for placeholder
public static class ColorRectExtensions
{
    public static ImageTexture GetTexture(this ColorRect rect)
    {
        var img = Image.CreateEmpty(180, 220, false, Image.Format.Rgba8);
        var color = rect.Color;
        for (int y = 0; y < 220; y++)
            for (int x = 0; x < 180; x++)
                img.SetPixel(x, y, color);
        return ImageTexture.CreateFromImage(img);
    }
}
