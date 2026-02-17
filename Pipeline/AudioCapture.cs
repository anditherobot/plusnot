using NAudio.Wave;

namespace plusnot.Pipeline;

public sealed class AudioCapture : IDisposable
{
    private WaveInEvent? waveIn;
    private readonly float[] ring = new float[4096];
    private int pos;
    private readonly object lk = new();

    public void Start()
    {
        waveIn = new WaveInEvent { WaveFormat = new WaveFormat(44100, 16, 1), BufferMilliseconds = 50 };
        waveIn.DataAvailable += (_, e) =>
        {
            lock (lk)
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                    ring[pos++ % ring.Length] = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8)) / 32768f;
        };
        waveIn.StartRecording();
    }

    public void Fill(float[] dest)
    {
        lock (lk)
        {
            int p = pos, n = dest.Length;
            for (int i = 0; i < n; i++)
            {
                int idx = (p - n + i) % ring.Length;
                dest[i] = ring[idx < 0 ? idx + ring.Length : idx];
            }
        }
    }

    public void Dispose() { waveIn?.StopRecording(); waveIn?.Dispose(); }
}
