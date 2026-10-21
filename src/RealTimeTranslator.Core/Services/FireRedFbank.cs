namespace RealTimeTranslator.Core.Services;

/// <summary>
/// FireRedVAD の学習時と同じ Kaldi Fbank と CMVN。16 kHz PCM を 25 ms 窓／10 ms hop で処理する。
/// snip_edges=true のため、末尾の未完成窓は次の入力まで保持し、ゼロ埋めしない。
/// </summary>
internal sealed class FireRedFbank
{
    internal const int FeatureSize = 80;
    internal const int MaxInputSamples = 512;
    internal const int MaxFramesPerBlock = 4;
    private const int WindowSize = 400;
    private const int HopSize = 160;
    private const int FftSize = 512;
    private const int SpectrumSize = FftSize / 2;
    private const float LogFloor = 1.1920928955078125e-7f;
    private readonly float[] _samples = new float[WindowSize + MaxInputSamples];
    private readonly float[] _window = new float[WindowSize];
    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imaginary = new float[FftSize];
    private readonly float[] _power = new float[SpectrumSize];
    private readonly float[] _melWeights = new float[FeatureSize * SpectrumSize];
    private readonly int[] _melStarts = new int[FeatureSize];
    private readonly int[] _melEnds = new int[FeatureSize];
    private readonly float[] _means = new float[FeatureSize];
    private readonly float[] _inverseStdDevs = new float[FeatureSize];
    private readonly float[] _cosines = new float[FftSize / 2];
    private readonly float[] _sines = new float[FftSize / 2];
    private readonly int[] _reversedIndices = new int[FftSize];
    private int _sampleCount;

    internal FireRedFbank(string cmvnPath)
    {
        ReadCmvn(cmvnPath);
        for (int i = 0; i < WindowSize; i++)
        {
            _window[i] = (float)Math.Pow(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1))), 0.85);
        }

        for (int i = 0; i < FftSize / 2; i++)
        {
            double angle = -2 * Math.PI * i / FftSize;
            _cosines[i] = (float)Math.Cos(angle);
            _sines[i] = (float)Math.Sin(angle);
        }

        for (int i = 0; i < FftSize; i++)
        {
            int value = i;
            int reversed = 0;
            for (int bit = 0; bit < 9; bit++)
            {
                reversed = (reversed << 1) | (value & 1);
                value >>= 1;
            }
            _reversedIndices[i] = reversed;
        }

        // Kaldi モードの三角フィルター。Slaney 正規化は librosa モードだけの設定なので使わない。
        float lowMel = MelScale(20f);
        float step = (MelScale(8000f) - lowMel) / (FeatureSize + 1);
        Span<float> melFrequencies = stackalloc float[SpectrumSize];
        for (int k = 0; k < SpectrumSize; k++)
        {
            melFrequencies[k] = MelScale(k * (16000f / FftSize));
        }
        for (int bin = 0; bin < FeatureSize; bin++)
        {
            _melStarts[bin] = SpectrumSize;
            float left = lowMel + (bin * step);
            float center = lowMel + ((bin + 1) * step);
            float right = lowMel + ((bin + 2) * step);
            for (int k = 0; k < SpectrumSize; k++)
            {
                float mel = melFrequencies[k];
                if (mel > left && mel < right)
                {
                    _melWeights[(bin * SpectrumSize) + k] = mel <= center
                        ? (mel - left) / (center - left)
                        : (right - mel) / (right - center);
                    _melStarts[bin] = Math.Min(_melStarts[bin], k);
                    _melEnds[bin] = k + 1;
                }
            }
        }
    }

    /// <summary>連続 PCM を追加し、新たに完成した frame-major 80 次元特徴量を返す。</summary>
    internal int ExtractFrames(ReadOnlySpan<float> samples, Span<float> features)
    {
        if (samples.Length > MaxInputSamples)
        {
            throw new ArgumentException($"1 回の入力は {MaxInputSamples} samples 以下です。", nameof(samples));
        }
        int totalSamples = _sampleCount + samples.Length;
        int frameCount = totalSamples < WindowSize ? 0 : 1 + ((totalSamples - WindowSize) / HopSize);
        if (features.Length < frameCount * FeatureSize)
        {
            throw new ArgumentException("特徴量の出力バッファが不足しています。", nameof(features));
        }
        foreach (float sample in samples)
        {
            if (!float.IsFinite(sample) || !float.IsFinite(sample * 32768f))
            {
                throw new ArgumentException("PCM に非有限値または範囲外の値が含まれています。", nameof(samples));
            }
        }

        samples.CopyTo(_samples.AsSpan(_sampleCount));
        for (int frame = 0; frame < frameCount; frame++)
        {
            ComputeFrame(_samples.AsSpan(frame * HopSize, WindowSize), features.Slice(frame * FeatureSize, FeatureSize));
        }
        int consumed = frameCount * HopSize;
        _sampleCount = totalSamples - consumed;
        _samples.AsSpan(consumed, _sampleCount).CopyTo(_samples);
        return frameCount;
    }

    internal void Reset()
    {
        _sampleCount = 0;
        Array.Clear(_samples);
    }

    private void ComputeFrame(ReadOnlySpan<float> samples, Span<float> features)
    {
        Array.Clear(_real);
        Array.Clear(_imaginary);
        float mean = 0f;
        for (int i = 0; i < WindowSize; i++)
        {
            _real[i] = samples[i] * 32768f;
            mean += _real[i];
        }
        mean /= WindowSize;
        for (int i = 0; i < WindowSize; i++) _real[i] -= mean;
        // 窓ごとの pre-emphasis。窓の先頭は同じ sample を前値として使う。
        for (int i = WindowSize - 1; i > 0; i--) _real[i] -= 0.97f * _real[i - 1];
        _real[0] -= 0.97f * _real[0];
        for (int i = 0; i < WindowSize; i++) _real[i] *= _window[i];
        ForwardFft();
        bool finitePower = true;
        for (int k = 0; k < SpectrumSize; k++)
        {
            _power[k] = (_real[k] * _real[k]) + (_imaginary[k] * _imaginary[k]);
            finitePower &= float.IsFinite(_power[k]);
        }
        for (int bin = 0; bin < FeatureSize; bin++)
        {
            float energy = 0f;
            int offset = bin * SpectrumSize;
            // 非ゼロの三角重みだけを元と同じ順序で加算する。
            // overflow 時は全域を計算し、0 * Infinity による従来の非有限値拒否も維持する。
            int start = finitePower ? _melStarts[bin] : 0;
            int end = finitePower ? _melEnds[bin] : SpectrumSize;
            for (int k = start; k < end; k++) energy += _power[k] * _melWeights[offset + k];
            float value = (MathF.Log(MathF.Max(LogFloor, energy)) - _means[bin]) * _inverseStdDevs[bin];
            if (!float.IsFinite(value)) throw new InvalidOperationException("FireRed Fbank の特徴量が非有限値になりました。");
            features[bin] = value;
        }
    }

    private void ForwardFft()
    {
        // 正方向 FFT は非正規化。全 sample と作業領域を再利用する。
        for (int i = 0; i < FftSize; i++)
        {
            int j = _reversedIndices[i];
            if (j > i) (_real[i], _real[j]) = (_real[j], _real[i]);
        }
        for (int length = 2; length <= FftSize; length *= 2)
        {
            int half = length / 2;
            int step = FftSize / length;
            for (int start = 0; start < FftSize; start += length)
            {
                for (int i = 0; i < half; i++)
                {
                    int a = start + i;
                    int b = a + half;
                    float cosine = _cosines[i * step];
                    float sine = _sines[i * step];
                    float real = (cosine * _real[b]) - (sine * _imaginary[b]);
                    float imaginary = (sine * _real[b]) + (cosine * _imaginary[b]);
                    _real[b] = _real[a] - real;
                    _imaginary[b] = _imaginary[a] - imaginary;
                    _real[a] += real;
                    _imaginary[a] += imaginary;
                }
            }
        }
    }

    private void ReadCmvn(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        // Kaldi の単一 binary double matrix: \0B、DM、2x81 の累積統計。
        if (stream.Length != 1311 || !reader.ReadBytes(5).AsSpan().SequenceEqual("\0BDM "u8)
            || reader.ReadByte() != 4 || reader.ReadInt32() != 2
            || reader.ReadByte() != 4 || reader.ReadInt32() != FeatureSize + 1)
        {
            throw new InvalidDataException($"FireRed CMVN は Kaldi binary DM 2x81 形式である必要があります: {path}");
        }
        Span<double> sums = stackalloc double[FeatureSize + 1];
        Span<double> sumSquares = stackalloc double[FeatureSize + 1];
        for (int i = 0; i <= FeatureSize; i++) sums[i] = reader.ReadDouble();
        for (int i = 0; i <= FeatureSize; i++) sumSquares[i] = reader.ReadDouble();
        for (int i = 0; i <= FeatureSize; i++)
        {
            if (!double.IsFinite(sums[i]) || !double.IsFinite(sumSquares[i]))
            {
                throw new InvalidDataException($"FireRed CMVN の統計値が非有限値です: {path}");
            }
        }
        double count = sums[FeatureSize];
        if (count <= 0) throw new InvalidDataException($"FireRed CMVN の sample 数が不正です: {path}");
        for (int i = 0; i < FeatureSize; i++)
        {
            double mean = sums[i] / count;
            double variance = Math.Max((sumSquares[i] / count) - (mean * mean), 1e-20);
            _means[i] = (float)mean;
            _inverseStdDevs[i] = (float)(1 / Math.Sqrt(variance));
            if (!float.IsFinite(_means[i]) || !float.IsFinite(_inverseStdDevs[i]) || _inverseStdDevs[i] <= 0)
            {
                throw new InvalidDataException($"FireRed CMVN の平均または標準偏差が不正です: {path}");
            }
        }
    }

    private static float MelScale(float frequency) => 1127f * MathF.Log(1f + (frequency / 700f));
}
