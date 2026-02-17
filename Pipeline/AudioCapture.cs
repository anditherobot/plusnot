using NAudio.Wave;

namespace plusnot.Pipeline;

public sealed class AudioCapture : IDisposable
{
    private WaveInEvent? waveIn;
    private readonly float[] buffer = new float[4096];
    private int writePos;
    private readonly object lockObj = new();

    public void Start()
    {
        waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(44100, 16, 1),
            BufferMilliseconds = 50
        };

        waveIn.DataAvailable += (sender, e) =>
        {
            lock (lockObj)
            {
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                    buffer[writePos % buffer.Length] = sample / 32768f;
                    writePos++;
                }
            }
        };

        waveIn.StartRecording();
    }

    public float[] GetWaveformSnapshot(int count)
    {
        var snapshot = new float[count];
        lock (lockObj)
        {
            int pos = writePos;
            for (int i = 0; i < count; i++)
            {
                int idx = (pos - count + i) % buffer.Length;
                if (idx < 0) idx += buffer.Length;
                snapshot[i] = buffer[idx];
            }
        }
        return snapshot;
    }

    public void GetWaveformSnapshot(float[] dest)
    {
        lock (lockObj)
        {
            int pos = writePos;
            int count = dest.Length;
            for (int i = 0; i < count; i++)
            {
                int idx = (pos - count + i) % buffer.Length;
                if (idx < 0) idx += buffer.Length;
                dest[i] = buffer[idx];
            }
        }
    }

    public void Dispose()
    {
        waveIn?.StopRecording();
        waveIn?.Dispose();
        waveIn = null;
    }
}
