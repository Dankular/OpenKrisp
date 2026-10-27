namespace OpenKrisp.Core.Notes;

/// <summary>Levels measured on the same mono mix sent to live transcription.</summary>
public sealed record AudioLevel(double PeakDb, double RmsDb)
{
    public static AudioLevel Silence { get; } = new(-60, -60);
    public static AudioLevel Measure(ReadOnlySpan<float> samples)
    {
        double peak = 0, energy = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample)) continue;
            peak = Math.Max(peak, Math.Abs(sample));
            energy += (double)sample * sample;
        }
        static double Db(double value) => Math.Clamp(20 * Math.Log10(Math.Max(value, 0.000001)), -60, 0);
        return new(Db(peak), Db(samples.Length == 0 ? 0 : Math.Sqrt(energy / samples.Length)));
    }
}
