using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Videos;

/// <summary>
/// The ffmpeg seam of the transcode worker (#161): probe the stored copy and
/// run the deterministic encode. Implementers never report network or tool
/// details; failures map to the worker's German reasons. Tests substitute
/// in-memory fakes, the image build carries the real tools.
/// </summary>
public interface IVideoTranscoder
{
	/// <summary>Inspects the file at the given path; null without a video stream.</summary>
	Task<VideoProbe?> InspectAsync(string inputPath, CancellationToken token);

	/// <summary>
	/// Encodes an MP4 H.264/AAC playback copy. The plan supplies the exact
	/// scaled display size (never up, always even) — audio maps only when it
	/// exists, and the output starts fast for streaming.
	/// </summary>
	Task TranscodeAsync(string inputPath, string outputPath, VideoPlan plan, CancellationToken token);
}

/// <summary>
/// ffmpeg/ffprobe command-line implementation (#161). Archives encode at
/// CRF 20 (high quality, <c>libx264</c>), AAC 192 kbit/s, <c>+faststart</c>;
/// the scale filter receives the plan's exact display size — ffmpeg applies
/// the rotation matrix before the filters, so portrait sources come out the
/// right way up at the planned display size. Exit codes and stderr are never
/// logged: every non-zero exit is the same deterministic unreadable failure
/// (<see cref="VideoWorkerException.UnreadableVideoReason"/> material), the
/// only detail the worker records.
/// </summary>
public sealed class FfmpegVideoTranscoder(IOptions<VideoOptions> options, ILogger<FfmpegVideoTranscoder> logger) : IVideoTranscoder
{
	public async Task<VideoProbe?> InspectAsync(string inputPath, CancellationToken token)
	{
		var stdout = await RunAsync(options.Value.FfprobePath,
			["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", inputPath],
			token);
		try
		{
			return VideoProbe.FromFfprobeJson(stdout.ToString());
		}
		catch (JsonException)
		{
			// Corrupt probe output: the bytes themselves are unreadable.
			return null;
		}
	}

	public async Task TranscodeAsync(string inputPath, string outputPath, VideoPlan plan, CancellationToken token)
	{
		var args = new List<string>
		{
			// Nominal: an earlier attempt's temporary output never blocks.
			"-y", "-v", "error", "-i", inputPath,
			// Stream map: the first video stream and, when it exists, the
			// first audio one (the trailing `?` keeps the command running
			// without it); subtitle, cover-art and other tracks stay out, and
			// the source's own metadata is dropped.
			"-map", "0:v:0",
			"-sn",
			"-map_metadata", "-1",
			"-c:v", "libx264", "-crf", "20", "-preset", "medium", "-pix_fmt", "yuv420p",
			"-vf", $"scale={plan.ScaleWidth}:{plan.ScaleHeight}",
		};
		if (plan.NeedsCopy)
			// Encode audio only when an audio track exists: no track, no AAC
			// settings, no false second object.
			args.AddRange(["-map", "0:a:0?", "-c:a", "aac", "-b:a", "192k"]);
		args.AddRange(["-movflags", "+faststart", "-f", "mp4", outputPath]);

		await RunAsync(options.Value.FfmpegPath, args, token);
	}

	/// <summary>
	/// Runs one ffmpeg/ffprobe invocation to completion under the token.
	/// stdout/stderr are drained (never logged); a non-zero or failed exit
	/// throws the deterministic unreadable failure so the worker classifies
	/// without re-reading tool output.
	/// </summary>
	private async Task<string> RunAsync(string tool, IReadOnlyList<string> arguments, CancellationToken token)
	{
		logger.LogDebug("Transcode tool {Tool} runs with {ArgumentCount} arguments", tool, arguments.Count);
		var info = new ProcessStartInfo(tool)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			// The argument list quotes paths natively; no shell line is built.
		};
		foreach (var argument in arguments)
			info.ArgumentList.Add(argument);
		using var process = new Process();
		process.StartInfo = info;
		if (!process.Start())
			throw new VideoWorkerException(VideoWorker.UnreadableVideoReason);
		void Kill()
		{
			try
			{
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
			}
		}
		await using var _ = token.Register(Kill);
		var stdoutDrain = process.StandardOutput.ReadToEndAsync(token);
		var stderrDrain = process.StandardError.ReadToEndAsync(token);
		await process.WaitForExitAsync(token);
		await Task.WhenAll(stdoutDrain, stderrDrain);
		if (process.ExitCode != 0)
			throw new VideoWorkerException(VideoWorker.UnreadableVideoReason);
		return stdoutDrain.Result;
	}
}

/// <summary>An exception a substitute transcoder may also throw for its classification.</summary>
public sealed class VideoWorkerException(string failureReason) : Exception(failureReason)
{
	/// <summary>The deterministic German reason (see <see cref="VideoWorker.UnreadableVideoReason"/>).</summary>
	public string FailureReason { get; } = failureReason;
}
