using NAudio.Wave;
using System.Buffers.Binary;

namespace CallDock.Core;

/// <summary>Sample-indexed, bounded WAV segments. Gaps become silence; overlapping packets are trimmed.</summary>
public sealed class PcmTimelineWriter : IDisposable
{
    private readonly string folder;
    private readonly WaveFormat format;
    private readonly long segmentFrames;
    private WaveFileWriter? writer;
    private long inSegment;
    private int segment;
    private readonly byte[] zeros = new byte[65536];
    public long FramesWritten { get; private set; }

    public PcmTimelineWriter(string folder, WaveFormat format, int segmentSeconds = 300)
    {
        this.folder = folder;
        this.format = format;
        segmentFrames = (long)format.SampleRate * segmentSeconds;
        Directory.CreateDirectory(folder);
    }

    public void WriteAt(long frame, ReadOnlySpan<byte> data)
    {
        if (data.Length % format.BlockAlign != 0) throw new InvalidDataException("Unaligned PCM packet.");
        if (frame > FramesWritten) PadTo(frame);
        var skip = Math.Max(0, FramesWritten - frame);
        if (skip >= data.Length / format.BlockAlign) return;
        Write(data[(int)(skip * format.BlockAlign)..]);
    }

    public void PadTo(long frame)
    {
        while (FramesWritten < frame)
        {
            var frames = (int)Math.Min(frame - FramesWritten, zeros.Length / format.BlockAlign);
            Write(zeros.AsSpan(0, frames * format.BlockAlign));
        }
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            writer ??= new WaveFileWriter(Path.Combine(folder, $"{segment:D5}.wav"), format);
            var frames = (int)Math.Min(data.Length / format.BlockAlign, segmentFrames - inSegment);
            var count = frames * format.BlockAlign;
            writer.Write(data[..count]);
            FramesWritten += frames;
            inSegment += frames;
            data = data[count..];
            if (inSegment == segmentFrames) { writer.Dispose(); writer = null; segment++; inSegment = 0; }
        }
    }

    public void Flush() => writer?.Flush();
    public void Dispose() => writer?.Dispose();

    public static bool RepairWave(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (file.Length < 44 || file.Length > uint.MaxValue) return false;
        Span<byte> header = stackalloc byte[12];
        file.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8)) return false;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> block = stackalloc byte[2];
        int align = 1;
        while (file.Position + 8 <= file.Length)
        {
            file.ReadExactly(chunk);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("fmt "u8) && length >= 16)
            {
                var start = file.Position;
                file.Position += 12;
                file.ReadExactly(block);
                align = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(block));
                file.Position = start + length + (length % 2);
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                var size = (uint)((file.Length - file.Position) / align * align);
                file.SetLength(file.Position + size);
                file.Position -= 4;
                BinaryPrimitives.WriteUInt32LittleEndian(chunk, size);
                file.Write(chunk[..4]);
                file.Position = 4;
                BinaryPrimitives.WriteUInt32LittleEndian(chunk, (uint)(file.Length - 8));
                file.Write(chunk[..4]);
                file.Flush(true);
                return true;
            }
            else file.Position += length + (length % 2);
        }
        return false;
    }
}
