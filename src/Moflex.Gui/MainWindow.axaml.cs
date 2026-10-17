using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Moflex.Core.Container;
using Moflex.Core.Pipeline;

namespace Moflex.Gui;

public partial class MainWindow : Window
{
    private string? _path;
    private MoflexFile? _file;

    private static readonly FilePickerFileType MoflexType = new("MOFLEX video") { Patterns = new[] { "*.moflex" } };

    public MainWindow()
    {
        InitializeComponent();
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

    private async Task<string?> PickSave(string title, string suggested, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggested, DefaultExtension = extension,
        });
        return file?.TryGetLocalPath();
    }

    private void Log(string line) => Dispatcher.UIThread.Post(() => LogBox.Text += line + Environment.NewLine);

    private void SetInspectEnabled(bool on)
    {
        ExportAudioButton.IsEnabled = on && _file?.Streams.Any(s => s.Kind == StreamKind.Audio) == true;
        ExportVideoButton.IsEnabled = on;
        VerifyButton.IsEnabled = on;
        OpenButton.IsEnabled = true;
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
        }
        catch (Exception ex) { InfoBox.Text = "Could not read the file: " + ex.Message; SetInspectEnabled(false); }
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
            int n = MoflexExport.WriteYuv(file, o);
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
        try { InfoBox.Text = MoflexExport.Describe(_file!) + Environment.NewLine + await Task.Run(work); }
        catch (Exception ex) { InfoBox.Text += Environment.NewLine + "Error: " + ex.Message; }
        finally { SetInspectEnabled(true); }
    }

    // ---------- encode tab ----------

    private async void OnPickInput(object? sender, RoutedEventArgs e)
    {
        var p = await PickOpen("Input video");
        if (p == null) return;
        InputBox.Text = p;
        if (string.IsNullOrWhiteSpace(OutputBox.Text))
            OutputBox.Text = Path.Combine(Path.GetDirectoryName(p) ?? "", Path.GetFileNameWithoutExtension(p) + ".moflex");
    }

    private async void OnPickLike(object? sender, RoutedEventArgs e)
    {
        var p = await PickOpen("Reference MOFLEX", MoflexType);
        if (p != null) LikeBox.Text = p;
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        var p = await PickSave("Output MOFLEX", "out.moflex", "moflex");
        if (p != null) OutputBox.Text = p;
    }

    private async void OnPickFfmpeg(object? sender, RoutedEventArgs e)
    {
        var p = await PickOpen("FFmpeg executable");
        if (p != null) FfmpegBox.Text = p;
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
            Ffmpeg = string.IsNullOrWhiteSpace(FfmpegBox.Text) ? "ffmpeg" : FfmpegBox.Text!,
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
