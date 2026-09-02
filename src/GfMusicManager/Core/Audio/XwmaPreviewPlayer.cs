using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using GfMusicManager.Core.Diagnostics;
using SkyrimScan.Core.Archives;
using SkyrimScan.Core.Models;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace GfMusicManager.Core.Audio;

/// <summary>
/// Plays Skyrim XWMA data through XAudio2, including entries read from BSA archives.
/// </summary>
public sealed class XwmaPreviewPlayer : IDisposable
{
    private readonly BsaArchiveReader _archiveReader = new();
    private IXAudio2? _audio;
    private IXAudio2MasteringVoice? _masteringVoice;
    private IXAudio2SourceVoice? _voice;
    private IntPtr _audioDataPointer;
    private IntPtr _packetTablePointer;
    private AssetSource? _currentAsset;
    private XwmaFile? _currentFile;
    private uint _sampleRate;
    private uint _startSample;
    private double _durationSeconds;
    private float _volume = 1.0f;
    private bool _isPaused;
    private bool _disposed;

    public AssetSource? CurrentAsset => _currentAsset;

    public bool HasLoaded => _voice is not null;

    public bool IsPaused => _voice is not null && _isPaused;

    public bool IsPlaying => _voice is not null && !_isPaused && !IsEnded;

    public bool IsEnded => _voice is not null && _voice.State.BuffersQueued == 0;

    public TimeSpan Duration => TimeSpan.FromSeconds(_durationSeconds);

    public float Volume
    {
        get => _volume;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _volume = Math.Clamp(value, 0.0f, 1.0f);
            _voice?.SetVolume(_volume);
        }
    }

    public TimeSpan Position
    {
        get
        {
            if (_voice is null || _sampleRate == 0)
            {
                return TimeSpan.Zero;
            }

            var seconds = (_startSample + _voice.State.SamplesPlayed) / (double)_sampleRate;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _durationSeconds));
        }
    }

    public void Play(AssetSource asset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        GfMusicManagerLog.Info(
            $"XwmaPreviewPlayer.Play: begin. mod={asset.ModName}, path={asset.VirtualPath}, " +
            $"sourceKind={asset.SourceKind}, archive={asset.ArchiveEntryPath ?? "<none>"}.");

        var extension = Path.GetExtension(asset.VirtualPath);
        if (!extension.Equals(".xwm", StringComparison.OrdinalIgnoreCase))
        {
            var displayExtension = string.IsNullOrWhiteSpace(extension)
                ? "不明な形式"
                : extension.TrimStart('.').ToUpperInvariant();
            throw new NotSupportedException(
                $"{displayExtension}形式は現在の試聴に対応していません。\n" +
                "試聴できる形式はXWMのみです。\n" +
                $"対象形式：{displayExtension}");
        }

        var file = XwmaFile.Read(ReadAsset(asset));
        GfMusicManagerLog.Info(
            $"XwmaPreviewPlayer.Play: decoded. sampleRate={file.SampleRate}, " +
            $"duration={file.DurationSeconds:0.###}s, audioBytes={file.AudioBytes.Length}.");
        Stop();
        EnsureAudio();

        StartVoice(file, asset, TimeSpan.Zero, startImmediately: true);
    }

    /// <summary>
    /// Moves the current XWMA preview position without re-reading the source asset.
    /// XAudio2 applies PlayBegin when the buffer is submitted, so the source voice
    /// is recreated while the already-read XWMA data is retained.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_voice is null || _currentAsset is null || _currentFile is null)
        {
            return;
        }

        var asset = _currentAsset;
        var file = _currentFile;
        var wasPaused = _isPaused;
        var clampedPosition = TimeSpan.FromSeconds(
            Math.Clamp(position.TotalSeconds, 0, file.DurationSeconds));

        ReleaseVoice();
        StartVoice(file, asset, clampedPosition, startImmediately: !wasPaused);
        GfMusicManagerLog.Info(
            $"XwmaPreviewPlayer.Seek: position={clampedPosition.TotalSeconds:0.###}s, " +
            $"paused={wasPaused}.");
    }

    private void StartVoice(
        XwmaFile file,
        AssetSource asset,
        TimeSpan position,
        bool startImmediately)
    {
        IntPtr audioDataPointer = IntPtr.Zero;
        IntPtr packetTablePointer = IntPtr.Zero;
        IXAudio2SourceVoice? voice = null;
        try
        {
            audioDataPointer = Marshal.AllocHGlobal(file.AudioBytes.Length);
            packetTablePointer = Marshal.AllocHGlobal(
                checked(file.DecodedPacketCumulativeBytes.Length * sizeof(uint)));
            Marshal.Copy(file.AudioBytes, 0, audioDataPointer, file.AudioBytes.Length);

            var packetTableBytes = new byte[file.DecodedPacketCumulativeBytes.Length * sizeof(uint)];
            for (var index = 0; index < file.DecodedPacketCumulativeBytes.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    packetTableBytes.AsSpan(index * sizeof(uint), sizeof(uint)),
                    file.DecodedPacketCumulativeBytes[index]);
            }

            Marshal.Copy(packetTableBytes, 0, packetTablePointer, packetTableBytes.Length);

            var format = WaveFormat.MarshalFrom(file.FormatBytes);
            voice = _audio!.CreateSourceVoice(format);
            var startSample = CalculateStartSample(file, position);
            var playLength = checked(file.TotalSamples - startSample);
            var buffer = new AudioBuffer(
                audioDataPointer,
                checked((uint)file.AudioBytes.Length),
                BufferFlags.EndOfStream)
            {
                PlayBegin = startSample,
                PlayLength = playLength
            };

            voice.SetVolume(_volume);
            SubmitWmaBuffer(
                voice,
                buffer,
                packetTablePointer,
                file.DecodedPacketCumulativeBytes.Length);
            if (startImmediately)
            {
                CheckResult(voice.Start(), "XAudio2 source voice start");
            }

            _voice = voice;
            _audioDataPointer = audioDataPointer;
            _packetTablePointer = packetTablePointer;
            _currentAsset = asset;
            _currentFile = file;
            _sampleRate = file.SampleRate;
            _startSample = startSample;
            _durationSeconds = file.DurationSeconds;
            _isPaused = !startImmediately;

            GfMusicManagerLog.Info(
                $"XwmaPreviewPlayer.StartVoice: startSample={startSample}, " +
                $"playLength={playLength}, started={startImmediately}.");

            voice = null;
            audioDataPointer = IntPtr.Zero;
            packetTablePointer = IntPtr.Zero;
        }
        finally
        {
            voice?.Dispose();
            FreePointer(ref packetTablePointer);
            FreePointer(ref audioDataPointer);
        }
    }

    private static uint CalculateStartSample(XwmaFile file, TimeSpan position)
    {
        if (file.TotalSamples <= 1 || position <= TimeSpan.Zero)
        {
            return 0;
        }

        var requested = position.TotalSeconds * file.SampleRate;
        var sample = Math.Round(requested, MidpointRounding.AwayFromZero);
        return (uint)Math.Clamp(sample, 0, file.TotalSamples - 1);
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_voice is null || _isPaused || IsEnded)
        {
            return;
        }

        CheckResult(_voice.Stop(), "XAudio2 source voice pause");
        _isPaused = true;
        GfMusicManagerLog.Info("XwmaPreviewPlayer.Pause: paused.");
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_voice is null || !_isPaused || IsEnded)
        {
            return;
        }

        CheckResult(_voice.Start(), "XAudio2 source voice resume");
        _isPaused = false;
        GfMusicManagerLog.Info("XwmaPreviewPlayer.Resume: resumed.");
    }

    public void TogglePause()
    {
        if (_isPaused)
        {
            Resume();
        }
        else
        {
            Pause();
        }
    }

    public void Stop()
    {
        ReleaseVoice();
        _currentAsset = null;
        _currentFile = null;
        _sampleRate = 0;
        _startSample = 0;
        _durationSeconds = 0;
        _isPaused = false;
    }

    private void ReleaseVoice()
    {
        if (_voice is not null)
        {
            try
            {
                _voice.Stop();
            }
            finally
            {
                _voice.Dispose();
                _voice = null;
            }
        }

        FreePointer(ref _packetTablePointer);
        FreePointer(ref _audioDataPointer);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _masteringVoice?.Dispose();
        _audio?.Dispose();
        _masteringVoice = null;
        _audio = null;
        _disposed = true;
    }

    private byte[] ReadAsset(AssetSource asset)
    {
        if (asset.IsFromArchive)
        {
            if (string.IsNullOrWhiteSpace(asset.ArchiveEntryPath))
            {
                throw new InvalidDataException("BSA音源にアーカイブ内パスがありません。");
            }

            return _archiveReader.ReadEntry(asset.SourcePath, asset.ArchiveEntryPath);
        }

        if (!File.Exists(asset.SourcePath))
        {
            throw new FileNotFoundException("音源ファイルが見つかりません。", asset.SourcePath);
        }

        return File.ReadAllBytes(asset.SourcePath);
    }

    private void EnsureAudio()
    {
        if (_audio is not null)
        {
            return;
        }

        _audio = XAudio2.XAudio2Create(ProcessorSpecifier.DefaultProcessor, false);
        _masteringVoice = _audio.CreateMasteringVoice();
        _audio.StartEngine();
    }

    private static void SubmitWmaBuffer(
        IXAudio2SourceVoice voice,
        AudioBuffer buffer,
        IntPtr packetTablePointer,
        int packetCount)
    {
        // Vortice keeps XAUDIO2_BUFFER_WMA as an internal interop struct. The
        // public interface still exposes the native overload, so invoke that
        // overload while retaining the exact native packet-table layout.
        var wmaType = typeof(IXAudio2SourceVoice).Assembly.GetType("Vortice.XAudio2.BufferWma")
            ?? throw new InvalidOperationException("Vortice.XAudio2.BufferWmaが見つかりません。");
        var wma = Activator.CreateInstance(wmaType)
            ?? throw new InvalidOperationException("BufferWmaを作成できません。");
        var decodedBytesField = wmaType.GetField("DecodedPacketCumulativeBytesPointer")
            ?? throw new InvalidOperationException("BufferWmaのパケットテーブル項目が見つかりません。");
        var packetCountField = wmaType.GetField("PacketCount")
            ?? throw new InvalidOperationException("BufferWmaのパケット数項目が見つかりません。");
        decodedBytesField.SetValue(wma, packetTablePointer);
        packetCountField.SetValue(wma, Convert.ChangeType(packetCount, packetCountField.FieldType));

        var submitMethod = voice.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(method =>
            {
                var parameters = method.GetParameters();
                var optionalWmaType = parameters.Length == 2
                    ? Nullable.GetUnderlyingType(parameters[1].ParameterType) ?? parameters[1].ParameterType
                    : null;
                return method.Name == "SubmitSourceBuffer"
                    && parameters.Length == 2
                    && optionalWmaType is not null
                    && optionalWmaType.Name == "BufferWma";
            });

        var result = submitMethod.Invoke(voice, new[] { (object)buffer, wma });
        CheckResult(result, "XAudio2 WMA buffer submit");
    }

    private static void CheckResult(object? result, string operation)
    {
        if (result is null)
        {
            return;
        }

        var resultType = result.GetType();
        var failure = resultType.GetProperty("Failure")?.GetValue(result)
            ?? resultType.GetField("Failure")?.GetValue(result);
        if (failure is bool isFailure && isFailure)
        {
            throw new InvalidOperationException($"{operation}に失敗しました: {result}");
        }
    }

    private static void FreePointer(ref IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        Marshal.FreeHGlobal(pointer);
        pointer = IntPtr.Zero;
    }

    private sealed class XwmaFile
    {
        private XwmaFile(
            byte[] formatBytes,
            byte[] audioBytes,
            uint[] decodedPacketCumulativeBytes,
            uint sampleRate,
            double durationSeconds,
            uint totalSamples)
        {
            FormatBytes = formatBytes;
            AudioBytes = audioBytes;
            DecodedPacketCumulativeBytes = decodedPacketCumulativeBytes;
            SampleRate = sampleRate;
            DurationSeconds = durationSeconds;
            TotalSamples = totalSamples;
        }

        public byte[] FormatBytes { get; }
        public byte[] AudioBytes { get; }
        public uint[] DecodedPacketCumulativeBytes { get; }
        public uint SampleRate { get; }
        public double DurationSeconds { get; }
        public uint TotalSamples { get; }

        public static XwmaFile Read(byte[] bytes)
        {
            if (bytes.Length < 12 || FourCc(bytes, 0) != "RIFF" || FourCc(bytes, 8) != "XWMA")
            {
                throw new InvalidDataException("RIFF/XWMA音源ではありません。");
            }

            byte[]? formatBytes = null;
            byte[]? audioBytes = null;
            uint[]? packetTable = null;
            var offset = 12;
            while (bytes.Length - offset >= 8)
            {
                var fourCc = FourCc(bytes, offset);
                var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(offset + 4, 4));
                var dataOffset = checked(offset + 8);
                if (chunkSize > (uint)(bytes.Length - dataOffset))
                {
                    throw new InvalidDataException($"XWMAの{fourCc}チャンクがファイル範囲を超えています。");
                }

                var size = checked((int)chunkSize);
                var dataEnd = checked(dataOffset + size);
                var nextOffset = checked(dataEnd + (int)(chunkSize & 1u));
                if (nextOffset > bytes.Length && dataEnd != bytes.Length)
                {
                    throw new InvalidDataException($"XWMAの{fourCc}チャンクのパディングがファイル範囲を超えています。");
                }

                switch (fourCc)
                {
                    case "fmt ":
                        formatBytes = bytes[dataOffset..dataEnd];
                        break;
                    case "dpds":
                        if (size == 0 || (size & 3) != 0)
                        {
                            throw new InvalidDataException("XWMAのdpdsチャンクが不正です。");
                        }

                        packetTable = new uint[size / sizeof(uint)];
                        for (var index = 0; index < packetTable.Length; index++)
                        {
                            packetTable[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                                bytes.AsSpan(dataOffset + index * sizeof(uint), sizeof(uint)));
                        }

                        break;
                    case "data":
                        audioBytes = bytes[dataOffset..dataEnd];
                        break;
                }

                offset = Math.Min(nextOffset, bytes.Length);
            }

            if (formatBytes is null || audioBytes is null || packetTable is null)
            {
                throw new InvalidDataException("XWMAにfmt、dpds、dataのいずれかがありません。");
            }

            if (formatBytes.Length < 16)
            {
                throw new InvalidDataException("XWMAのfmtチャンクが短すぎます。");
            }

            var channels = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.AsSpan(2, 2));
            var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(formatBytes.AsSpan(4, 4));
            var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.AsSpan(14, 2));
            var decodedBytesPerSecond = (double)channels * bitsPerSample / 8 * sampleRate;
            var durationSeconds = decodedBytesPerSecond <= 0
                ? 0
                : packetTable[^1] / decodedBytesPerSecond;
            var bytesPerSample = (double)channels * bitsPerSample / 8;
            var totalSamples = bytesPerSample <= 0
                ? 0
                : (uint)Math.Min(uint.MaxValue, Math.Max(0, Math.Round(packetTable[^1] / bytesPerSample)));

            return new XwmaFile(
                formatBytes,
                audioBytes,
                packetTable,
                sampleRate,
                durationSeconds,
                totalSamples);
        }

        private static string FourCc(byte[] bytes, int offset) =>
            Encoding.ASCII.GetString(bytes, offset, 4);
    }
}
