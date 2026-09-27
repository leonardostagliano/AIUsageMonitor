using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace AIUsageMonitor.Tests;

/// <summary>
/// The two app sounds of spec 2026-09-27 §8, as committed under <c>src/AIUsageMonitor.App/Assets/Sounds</c> and embedded
/// by the App project. The tests do not reference the App, so the files are read from the repository, as
/// <see cref="ThemePaletteTests"/> does with Theme.xaml; <c>tools/sounds/generate-sounds.test.mjs</c> checks that the
/// script still produces them byte for byte.
/// </summary>
public class NotificationSoundAssetTests
{
    private const double FullScale = 32767;

    [Theory]
    [InlineData("done.wav")]
    [InlineData("attention.wav")]
    public void Sounds_are_16_bit_mono_pcm_at_44_1_khz(string file)
    {
        var wav = Wav.Read(SoundPath(file));

        Assert.Equal(1, wav.Format); // PCM
        Assert.Equal(1, wav.Channels);
        Assert.Equal(44100, wav.SampleRate);
        Assert.Equal(16, wav.BitsPerSample);
        Assert.Equal(88200, wav.ByteRate);
        Assert.Equal(2, wav.BlockAlign);
    }

    [Theory]
    [InlineData("done.wav", 520, -14.0)]
    [InlineData("attention.wav", 420, -10.0)]
    public void Duration_and_peak_match_the_spec(string file, double durationMs, double peakDbfs)
    {
        var wav = Wav.Read(SoundPath(file));

        Assert.InRange(wav.Samples.Length * 1000.0 / wav.SampleRate, durationMs - 5, durationMs + 5);
        var peak = wav.Samples.Max(sample => Math.Abs((int)sample));
        Assert.InRange(20 * Math.Log10(peak / FullScale), peakDbfs - 0.5, peakDbfs + 0.5);
    }

    [Fact]
    public void Done_starts_from_silence_and_its_tail_ends_on_zero()
    {
        var samples = Wav.Read(SoundPath("done.wav")).Samples;

        Assert.Equal(0, samples[0]);
        Assert.Equal(0, samples[^1]);
    }

    [Fact]
    public void App_project_embeds_the_two_sounds_under_the_names_NotificationSound_reads()
    {
        var project = XDocument.Load(Path.Combine(FindRepoRoot(), "src", "AIUsageMonitor.App", "AIUsageMonitor.App.csproj"));

        var item = Assert.Single(project.Descendants("EmbeddedResource"),
            e => (string?)e.Attribute("Include") == @"Assets\Sounds\*.wav");
        Assert.Equal("Sounds.%(Filename)%(Extension)", (string?)item.Attribute("LogicalName"));
        // The wildcard embeds every WAV of the folder: only the two sounds may be there.
        Assert.Equal(new[] { "attention.wav", "done.wav" },
            Directory.GetFiles(Path.GetDirectoryName(SoundPath("done.wav"))!, "*.wav").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
    }

    private static string SoundPath(string file) =>
        Path.Combine(FindRepoRoot(), "src", "AIUsageMonitor.App", "Assets", "Sounds", file);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AIUsageMonitor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    /// <summary>The "fmt " and "data" chunks of a RIFF/WAVE file, walked chunk by chunk.</summary>
    private sealed record Wav(int Format, int Channels, int SampleRate, int ByteRate, int BlockAlign, int BitsPerSample, short[] Samples)
    {
        public static Wav Read(string path)
        {
            Assert.True(File.Exists(path), $"{path} is missing: run node tools/sounds/generate-sounds.js");
            var bytes = File.ReadAllBytes(path);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));

            byte[]? format = null;
            short[]? samples = null;
            for (var at = 12; at + 8 <= bytes.Length;)
            {
                var id = Encoding.ASCII.GetString(bytes, at, 4);
                var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
                var body = bytes.AsSpan(at + 8, size);
                if (id == "fmt ") format = body.ToArray();
                if (id == "data")
                {
                    samples = new short[size / 2];
                    for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(body[(i * 2)..]);
                }
                at += 8 + size + (size & 1);
            }
            Assert.NotNull(format);
            Assert.NotNull(samples);
            return new Wav(
                Format: BinaryPrimitives.ReadUInt16LittleEndian(format),
                Channels: BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2)),
                SampleRate: BinaryPrimitives.ReadInt32LittleEndian(format.AsSpan(4)),
                ByteRate: BinaryPrimitives.ReadInt32LittleEndian(format.AsSpan(8)),
                BlockAlign: BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12)),
                BitsPerSample: BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14)),
                Samples: samples);
        }
    }
}
