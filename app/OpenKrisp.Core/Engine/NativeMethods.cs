using System.Runtime.InteropServices;

namespace OpenKrisp.Core.Engine;

/// <summary>P/Invoke declarations for crates/engine/include/openkrisp_engine.h. Keep the two in sync by hand.</summary>
internal static class NativeMethods
{
    // Resolves openkrisp_engine.dll on Windows and libopenkrisp_engine.so on Linux.
    private const string Lib = "openkrisp_engine";

    [StructLayout(LayoutKind.Sequential)]
    internal struct VcStatus
    {
        public byte MicNc;
        public byte SpeakerNc;
        public byte VoiceIsolation;
        public byte VoiceIsolationActive;
        public byte EchoCancelActive;
        public byte NearbyVoiceRemovalActive;
        public byte Reserved0;
        public byte Reserved1;
        public uint SampleRate;
        public uint Block;
        public ulong MicSamples;
        public ulong SpeakerSamples;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VcAudioConfig
    {
        public IntPtr MicInput;          // UTF-8, NULL = default
        public int MicSink;
        public IntPtr MicSinkDevice;     // UTF-8, NULL = default
        public int MicyFormat;
        public IntPtr SpeakerLoopback;   // UTF-8, NULL = lane off
        public IntPtr SpeakerOutput;     // UTF-8, NULL = default
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VcAudioStats
    {
        public byte Running;
        public byte R0, R1, R2, R3, R4, R5, R6;
        public ulong MicInputDropped;
        public ulong MicOutputUnderrunFrames;
        public ulong SpeakerInputDropped;
        public ulong SpeakerOutputUnderrunFrames;
        public ulong MicWorkerMaxBlockUs;
        public ulong SpeakerWorkerMaxBlockUs;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vc_audio_list_devices(int kind, byte[] buf, nuint cap);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vc_audio_start(IntPtr h, ref VcAudioConfig cfg, byte[] err, nuint errCap);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vc_audio_stop(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vc_audio_get_stats(IntPtr h, out VcAudioStats stats);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr vc_engine_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPathUtf8, uint sampleRate, byte[] err, nuint errCap);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void vc_engine_destroy(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vc_set_mic_nc(IntPtr h, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vc_set_speaker_nc(IntPtr h, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vc_set_voice_isolation(IntPtr h, int mode);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vc_get_status(IntPtr h, out VcStatus status);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int vc_process(
        IntPtr h, int lane, float[] input, nuint n, float[] output, nuint cap, out nuint written);
}
