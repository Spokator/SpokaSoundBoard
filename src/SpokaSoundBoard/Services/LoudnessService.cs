using System.Threading.Channels;
using System.Windows.Threading;
using SpokaSoundBoard.Core.Audio;

namespace SpokaSoundBoard.Services;

/// <summary>Analyses the loudness of sounds and tracks one at a time in the background.</summary>
public sealed class LoudnessService : IDisposable
{
    private readonly Channel<(string Path, Action<double?> Done)> _queue = Channel.CreateUnbounded<(string, Action<double?>)>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dispatcher _dispatcher;

    public LoudnessService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _ = Task.Run(WorkAsync);
    }

    /// <param name="done">Called on the UI thread with the correction in dB (null if the file could not be read).</param>
    public void Enqueue(string path, Action<double?> done) => _queue.Writer.TryWrite((path, done));

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var (path, done) in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                double? gain = null;
                try
                {
                    gain = await Task.Run(() => LoudnessAnalyzer.AnalyzeFile(path, _cts.Token), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // Unreadable file: no correction.
                }
                // 0 means "analysed, nothing to do"; avoids re-analysing unreadable files on every start.
                var result = gain ?? 0;
                _ = _dispatcher.BeginInvoke(() => done(result));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
    }
}
