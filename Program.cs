using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var argsResult = CliOptions.Parse(args);
if (!argsResult.Success)
{
    Console.WriteLine(argsResult.ErrorMessage);
    CliOptions.PrintUsage();
    return 1;
}

var options = argsResult.Options!;

try
{
    var runner = new JobRunner();
    var summary = await runner.RunAsync(options);

    Console.WriteLine();
    Console.WriteLine("=== Job Summary ===");
    Console.WriteLine($"Job path: {summary.JobPath}");
    Console.WriteLine($"Assignment name: {summary.AssignmentName}");
    Console.WriteLine($"Submissions found: {summary.SubmissionCount}");
    Console.WriteLine($"Extraction mode: {summary.ExtractorMode}");
    Console.WriteLine($"Use cache: {summary.UseCache}");
    Console.WriteLine($"Graded: {summary.GradedCount}");
    Console.WriteLine($"Need review: {summary.ReviewCount}");
    Console.WriteLine("Reports:");
    Console.WriteLine($"- {summary.GradeReportPath}");
    Console.WriteLine($"- {summary.ReviewReportPath}");
    Console.WriteLine($"- {summary.ExtractionSummaryPath}");

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    return 1;
}

internal sealed record CliOptions(
    string JobPath,
    bool ExtractOnly,
    bool GradeOnly,
    bool UseCache,
    bool ForceReextract)
{
    public static ParseResult Parse(string[] args)
    {
        string? jobPath = null;
        bool extractOnly = false;
        bool gradeOnly = false;
        bool useCache = false;
        bool forceReextract = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--job":
                    if (i + 1 >= args.Length)
                    {
                        return ParseResult.Fail("Missing value for --job");
                    }
                    jobPath = args[++i];
                    break;
                case "--extract-only": extractOnly = true; break;
                case "--grade-only": gradeOnly = true; break;
                case "--use-cache": useCache = true; break;
                case "--force-reextract": forceReextract = true; break;
                default:
                    return ParseResult.Fail($"Unknown argument: {arg}");
            }
        }

        if (string.IsNullOrWhiteSpace(jobPath))
        {
            return ParseResult.Fail("--job <path> is required");
        }

        if (extractOnly && gradeOnly)
        {
            return ParseResult.Fail("--extract-only and --grade-only cannot be used together");
        }

        return ParseResult.Ok(new CliOptions(jobPath!, extractOnly, gradeOnly, useCache, forceReextract));
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run -- --job <path> [--extract-only] [--grade-only] [--use-cache] [--force-reextract]");
    }
}

internal sealed record ParseResult(bool Success, CliOptions? Options, string ErrorMessage)
{
    public static ParseResult Ok(CliOptions options) => new(true, options, string.Empty);
    public static ParseResult Fail(string errorMessage) => new(false, null, errorMessage);
}

internal sealed class JobRunner
{
    public async Task<JobRunSummary> RunAsync(CliOptions options)
    {
        var job = JobContext.Create(options.JobPath);
        var answerKey = LoadAnswerKey(job.AnswerKeyPath);
        var settings = LoadOrCreateSettings(job.SettingsPath);

        Console.WriteLine($"Job path: {job.JobPath}");
        Console.WriteLine($"Assignment name: {answerKey.AssignmentName}");

        var submissions = Directory.GetFiles(job.SubmissionsPath)
            .Where(IsSupportedInput)
            .OrderBy(Path.GetFileName)
            .ToList();

        Console.WriteLine($"Submissions found: {submissions.Count}");
        Console.WriteLine($"Extraction mode: {settings.ExtractorMode}");
        Console.WriteLine($"Use cache: {options.UseCache && !options.ForceReextract}");

        var extracted = new List<ExtractedSubmission>();
        if (!options.GradeOnly)
        {
            foreach (var file in submissions)
            {
                var outputPath = Path.Combine(job.ExtractedJsonPath, SafeFileName(Path.GetFileNameWithoutExtension(file)) + ".json");
                if (options.UseCache && !options.ForceReextract && File.Exists(outputPath))
                {
                    extracted.Add(ReadJson<ExtractedSubmission>(outputPath));
                    continue;
                }

                var item = await ExtractAsync(file, answerKey, settings);
                WriteJson(outputPath, item);
                extracted.Add(item);
            }
        }

        if (options.GradeOnly)
        {
            extracted = Directory.GetFiles(job.ExtractedJsonPath, "*.json")
                .OrderBy(Path.GetFileName)
                .Select(ReadJson<ExtractedSubmission>)
                .ToList();
        }

        var extractionSummaryPath = Path.Combine(job.ResultsPath, "extraction-summary.csv");
        CsvWriter.WriteExtractionSummary(extractionSummaryPath, extracted);

        var gradePath = Path.Combine(job.ResultsPath, "grade-report.csv");
        var reviewPath = Path.Combine(job.ResultsPath, "review-needed.csv");

        var gradeRows = new List<GradeRow>();
        if (!options.ExtractOnly)
        {
            gradeRows = extracted.Select(e => Grade(answerKey, settings, e)).ToList();
            CsvWriter.WriteGradeReport(gradePath, gradeRows);
            CsvWriter.WriteReviewReport(reviewPath, gradeRows.Where(x => x.NeedsReview).ToList());
        }

        return new JobRunSummary(
            job.JobPath,
            answerKey.AssignmentName,
            submissions.Count,
            settings.ExtractorMode.ToString(),
            options.UseCache && !options.ForceReextract,
            gradeRows.Count,
            gradeRows.Count(x => x.NeedsReview),
            gradePath,
            reviewPath,
            extractionSummaryPath);
    }

    private static bool IsSupportedInput(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".pdf" or ".docx" or ".jpg" or ".jpeg" or ".png";
    }

    private static async Task<ExtractedSubmission> ExtractAsync(string file, AnswerKey key, JobSettings settings)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var warnings = new List<string>();

        ExtractedSubmission? rule = null;
        ExtractedSubmission? ai = null;

        if (settings.ExtractorMode is ExtractorMode.RuleBased or ExtractorMode.Hybrid)
        {
            if (ext is ".docx" or ".pdf")
            {
                rule = ExtractRuleBased(file);
            }
        }

        var needsAiByType = ext is ".jpg" or ".jpeg" or ".png";
        var fewerThanExpected = rule != null && rule.Answers.Count < key.Questions.Count;

        if (settings.ExtractorMode is ExtractorMode.Ai or ExtractorMode.Hybrid)
        {
            if (needsAiByType || rule == null || fewerThanExpected)
            {
                ai = await ExtractWithAiAsync(file);
            }
        }

        if (ai != null)
        {
            return ai with { Warnings = warnings.Concat(ai.Warnings).Distinct().ToList() };
        }

        if (rule != null)
        {
            if (needsAiByType || fewerThanExpected)
            {
                warnings.Add("AI fallback failed; using rule-based extraction.");
            }
            return rule with { Warnings = warnings.Concat(rule.Warnings).Distinct().ToList() };
        }

        return new ExtractedSubmission(Path.GetFileName(file), "", new(), "low", "None", false, new() { "No extraction strategy succeeded." });
    }

    private static ExtractedSubmission ExtractRuleBased(string file)
    {
        var text = File.ReadAllText(file);
        var answers = ParseAnswers(text);
        var student = ParseStudent(text) ?? Path.GetFileNameWithoutExtension(file);
        return new ExtractedSubmission(Path.GetFileName(file), student, answers, "high", "RuleBased", false, new());
    }

    private static Task<ExtractedSubmission?> ExtractWithAiAsync(string file)
    {
        // Stubbed AI extraction point. Replace with actual AI/OCR client.
        return Task.FromResult<ExtractedSubmission?>(null);
    }

    private static Dictionary<string, string> ParseAnswers(string text)
    {
        var matches = Regex.Matches(text, @"(?m)^\s*(\d+)\s*[:\-]\s*([A-Za-z])\s*$");
        var result = new Dictionary<string, string>();
        foreach (Match m in matches)
        {
            result[m.Groups[1].Value] = m.Groups[2].Value.ToUpperInvariant();
        }
        return result;
    }

    private static string? ParseStudent(string text)
    {
        var m = Regex.Match(text, @"(?im)^\s*name\s*[:\-]\s*(.+)$");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static GradeRow Grade(AnswerKey key, JobSettings settings, ExtractedSubmission submission)
    {
        var total = key.Questions.Count;
        var correct = 0;
        var missed = new List<string>();
        var missing = new List<string>();
        var extra = submission.Answers.Keys.Where(k => !key.Questions.ContainsKey(k)).OrderBy(x => x).ToList();
        var warnings = new List<string>(submission.Warnings);

        foreach (var question in key.Questions.OrderBy(k => int.TryParse(k.Key, out var n) ? n : int.MaxValue))
        {
            if (!submission.Answers.TryGetValue(question.Key, out var given))
            {
                missing.Add(question.Key);
                missed.Add(question.Key);
                continue;
            }

            if (string.Equals(given, question.Value, StringComparison.OrdinalIgnoreCase)) correct++;
            else missed.Add(question.Key);
        }

        if (extra.Any()) warnings.Add($"Extra questions found: {string.Join(";", extra)}");
        if (missing.Any()) warnings.Add($"Missing questions: {string.Join(";", missing)}");

        var confidence = ConfidenceRank.Parse(submission.Confidence);
        var threshold = ConfidenceRank.Parse(settings.NeedsReviewWhenConfidenceBelow);

        var needsReview = confidence < threshold
            || warnings.Any()
            || missing.Any()
            || string.IsNullOrWhiteSpace(submission.StudentName)
            || !submission.Answers.Any()
            || (submission.ExtractorUsed == "AIRequired" && confidence != ConfidenceRank.High);

        var percent = total == 0 ? 0 : (double)correct / total * 100;

        return new GradeRow(
            key.AssignmentName,
            submission.StudentName,
            submission.SourceFileName,
            correct,
            total,
            percent,
            string.Join(";", missed),
            string.Join(";", missing),
            string.Join(";", extra),
            needsReview,
            string.Join(" | ", warnings.Distinct()),
            submission.ExtractorUsed,
            submission.Confidence);
    }

    private static AnswerKey LoadAnswerKey(string path) => ReadJson<AnswerKey>(path);

    private static JobSettings LoadOrCreateSettings(string path)
    {
        if (!File.Exists(path))
        {
            var settings = JobSettings.Default;
            WriteJson(path, settings);
            return settings;
        }
        return ReadJson<JobSettings>(path);
    }

    private static T ReadJson<T>(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions())
               ?? throw new InvalidOperationException($"Could not deserialize {path}");
    }

    private static void WriteJson<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions());
        File.WriteAllText(path, json);
    }

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "submission" : safe;
    }
}

internal sealed class JobContext
{
    public required string JobPath { get; init; }
    public required string AnswerKeyPath { get; init; }
    public required string SettingsPath { get; init; }
    public required string SubmissionsPath { get; init; }
    public required string ResultsPath { get; init; }
    public required string ExtractedJsonPath { get; init; }

    public static JobContext Create(string jobPath)
    {
        var full = Path.GetFullPath(jobPath);
        var answer = Path.Combine(full, "answer-key.json");
        var settings = Path.Combine(full, "settings.json");
        var submissions = Path.Combine(full, "submissions");
        var results = Path.Combine(full, "results");
        var extracted = Path.Combine(results, "extracted-json");

        if (!File.Exists(answer)) throw new InvalidOperationException($"Missing answer key: {answer}");
        if (!Directory.Exists(submissions)) throw new InvalidOperationException($"Missing submissions folder: {submissions}");
        Directory.CreateDirectory(results);
        Directory.CreateDirectory(extracted);

        return new JobContext
        {
            JobPath = full,
            AnswerKeyPath = answer,
            SettingsPath = settings,
            SubmissionsPath = submissions,
            ResultsPath = results,
            ExtractedJsonPath = extracted
        };
    }
}

internal enum ExtractorMode { RuleBased, Ai, Hybrid }

internal sealed record AnswerKey(string AssignmentName, Dictionary<string, string> Questions);
internal sealed record JobSettings(
    ExtractorMode ExtractorMode,
    List<string> ValidChoices,
    bool AllowBlankAnswers,
    bool NeedsReviewWhenAiUsed,
    string NeedsReviewWhenConfidenceBelow,
    string StudentNameFallback)
{
    public static JobSettings Default => new(ExtractorMode.Hybrid, new() { "A", "B", "C", "D" }, true, false, "high", "FileName");
}

internal sealed record ExtractedSubmission(
    string SourceFileName,
    string StudentName,
    Dictionary<string, string> Answers,
    string Confidence,
    string ExtractorUsed,
    bool UsedAi,
    List<string> Warnings);

internal sealed record GradeRow(
    string AssignmentName,
    string StudentName,
    string SourceFileName,
    int CorrectCount,
    int TotalQuestions,
    double Percent,
    string MissedQuestions,
    string MissingQuestions,
    string ExtraQuestions,
    bool NeedsReview,
    string Warnings,
    string ExtractorUsed,
    string Confidence);

internal sealed record JobRunSummary(
    string JobPath,
    string AssignmentName,
    int SubmissionCount,
    string ExtractorMode,
    bool UseCache,
    int GradedCount,
    int ReviewCount,
    string GradeReportPath,
    string ReviewReportPath,
    string ExtractionSummaryPath);

internal static class CsvWriter
{
    public static void WriteGradeReport(string path, List<GradeRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AssignmentName,StudentName,SourceFileName,CorrectCount,TotalQuestions,Percent,MissedQuestions,MissingQuestions,ExtraQuestions,NeedsReview,Warnings,ExtractorUsed,Confidence");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(',', Esc(r.AssignmentName), Esc(r.StudentName), Esc(r.SourceFileName), r.CorrectCount, r.TotalQuestions, r.Percent.ToString("F2"), Esc(r.MissedQuestions), Esc(r.MissingQuestions), Esc(r.ExtraQuestions), r.NeedsReview, Esc(r.Warnings), Esc(r.ExtractorUsed), Esc(r.Confidence)));
        }
        File.WriteAllText(path, sb.ToString());
    }

    public static void WriteReviewReport(string path, List<GradeRow> rows) => WriteGradeReport(path, rows);

    public static void WriteExtractionSummary(string path, List<ExtractedSubmission> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SourceFileName,StudentName,ExtractorUsed,UsedAi,AnswersFound,Confidence,Warnings");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(',', Esc(r.SourceFileName), Esc(r.StudentName), Esc(r.ExtractorUsed), r.UsedAi, r.Answers.Count, Esc(r.Confidence), Esc(string.Join(" | ", r.Warnings))));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static string Esc(string input)
    {
        var escaped = input.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}

internal enum ConfidenceRank { Low = 0, Medium = 1, High = 2 }
internal static class ConfidenceRankExtensions
{
    public static ConfidenceRank Parse(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "high" => ConfidenceRank.High,
            "medium" => ConfidenceRank.Medium,
            _ => ConfidenceRank.Low,
        };
    }
}
