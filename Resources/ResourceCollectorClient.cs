using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

sealed class ResourceCollectorClient : IAsyncDisposable
{
    readonly string _pipeName = CollectorPipe.Prefix + Guid.NewGuid().ToString("N");
    readonly NamedPipeServerStream _pipe;
    readonly SemaphoreSlim _writeLock = new(1);
    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource<CollectorMessage>? _sample;
    StreamWriter? _writer;
    Task? _receive;
    Process? _process;
    bool _disposed;

    public event Action<ProcessKey, string>? Started;
    public event Action<ProcessKey>? Exited;
    public event Action<string>? Failed;

    public ResourceCollectorClient() =>
        _pipe = new(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public async Task ConnectAsync(string[] watchedNames, CancellationToken token)
    {
        _process = await Task.Run(() => Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--resource-collector {_pipeName} {Environment.ProcessId}",
        }) ?? throw new IOException("The resource collector did not start."), token);
        await _pipe.WaitForConnectionAsync(token);
        CollectorPipe.VerifyPeer(_pipe, _process.Id, server: true);
        _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
        _receive = ReceiveAsync();
        await _ready.Task.WaitAsync(token);
        await WatchAsync(watchedNames, token);
    }

    public Task WatchAsync(string[] names, CancellationToken token) => SendAsync(new("watch", names), token);

    public async Task<CollectorMessage> SampleAsync(int[]? targets, int[]? alivePids, CancellationToken token)
    {
        var completion = new TaskCompletionSource<CollectorMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sample = completion;
        try
        {
            await SendAsync(new("sample", TargetPids: targets, AlivePids: alivePids), token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        finally { _sample = null; }
    }

    async Task SendAsync(CollectorRequest request, CancellationToken token)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            if (_writer is null)
                throw new IOException("Resource collector is not connected.");
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request, ResourceJson.Default.CollectorRequest).AsMemory(), token);
        }
        finally { _writeLock.Release(); }
    }

    async Task ReceiveAsync()
    {
        using var reader = new StreamReader(_pipe, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var message = JsonSerializer.Deserialize(line, ResourceJson.Default.CollectorMessage)
                    ?? throw new InvalidDataException("Empty resource collector response.");
                switch (message.Type)
                {
                    case "ready": _ready.TrySetResult(); break;
                    case "sample": _sample?.TrySetResult(message); break;
                    case "start" when message.Key is { } key && message.Name is { } name:
                        Started?.Invoke(key, name);
                        break;
                    case "exit" when message.Key is { } key: Exited?.Invoke(key); break;
                    case "error": throw new IOException(message.Error ?? "Resource collector failed.");
                    default: throw new InvalidDataException("Unknown resource collector response.");
                }
            }
            if (!_disposed)
                throw new IOException("Resource collector disconnected.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
        {
            _ready.TrySetException(ex);
            _sample?.TrySetException(ex);
            if (!_disposed)
            {
                CrashLog.Write(ex);
                Failed?.Invoke(ex.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _pipe.Dispose(); // EOF stops the elevated collector, including its ETW session
        if (_receive is not null)
            await _receive;
        if (_process is not null)
        {
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { _process.Dispose(); }
        }
    }
}
