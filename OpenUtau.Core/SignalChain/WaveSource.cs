﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OpenUtau.Core.SignalChain {
    public class WaveSource : ISignalSource {
        public readonly int offset;
        public readonly int estimatedLength;
        public readonly int channels;

        private readonly object lockObj = new object();
        private readonly List<Vector2> envelope;
        private float[] data;

        public WaveSource(double offsetMs, double estimatedLengthMs, List<Vector2> envelope, double skipOverMs, int channels) {
            this.channels = channels;
            offset = (int)((offsetMs - skipOverMs) * 44100 / 1000) * channels;
            estimatedLength = (int)(estimatedLengthMs * 44100 / 1000) * channels;
            int skipSamples = (int)(skipOverMs * 44100 / 1000) * channels;
            if (envelope == null) {
                envelope = new List<Vector2>() {
                    new Vector2(0, 1),
                    new Vector2((float)estimatedLengthMs, 1),
                };
            }
            this.envelope = EnvelopeMsToSamples(envelope, skipSamples);
        }

        public void SetWaveData(byte[] data) {
            if (data == null || data.Length == 0) {
                lock (lockObj) {
                    this.data = new float[0];
                }
                return;
            }
            var samples = new List<float>();
            using (var stream = new MemoryStream(data)) {
                using (var waveFileReader = new WaveFileReader(stream)) {
                    if (waveFileReader.WaveFormat.SampleRate != 44100) {
                        throw new Exception($"SampleRate {waveFileReader.WaveFormat.SampleRate}");
                    }
                    ISampleProvider sampleProvider = null;
                    switch (waveFileReader.WaveFormat.BitsPerSample) {
                        case 8:
                            sampleProvider = new Pcm8BitToSampleProvider(waveFileReader);
                            break;
                        case 16:
                            sampleProvider = new Pcm16BitToSampleProvider(waveFileReader);
                            break;
                        case 24:
                            sampleProvider = new Pcm24BitToSampleProvider(waveFileReader);
                            break;
                        case 32:
                            sampleProvider = new Pcm32BitToSampleProvider(waveFileReader);
                            break;
                        default:
                            throw new Exception($"Unexpected bits per sample {waveFileReader.WaveFormat.BitsPerSample}");
                    }
                    if (waveFileReader.WaveFormat.Channels == 2) {
                        sampleProvider = sampleProvider.ToMono(1, 0);
                    } else if (waveFileReader.WaveFormat.Channels != 1) {
                        throw new Exception($"Unexpected channel count {waveFileReader.WaveFormat.Channels}");
                    }
                    var buffer = new float[sampleProvider.WaveFormat.SampleRate];
                    int n;
                    while ((n = sampleProvider.Read(buffer, 0, buffer.Length)) > 0) {
                        samples.AddRange(buffer.Take(n));
                    }
                }
            }
            lock (lockObj) {
                this.data = samples.ToArray();
                ApplyEnvelope(this.data, envelope);
            }
        }

        public void SetSamples(float[] samples) {
            lock (lockObj) {
                data = samples;
            }
        }

        public bool IsReady(int position, int count) {
            int copies = 2 / channels;
            return position + count <= offset * copies
                || offset * copies + estimatedLength * copies <= position
                || data != null;
        }

        public int Mix(int position, float[] buffer, int index, int count) {
            if (data == null) {
                return 0;
            }
            int copies = 2 / channels;
            int start = Math.Max(position, offset * copies);
            int end = Math.Min(position + count, offset * copies + data.Length * copies);
            for (int i = start; i < end; ++i) {
                buffer[index + i - position] += data[i / copies - offset];
            }
            return end;
        }

        private static void ApplyEnvelope(float[] data, List<Vector2> envelope) {
            int nextPoint = 0;
            for (int i = 0; i < data.Length; ++i) {
                while (nextPoint < envelope.Count && i > envelope[nextPoint].X) {
                    nextPoint++;
                }
                float gain;
                if (nextPoint == 0) {
                    gain = envelope.First().Y;
                } else if (nextPoint >= envelope.Count) {
                    gain = envelope.Last().Y;
                } else {
                    var p0 = envelope[nextPoint - 1];
                    var p1 = envelope[nextPoint];
                    if (p0.X >= p1.X) {
                        gain = p0.Y;
                    } else {
                        gain = p0.Y + (p1.Y - p0.Y) * (i - p0.X) / (p1.X - p0.X);
                    }
                }
                data[i] *= gain;
            }
        }

        private static List<Vector2> EnvelopeMsToSamples(List<Vector2> envelope, int skipOverSamples) {
            envelope = new List<Vector2>(envelope);
            double shift = -envelope[0].X;
            for (var i = 0; i < envelope.Count; i++) {
                var point = envelope[i];
                point.X = (float)((point.X + shift) * 44100 / 1000) + skipOverSamples;
                point.Y /= 100;
                envelope[i] = point;
            }
            return envelope;
        }
    }
}
