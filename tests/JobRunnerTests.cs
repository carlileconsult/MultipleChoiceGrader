using System.Text.Json;
using Xunit;

public class JobRunnerTests
{
    [Fact]
    public async Task SingleFilePath_ProcessesOnlyOneFile_AndWritesDebugJson()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sub = Path.Combine(root, "subs"); Directory.CreateDirectory(sub);
        var res = Path.Combine(root, "results");
        var keyPath = Path.Combine(root, "answer-key.json");
        var f1 = Path.Combine(sub, "s1.jpg"); var f2 = Path.Combine(sub, "s2.jpg");
        await File.WriteAllTextAsync(f1, "x"); await File.WriteAllTextAsync(f2, "x");
        await File.WriteAllTextAsync(keyPath, JsonSerializer.Serialize(new { quizId = "Q1", title = "Q1", answers = new[] { new { questionNumber = 1, correctChoice = "a" } } }));

        var runner = new JobRunner();
        var summary = await runner.RunAsync(
            new CliOptions(null, keyPath, sub, res, false, false, false, false),
            new GradingOptions { SingleFilePath = f1, ExtractionMode = "OpenAI", ManualReviewConfidenceThreshold = 0.7m },
            new OpenAiOptions(),
            new DiagnosticsOptions
            {
                Verbose = false,
                PauseOnFatalError = false
            });
        Assert.Equal(1, summary.Processed);
        Assert.True(Directory.GetFiles(summary.ExtractedAnswersFolder, "*.json").Length >= 1);
    }
}
