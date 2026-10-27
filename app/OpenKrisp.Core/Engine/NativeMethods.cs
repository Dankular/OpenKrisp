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
