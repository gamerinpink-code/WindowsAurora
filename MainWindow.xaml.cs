using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NAudio.Dsp;
using NAudio.Wave;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace WindowsAurora;

public partial class MainWindow : Window
{
    // --- Audio & FFT Configuration ---
    private const int FftLength = 4096;
    private const int BandCount = 96;
    private WasapiLoopbackCapture? _capture;
    private readonly Complex[] _fftBuffer = new Complex[FftLength];
    private readonly float[] _window = new float[FftLength];
    private int _fftPos;

    private readonly float[] _bands = new float[BandCount];
    private readonly float[] _peaks = new float[BandCount];
    private readonly float[] _leftWave = new float[256];
    private readonly float[] _rightWave = new float[256];
    private readonly object _audioLock = new();

    // --- Render & Starfield State ---
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _hudHideTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private float _phase;
    private float _bassEnergy;
    private float _midEnergy;
    private float _highEnergy;

    private struct Star { public float X, Y, Z, Hue; }
    private readonly Star[] _stars = new Star[240];
    private readonly Random _rng = new();

    // --- Fullscreen & Modes ---
    private bool _isFullscreen;
    private WindowState _prevState;
    private WindowStyle _prevStyle;
    private ResizeMode _prevResize;
    private int _currentMode;

    private readonly string[] _modeNames =
    {
        "1. Northern Lights (Aurora Ribbons)",
        "2. Radial Cyber-Halo",
        "3. Hyperdrive Starfield",
        "4. Liquid Silk Oscilloscope",
        "5. Neon Spectrum + Peak Hold"
    };

    public MainWindow()
    {
        InitializeComponent();

        // Precompute Hann window for clean FFT bins
        for (int i = 0; i < FftLength; i++)
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftLength - 1)));

        // Initialize 3D starfield
        for (int i = 0; i < _stars.Length; i++)
            ResetStar(ref _stars[i], randomZ: true);

        StartAudioCapture();

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
        };
        _renderTimer.Tick += (_, _) => SkiaCanvas.InvalidateVisual();
        _renderTimer.Start();

        _hudHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hudHideTimer.Tick += (_, _) => HideHud();
        _hudHideTimer.Start();
    }

    // =========================================================================
    // AUDIO CAPTURE & EXPONENTIAL FFT PIPELINE
    // =========================================================================
    private void StartAudioCapture()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnAudioDataAvailable;
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not start Windows audio loopback:\n{ex.Message}",
                "AuroraVisualiser", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture == null || e.BytesRecorded == 0) return;

        int bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
        int channels = _capture.WaveFormat.Channels;
        int frameBytes = bytesPerSample * channels;
        int frames = e.BytesRecorded / frameBytes;

        lock (_audioLock)
        {
            for (int f = 0; f < frames; f++)
            {
                int offset = f * frameBytes;
                float left = BitConverter.ToSingle(e.Buffer, offset);
                float right = channels > 1 ? BitConverter.ToSingle(e.Buffer, offset + 4) : left;
                float mono = (left + right) * 0.5f;

                // Shift stereo oscilloscope ring buffer
                int waveIdx = (_fftPos + f) % _leftWave.Length;
                _leftWave[waveIdx] = left;
                _rightWave[waveIdx] = right;

                _fftBuffer[_fftPos].X = mono * _window[_fftPos];
                _fftBuffer[_fftPos].Y = 0;
                _fftPos++;

                if (_fftPos >= FftLength)
                {
                    _fftPos = 0;
                    ProcessFftFrame(_capture.WaveFormat.SampleRate);
                }
            }
        }
    }

    private void ProcessFftFrame(int sampleRate)
    {
        FastFourierTransform.FFT(true, (int)Math.Log2(FftLength), _fftBuffer);

        int halfFft = FftLength / 2;
        float minFreq = 28f;
        float maxFreq = MathF.Min(16000f, sampleRate * 0.45f);
        float logMin = MathF.Log10(minFreq);
        float logMax = MathF.Log10(maxFreq);

        for (int b = 0; b < BandCount; b++)
        {
            // Exponential frequency scaling across bins
            float t0 = (float)b / BandCount;
            float t1 = (float)(b + 1) / BandCount;
            int binStart = Math.Clamp((int)(MathF.Pow(10f, logMin + (logMax - logMin) * t0) * FftLength / sampleRate), 1, halfFft - 1);
            int binEnd = Math.Clamp((int)(MathF.Pow(10f, logMin + (logMax - logMin) * t1) * FftLength / sampleRate), binStart + 1, halfFft);

            float sum = 0f;
            for (int k = binStart; k < binEnd; k++)
            {
                float mag = MathF.Sqrt(_fftBuffer[k].X * _fftBuffer[k].X + _fftBuffer[k].Y * _fftBuffer[k].Y);
                sum += mag;
            }

            float avg = sum / (binEnd - binStart);
            // Frequency tilt boost so highs dance cleanly alongside bass
            float tilt = 1f + (b / (float)BandCount) * 2.6f;
            float target = Math.Clamp(MathF.Sqrt(avg * 18f) * tilt, 0f, 1.35f);

            // Asymmetric exponential attack & decay
            float coeff = target > _bands[b] ? 0.62f : 0.14f;
            _bands[b] += (target - _bands[b]) * coeff;

            if (_bands[b] > _peaks[b])
                _peaks[b] = _bands[b];
            else
                _peaks[b] = MathF.Max(0f, _peaks[b] - 0.012f);
        }
    }

    // =========================================================================
    // SKIASHARP RENDERING DISPATCHER
    // =========================================================================
    private void SkiaCanvas_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        SKCanvas canvas = e.Surface.Canvas;
        int w = e.Info.Width;
        int h = e.Info.Height;

        float[] bandsCopy = new float[BandCount];
        float[] peaksCopy = new float[BandCount];
        float[] leftCopy = new float[_leftWave.Length];
        float[] rightCopy = new float[_rightWave.Length];

        lock (_audioLock)
        {
            Array.Copy(_bands, bandsCopy, BandCount);
            Array.Copy(_peaks, peaksCopy, BandCount);
            Array.Copy(_leftWave, leftCopy, leftCopy.Length);
            Array.Copy(_rightWave, rightCopy, rightCopy.Length);
        }

        // Compute normalized energy bands
        _bassEnergy = AverageRange(bandsCopy, 0, 14);
        _midEnergy = AverageRange(bandsCopy, 14, 52);
        _highEnergy = AverageRange(bandsCopy, 52, BandCount);
        _phase += 0.012f + _midEnergy * 0.045f;

        // Deep polar night sky gradient background
        using var bgPaint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, h),
                new[] { new SKColor(4, 7, 16), new SKColor(8, 15, 32), new SKColor(5, 9, 19) },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, w, h, bgPaint);

        switch (_currentMode)
        {
            case 0: DrawNorthernLights(canvas, w, h, bandsCopy); break;
            case 1: DrawRadialCyberHalo(canvas, w, h, bandsCopy); break;
            case 2: DrawHyperdriveStarfield(canvas, w, h, bandsCopy); break;
            case 3: DrawLiquidOscilloscope(canvas, w, h, leftCopy, rightCopy); break;
            case 4: DrawNeonSpectrum(canvas, w, h, bandsCopy, peaksCopy); break;
        }
    }

    // =========================================================================
    // MODE 1: NORTHERN LIGHTS (MULTI-LAYER AURORA SILK RIBBONS)
    // =========================================================================
    private void DrawNorthernLights(SKCanvas canvas, int w, int h, float[] bands)
    {
        // Subtle twinkling background stars
        using var starPaint = new SKPaint { IsAntialias = true };
        for (int i = 0; i < 90; i++)
        {
            float sx = ((i * 7919) % 1000) / 1000f * w;
            float sy = ((i * 6271) % 1000) / 1000f * (h * 0.65f);
            byte alpha = (byte)(60 + 110 * MathF.Sin(_phase * 1.4f + i));
            starPaint.Color = new SKColor(210, 235, 255, alpha);
            canvas.DrawCircle(sx, sy, (i % 3 == 0) ? 1.6f : 1.0f, starPaint);
        }

        // Three overlapping aurora curtains (Back Violet -> Mid Teal -> Front Emerald)
        DrawAuroraCurtain(canvas, w, h, bands,
            baseYRatio: 0.44f, heightScale: 0.42f, freqMult: 1.8f, phaseOffset: 0.0f,
            bottomColor: new SKColor(16, 185, 129, 210),
            midColor: new SKColor(139, 92, 246, 130),
            blurSigma: 18f);

        DrawAuroraCurtain(canvas, w, h, bands,
            baseYRatio: 0.54f, heightScale: 0.48f, freqMult: 2.5f, phaseOffset: 2.1f,
            bottomColor: new SKColor(52, 211, 153, 235),
            midColor: new SKColor(56, 189, 248, 145),
            blurSigma: 12f);

        DrawAuroraCurtain(canvas, w, h, bands,
            baseYRatio: 0.64f, heightScale: 0.52f, freqMult: 3.2f, phaseOffset: 4.3f,
            bottomColor: new SKColor(110, 231, 183, 250),
            midColor: new SKColor(168, 85, 247, 150),
            blurSigma: 6f);
    }

    private void DrawAuroraCurtain(
        SKCanvas canvas, int w, int h, float[] bands,
        float baseYRatio, float heightScale, float freqMult, float phaseOffset,
        SKColor bottomColor, SKColor midColor, float blurSigma)
    {
        int segments = BandCount;
        float stepX = (float)w / (segments - 1);
        float baseY = h * baseYRatio;

        var bottomPts = new SKPoint[segments];
        var topPts = new SKPoint[segments];

        for (int i = 0; i < segments; i++)
        {
            float nx = (float)i / (segments - 1);
            float energy = bands[i];

            // Flowing silk wave fold
            float wave = MathF.Sin(nx * MathF.PI * freqMult + _phase + phaseOffset) * (35f + _bassEnergy * 55f)
                       + MathF.Cos(nx * MathF.PI * (freqMult * 1.7f) - _phase * 0.7f) * 22f;

            float curtainHeight = (60f + energy * h * heightScale) * (0.65f + 0.35f * MathF.Sin(nx * MathF.PI));
            float by = baseY + wave;
            float ty = by - curtainHeight;

            bottomPts[i] = new SKPoint(i * stepX, by);
            topPts[i] = new SKPoint(i * stepX, ty);
        }

        using var path = new SKPath();
        path.MoveTo(bottomPts[0]);
        for (int i = 1; i < segments; i++)
        {
            var mid = new SKPoint((bottomPts[i - 1].X + bottomPts[i].X) * 0.5f, (bottomPts[i - 1].Y + bottomPts[i].Y) * 0.5f);
            path.QuadTo(bottomPts[i - 1], mid);
        }
        path.LineTo(bottomPts[^1]);

        for (int i = segments - 1; i >= 1; i--)
        {
            var mid = new SKPoint((topPts[i].X + topPts[i - 1].X) * 0.5f, (topPts[i].Y + topPts[i - 1].Y) * 0.5f);
            path.QuadTo(topPts[i], mid);
        }
        path.LineTo(topPts[0]);
        path.Close();

        using var fillPaint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.Plus,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSigma),
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, baseY + 40),
                new SKPoint(0, baseY - h * heightScale),
                new[] { bottomColor, midColor, SKColors.Transparent },
                new[] { 0.0f, 0.45f, 1.0f },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawPath(path, fillPaint);

        // Crisp luminous lower edge highlight
        using var edgePath = new SKPath();
        edgePath.MoveTo(bottomPts[0]);
        for (int i = 1; i < segments; i++)
            edgePath.LineTo(bottomPts[i]);

        using var edgePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f + _highEnergy * 3f,
            BlendMode = SKBlendMode.Plus,
            Color = bottomColor.WithAlpha(220),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f)
        };
        canvas.DrawPath(edgePath, edgePaint);
    }

    // =========================================================================
    // MODE 2: RADIAL CYBER-HALO
    // =========================================================================
    private void DrawRadialCyberHalo(SKCanvas canvas, int w, int h, float[] bands)
    {
        float cx = w * 0.5f;
        float cy = h * 0.5f;
        float baseRadius = MathF.Min(w, h) * 0.22f + _bassEnergy * 42f;

        // Ambient core glow
        using var glowPaint = new SKPaint
        {
            BlendMode = SKBlendMode.Plus,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy), baseRadius * 2.2f,
                new[] { new SKColor(52, 211, 153, 95), new SKColor(139, 92, 246, 40), SKColors.Transparent },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawCircle(cx, cy, baseRadius * 2.2f, glowPaint);

        using var spikePaint = new SKPaint
        {
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = 3.5f,
            BlendMode = SKBlendMode.Plus
        };

        int totalSpikes = BandCount * 2;
        for (int i = 0; i < totalSpikes; i++)
        {
            int bandIdx = i < BandCount ? i : (totalSpikes - 1 - i);
            float angle = (i / (float)totalSpikes) * MathF.PI * 2f - MathF.PI * 0.5f + _phase * 0.15f;
            float len = 12f + bands[bandIdx] * MathF.Min(w, h) * 0.24f;

            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);

            float hue = (155f + (bandIdx / (float)BandCount) * 135f + _phase * 25f) % 360f;
            spikePaint.Color = SKColor.FromHsl(hue, 88f, 62f);

            canvas.DrawLine(
                cx + cos * baseRadius,
                cy + sin * baseRadius,
                cx + cos * (baseRadius + len),
                cy + sin * (baseRadius + len),
                spikePaint);
        }
    }

    // =========================================================================
    // MODE 3: HYPERDRIVE STARFIELD
    // =========================================================================
    private void DrawHyperdriveStarfield(SKCanvas canvas, int w, int h, float[] bands)
    {
        float cx = w * 0.5f;
        float cy = h * 0.5f;
        float speed = 0.008f + _bassEnergy * 0.045f + _midEnergy * 0.02f;

        using var streakPaint = new SKPaint
        {
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            BlendMode = SKBlendMode.Plus
        };

        for (int i = 0; i < _stars.Length; i++)
        {
            ref var s = ref _stars[i];
            float prevZ = s.Z;
            s.Z -= speed;
            if (s.Z <= 0.02f)
            {
                ResetStar(ref s, randomZ: false);
                prevZ = s.Z;
            }

            float sx = cx + (s.X / s.Z) * cx;
            float sy = cy + (s.Y / s.Z) * cy;
            float px = cx + (s.X / prevZ) * cx;
            float py = cy + (s.Y / prevZ) * cy;

            if (sx < -50 || sx > w + 50 || sy < -50 || sy > h + 50)
            {
                ResetStar(ref s, randomZ: false);
                continue;
            }

            float depth = 1f - s.Z;
            streakPaint.StrokeWidth = 1f + depth * (3.5f + _bassEnergy * 3f);
            streakPaint.Color = SKColor.FromHsl((s.Hue + _phase * 30f) % 360f, 85f, 55f + depth * 35f);
            canvas.DrawLine(px, py, sx, sy, streakPaint);
        }
    }

    private void ResetStar(ref Star s, bool randomZ)
    {
        s.X = (float)(_rng.NextDouble() * 2.0 - 1.0);
        s.Y = (float)(_rng.NextDouble() * 2.0 - 1.0);
        s.Z = randomZ ? (float)_rng.NextDouble() * 0.95f + 0.05f : 1.0f;
        s.Hue = 150f + (float)_rng.NextDouble() * 140f;
    }

    // =========================================================================
    // MODE 4: LIQUID SILK OSCILLOSCOPE (CHROMATIC RGB STRANDS)
    // =========================================================================
    private void DrawLiquidOscilloscope(SKCanvas canvas, int w, int h, float[] left, float[] right)
    {
        float cy = h * 0.5f;
        float amp = h * (0.28f + _bassEnergy * 0.22f);

        DrawWaveStrand(canvas, w, cy, amp, left, right, -6f, new SKColor(236, 72, 153, 210));
        DrawWaveStrand(canvas, w, cy, amp, left, right, 0f, new SKColor(52, 211, 153, 240));
        DrawWaveStrand(canvas, w, cy, amp, left, right, +6f, new SKColor(56, 189, 248, 210));
    }

    private void DrawWaveStrand(SKCanvas canvas, int w, float cy, float amp, float[] left, float[] right, float yOffset, SKColor color)
    {
        using var path = new SKPath();
        float stepX = (float)w / (left.Length - 1);

        for (int i = 0; i < left.Length; i++)
        {
            float sample = (left[i] + right[(i + 16) % right.Length]) * 0.5f;
            float x = i * stepX;
            float y = cy + yOffset + sample * amp;
            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3.2f + _bassEnergy * 3f,
            Color = color,
            BlendMode = SKBlendMode.Plus,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.5f)
        };
        canvas.DrawPath(path, paint);
    }

    // =========================================================================
    // MODE 5: NEON SPECTRUM + PEAK HOLD + FLOOR REFLECTION
    // =========================================================================
    private void DrawNeonSpectrum(SKCanvas canvas, int w, int h, float[] bands, float[] peaks)
    {
        float floorY = h * 0.72f;
        float maxBarH = h * 0.56f;
        float totalSlot = (w * 0.88f) / BandCount;
        float barW = MathF.Max(3f, totalSlot * 0.72f);
        float startX = w * 0.06f;

        using var barPaint = new SKPaint { IsAntialias = true };
        using var peakPaint = new SKPaint { IsAntialias = true, Color = new SKColor(240, 253, 250, 230) };

        for (int i = 0; i < BandCount; i++)
        {
            float x = startX + i * totalSlot;
            float barH = MathF.Max(4f, bands[i] * maxBarH);
            float peakY = floorY - peaks[i] * maxBarH - 5f;

            // Main upward bar
            barPaint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(x, floorY), new SKPoint(x, floorY - barH),
                new[] { new SKColor(16, 185, 129), new SKColor(56, 189, 248), new SKColor(168, 85, 247) },
                SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(x, floorY - barH, barW, barH, 3f, 3f, barPaint);

            // Subtle mirrored floor reflection
            barPaint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(x, floorY + 3f), new SKPoint(x, floorY + barH * 0.35f),
                new[] { new SKColor(52, 211, 153, 75), SKColors.Transparent },
                SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(x, floorY + 3f, barW, barH * 0.35f, 3f, 3f, barPaint);

            // Peak hold cap
            canvas.DrawRoundRect(x, peakY, barW, 3f, 1.5f, 1.5f, peakPaint);
        }
    }

    private static float AverageRange(float[] arr, int start, int end)
    {
        float sum = 0f;
        for (int i = start; i < end; i++) sum += arr[i];
        return sum / Math.Max(1, end - start);
    }

    // =========================================================================
    // TRUE BORDERLESS FULLSCREEN & AUTO-HIDING HUD
    // =========================================================================
    public void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _prevState = WindowState;
            _prevStyle = WindowStyle;
            _prevResize = ResizeMode;

            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Topmost = true;
            _isFullscreen = true;
            FullscreenBtn.Content = "❐ Windowed (F11)";
        }
        else
        {
            Topmost = false;
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            WindowState = _prevState;
            _isFullscreen = false;
            FullscreenBtn.Content = "⛶ Fullscreen (F11)";
        }
    }

    private void SwitchMode(int delta)
    {
        _currentMode = (_currentMode + delta + _modeNames.Length) % _modeNames.Length;
        ModeTitleText.Text = _modeNames[_currentMode];
        ShowHud();
    }

    private void ShowHud()
    {
        Mouse.OverrideCursor = null;
        HudBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180)));
        _hudHideTimer.Stop();
        _hudHideTimer.Start();
    }

    private void HideHud()
    {
        if (_isFullscreen)
            Mouse.OverrideCursor = Cursors.None;
        HudBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(450)));
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11: ToggleFullscreen(); break;
            case Key.Escape when _isFullscreen: ToggleFullscreen(); break;
            case Key.Right: SwitchMode(1); break;
            case Key.Left: SwitchMode(-1); break;
            case Key.D1: _currentMode = 0; SwitchMode(0); break;
            case Key.D2: _currentMode = 1; SwitchMode(0); break;
            case Key.D3: _currentMode = 2; SwitchMode(0); break;
            case Key.D4: _currentMode = 3; SwitchMode(0); break;
            case Key.D5: _currentMode = 4; SwitchMode(0); break;
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e) => ShowHud();
    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ToggleFullscreen();
    private void PrevMode_Click(object sender, RoutedEventArgs e) => SwitchMode(-1);
    private void NextMode_Click(object sender, RoutedEventArgs e) => SwitchMode(1);
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _renderTimer.Stop();
        _capture?.StopRecording();
        _capture?.Dispose();
    }
}
