using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MinimalProcessLoopbackWpf;

/// <summary>
/// ドロップダウン用のプロセス表示アイテム
/// </summary>
public record ProcessItem(int ProcessId, int CaptureProcessId, string DisplayName);

/// <summary>
/// Process Loopback の最小検証用ウィンドウ。
/// 公式ドキュメントどおり、UI スレッドで CreateForProcessCaptureAsync を await し、同じスレッドで StartRecording を呼ぶ。
/// </summary>
public partial class MainWindow
{
    private WasapiCapture? _capture;
    private int _dataAvailableCount;
    private readonly ObservableCollection<ProcessItem> _processItems = [];

    public MainWindow()
    {
        InitializeComponent();
        ProcessCombo.ItemsSource = _processItems;
        Loaded += (_, _) => RefreshProcesses();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshProcesses();
    }

    /// <summary>
    /// オーディオセッションを持つプロセス ID を列挙する
    /// </summary>
    private static HashSet<int> GetActiveAudioProcessIds()
    {
        var processIds = new HashSet<int>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        try
                        {
                            if ((int)session.State == 2) continue;
                            uint pid = 0;
                            try { pid = session.GetProcessID; } catch { }
                            if (pid > 0) processIds.Add((int)pid);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
        return processIds;
    }

    private void RefreshProcesses()
    {
        var currentPid = Environment.ProcessId;
        var activeIds = GetActiveAudioProcessIds();
        _processItems.Clear();
        if (activeIds.Count == 0)
        {
            StatusText.Text = "オーディオセッションを持つプロセスがありません。音を再生しているアプリを起動して「更新」を押してください。";
            return;
        }
        var activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in activeIds)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.Id != currentPid && !string.IsNullOrEmpty(p.ProcessName))
                    activeNames.Add(p.ProcessName);
            }
            catch { }
        }
        var sessionOwnerByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in activeIds)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.Id != currentPid && !string.IsNullOrEmpty(p.ProcessName) && !sessionOwnerByName.ContainsKey(p.ProcessName))
                    sessionOwnerByName[p.ProcessName] = p.Id;
            }
            catch { }
        }
        var allProcesses = Process.GetProcesses()
            .Where(p => p.Id != currentPid && (activeIds.Contains(p.Id) || activeNames.Contains(p.ProcessName)))
            .OrderBy(p => p.ProcessName)
            .ThenBy(p => p.Id)
            .ToList();
        var nameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in allProcesses)
        {
            try
            {
                var title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle;
                var name = p.ProcessName;
                nameCount.TryGetValue(name, out var count);
                nameCount[name] = count + 1;
                var displayTitle = nameCount[name] > 1 ? $"{title} (PID: {p.Id})" : title;
                var capturePid = activeIds.Contains(p.Id) ? p.Id : (sessionOwnerByName.TryGetValue(name, out var owner) ? owner : p.Id);
                _processItems.Add(new ProcessItem(p.Id, capturePid, $"{name} (PID: {p.Id}) - {displayTitle}"));
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }
        if (_processItems.Count > 0)
            ProcessCombo.SelectedIndex = 0;
        StatusText.Text = $"オーディオセッションを持つプロセスを {_processItems.Count} 件表示。";
    }

    /// <summary>
    /// キャプチャ開始（UI スレッドで実行されるボタンクリック）
    /// </summary>
    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = (ProcessItem?)ProcessCombo.SelectedItem;
        if (selected == null)
        {
            MessageBox.Show("プロセスを選択してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // NAudio のミニマルでは「コンボで選んだ行の ProcessId」をそのまま渡して実音が取れた。
        // CaptureProcessId（セッションオーナー）だと環境によってプレースホルダーのみになることがあるので、両方ログしつつ ProcessId で試す。
        var processId = selected.ProcessId;
        var capturePid = selected.CaptureProcessId;
        var includeTree = false;
        Log($"Using ProcessId={processId}, CaptureProcessId={capturePid}, includeProcessTree={includeTree}");
        Log($"NAudio: {typeof(WasapiCapture).Assembly.Location}");

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = $"PID {processId} でキャプチャ開始中...";
        LogBox.Clear();
        _dataAvailableCount = 0;

        var threadIdBefore = Thread.CurrentThread.ManagedThreadId;
        var syncCtxBefore = SynchronizationContext.Current;
        Log($"Start: ThreadId={threadIdBefore}, SyncContextNull={syncCtxBefore == null}");

        try
        {
            // ConfigureAwait(false) は付けない → 継続も UI スレッドで実行される
            _capture = await WasapiCapture.CreateForProcessCaptureAsync(processId, includeTree);

            var threadIdAfter = Thread.CurrentThread.ManagedThreadId;
            var syncCtxAfter = SynchronizationContext.Current;
            Log($"After CreateForProcessCaptureAsync: ThreadId={threadIdAfter}, SyncContextNull={syncCtxAfter == null}, sameThread={threadIdBefore == threadIdAfter}");

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            var fmt = _capture.WaveFormat;
            Log($"WaveFormat: {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit");
            StatusText.Text = $"PID {processId} でキャプチャ中。停止ボタンで停止。";
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.GetType().Name}: {ex.Message}");
            StatusText.Text = $"エラー: {ex.Message}";
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _capture?.StopRecording();
        }
        catch (Exception ex)
        {
            Log($"Stop error: {ex.Message}");
        }
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = true;
        StatusText.Text = "停止しました。";
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        var bufferCopy = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer!, 0, bufferCopy, 0, e.BytesRecorded);

        var fmt = _capture!.WaveFormat;
        var (raw16Min, raw16Max) = GetRaw16BitRange(bufferCopy, e.BytesRecorded, fmt);
        var samples = ConvertToFloat(bufferCopy, e.BytesRecorded, fmt);
        var max = samples.Length > 0 ? samples.Max(s => Math.Abs(s)) : 0f;
        var avg = samples.Length > 0 ? samples.Average(s => Math.Abs(s)) : 0f;

        var n = Interlocked.Increment(ref _dataAvailableCount);
        if (n <= 5 || n % 50 == 0)
        {
            var rawStr = raw16Min.HasValue ? $", raw16=[{raw16Min.Value},{raw16Max!.Value}]" : "";
            var line = $"[#{n}] bytes={e.BytesRecorded}, max={max:F6}, avg={avg:F6}{rawStr}";
            Log(line);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Log($"RecordingStopped with error: {e.Exception.Message}");
        else
            Log($"RecordingStopped (normal). Total callbacks: {_dataAvailableCount}");
        try
        {
            _capture?.Dispose();
        }
        catch { }
        _capture = null;
        Dispatcher.BeginInvoke(() =>
        {
            StopButton.IsEnabled = false;
            StartButton.IsEnabled = true;
            StatusText.Text = "停止しました。";
        });
    }

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        void Append()
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Append);
            return;
        }
        Append();
    }

    private static (int? Min, int? Max) GetRaw16BitRange(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.BitsPerSample != 16 || bytesRecorded < 2) return (null, null);
        var count = bytesRecorded / 2;
        int min = int.MaxValue, max = int.MinValue;
        for (var i = 0; i < count; i++)
        {
            var s = BitConverter.ToInt16(buffer, i * 2);
            if (s < min) min = s;
            if (s > max) max = s;
        }
        return (min, max);
    }

    private static float[] ConvertToFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var totalSamples = bytesRecorded / bytesPerSample;
        using var stream = new MemoryStream(buffer, 0, bytesRecorded, false);
        using var rawStream = new RawSourceWaveStream(stream, format);
        var provider = rawStream.ToSampleProvider();
        if (format.Channels == 2)
        {
            var stereoTomono = new StereoToMonoSampleProvider(provider);
            var monoCount = totalSamples / 2;
            var samples = new float[monoCount];
            var read = stereoTomono.Read(samples, 0, monoCount);
            return read == monoCount ? samples : samples.Take(read).ToArray();
        }
        var all = new float[totalSamples];
        var r = provider.Read(all, 0, totalSamples);
        return r == totalSamples ? all : all.Take(r).ToArray();
    }
}
