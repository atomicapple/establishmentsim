#if TOOLS
using Godot;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.ReflectorNet;
using System;
using System.Threading;

[Tool]
public partial class McpPluginBridge : EditorPlugin
{
    private IMcpPlugin _plugin;
    private Reflector _reflector;
    private CancellationTokenSource _cts;

    public override void _EnterTree()
    {
        GD.Print("[Godot-MCP] Initializing MCP plugin bridge...");

        try
        {
            _reflector = new Reflector();
            _cts = new CancellationTokenSource();

            var version = new com.IvanMurzak.McpPlugin.Common.Version
            {
                Api = "1.0.0",
                Plugin = "7.2.0"
            };

            _plugin = new McpPluginBuilder(version)
                .WithConfig(config =>
                {
                    config.Host = "http://localhost:8080";
                    config.KeepConnected = true;
                    config.GenerateSkillFiles = true;
                    config.SkillsPath = "SKILLS";
                })
                .WithToolsFromAssembly(typeof(McpPluginBridge).Assembly)
                .WithPromptsFromAssembly(typeof(McpPluginBridge).Assembly)
                .WithResourcesFromAssembly(typeof(McpPluginBridge).Assembly)
                .Build(_reflector);

            GD.Print("[Godot-MCP] Plugin bridge built successfully. Connecting...");

            // Fire-and-forget: connect in background
            _ = _plugin.Connect(_cts.Token);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Godot-MCP] Failed to initialize: {ex.Message}");
        }
    }

    public override void _ExitTree()
    {
        GD.Print("[Godot-MCP] Shutting down MCP plugin bridge...");
        _cts.Cancel();

        if (_plugin is IDisposable disposable)
            disposable.Dispose();

        _cts.Dispose();

        _plugin = null;
        _reflector = null;
        _cts = null;
    }
}
#endif
