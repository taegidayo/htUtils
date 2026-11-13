using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace HtUtil.Agent;

/// What this PC plays (WASAPI loopback of the default speakers) → Opus 48 kHz stereo, 20 ms frames → the audio track.
/// Silence = WASAPI delivers nothing = nothing is sent.
sealed class AudioStream : IDisposable
{
    public static readonly AudioFormat Opus = new(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1");
    const int Frame = 960; // 20 ms at 48 kHz, per channel

    readonly WasapiLoopbackCapture _cap = new();
    readonly CancellationTokenSource _cts = new();

    public AudioStream(RTCPeerConnection pc)
    {
        var buf = new BufferedWaveProvider(_cap.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(1), ReadFully = false };
        _cap.DataAvailable += (_, e) => buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
        ISampleProvider src = buf.ToSampleProvider();
        src = src.WaveFormat.Channels switch { 1 => new MonoToStereoSampleProvider(src), 2 => src, _ => new FirstTwo(src) };
        if (src.WaveFormat.SampleRate != 48000) src = new WdlResamplingSampleProvider(src, 48000);
        Log.Write($"audio: {_cap.WaveFormat}");
        _cap.StartRecording();

        var enc = new AudioEncoder(includeLinearFormats: false, includeOpus: true);
        _ = Task.Run(async () =>
        {
            var f = new float[Frame * 2]; var pcm = new short[Frame * 2]; int have = 0;
            while (!_cts.IsCancellationRequested)
            {
                int n;
                while ((n = src.Read(f, have, f.Length - have)) > 0)
                {
                    have += n;
                    if (have < f.Length) continue;
                    for (int i = 0; i < f.Length; i++) pcm[i] = (short)Math.Clamp(f[i] * 32767f, -32768f, 32767f);
                    try
                    {
                        // WASAPI sends nothing while silent: after a pause, jump the RTP clock to wall time (48 kHz) so the
                        // receiver doesn't take the resumed sound as late and grow its buffer; back-to-back frames stay +960.
                        var tr = pc.AudioStream.LocalTrack;
                        var wall = (uint)(Environment.TickCount64 * 48);
                        if ((int)(wall - tr.Timestamp) > 48 * 100) RtpClock.Set(tr, wall);
                        pc.SendAudio(Frame, enc.EncodeAudio(pcm, Opus));
                    }
                    catch { }
                    have = 0;
                }
                await Task.Delay(10).ContinueWith(_ => { });
            }
        });
    }

    public void Dispose() { _cts.Cancel(); try { _cap.StopRecording(); } catch { } _cap.Dispose(); }

    /// 5.1 / 7.1 speakers: keep front left/right.
    sealed class FirstTwo(ISampleProvider src) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        float[] _tmp = [];
        public int Read(float[] buffer, int offset, int count)
        {
            int ch = src.WaveFormat.Channels, frames = count / 2;
            if (_tmp.Length < frames * ch) _tmp = new float[frames * ch];
            int got = src.Read(_tmp, 0, frames * ch) / ch;
            for (int i = 0; i < got; i++) { buffer[offset + i * 2] = _tmp[i * ch]; buffer[offset + i * 2 + 1] = _tmp[i * ch + 1]; }
            return got * 2;
        }
    }
}
