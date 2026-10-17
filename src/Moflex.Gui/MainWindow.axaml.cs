using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Moflex.Core.Container;
using Moflex.Core.Pipeline;
using Moflex.Core.Video;

namespace Moflex.Gui;

public partial class MainWindow : Window
{
    private string? _path;
    private MoflexFile? _file;

    // preview
    private MoflexVideoReader? _reader;
    private readonly object _readerLock = new();
    private int _readerGen;
    private readonly WriteableBitmap?[] _bitmaps = new WriteableBitmap?[2];
    private int _bitmapIndex;
    private CancellationTokenSource? _playCts;
    private int _seekRequest = -1, _seekBusy;
    private bool _sliderUpdating;
    private WaveOutPlayer? _audio;
    private volatile bool _restartAudio;
    private CancellationTokenSource? _analyzeCts;

    private static readonly FilePickerFileType MoflexType = new("MOFLEX video") { Patterns = new[] { "*.moflex" } };
    private static readonly FilePickerFileType AviType = new("AVI video") { Patterns = new[] { "*.avi" } };
    private static readonly FilePickerFileType MovType = new("QuickTime video") { Patterns = new[] { "*.mov" } };
    private static readonly FilePickerFileType MkvType = new("Matroska video") { Patterns = new[] { "*.mkv" } };

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        _playCts?.Cancel();
        _analyzeCts?.Cancel();
        _audio?.Dispose();
        base.OnClosed(e);
    }

    // ---------- helpers ----------

    private async Task<string?> PickOpen(string title, params FilePickerFileType[] types)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false, FileTypeFilter = types.Length > 0 ? types : null,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickSave(string title, string suggested, string extension, params FilePickerFileType[] types)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggested, DefaultExtension = extension,
            FileTypeChoices = types.Length > 0 ? types : null,
        });
        return file?.TryGetLocalPath();
    }

    private string Ffmpeg => string.IsNullOrWhiteSpace(FfmpegBox.Text) ? "ffmpeg" : FfmpegBox.Text!.Trim('"');

    private EyeLayout Eyes => EyeBox.SelectedIndex switch { 1 => EyeLayout.Left, 2 => EyeLayout.Right, _ => EyeLayout.SideBySide };

    private void Log(string line) => Dispatcher.UIThread.Post(() => LogBox.Text += line + Environment.NewLine);

    private void SetInspectEnabled(bool on)
    {
        ExportAudioButton.IsEnabled = on && _file?.Streams.Any(s => s.Kind == StreamKind.Audio) == true;
        ExportFileButton.IsEnabled = on;
        ExportVideoButton.IsEnabled = on;
        VerifyButton.IsEnabled = on;
        OpenButton.IsEnabled = true;
    }

    private async void OnPickFfmpeg(object? sender, RoutedEventArgs e)
    {
        var p = await PickOpen("FFmpeg executable");
        if (p != null) FfmpegBox.Text = p;
    }

    // ---------- inspect tab ----------

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpen("Open MOFLEX", MoflexType);
        if (path == null) return;
        try
        {
            _file = await Task.Run(() => MoflexFile.Load(path));
            _path = path;
            FileLabel.Text = path;
            InfoBox.Text = MoflexExport.Describe(_file);
            SetInspectEnabled(true);
            LoadPreview(_file, 0);
            await AnalyzeOpenedFile(_file);
        }
        catch (Exception ex) { InfoBox.Text = "Could not read the file: " + ex.Message; SetInspectEnabled(false); }
    }

    private async void OnExportFile(object? sender, RoutedEventArgs e)
    {
        if (_file == null || _path == null) return;
        var target = await PickSave("Export video (lossless)", Path.GetFileNameWithoutExtension(_path) + ".avi", "avi", AviType, MovType, MkvType);
        if (target == null) return;
        var job = new VideoExport
        {
            File = _file, Output = target, Ffmpeg = Ffmpeg, Eyes = _reader?.IsStereo == true ? Eyes : null,
            Codec = (CodecBox.SelectedItem as ComboBoxItem)?.Content as string ?? "utvideo",
            Progress = (n, total) => Dispatcher.UIThread.Post(() => ExportProgress.Value = total > 0 ? n * 100.0 / total : 0),
        };
        await RunInspect(() =>
        {
            int n = job.Run();
            return $"{n} pictures written to {target} ({job.Codec}, lossless, with sound).\n"
                 + "Edit it, then encode it back on the Encode tab (a side-by-side 3D file is detected automatically).";
        });
    }

    private async void OnExportAudio(object? sender, RoutedEventArgs e)
    {
        if (_file == null || _path == null) return;
        var target = await PickSave("Export audio", Path.GetFileNameWithoutExtension(_path) + ".wav", "wav");
        if (target == null) return;
        var file = _file;
        await RunInspect(() => { using var o = File.Create(target); MoflexExport.WriteWav(file, o); return $"Audio written to {target}"; });
    }

    private async void OnExportVideo(object? sender, RoutedEventArgs e)
    {
        if (_file == null || _path == null) return;
        var target = await PickSave("Export video (raw yuv420p)", Path.GetFileNameWithoutExtension(_path) + ".yuv", "yuv");
        if (target == null) return;
        var file = _file;
        var v = file.Streams.First(s => s.Kind == StreamKind.Video);
        await RunInspect(() =>
        {
            using var o = File.Create(target);
            int n = MoflexExport.WriteYuv(file, o, progress: (k, total) => Dispatcher.UIThread.Post(() => ExportProgress.Value = k * 100.0 / total));
            return $"{n} frames written to {target} ({v.Width}x{v.Height}, yuv420p).\n"
                 + $"Play with: ffplay -f rawvideo -pixel_format yuv420p -video_size {v.Width}x{v.Height} \"{target}\"";
        });
    }

    private async void OnVerify(object? sender, RoutedEventArgs e)
    {
        if (_file == null || _path == null) return;
        var file = _file;
        var path = _path;
        await RunInspect(() =>
        {
            var original = File.ReadAllBytes(path);
            bool same = MoflexRemux.Rebuild(file, original).AsSpan().SequenceEqual(original);
            int n = MoflexExport.WriteYuv(file, Stream.Null);
            return (same ? "Container: the muxer reproduces this file byte for byte." : "Container: this file uses a layout the muxer does not reproduce exactly.")
                 + $"\nVideo: {n} frames decoded without errors.";
        });
    }

    private async Task RunInspect(Func<string> work)
    {
        SetInspectEnabled(false);
        OpenButton.IsEnabled = false;
        ExportProgress.Value = 0;
        try { InfoBox.Text = MoflexExport.Describe(_file!) + Environment.NewLine + await Task.Run(work); }
        catch (Exception ex) { InfoBox.Text += Environment.NewLine + "Error: " + ex.Message; }
        finally { SetInspectEnabled(true); }
    }

    // ---------- preview ----------

    /// <summary>Loads the sound for the preview and checks whether a 3D movie really differs between the eyes.</summary>
    private async Task AnalyzeOpenedFile(MoflexFile file)
    {
        _analyzeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _analyzeCts = cts;
        _audio?.Dispose();
        _audio = null;
        StereoLabel.Text = "";
        if (WaveOutPlayer.IsSupported && file.Streams.Any(s => s.Kind == StreamKind.Audio))
        {
            try
            {
                var pcm = await Task.Run(() => MoflexExport.DecodeAudio(file, out int rate, out int ch) is var d ? (d, rate, ch) : default, cts.Token);
                if (cts.IsCancellationRequested) return;
                _audio = new WaveOutPlayer(pcm.d, pcm.rate, pcm.ch);
                _restartAudio = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { InfoBox.Text += Environment.NewLine + "Preview sound unavailable: " + ex.Message; }
            catch (OperationCanceledException) { return; }
        }
        if (_reader?.IsStereo != true) return;
        StereoLabel.Text = "3D: comparing the left and right pictures...";
        try
        {
            var report = await Task.Run(() => MoflexVideoReader.AnalyzeStereo(file, ct: cts.Token), cts.Token);
            if (cts.IsCancellationRequested || report == null) return;
            StereoLabel.Text = report.IsReal3D
                ? "3D: " + report + ". Exported side by side."
                : "3D layout, but both eyes show the same picture (2D movie). Exported as one eye.";
            InfoBox.Text += $"{Environment.NewLine}3D         : {report}";
            EyeBox.SelectedIndex = report.IsReal3D ? 0 : 1;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StereoLabel.Text = "3D check failed: " + ex.Message; }
    }

    private void OnSoundChanged(object? sender, RoutedEventArgs e)
    {
        if (SoundBox.IsChecked == true) _restartAudio = true;
        else _audio?.Stop();
    }

    private void LoadPreview(MoflexFile file, int frame)
    {
        StopPlayback();
        MoflexVideoReader reader;
        try { reader = new MoflexVideoReader(file, Eyes); }
        catch (Exception ex) { InfoBox.Text += Environment.NewLine + "Preview unavailable: " + ex.Message; return; }
        lock (_readerLock) { _reader = reader; _readerGen++; }
        for (int i = 0; i < 2; i++)
            _bitmaps[i] = new WriteableBitmap(new PixelSize(reader.Width, reader.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        EyeBox.IsEnabled = reader.IsStereo;
        _sliderUpdating = true;
        SeekSlider.Maximum = Math.Max(0, reader.FrameCount - 1);
        SeekSlider.Value = Math.Min(frame, SeekSlider.Maximum);
        _sliderUpdating = false;
        SeekSlider.IsEnabled = PlayButton.IsEnabled = reader.FrameCount > 0;
        RequestSeek(frame);
    }

    private void OnEyeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_file != null && _reader?.IsStereo == true) LoadPreview(_file, (int)SeekSlider.Value);
    }

    private void OnSeek(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_sliderUpdating || _reader == null) return;
        RequestSeek((int)e.NewValue);
    }

    // Seeks run on one background worker that always jumps to the latest requested position.
    private void RequestSeek(int frame)
    {
        Volatile.Write(ref _seekRequest, frame);
        if (Interlocked.CompareExchange(ref _seekBusy, 1, 0) == 0) Task.Run(SeekLoop);
    }

    private void SeekLoop()
    {
        while (true)
        {
            int t;
            while ((t = Interlocked.Exchange(ref _seekRequest, -1)) >= 0)
            {
                YuvFrame? f; int gen;
                lock (_readerLock)
                {
                    if (_reader == null) continue;
                    try { _reader.Seek(t); f = _reader.Read(); }
                    catch (Exception ex) { Dispatcher.UIThread.Post(() => InfoBox.Text += Environment.NewLine + "Preview: " + ex.Message); f = null; }
                    gen = _readerGen;
                }
                if (f != null) Show(f, t, gen);
            }
            Volatile.Write(ref _seekBusy, 0);
            if (Volatile.Read(ref _seekRequest) < 0 || Interlocked.CompareExchange(ref _seekBusy, 1, 0) != 0) return;
        }
    }

    private void Show(YuvFrame f, int index, int gen)
    {
        var bgra = new byte[f.Width * f.Height * 4];
        MoflexVideoReader.ToBgra(f, bgra);
        Dispatcher.UIThread.Post(() =>
        {
            if (gen != _readerGen || _reader == null) return;
            var bmp = _bitmaps[_bitmapIndex ^= 1]!;
            using (var fb = bmp.Lock())
            {
                int row = f.Width * 4;
                for (int y = 0; y < f.Height; y++) Marshal.Copy(bgra, y * row, fb.Address + y * fb.RowBytes, row);
            }
            PreviewImage.Source = bmp;
            PreviewImage.InvalidateVisual();
            _sliderUpdating = true;
            SeekSlider.Value = index;
            _sliderUpdating = false;
            TimeLabel.Text = $"{Clock(_reader.Seconds(index))} / {Clock(_reader.Seconds(_reader.FrameCount))}";
        });
    }

    private static string Clock(double s) => $"{(int)(s / 60)}:{s % 60:00.00}";

    private void OnPlayPause(object? sender, RoutedEventArgs e)
    {
        if (_playCts != null) { StopPlayback(); return; }
        var reader = _reader;
        if (reader == null) return;
        if ((int)SeekSlider.Value >= reader.FrameCount - 1) RequestSeek(0);
        var cts = new CancellationTokenSource();
        _playCts = cts;
        PlayButton.Content = "Pause";
        int gen = _readerGen;
        Task.Run(() => PlayLoop(reader, gen, cts.Token));
    }

    private void StopPlayback()
    {
        _audio?.Stop();
        _playCts?.Cancel();
        _playCts = null;
        PlayButton.Content = "Play";
    }

    // The picture follows a clock that starts with the sound; after a seek (or a sound toggle) both restart together.
    // A picture that is late by more than one frame is decoded but not shown, so the picture catches up with the sound.
    private void PlayLoop(MoflexVideoReader reader, int gen, CancellationToken ct)
    {
        var clock = new Stopwatch();
        double frameSeconds = (double)reader.FpsDen / reader.FpsNum, start = 0;
        int expected = -1;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                YuvFrame? f; int index;
                lock (_readerLock)
                {
                    if (gen != _readerGen) return;
                    index = reader.Position;
                    f = reader.Read();
                }
                if (f == null) break;
                if (index != expected || _restartAudio)
                {
                    _restartAudio = false;
                    start = reader.Seconds(index);
                    clock.Restart();
                    var audio = _audio;
                    if (audio != null && !ct.IsCancellationRequested && Dispatcher.UIThread.Invoke(() => SoundBox.IsChecked == true)) audio.Play(start);
                }
                expected = index + 1;
                double wait = reader.Seconds(index) - start - clock.Elapsed.TotalSeconds;
                if (wait > 0) ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait));
                if (ct.IsCancellationRequested) break;
                if (wait > -frameSeconds) Show(f, index, gen);
            }
        }
        catch (Exception ex) { Dispatcher.UIThread.Post(() => InfoBox.Text += Environment.NewLine + "Preview: " + ex.Message); }
        Dispatcher.UIThread.Post(() =>
        {
            if (!ct.IsCancellationRequested && gen == _readerGen) StopPlayback();
        });
    }

    // ---------- encode tab ----------

    private async void OnPickInput(object? sender, RoutedEventArgs e)
    {
        var p = await PickOpen("Input video");
        if (p == null) return;
        InputBox.Text = p;
        if (string.IsNullOrWhiteSpace(OutputBox.Text))
            OutputBox.Text = Path.Combine(Path.GetDirectoryName(p) ?? "", Path.GetFileNameWithoutExtension(p) + ".moflex");
        await GuessSideBySide();
    }

    private async void OnPickLike(object? sender, RoutedEventArgs e)
    {
        var p = await PickOpen("Reference MOFLEX", MoflexType);
        if (p == null) return;
        LikeBox.Text = p;
        await GuessSideBySide();
    }

    /// <summary>Ticks the side-by-side box when the reference is 3D and the input is twice as wide as one eye.</summary>
    private async Task GuessSideBySide()
    {
        string? input = InputBox.Text, like = LikeBox.Text, ffmpeg = Ffmpeg;
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(like) || input.EndsWith(".yuv", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var guess = await Task.Run(() =>
            {
                var v = MoflexFile.Load(like).Streams.First(s => s.Kind == StreamKind.Video);
                if (v.DescriptorType != 3) return (bool?)false;
                return EncodeJob.ProbeSize(ffmpeg, input) is { } s ? EncodeJob.LooksSideBySide(s.Width, s.Height, v.Width, v.Height) : null;
            });
            if (guess is not bool sbs || SbsBox.IsChecked == sbs) return;
            SbsBox.IsChecked = sbs;
            if (sbs) Log("The input is twice as wide as the reference: treating it as side-by-side 3D.");
        }
        catch { }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        var p = await PickSave("Output MOFLEX", "out.moflex", "moflex");
        if (p != null) OutputBox.Text = p;
    }

    private async void OnEncode(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text) || string.IsNullOrWhiteSpace(OutputBox.Text))
        {
            Log("Choose an input video and an output file first.");
            return;
        }
        var job = new EncodeJob
        {
            Input = InputBox.Text!, Output = OutputBox.Text!,
            Like = string.IsNullOrWhiteSpace(LikeBox.Text) ? null : LikeBox.Text,
            Ffmpeg = Ffmpeg,
            Quantizer = (int)(QBox.Value ?? 18), PredictedQuantizer = (int)(PqBox.Value ?? 22),
            SideBySide = SbsBox.IsChecked == true,
            Width = (int)(WidthBox.Value ?? 400), Height = (int)(HeightBox.Value ?? 240),
            Log = Log,
            Progress = (n, total) => Dispatcher.UIThread.Post(() => EncodeProgress.Value = total > 0 ? n * 100.0 / total : 0),
        };
        if (job.Like == null && FpsBox.Text is { Length: > 0 } fps)
        {
            var t = fps.Split('/');
            if (long.TryParse(t[0], out var a))
            {
                job.FpsNum = a;
                job.FpsDen = t.Length > 1 && long.TryParse(t[1], out var b) ? b : 1;
                job.FpsGiven = true;
            }
        }
        EncodeButton.IsEnabled = false;
        EncodeProgress.Value = 0;
        try { await Task.Run(job.Run); }
        catch (Exception ex) { Log("Error: " + ex.Message); }
        finally { EncodeButton.IsEnabled = true; }
    }
}
