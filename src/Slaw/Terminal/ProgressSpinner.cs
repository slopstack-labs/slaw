using static SlopstackLabs.Slaw.Terminal.TerminalPalette;

namespace SlopstackLabs.Slaw.Terminal;

/// <summary>
/// A live animated progress indicator on stderr. Call <see cref="Succeed"/> or
/// <see cref="Fail"/> exactly once after starting.
///
/// When the attached stream cannot render ANSI escape sequences the animation is
/// suppressed entirely and each step settles into a single static line instead
/// of scrolling thousands of redraws into a log file.
/// </summary>
internal sealed class ProgressSpinner
{
    private static readonly TimeSpan AnimationFrameInterval = TimeSpan.FromMilliseconds(80);

    private readonly string progressLabel;
    private readonly CancellationTokenSource terminationSignalSource = new();
    private readonly Task animationLoopTask;
    private long accumulatedInferenceTokens;

    private ProgressSpinner(string progressLabel)
    {
        this.progressLabel = progressLabel;
        animationLoopTask = AnimationsEnabled
            ? Task.Run(RunAnimationLoopAsync)
            : Task.CompletedTask;
    }

    public static ProgressSpinner Start(string progressLabel) => new(progressLabel);

    /// <summary>Sets the displayed token counter to an absolute value.</summary>
    public void SetTokens(long absoluteTokenCount) =>
        Interlocked.Exchange(ref accumulatedInferenceTokens, absoluteTokenCount);

    /// <summary>Stops the spinner and prints a green success marker.</summary>
    public void Succeed()
    {
        StopAnimation();
        var accumulatedTokenCount = Interlocked.Read(ref accumulatedInferenceTokens);
        if (accumulatedTokenCount > 0)
        {
            Console.Error.WriteLine($"{ClearLine}  {Green}{GlyphOK}{Reset}  {progressLabel}  {Dim}({accumulatedTokenCount} tokens){Reset}");
        }
        else
        {
            Console.Error.WriteLine($"{ClearLine}  {Green}{GlyphOK}{Reset}  {progressLabel}");
        }
    }

    /// <summary>Stops the spinner and prints a red failure marker.</summary>
    public void Fail()
    {
        StopAnimation();
        Console.Error.WriteLine($"{ClearLine}  {Red}{GlyphFail}{Reset}  {progressLabel}");
    }

    private void StopAnimation()
    {
        terminationSignalSource.Cancel();
        animationLoopTask.Wait();
    }

    private async Task RunAnimationLoopAsync()
    {
        for (var animationFrameIndex = 0; !terminationSignalSource.IsCancellationRequested; animationFrameIndex++)
        {
            var currentFrameGlyph = SpinnerAnimationFrames[animationFrameIndex % SpinnerAnimationFrames.Count];
            var accumulatedTokenCount = Interlocked.Read(ref accumulatedInferenceTokens);
            Console.Error.Write(accumulatedTokenCount > 0
                ? $"{ClearLine}  {Cyan}{currentFrameGlyph}{Reset}  {progressLabel}  {Dim}{accumulatedTokenCount} tokens{Reset}"
                : $"{ClearLine}  {Cyan}{currentFrameGlyph}{Reset}  {progressLabel}");
            try
            {
                await Task.Delay(AnimationFrameInterval, terminationSignalSource.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }
}
