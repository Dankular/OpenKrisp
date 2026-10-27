using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dmo;
using NAudio.Wave;

namespace OpenKrisp.App.Audio;

/// <summary>Windows Voice Capture DSP in source mode. Owns capture/reference synchronization.</summary>
internal sealed class WindowsAecCapture : IDisposable
{
    private readonly object _com;
    private readonly IAecMediaObject _dmo;
    private readonly DmoOutputDataBuffer[] _output = { new(32000) };
    private readonly bool _comInitialized;
    public WaveFormat WaveFormat { get; } = new(16000, 16, 1);

    public WindowsAecCapture(string microphoneId, string speakerId)
    {
        var hr = CoInitializeEx(IntPtr.Zero, 0);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        _comInitialized = true;
        try
        {
            _com = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("745057c7-f353-4f2d-a7ee-58434477730e"), throwOnError: true)!)!;
            _dmo = (IAecMediaObject)_com;
            var properties = (IAecPropertyStore)_com;
            Set(properties, 2, 0); // SINGLE_CHANNEL_AEC; selects an actual AEC processing pipeline.
            Set(properties, 3, -1, boolean: true); // Source mode.
            var mic = DeviceIndex(microphoneId, DataFlow.Capture);
            var speaker = DeviceIndex(speakerId, DataFlow.Render);
            Set(properties, 4, (speaker << 16) | (mic & 0xffff));
            Set(properties, 5, -1, boolean: true); // Override feature defaults.
            Set(properties, 8, 0); // DPDFNet provides noise reduction after AEC.
            Set(properties, 9, 0, boolean: true); // Keep microphone gain stable.
            var type = new DmoMediaType();
            Check(MoInitMediaType(ref type, Marshal.SizeOf<WaveFormat>()));
            try { type.SetWaveFormat(WaveFormat); Check(_dmo.SetOutputType(0, ref type, 0)); }
            finally { MoFreeMediaType(ref type); }
            Check(_dmo.AllocateStreamingResources());
        }
        catch { Dispose(); throw; }
    }

    public int Read(byte[] buffer)
    {
        _output[0].MediaBuffer.SetLength(0);
        var result = _dmo.ProcessOutput(0, 1, _output, out _);
        if (result == 1) return 0; // S_FALSE: waiting for the next synchronized audio block.
        Check(result);
        var count = _output[0].Length;
        if (count > buffer.Length) throw new IOException("AEC output buffer is too small.");
        if (count > 0) _output[0].RetrieveData(buffer, 0);
        return count;
    }

    private static int DeviceIndex(string id, DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        for (var i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            if (device.ID == id) return i;
        }
        throw new InvalidOperationException("The selected audio device is disconnected. Refresh devices and select it again.");
    }

    private static void Set(IAecPropertyStore store, int id, int value, bool boolean = false)
    {
        var key = new PropertyKey { Format = new Guid("6f52c567-0360-4bd2-9617-ccbf1421c939"), Id = id };
        var variant = new Variant { Type = (ushort)(boolean ? 11 : 3), Value = value };
        Check(store.SetValue(ref key, ref variant));
    }
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    public void Dispose()
    {
        if (_dmo is not null) { _dmo.FreeStreamingResources(); }
        _output[0].Dispose();
        if (_com is not null && Marshal.IsComObject(_com)) Marshal.ReleaseComObject(_com);
        if (_comInitialized) CoUninitialize();
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("msdmo.dll")] private static extern int MoInitMediaType(ref DmoMediaType type, int formatSize);
    [DllImport("msdmo.dll")] private static extern int MoFreeMediaType(ref DmoMediaType type);
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public int Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public int Value;
    }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAecPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out Variant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref Variant value);
        [PreserveSig] int Commit();
    }
    // IMediaObject vtable from mediaobj.h. Only source-mode output methods are called.
    [ComImport, Guid("d8ad0f58-5494-4102-97c5-ec798e59bcf4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAecMediaObject
    {
        [PreserveSig] int GetStreamCount(out int inputs, out int outputs);
        [PreserveSig] int GetInputStreamInfo(int stream, out int flags);
        [PreserveSig] int GetOutputStreamInfo(int stream, out int flags);
        [PreserveSig] int GetInputType(int stream, int index, IntPtr type);
        [PreserveSig] int GetOutputType(int stream, int index, IntPtr type);
        [PreserveSig] int SetInputType(int stream, ref DmoMediaType type, int flags);
        [PreserveSig] int SetOutputType(int stream, ref DmoMediaType type, int flags);
        [PreserveSig] int GetInputCurrentType(int stream, IntPtr type);
        [PreserveSig] int GetOutputCurrentType(int stream, IntPtr type);
        [PreserveSig] int GetInputSizeInfo(int stream, out int size, out int lookahead, out int alignment);
        [PreserveSig] int GetOutputSizeInfo(int stream, out int size, out int alignment);
        [PreserveSig] int GetInputMaxLatency(int stream, out long latency);
        [PreserveSig] int SetInputMaxLatency(int stream, long latency);
        [PreserveSig] int Flush();
        [PreserveSig] int Discontinuity(int stream);
        [PreserveSig] int AllocateStreamingResources();
        [PreserveSig] int FreeStreamingResources();
        [PreserveSig] int GetInputStatus(int stream, out int flags);
        [PreserveSig] int ProcessInput(int stream, IntPtr buffer, int flags, long timestamp, long duration);
        [PreserveSig] int ProcessOutput(int flags, int count, [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] DmoOutputDataBuffer[] buffers, out int status);
        [PreserveSig] int Lock(int value);
    }
}
