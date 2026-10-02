using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application.Content;

namespace OnVoyage.Factory.Infrastructure.Audio;

/// <summary>
/// Offline voice for development: the <c>espeak-ng</c> command line (GPL, run as a separate process, nothing linked). The output is a robotic
/// but intelligible WAV that goes through the same ffmpeg normalisation and ID3 tagging as the real voice (<c>AI_GENERATED=true</c>). Never for production.
/// </summary>
internal sealed class EspeakSpeechProvider(IConfiguration configuration, ILogger<EspeakSpeechProvider> logger) : ITextToSpeechProvider
{
    public const string ProviderName = "espeak-ng";

    private string Binary => configuration["Factory:Tts:EspeakPath"] ?? "espeak-ng";

    private bool? probed;

    public bool IsAvailable => probed ??= Probe(Binary);

    public async Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("onvoyage-espeak-").FullName;
        try
        {
            var wav = Path.Combine(directory, "speech.wav");
            var voice = request.Language == "en" ? "en-gb" : "fr";
            var start = new ProcessStartInfo(Binary) { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in new[] { "-v", voice, "-s", "150", "-p", "42", "-w", wav, "--stdin" })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("espeak-ng could not be started.");
            await process.StandardInput.WriteAsync(request.Text.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || !File.Exists(wav))
            {
                logger.LogWarning("espeak-ng failed with exit code {Code}: {Error}", process.ExitCode, await error);
                throw new ExternalServiceException("The offline voice (espeak-ng) failed.");
            }

            return new SpeechResult(await File.ReadAllBytesAsync(wav, cancellationToken), ProviderName, $"espeak-ng-{voice}", request.Text.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool Probe(string binary)
    {
        try
        {
            var start = new ProcessStartInfo(binary, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
