#if TOOLS
using Godot;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet;
using System;
using System.Threading;

/// <summary>
/// Main Godot Editor Plugin entry point for the MCP integration.
/// Initializes the MCP server bridge on editor boot with standard tools
/// and runtime error capture, and performs clean shutdown on exit.
/// </summary>
[Tool]
public partial class PluginMain : EditorPlugin
{
    private IMcpPlugin _plugin;
    private Reflector _reflector;
    private CancellationTokenSource _cts;

    /// <summary>
    /// Called when the plugin is activated (editor boot or manual enable).
    /// Builds the MCP server with standard tools and runtime error capture,
    /// then connects to the MCP bridge in the background.
    /// </summary>
    public override void _EnterTree()
    {
        GD.Print("[Godot-MCP] ========================================");
        GD.Print("[Godot-MCP] PluginMain initializing...");
        GD.Print("[Godot-MCP] ========================================");

        try
        {
            _reflector = new Reflector();
            _cts = new CancellationTokenSource();

            _plugin = new McpServerBuilder()
                .WithStandardTools()
                .WithRuntimeErrorCapture()
                .Build(_reflector);

            GD.Print($"[Godot-MCP] Server built (v{_plugin.Version.Plugin}). Connecting to bridge...");

            // Connect asynchronously — fire-and-forget in the editor loop.
            // The plugin remains usable even before the connection handshake completes.
            _ = ConnectAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Godot-MCP] CRITICAL: Failed to initialize plugin: {ex}");
        }
    }

    /// <summary>
    /// Called when the plugin is deactivated (editor close or manual disable).
    /// Performs graceful shutdown: cancels pending operations, disconnects
    /// from the MCP bridge, and disposes managed resources.
    /// </summary>
    public override void _ExitTree()
    {
        GD.Print("[Godot-MCP] Shutting down...");

        try
        {
            // Signal cancellation to any in-flight operations
            _cts?.Cancel();

            // Disconnect from the MCP bridge
            if (_plugin != null)
            {
                GD.Print("[Godot-MCP] Disconnecting from MCP bridge...");
                _plugin.Disconnect(CancellationToken.None)
                      .ContinueWith(t =>
                      {
                          if (t.IsFaulted)
                              GD.PrintErr($"[Godot-MCP] Disconnect error: {t.Exception}");
                      });
            }

            // Dispose managed resources
            if (_plugin is IDisposable disposable)
            {
                disposable.Dispose();
                GD.Print("[Godot-MCP] Plugin disposed.");
            }

            _cts?.Dispose();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Godot-MCP] Error during shutdown: {ex.Message}");
        }
        finally
        {
            _plugin = null;
            _reflector = null;
            _cts = null;

            GD.Print("[Godot-MCP] Shutdown complete.");
        }
    }

    /// <summary>
    /// Background connection with retry logic.
    /// </summary>
    private async System.Threading.Tasks.Task ConnectAsync(CancellationToken ct)
    {
        const int maxRetries = 5;
        const int retryDelayMs = 2000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                GD.Print("[Godot-MCP] Connection cancelled.");
                return;
            }

            try
            {
                GD.Print($"[Godot-MCP] Connection attempt {attempt}/{maxRetries}...");
                var connected = await _plugin.Connect(ct);

                if (connected)
                {
                    GD.Print($"[Godot-MCP] ✓ Connected successfully on attempt {attempt}.");
                    GD.Print($"[Godot-MCP] Handshake status: {_plugin.VersionHandshakeStatus}");
                    GD.Print($"[Godot-MCP] Tool calls tracked: {_plugin.ToolCallsCount}");
                    return;
                }

                GD.Print($"[Godot-MCP] Connection returned false on attempt {attempt}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                GD.Print($"[Godot-MCP] Connection attempt {attempt} failed: {ex.Message}");
            }

            if (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                GD.Print($"[Godot-MCP] Retrying in {retryDelayMs}ms...");
                await System.Threading.Tasks.Task.Delay(retryDelayMs, ct);
            }
        }

        GD.PrintErr("[Godot-MCP] All connection attempts exhausted. Is the MCP bridge server running?");
    }
}
#endif
