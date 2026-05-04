using System.Text.Json;
using Xunit;

public class JobRunnerTests
{
    [Fact]
    public async Task EmptyTextSubmission_IsManualReview()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sub = Path.Combine(root, "subs"); Directory.CreateDirectory(sub);
        var res = Path.Combine(root, "results");
        var keyPath = Path.Combine(root, "answer-key.json");
        await File.WriteAllTextAsync(Path.Combine(sub, "s.doc"), "");
        await File.WriteAllTextAsync(keyPath, JsonSerializer.Serialize(new { assignmentName = "A1", questions = new Dictionary<string, string>{{"1","A"}} }));

        var runner = new JobRunner();
        var summary = await runner.RunAsync(new CliOptions(null, keyPath, sub, res, false, false, false, false), new GradingOptions());
        var reviewCsv = await File.ReadAllTextAsync(summary.ReviewReportPath);
        Assert.Contains("true", reviewCsv);
    }
}
