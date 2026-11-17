using Concentus;
using Concentus.Oggfile;
using NAudio.Wave;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Reads Ogg Opus files (.opus, and the many .ogg files that hold Opus rather than Vorbis: Discord and WhatsApp voice
/// messages, most recorders, many game files) as 48 kHz stereo float. Decoded with Concentus (pure C#).
/// </summary>
public sealed class OpusWaveReader : WaveStream
{
    private const int Rate = 48000, Channels = 2;

    private readonly FileStream _file;
    private readonly OpusOggReadStream _ogg;
    private readonly long _length;
    private float[] _pending = [];
    private int _pendingOffset, _pendingCount;
    private long _position;
    private long _skipFloats; // after a seek: decoded audio before the asked position (seeking lands on a page start)

    public OpusWaveReader(string path)
    {
        _file = File.OpenRead(path);
        try
        {
            // A stereo decoder also plays mono streams (Opus upmixes them itself).
            _ogg = new OpusOggReadStream(OpusCodecFactory.CreateDecoder(Rate, Channels), _file);
            if (!_ogg.HasNextPacket) throw new InvalidDataException(_ogg.LastError ?? "Fichier Opus vide ou illisible.");
            _length = _ogg.CanSeek ? (long)(_ogg.TotalTime.TotalSeconds * Rate) * Channels * sizeof(float) : 0;
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    /// <summary>True when the file is Ogg with an Opus stream (its first page carries « OpusHead »).</summary>
    public static bool IsOpus(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[96];
            var n = f.Read(head, 0, head.Length);
            if (n < 36 || head[0] != 'O' || head[1] != 'g' || head[2] != 'g' || head[3] != 'S') return false;
            return head.AsSpan(0, n).IndexOf("OpusHead"u8) >= 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public override WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (!_ogg.CanSeek) throw new NotSupportedException();
            var frame = Math.Clamp(value, 0, _length) / (Channels * sizeof(float));
            _ogg.SeekTo(TimeSpan.FromSeconds(frame / (double)Rate));
            // It lands at the start of the page holding that time (pages last up to about a second).
            var landed = (long)Math.Round(_ogg.CurrentTime.TotalSeconds * Rate);
            _skipFloats = Math.Max(0, frame - landed) * Channels;
            _pendingCount = _pendingOffset = 0;
            _position = frame * Channels * sizeof(float);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        count -= count % sizeof(float);
        var written = 0;
        while (written < count)
        {
            if (_pendingCount == 0 && !DecodeNext()) break;
            var floats = Math.Min(_pendingCount, (count - written) / sizeof(float));
            Buffer.BlockCopy(_pending, _pendingOffset * sizeof(float), buffer, offset + written, floats * sizeof(float));
            _pendingOffset += floats;
            _pendingCount -= floats;
            written += floats * sizeof(float);
        }
        _position += written;
        return written;
    }

    private bool DecodeNext()
    {
        while (_ogg.HasNextPacket)
        {
            var packet = _ogg.DecodeNextPacket();
            if (packet == null || packet.Length == 0) continue; // header or damaged packet: skip it
            var skip = (int)Math.Min(_skipFloats, packet.Length);
            _skipFloats -= skip;
            if (skip == packet.Length) continue;
            if (_pending.Length < packet.Length) _pending = new float[packet.Length];
            for (var i = skip; i < packet.Length; i++) _pending[i - skip] = packet[i] / 32768f;
            _pendingOffset = 0;
            _pendingCount = packet.Length - skip;
            return true;
        }
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}
