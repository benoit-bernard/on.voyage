using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Infrastructure.Llm;
using OpenAI;
using OpenAI.Audio;

namespace OnVoyage.Factory.Infrastructure.Audio;

/// <summary><c>gpt-4o-mini-tts</c> through the OpenAI SDK (§8.7). Called once per part, from the worker, never on a traveler's request.</summary>
internal sealed class OpenAiSpeechProvider(OpenAIClient client, IConfiguration configuration, LlmUsageRecorder usage) : ITextToSpeechProvider
{
    public const string ProviderName = "openai";

    public async Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        var model = configuration["Factory:Tts:Model"] ?? "gpt-4o-mini-tts";
        var started = Stopwatch.GetTimestamp();
        try
        {
#pragma warning disable OPENAI001 // Instructions (gpt-4o-mini-tts) is still flagged as evaluation-only by the SDK.
            var options = new SpeechGenerationOptions { ResponseFormat = GeneratedSpeechFormat.Mp3, Instructions = request.Instructions };
#pragma warning restore OPENAI001
            ClientResult<BinaryData> result = await client.GetAudioClient(model).GenerateSpeechAsync(request.Text, new GeneratedSpeechVoice(request.Voice), options, cancellationToken);

            // Speech is billed per character on the input side in this estimate; the real price is set per model in configuration.
            var characterPrice = configuration.GetValue<double?>($"Factory:Llm:Pricing:{model}:PerMillionCharacters") ?? 0d;
            await usage.RecordAsync("tts", model, null, null, null, request.Text.Length, 0, Stopwatch.GetElapsedTime(started), true, request.Text.Length * characterPrice / 1_000_000d);
            return new SpeechResult(result.Value.ToArray(), ProviderName, model, request.Text.Length);
        }
        catch (Exception ex) when (ex is ClientResultException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await usage.RecordAsync("tts", model, null, null, null, 0, 0, Stopwatch.GetElapsedTime(started), false);
            throw new ExternalServiceException($"Speech synthesis failed ({ex.GetType().Name}).", ex);
        }
    }
}

/// <summary>
/// ffmpeg post-processing of §8.7 in two passes (measure, then apply linearly) so the loudness lands on target: trim silence over 300 ms at
/// both ends, EBU R128 normalisation, mono, 44.1 kHz, MP3 at 48 kbit/s. Then the ID3v2 tags of §8.8, including <c>AI_GENERATED=true</c>.
/// </summary>
internal sealed class FfmpegAudioProcessor(IConfiguration configuration, ILogger<FfmpegAudioProcessor> logger) : IAudioProcessor
{
    private string Ffmpeg => configuration["Factory:Audio:FfmpegPath"] ?? "ffmpeg";

    private double TargetLufs => configuration.GetValue("Factory:Audio:LoudnessLufs", -16d);

    private int BitrateKbps => configuration.GetValue("Factory:Audio:BitrateKbps", 48);

    private const string TrimSilence =
        "silenceremove=start_periods=1:start_duration=0.3:start_threshold=-50dB:detection=peak,areverse," +
        "silenceremove=start_periods=1:start_duration=0.3:start_threshold=-50dB:detection=peak,areverse";

    public async Task<ProcessedAudio> ProcessAsync(byte[] input, AudioTags tags, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("onvoyage-audio-").FullName;
        try
        {
            var source = Path.Combine(directory, "input.bin");
            var target = Path.Combine(directory, "output.mp3");
            await File.WriteAllBytesAsync(source, input, cancellationToken);

            var target_i = TargetLufs.ToString("0.#", CultureInfo.InvariantCulture);
            var measure = await RunAsync(["-hide_banner", "-nostats", "-i", source, "-af", $"{TrimSilence},loudnorm=I={target_i}:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"], cancellationToken);
            var measured = ParseLoudness(measure);

            var filter = $"{TrimSilence},loudnorm=I={target_i}:TP=-1.5:LRA=11" +
                $":measured_I={measured.I}:measured_TP={measured.Tp}:measured_LRA={measured.Lra}:measured_thresh={measured.Thresh}:offset={measured.Offset}:linear=true";
            await RunAsync(["-hide_banner", "-loglevel", "error", "-y", "-i", source, "-af", filter, "-ac", "1", "-ar", "44100", "-c:a", "libmp3lame", "-b:a", $"{BitrateKbps}k", target], cancellationToken);

            var seconds = WriteTags(target, tags);
            var bytes = await File.ReadAllBytesAsync(target, cancellationToken);
            return new ProcessedAudio(bytes, seconds, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int WriteTags(string path, AudioTags tags)
    {
        using var file = TagLib.File.Create(path);
        var tag = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
        tag.Title = tags.Title;
        tag.Performers = ["ON.VOYAGE"];

        void Custom(string name, string value) => TagLib.Id3v2.UserTextInformationFrame.Get(tag, name, true).Text = [value];
        Custom("AI_GENERATED", "true");
        Custom("TTS_PROVIDER", tags.TtsProvider);
        Custom("TTS_MODEL", tags.TtsModel);
        Custom("CONTENT_ID", tags.ContentId);
        Custom("CONTENT_VERSION", tags.ContentVersion.ToString(CultureInfo.InvariantCulture));
        file.Save();
        return (int)Math.Ceiling(file.Properties.Duration.TotalSeconds);
    }

    private sealed record Loudness(string I, string Tp, string Lra, string Thresh, string Offset);

    private static Loudness ParseLoudness(string ffmpegOutput)
    {
        var start = ffmpegOutput.LastIndexOf('{');
        var end = ffmpegOutput.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("ffmpeg did not report a loudness measurement (empty or silent audio?).");
        }

        using var document = JsonDocument.Parse(ffmpegOutput[start..(end + 1)]);
        string Read(string name) => document.RootElement.GetProperty(name).GetString() ?? throw new InvalidOperationException($"Missing {name}.");
        return new Loudness(Read("input_i"), Read("input_tp"), Read("input_lra"), Read("input_thresh"), Read("target_offset"));
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await output;
        if (process.ExitCode != 0)
        {
            logger.LogWarning("ffmpeg failed with exit code {Code}.", process.ExitCode);
            throw new InvalidOperationException($"ffmpeg failed with exit code {process.ExitCode}: {error[^Math.Min(error.Length, 400)..]}");
        }

        return error;
    }
}

/// <summary>Files on local disk under <c>Factory:MediaDirectory</c>. Production uses S3-compatible storage behind a CDN (§9.7); the port is the same.</summary>
internal sealed class LocalMediaStorage(IConfiguration configuration) : IMediaStorage
{
    private string Root => Path.GetFullPath(configuration["Factory:MediaDirectory"] ?? Path.Combine(configuration["Factory:DataDirectory"] ?? Path.Combine(Path.GetTempPath(), "onvoyage-factory"), "media"));

    public async Task<string> SaveAsync(string relativePath, byte[] content, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The path leaves the media directory.", nameof(relativePath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content, cancellationToken);
        return relativePath;
    }
}
