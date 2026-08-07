using Godot;
using System.Collections.Generic;

public partial class CharacterSpawner : Node
{
    private Node3D _character;
    private Camera3D _camera;
    private float _yaw, _pitch, _camDistance = 4f;
    private Vector3 _camTarget = Vector3.Zero;
    private AnimationPlayer _animPlayer;
    private List<string> _anims = new();
    private bool _wasMoving;

    public override void _Ready() => CallDeferred(nameof(SpawnCharacter));

    private void SpawnCharacter()
    {
        var root = GetTree()?.Root;
        if (root == null) return;

        var scene3D = new Node3D { Name = "AlleyScene" };
        root.AddChild(scene3D);
        root.MoveChild(scene3D, 0);

        BuildAlley(scene3D);
        SetupCamera(scene3D);
        LoadCharacter(scene3D);
        LoadAndApplyAnimations();
    }

    // ── ALLEY ───────────────────────────────────────────────────────
    private void BuildAlley(Node3D p)
    {
        var env = new WorldEnvironment();
        env.Environment = new Godot.Environment { BackgroundColor = new Color(0.02f, 0.02f, 0.05f), AmbientLightColor = new Color(0.15f, 0.12f, 0.2f), AmbientLightEnergy = 0.3f };
        p.AddChild(env);

        var moon = new DirectionalLight3D { RotationDegrees = new Vector3(-60, 30, 0), LightColor = new Color(0.6f, 0.65f, 0.9f), LightEnergy = 0.4f, ShadowEnabled = true };
        p.AddChild(moon);

        p.AddChild(MakeBox(new Vector3(8, 0.1f, 20), new Vector3(0, -0.1f, 0), new Color(0.12f, 0.10f, 0.08f)));
        p.AddChild(MakeBox(new Vector3(3, 0.15f, 20), new Vector3(-2.5f, -0.05f, 0), new Color(0.18f, 0.16f, 0.14f)));
        p.AddChild(MakeBox(new Vector3(3, 0.15f, 20), new Vector3(2.5f, -0.05f, 0), new Color(0.18f, 0.16f, 0.14f)));
        p.AddChild(MakeBox(new Vector3(0.3f, 6, 20), new Vector3(-4f, 3f, 0), new Color(0.08f, 0.06f, 0.04f)));
        p.AddChild(MakeBox(new Vector3(0.3f, 6, 20), new Vector3(4f, 3f, 0), new Color(0.08f, 0.06f, 0.04f)));

        for (float z = -8; z <= 8; z += 4)
        {
            p.AddChild(MakeBox(new Vector3(0.05f, 0.5f, 0.8f), new Vector3(-3.85f, 2.5f, z), new Color(1f, 0.7f, 0.2f, 0.3f)));
            p.AddChild(MakeBox(new Vector3(0.05f, 0.5f, 0.8f), new Vector3(3.85f, 2.5f, z), new Color(1f, 0.7f, 0.2f, 0.3f)));
        }
        for (float z = -6; z <= 6; z += 6)
        {
            p.AddChild(MakeBox(new Vector3(0.1f, 3.5f, 0.1f), new Vector3(0, 1.75f, z), new Color(0.2f, 0.2f, 0.2f)));
            p.AddChild(new OmniLight3D { Position = new Vector3(0, 3.5f, z), LightColor = new Color(1f, 0.7f, 0.3f), LightEnergy = 1.5f, OmniRange = 5f });
        }
        p.AddChild(MakeBox(new Vector3(0.3f, 0.5f, 0.3f), new Vector3(-1.5f, 0.3f, 3f), new Color(0.15f, 0.15f, 0.15f)));
        p.AddChild(MakeBox(new Vector3(0.4f, 0.6f, 0.4f), new Vector3(1.8f, 0.35f, -2f), new Color(0.25f, 0.15f, 0.05f)));
    }

    private static MeshInstance3D MakeBox(Vector3 s, Vector3 p, Color c)
    {
        var m = new MeshInstance3D { Mesh = new BoxMesh { Size = s }, Position = p };
        var mat = new StandardMaterial3D { AlbedoColor = c };
        if (c.A < 0.99f) mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        m.MaterialOverride = mat;
        return m;
    }

    // ── CAMERA ──────────────────────────────────────────────────────
    private void SetupCamera(Node3D p)
    {
        _camera = new Camera3D { Current = true, Position = new Vector3(3, 2.5f, 5) };
        p.AddChild(_camera);
        _camera.LookAt(Vector3.Zero, Vector3.Up);
        _camDistance = _camera.Position.Length();
        _yaw = Mathf.Atan2(_camera.Position.X, _camera.Position.Z);
        _pitch = Mathf.Asin(Mathf.Clamp(_camera.Position.Y / _camDistance, -1f, 1f));
    }

    // ── CHARACTER ───────────────────────────────────────────────────
    private void LoadCharacter(Node3D p)
    {
        GD.Print("[CharacterSpawner] Loading character...");
        try
        {
            using var f = Godot.FileAccess.Open("res://Assets/Characters/wealthy_gentleman.glb", Godot.FileAccess.ModeFlags.Read);
            if (f == null) { GD.PrintErr("Cannot open char GLB"); return; }
            var d = f.GetBuffer((long)f.GetLength());
            var gltf = new GltfDocument();
            var st = new GltfState { BasePath = "res://Assets/Characters/" };
            if (gltf.AppendFromBuffer(d, "res://Assets/Characters/", st) != Error.Ok) { GD.PrintErr("Char GLTF failed"); return; }
            _character = gltf.GenerateScene(st) as Node3D;
            if (_character == null) { GD.PrintErr("Char null"); return; }
            p.AddChild(_character);
            GD.Print("[CharacterSpawner] ✓ Character loaded.");
        }
        catch (System.Exception ex) { GD.PrintErr($"Char error: {ex.Message}"); }
    }

    // ── ANIMATIONS ──────────────────────────────────────────────────
    private void LoadAndApplyAnimations()
    {
        if (_character == null) return;

        GD.Print("[CharacterSpawner] Loading animations...");
        try
        {
            using var f = Godot.FileAccess.Open("res://Assets/Characters/wealthy_gentleman_anims.glb", Godot.FileAccess.ModeFlags.Read);
            if (f == null) { GD.PrintErr("Cannot open anims GLB"); return; }
            var d = f.GetBuffer((long)f.GetLength());
            var gltf = new GltfDocument();
            var st = new GltfState { BasePath = "res://Assets/Characters/" };
            if (gltf.AppendFromBuffer(d, "res://Assets/Characters/", st) != Error.Ok) { GD.PrintErr("Anims GLTF failed"); return; }
            var animNode = gltf.GenerateScene(st);
            if (animNode == null) { GD.PrintErr("Anim scene null"); return; }

            // Find AnimationPlayer in the animations scene
            var srcPlayer = FindNode<AnimationPlayer>(animNode);
            if (srcPlayer == null) { GD.PrintErr("No AnimationPlayer in anims GLB"); return; }

            // Find AnimationPlayer on the character
            _animPlayer = FindNode<AnimationPlayer>(_character);
            if (_animPlayer == null)
            {
                // Create one if the character doesn't have it
                _animPlayer = new AnimationPlayer { Name = "AnimationPlayer" };
                _character.AddChild(_animPlayer);
            }

            // Copy all animation libraries from the source, replacing existing
            var libs = srcPlayer.GetAnimationLibraryList();
            GD.Print($"[CharacterSpawner] Source libraries ({libs.Count}): names='{string.Join("','", libs)}'");

            // First: clear existing libraries on character
            var existingLibs = _animPlayer.GetAnimationLibraryList();
            foreach (var el in existingLibs)
                _animPlayer.RemoveAnimationLibrary(el);

            foreach (var libName in libs)
            {
                var lib = srcPlayer.GetAnimationLibrary(libName);
                if (lib != null)
                {
                    _animPlayer.AddAnimationLibrary(libName, lib);
                    GD.Print($"[CharacterSpawner]   Added library '{libName}'");
                }
            }

            // List final animations
            _anims = new List<string>(_animPlayer.GetAnimationList());
            GD.Print($"[CharacterSpawner] Final animations ({_anims.Count}): {string.Join(", ", _anims)}");

            if (_anims.Count > 0)
            {
                // Prefer standing idle over sitting idle
                string first = _anims.Find(a => a == "Idle_7") ??
                               _anims.Find(a => a.ToLower().Contains("idle") && !a.ToLower().Contains("chair")) ??
                               _anims.Find(a => a.ToLower().Contains("idle")) ??
                               _anims[0];
                _animPlayer.Play(first);
                GD.Print($"[CharacterSpawner] Playing: {first}");
            }

            // Clean up the temporary animation scene
            animNode.QueueFree();
        }
        catch (System.Exception ex) { GD.PrintErr($"Anim error: {ex.Message}"); }
    }

    private static T FindNode<T>(Node root) where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T t) return t;
            var found = FindNode<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    // ── INPUT ───────────────────────────────────────────────────────
    public override void _Input(InputEvent evt)
    {
        if (evt is InputEventMouseMotion mm && Input.IsMouseButtonPressed(MouseButton.Right))
        {
            _yaw   -= mm.Relative.X * 0.005f;
            _pitch -= mm.Relative.Y * 0.005f;
            _pitch  = Mathf.Clamp(_pitch, -1.4f, 1.4f);
        }
        if (evt is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)   _camDistance = Mathf.Max(1f, _camDistance - 0.5f);
            if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) _camDistance = Mathf.Min(15f, _camDistance + 0.5f);
        }
        // 1-9 to switch animations
        if (_animPlayer != null && evt is InputEventKey k && k.Pressed && k.Keycode >= Key.Key1 && k.Keycode <= Key.Key9)
        {
            int idx = (int)k.Keycode - (int)Key.Key1;
            if (idx < _anims.Count) { _animPlayer.Play(_anims[idx]); GD.Print($"Anim: {_anims[idx]}"); }
        }
    }

    // ── MOVEMENT ────────────────────────────────────────────────────
    public override void _Process(double delta)
    {
        bool moving = false;
        if (_character != null && _camera != null)
        {
            // Camera-relative: forward = direction camera is looking (on XZ plane)
            Vector3 camLook = (_camTarget - _camera.Position) with { Y = 0 };
            Vector3 forward = camLook.LengthSquared() > 0.01f ? camLook.Normalized() : new Vector3(0, 0, -1);
            Vector3 right = forward.Cross(Vector3.Up).Normalized();

            Vector3 move = Vector3.Zero;
            if (Input.IsKeyPressed(Key.W)) { move += forward; moving = true; }
            if (Input.IsKeyPressed(Key.S)) { move -= forward; moving = true; }
            if (Input.IsKeyPressed(Key.A)) { move -= right;   moving = true; }
            if (Input.IsKeyPressed(Key.D)) { move += right;   moving = true; }

            if (moving)
            {
                var dir = move.Normalized();
                _character.Position += dir * 2.5f * (float)delta;
                _camTarget = _character.Position;
                if (dir != Vector3.Zero)
                    _character.LookAt(_character.Position + dir, Vector3.Up);
            }

            // Animation switching
            if (_animPlayer != null && _anims.Count >= 2)
            {
                if (moving && !_wasMoving)
                {
                    string w = _anims.Find(a => a == "Walking") ??
                               _anims.Find(a => a == "Running") ??
                               _anims.Find(a => a.ToLower().Contains("walk")) ??
                               _anims.Find(a => a.ToLower().Contains("run")) ??
                               _anims.Find(a => a.ToLower().Contains("jog")) ??
                               _anims[1];
                    _animPlayer.Play(w);
                }
                else if (!moving && _wasMoving)
                {
                    string i = _anims.Find(a => a == "Idle_7") ??
                               _anims.Find(a => a.ToLower().Contains("idle") && !a.ToLower().Contains("chair") && !a.ToLower().Contains("turn")) ??
                               _anims.Find(a => a.ToLower().Contains("idle")) ??
                               _anims[0];
                    _animPlayer.Play(i);
                }
            }
            _wasMoving = moving;
        }

        if (_camera == null) return;
        Vector3 off = new Vector3(Mathf.Cos(_pitch) * Mathf.Sin(_yaw), Mathf.Sin(_pitch), Mathf.Cos(_pitch) * Mathf.Cos(_yaw)) * _camDistance;
        _camera.Position = _camTarget + off;
        _camera.LookAt(_camTarget, Vector3.Up);
    }
}
