namespace SpokaSoundBoard.Core.Audio;

/// <summary>Thread-safe FIFO of float samples that drops the oldest data when full.</summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _buffer;
    private readonly object _lock = new();
    private int _read, _write, _count;

    public FloatRingBuffer(int capacity) => _buffer = new float[capacity];

    public int Capacity => _buffer.Length;

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    public void Clear()
    {
        lock (_lock) _read = _write = _count = 0;
    }

    public void Write(ReadOnlySpan<float> data)
    {
        lock (_lock)
        {
            if (data.Length > _buffer.Length) data = data[^_buffer.Length..];
            var overflow = _count + data.Length - _buffer.Length;
            if (overflow > 0) SkipUnlocked(overflow);

            var first = Math.Min(data.Length, _buffer.Length - _write);
            data[..first].CopyTo(_buffer.AsSpan(_write));
            data[first..].CopyTo(_buffer);
            _write = (_write + data.Length) % _buffer.Length;
            _count += data.Length;
        }
    }

    public int Read(Span<float> destination)
    {
        lock (_lock)
        {
            var n = Math.Min(destination.Length, _count);
            var first = Math.Min(n, _buffer.Length - _read);
            _buffer.AsSpan(_read, first).CopyTo(destination);
            _buffer.AsSpan(0, n - first).CopyTo(destination[first..]);
            _read = (_read + n) % _buffer.Length;
            _count -= n;
            return n;
        }
    }

    public void Skip(int count)
    {
        lock (_lock) SkipUnlocked(Math.Min(count, _count));
    }

    private void SkipUnlocked(int count)
    {
        _read = (_read + count) % _buffer.Length;
        _count -= count;
    }
}
