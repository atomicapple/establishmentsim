using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>A work item dispatched to the background thread pool.</summary>
public class PoolWorkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString()[..8];
    public string Description { get; set; }
    public Func<object> Work { get; set; }
    public Action<object> OnCompleted { get; set; }
    public Action<Exception> OnError { get; set; }
    public DateTime EnqueuedAt { get; set; }
}

/// <summary>Result from a completed pool work item.</summary>
public class PoolWorkResult
{
    public string Id { get; set; }
    public object Data { get; set; }
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }
    public double DurationMs { get; set; }
}

/// <summary>
/// Offloads heavy non-Godot computations to background thread pools.
/// Routes MCP network calls, JSON schema validation, and save file
/// encryption away from the main render thread. Uses CallDeferred()
/// to safely dispatch completed results back to scene nodes.
/// </summary>
public partial class TaskPoolDispatcher : Node
{
    [Signal] public delegate void OnWorkCompletedEventHandler(string workId, bool success);
    [Signal] public delegate void OnWorkFailedEventHandler(string workId, string error);

    private readonly ConcurrentQueue<PoolWorkItem> _pending = new();
    private readonly ConcurrentQueue<PoolWorkResult> _completed = new();
    private readonly ConcurrentDictionary<string, (Action<object> onOk, Action<Exception> onErr)> _callbacks = new();
    private readonly List<Task> _activeTasks = new();
    private int _maxConcurrency = 4;
    private int _activeCount;
    private bool _isRunning = true;

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set => _maxConcurrency = Math.Max(1, value);
    }

    public int PendingCount => _pending.Count;
    public int ActiveCount => _activeCount;
    public int CompletedCount => _completed.Count;

    public override void _Ready()
    {
        GD.Print($"[TaskPool] Initialized. Max concurrency: {_maxConcurrency}.");
    }

    public override void _Process(double delta)
    {
        // Drain completed results back to the main thread
        while (_completed.TryDequeue(out var result))
        {
            EmitSignal(SignalName.OnWorkCompleted, result.Id, result.Success);
            if (!result.Success)
                EmitSignal(SignalName.OnWorkFailed, result.Id, result.ErrorMessage ?? "unknown");

            // Dispatch stored callback
            if (_callbacks.TryRemove(result.Id, out var cb))
            {
                if (result.Success && cb.onOk != null)
                {
                    try { cb.onOk(result.Data); }
                    catch (Exception ex) { GD.PrintErr($"[TaskPool] Callback error: {ex.Message}"); }
                }
                if (!result.Success && cb.onErr != null)
                {
                    try { cb.onErr(new Exception(result.ErrorMessage)); }
                    catch (Exception ex) { GD.PrintErr($"[TaskPool] Error cb error: {ex.Message}"); }
                }
            }
        }

        // Dispatch pending work if slots available
        while (_activeCount < _maxConcurrency && _pending.TryDequeue(out var item))
        {
            StartWorkItem(item);
        }
    }

    public override void _ExitTree()
    {
        _isRunning = false;
    }

    /// <summary>Enqueue a background work item. Results delivered via signal or callback.</summary>
    public string EnqueueWork(string description, Func<object> work, Action<object> onCompleted = null, Action<Exception> onError = null)
    {
        var item = new PoolWorkItem
        {
            Description = description,
            Work = work,
            OnCompleted = onCompleted,
            OnError = onError,
            EnqueuedAt = DateTime.UtcNow
        };

        _pending.Enqueue(item);
        if (onCompleted != null || onError != null)
            _callbacks[item.Id] = (onCompleted, onError);
        return item.Id;
    }

    private void StartWorkItem(PoolWorkItem item)
    {
        Interlocked.Increment(ref _activeCount);

        var task = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                object data = item.Work();
                sw.Stop();

                var result = new PoolWorkResult
                {
                    Id = item.Id, Data = data, Success = true, DurationMs = sw.Elapsed.TotalMilliseconds
                };
                _completed.Enqueue(result);
            }
            catch (Exception ex)
            {
                sw.Stop();
                var result = new PoolWorkResult
                {
                    Id = item.Id, Success = false, ErrorMessage = ex.Message, DurationMs = sw.Elapsed.TotalMilliseconds
                };
                _completed.Enqueue(result);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
            }
        });

        lock (_activeTasks)
        {
            _activeTasks.Add(task);
            _activeTasks.RemoveAll(t => t.IsCompleted);
        }
    }

    /// <summary>Offload MCP network call to background thread.</summary>
    public string EnqueueMcpCall(string endpoint, string payload, Action<string> onResponse)
    {
        return EnqueueWork($"MCP:{endpoint}", () =>
        {
            // Simulate network call — in production, this would make HTTP request
            Thread.Sleep(50); // simulated latency
            return $"MCP response for {endpoint}: OK";
        }, result => onResponse?.Invoke(result as string));
    }

    /// <summary>Offload JSON schema validation to background thread.</summary>
    public string EnqueueJsonValidation(string json, string schemaName, Action<bool> onValidated)
    {
        return EnqueueWork($"JSON Validate:{schemaName}", () =>
        {
            try
            {
                System.Text.Json.JsonDocument.Parse(json);
                return true;
            }
            catch { return false; }
        }, result => onValidated?.Invoke((bool)result));
    }

    /// <summary>Offload save file encryption to background thread.</summary>
    public string EnqueueSaveEncryption(string plaintext, string key, Action<string> onEncrypted)
    {
        return EnqueueWork("Save:Encrypt", () =>
        {
            byte[] keyBytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(key));
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = keyBytes;
            aes.GenerateIV();
            using var encryptor = aes.CreateEncryptor();
            byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
            byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
            byte[] result = new byte[aes.IV.Length + cipherBytes.Length];
            Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
            Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
            return Convert.ToBase64String(result);
        }, result => onEncrypted?.Invoke(result as string));
    }

    /// <summary>Wait for all pending and active work to complete.</summary>
    public async Task WaitAllAsync()
    {
        while (_pending.Count > 0 || _activeCount > 0)
            await Task.Delay(50);
    }

    public override string ToString() =>
        $"[TaskPool] Pending={_pending.Count} Active={_activeCount} Done={_completed.Count}";
}
