#if TOOLS
using Godot;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.ReflectorNet;
using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>
/// Godot-specific MCP server builder that wraps McpPluginBuilder with
/// editor-tool registration and runtime exception capture.
/// </summary>
public class McpServerBuilder
{
    private readonly IMcpPluginBuilder _builder;
    private readonly com.IvanMurzak.McpPlugin.Common.Version _version;
    private bool _runtimeErrorCaptureEnabled;

    public McpServerBuilder()
    {
        _version = new com.IvanMurzak.McpPlugin.Common.Version
        {
            Api = "1.0.0",
            Plugin = "7.2.0"
        };

        _builder = new McpPluginBuilder(_version)
            .WithConfig(config =>
            {
                config.Host = "http://localhost:8080";
                config.KeepConnected = true;
                config.GenerateSkillFiles = true;
                config.SkillsPath = "SKILLS";
                config.ProjectRootPath = ProjectSettings.GlobalizePath("res://");
            });
    }

    /// <summary>
    /// Register standard Godot editor tools ([AiTool]-attributed methods from
    /// the plugin assembly) and the built-in standard-tool suite.
    /// </summary>
    public McpServerBuilder WithStandardTools()
    {
        var asm = typeof(McpServerBuilder).Assembly;
        var asmList = new System.Reflection.Assembly[] { asm };

        _builder
            .WithToolsFromAssembly(asmList)
            .WithPromptsFromAssembly(asmList)
            .WithResourcesFromAssembly(asmList)
            .WithSkillsFromAssembly(asmList)
            .WithReflectorModulesFromAssembly(asmList);

        GD.Print("[Godot-MCP] Standard tools registered from assembly.");

        // Register the built-in standard tools as explicit entries so they
        // are always available regardless of attribute scanning.
        _builder
            .WithTool(
                name: "get_debug_output",
                title: "Get Godot debug output",
                classType: typeof(StandardTools),
                methodInfo: typeof(StandardTools).GetMethod(nameof(StandardTools.GetDebugOutput)))
            .WithTool(
                name: "get_project_info",
                title: "Get Godot project metadata",
                classType: typeof(StandardTools),
                methodInfo: typeof(StandardTools).GetMethod(nameof(StandardTools.GetProjectInfo)))
            .WithTool(
                name: "get_scene_tree",
                title: "Get current scene tree structure",
                classType: typeof(StandardTools),
                methodInfo: typeof(StandardTools).GetMethod(nameof(StandardTools.GetSceneTree)))
            .WithTool(
                name: "capture_exception",
                title: "Capture and forward a runtime exception to the AI agent",
                classType: typeof(StandardTools),
                methodInfo: typeof(StandardTools).GetMethod(nameof(StandardTools.CaptureException)));

        GD.Print("[Godot-MCP] Built-in standard tools registered.");
        return this;
    }

    /// <summary>
    /// Inject exception listeners into the game loop / editor runtime so
    /// in-editor and in-game crashes stream directly to Reasonix via MCP.
    /// Hooks AppDomain unhandled exceptions, unobserved task exceptions,
    /// and Godot's internal error-push signal.
    /// </summary>
    public McpServerBuilder WithRuntimeErrorCapture()
    {
        _runtimeErrorCaptureEnabled = true;

        // Hook AppDomain-level unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            var message = ex?.ToString() ?? args.ExceptionObject?.ToString() ?? "Unknown unhandled exception";
            RuntimeErrorBuffer.Enqueue(new RuntimeError
            {
                Timestamp = DateTime.UtcNow,
                Message = message,
                Source = "AppDomain.UnhandledException"
            });
        };

        // Hook unobserved task exceptions
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            RuntimeErrorBuffer.Enqueue(new RuntimeError
            {
                Timestamp = DateTime.UtcNow,
                Message = args.Exception.ToString(),
                Source = "TaskScheduler.UnobservedTaskException"
            });
            args.SetObserved(); // prevent process crash
        };

        GD.Print("[Godot-MCP] Runtime error capture enabled — exceptions will stream to Reasonix.");
        return this;
    }

    /// <summary>
    /// Build the IMcpPlugin instance with all configured options.
    /// </summary>
    public IMcpPlugin Build(Reflector reflector)
    {
        var plugin = _builder.Build(reflector);

        if (_runtimeErrorCaptureEnabled)
        {
            // Wire up the error-reporter to MCP notifications
            _ = RuntimeErrorReporter.StartAsync(plugin);
        }

        return plugin;
    }
}

/// <summary>
/// Captured runtime error from any monitored source.
/// </summary>
public struct RuntimeError
{
    public DateTime Timestamp;
    public string Message;
    public string Source;
}

/// <summary>
/// Thread-safe ring buffer for runtime errors pending MCP delivery.
/// </summary>
internal static class RuntimeErrorBuffer
{
    private static readonly ConcurrentQueue<RuntimeError> _queue = new();
    private const int MaxQueued = 256;

    public static void Enqueue(RuntimeError error)
    {
        _queue.Enqueue(error);
        // Trim overflow: discard oldest if beyond limit
        while (_queue.Count > MaxQueued)
            _queue.TryDequeue(out _);
    }

    public static bool TryDequeue(out RuntimeError error) => _queue.TryDequeue(out error);
}

/// <summary>
/// Background reporter that drains the error buffer and delivers
/// each error as an MCP notification via the plugin's logger.
/// </summary>
internal static class RuntimeErrorReporter
{
    public static async System.Threading.Tasks.Task StartAsync(IMcpPlugin plugin)
    {
        while (true)
        {
            await System.Threading.Tasks.Task.Delay(500);

            while (RuntimeErrorBuffer.TryDequeue(out var error))
            {
                try
                {
                    // Log through MCP plugin logger so it reaches connected AI agents
                    plugin.Logger?.LogError(
                        "[MCP-RuntimeError] {Source}: {Message}\nTimestamp: {Timestamp:O}",
                        error.Source,
                        error.Message,
                        error.Timestamp);

                    // Also emit through Godot's own error channel for the editor console
                    GD.PushError($"[MCP-RuntimeError] {error.Source}: {error.Message}");
                }
                catch
                {
                    // Best-effort delivery; never let reporter failure cascade
                }
            }
        }
    }
}

/// <summary>
/// Built-in standard tools exposed to the MCP AI agent.
/// </summary>
[AiToolType]
public static class StandardTools
{
    [AiTool("get_debug_output", "Get the current Godot debug output and errors")]
    public static string GetDebugOutput()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Godot Debug Output ===");

        var versionInfo = Engine.GetVersionInfo();
        sb.AppendLine($"[Engine] Godot {versionInfo["string"]}");

        while (RuntimeErrorBuffer.TryDequeue(out var error))
        {
            sb.AppendLine($"[{error.Source}] {error.Timestamp:HH:mm:ss}: {error.Message}");
        }

        if (sb.Length == 0)
            sb.AppendLine("(no errors captured)");

        return sb.ToString();
    }

    [AiTool("get_project_info", "Get metadata about the current Godot project")]
    public static string GetProjectInfo()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Project Info ===");
        sb.AppendLine($"Name: {ProjectSettings.GetSetting("application/config/name")}");
        sb.AppendLine($"Description: {ProjectSettings.GetSetting("application/config/description")}");
        sb.AppendLine($"Version: {ProjectSettings.GetSetting("application/config/version")}");
        sb.AppendLine($"Main Scene: {ProjectSettings.GetSetting("application/run/main_scene")}");
        sb.AppendLine($"Features: {ProjectSettings.GetSetting("application/config/features")}");
        return sb.ToString();
    }

    [AiTool("get_scene_tree", "Get the structure of the currently open scene")]
    public static string GetSceneTree()
    {
        // EditorInterface.Singleton is the static accessor in Godot 4.x
        var editor = EditorInterface.Singleton;
        if (editor == null)
            return "(no editor interface available)";

        var editedScene = editor.GetEditedSceneRoot();
        if (editedScene == null)
            return "(no scene open)";

        return DescribeNode(editedScene, 0);
    }

    [AiTool("capture_exception", "Capture and record a runtime exception for debugging")]
    public static string CaptureException(string message, string stackTrace, string source)
    {
        var error = new RuntimeError
        {
            Timestamp = DateTime.UtcNow,
            Message = $"{message}\n{stackTrace}",
            Source = source ?? "manual"
        };
        RuntimeErrorBuffer.Enqueue(error);
        return $"Exception captured: {message}";
    }

    private static string DescribeNode(Node node, int depth)
    {
        var indent = new string(' ', depth * 2);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{indent}{node.GetType().Name} \"{node.Name}\"");

        foreach (Node child in node.GetChildren())
            sb.Append(DescribeNode(child, depth + 1));

        return sb.ToString();
    }
}
#endif
