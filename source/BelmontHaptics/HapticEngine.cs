using System;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BelmontHaptics
{
    /// <summary>
    /// Finds the DualSense's USB audio endpoint (4ch, 48 kHz) and streams synthesized
    /// waveforms into channels 3/4, which drive the left/right voice-coil actuators.
    /// </summary>
    internal sealed class HapticEngine
    {
        Thread _watch;
        volatile bool _run;
        WasapiOut _out;
        HapticSynth _synth;
        MMDevice _dev;
        public bool Connected => _out != null && _out.PlaybackState == PlaybackState.Playing;

        public void Start()
        {
            _run = true;
            _watch = new Thread(Watch) { IsBackground = true, Name = "BelmontHaptics" };
            _watch.Start();
        }

        public void Stop()
        {
            _run = false;
            Close();
        }

        public void SetMotors(float low, float high) => _synth?.SetTargets(low, high);
        public void Pause(bool p) => _synth?.SetPaused(p);

        void Watch()
        {
            bool warned = false;
            while (_run)
            {
                try
                {
                    if (!Connected)
                    {
                        Close();
                        if (TryOpen()) { warned = false; Plugin.L.LogInfo($"DualSense haptics connected: {_dev.FriendlyName}"); }
                        else if (!warned) { warned = true; Plugin.L.LogWarning("No DualSense audio device found (needs USB cable, or DSX+ BT Audio/Haptics). Retrying in background; game keeps normal rumble meanwhile."); }
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning("Haptics device error: " + e.Message); Close(); }
                Thread.Sleep(2000);
            }
        }

        bool TryOpen()
        {
            var parts = Plugin.DeviceMatch.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            using var en = new MMDeviceEnumerator();
            var dev = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => parts.Any(p => d.FriendlyName.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                                     && d.AudioClient.MixFormat.Channels >= 4);
            if (dev == null) return false;

            var mix = dev.AudioClient.MixFormat;
            Plugin.L.LogInfo($"DualSense audio found: {dev.FriendlyName} ({mix.SampleRate} Hz, {mix.Channels} ch, {mix.BitsPerSample} bit, {mix.Encoding})");
            _synth = new HapticSynth(mix.SampleRate, mix.Channels);
            var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, 15);
            o.Init((IWaveProvider)_synth);
            o.Play();
            _dev = dev;
            _out = o;
            if (Plugin.StartupChime.Value) _synth.Chime();
            StartLoopback(en, dev.ID);
            return true;
        }

        WasapiLoopbackCapture _cap;
        void StartLoopback(MMDeviceEnumerator en, string dsId)
        {
            if (!Plugin.AudioHaptics.Value) return;
            try
            {
                var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (def.ID == dsId) { Plugin.L.LogWarning("Default audio output is the DualSense itself, audio haptics skipped. Set your headphones/speakers as default."); return; }
                var cap = new WasapiLoopbackCapture(def);
                var wf = cap.WaveFormat; int ch = wf.Channels; bool isFloat = wf.BitsPerSample == 32;
                cap.DataAvailable += (s, e) =>
                {
                    var syn = _synth; if (syn == null || !isFloat) return;
                    int frames = e.BytesRecorded / (4 * ch);
                    for (int i = 0; i < frames; i++)
                    {
                        float m = 0; int b = i * ch * 4;
                        for (int c = 0; c < ch; c++) m += BitConverter.ToSingle(e.Buffer, b + c * 4);
                        syn.PushAudio(m / ch);
                    }
                };
                cap.StartRecording();
                _cap = cap;
                Plugin.L.LogInfo($"Audio haptics listening to: {def.FriendlyName}");
            }
            catch (Exception ex) { Plugin.L.LogWarning("Audio haptics failed: " + ex.Message); }
        }

        void Close()
        {
            try { _cap?.StopRecording(); _cap?.Dispose(); } catch { }
            _cap = null;
            try { _out?.Stop(); } catch { }
            try { _out?.Dispose(); } catch { }
            _out = null;
            try { _dev?.Dispose(); } catch { }
            _dev = null;
        }
    }

    /// <summary>
    /// Turns the game's two rumble motor values into voice-coil waveforms:
    /// heavy motor = low sine on the left grip, light motor = higher sine on the right,
    /// plus a short decaying click whenever the rumble jumps (hits, landings).
    /// </summary>
    internal sealed class HapticSynth : ISampleProvider, IWaveProvider
    {
        public WaveFormat WaveFormat { get; }
        readonly int _ch;
        readonly float _sr;
        volatile float _tLow, _tHigh;
        volatile bool _paused;
        float _low, _high, _lastSum;
        double _phL, _phH, _phT;
        float _tr; // transient envelope
        int _chime;

        public HapticSynth(int sampleRate, int channels)
        {
            _sr = sampleRate;
            _ch = channels;
            WaveFormat = new WaveFormatExtensible(sampleRate, 32, channels);
        }

        public void SetTargets(float low, float high)
        {
            _tLow = Math.Clamp(low, 0f, 1f);
            _tHigh = Math.Clamp(high, 0f, 1f);
        }

        public void SetPaused(bool p) => _paused = p;
        float[] _fb = new float[0];
        public int Read(byte[] buffer, int offset, int count)
        {
            int n = count / 4;
            if (_fb.Length < n) _fb = new float[n];
            int got = Read(_fb, 0, n);
            Buffer.BlockCopy(_fb, 0, buffer, offset, got * 4);
            return got * 4;
        }
        // ---- audio-to-haptics feed (game audio loopback) ----
        readonly float[] _ring = new float[16384];
        int _w, _r;
        float _lp1, _lp2, _env;
        public void PushAudio(float mono)
        {
            int next = (_w + 1) & 16383;
            if (next == _r) _r = (_r + 1) & 16383; // overwrite oldest
            _ring[_w] = mono; _w = next;
        }
        float PopAudio()
        {
            int avail = (_w - _r) & 16383;
            if (avail > 2400) _r = (_w - 960) & 16383; // keep latency ~20ms
            if (_r == _w) return 0f;
            float v = _ring[_r]; _r = (_r + 1) & 16383; return v;
        }
        public void Chime() => _chime = (int)(_sr * 0.12f);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / _ch;
            float str = Plugin.Enabled.Value ? Plugin.Strength.Value : 0f;
            float lowHz = Plugin.LowHz.Value, highHz = Plugin.HighHz.Value;
            float lg = Plugin.LowGain.Value, hg = Plugin.HighGain.Value;
            float bleed = Plugin.SideBleed.Value;
            float tg = Plugin.TransientGain.Value, tth = Plugin.TransientThreshold.Value;
            bool a2h = Plugin.AudioHaptics.Value; float ag = Plugin.AudioGain.Value, gate = Plugin.AudioGate.Value;
            float lpa = 1f - MathF.Exp(-2f * MathF.PI * Plugin.AudioCutoffHz.Value / _sr);

            float rg = Plugin.RumbleGain.Value;
            float tl = _paused ? 0 : _tLow * rg, th = _paused ? 0 : _tHigh * rg;
            PrepSfx();

            // transient detection once per buffer (~15 ms)
            float sum = tl + th;
            if (sum - _lastSum > tth) _tr = Math.Min(1f, _tr + (sum - _lastSum) * tg);
            _lastSum = sum;

            float atk = 1f - MathF.Exp(-1f / (_sr * 0.004f));
            float rel = 1f - MathF.Exp(-1f / (_sr * 0.045f));
            float trDecay = MathF.Exp(-1f / (_sr * 0.025f));
            double dL = 2 * Math.PI * lowHz / _sr, dH = 2 * Math.PI * highHz / _sr, dT = 2 * Math.PI * 140.0 / _sr;

            for (int i = 0; i < frames; i++)
            {
                _low += (tl - _low) * (tl > _low ? atk : rel);
                _high += (th - _high) * (th > _high ? atk : rel);
                _phL += dL; _phH += dH; _phT += dT;
                if (_phL > 2 * Math.PI) _phL -= 2 * Math.PI;
                if (_phH > 2 * Math.PI) _phH -= 2 * Math.PI;
                if (_phT > 2 * Math.PI) _phT -= 2 * Math.PI;

                float sL = (float)Math.Sin(_phL) * _low * lg;
                float sH = (float)Math.Sin(_phH) * _high * hg;
                float sT = (float)Math.Sin(_phT) * _tr;
                _tr *= trDecay;

                if (_chime > 0) { sT += (float)Math.Sin(_phH * 1.3) * 0.5f; _chime--; }

                float sA = 0f;
                float raw = PopAudio();
                if (a2h)
                {
                    _lp1 += (raw - _lp1) * lpa; _lp2 += (_lp1 - _lp2) * lpa; // 2-pole low-pass: keep thumps, drop hiss/voices
                    float mag = MathF.Abs(_lp2);
                    _env += (mag - _env) * (mag > _env ? 0.01f : 0.0005f);
                    float g = _env > gate ? 1f : _env / Math.Max(gate, 1e-5f);
                    sA = _lp2 * ag * g * g;
                }
                SfxSample(out float xL, out float xR);
                float left = Soft((sL + sH * bleed + sT + sA + xL) * str);
                float right = Soft((sH + sL * bleed + sT + sA + xR) * str);

                int b = offset + i * _ch;
                for (int c = 0; c < _ch; c++) buffer[b + c] = 0f;
                buffer[b + 2] = left;   // ch3 = left actuator
                buffer[b + 3] = right;  // ch4 = right actuator
            }
            return frames * _ch;
        }

        // ---- v0.3 FMOD SFX layer ----
        const int MaxCat = 16;
        readonly float[] _sTgt = new float[MaxCat], _sEnv = new float[MaxCat], _sDec = new float[MaxCat], _sGain = new float[MaxCat], _sL = new float[MaxCat], _sR = new float[MaxCat];
        readonly double[] _sPh = new double[MaxCat], _sDph = new double[MaxCat];
        int _nCat;
        float _sAtk;
        void PrepSfx()
        {
            var cats = FmodSfx.Cats;
            _nCat = FmodSfx.On != null && FmodSfx.On.Value && !_paused ? Math.Min(cats.Count, MaxCat) : 0;
            if (_nCat == 0) return;
            float sens = FmodSfx.AudioSens.Value, master = FmodSfx.Strength.Value;
            _sAtk = 1f - MathF.Exp(-1f / (_sr * 0.002f));
            for (int c = 0; c < _nCat; c++)
            {
                var k = cats[c];
                float pk = Interlocked.Exchange(ref FmodSfx.Peak[c], 0f);
                float kick = Interlocked.Exchange(ref FmodSfx.KickNow[c], 0f);
                if (!k.On.Value) { _sTgt[c] = 0; continue; }
                float lvl = Math.Max(Math.Min(1f, pk * sens), kick);
                if (lvl > _sTgt[c]) _sTgt[c] = lvl;
                _sDec[c] = MathF.Exp(-1f / (_sr * Math.Max(5f, k.ReleaseMs.Value) * 0.001f));
                _sGain[c] = k.Gain.Value * master;
                _sDph[c] = 2 * Math.PI * k.Hz.Value / _sr;
                switch (k.Side.Value)
                {
                    case FmodSfx.Side.Left: _sL[c] = 1; _sR[c] = 0.15f; break;
                    case FmodSfx.Side.Right: _sL[c] = 0.15f; _sR[c] = 1; break;
                    case FmodSfx.Side.Alternate: bool r = FmodSfx.AltRight[c] != 0; _sL[c] = r ? 0.2f : 1; _sR[c] = r ? 1 : 0.2f; break;
                    default: _sL[c] = 1; _sR[c] = 1; break;
                }
            }
        }
        void SfxSample(out float l, out float r)
        {
            l = 0; r = 0;
            for (int c = 0; c < _nCat; c++)
            {
                _sTgt[c] *= _sDec[c];
                _sEnv[c] += (_sTgt[c] - _sEnv[c]) * _sAtk;
                if (_sEnv[c] < 1e-4f) continue;
                _sPh[c] += _sDph[c]; if (_sPh[c] > 2 * Math.PI) _sPh[c] -= 2 * Math.PI;
                float v = (float)Math.Sin(_sPh[c]) * _sEnv[c] * _sGain[c];
                l += v * _sL[c]; r += v * _sR[c];
            }
        }

        static float Soft(float x) => MathF.Tanh(x * 1.4f);
    }
}
