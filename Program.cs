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
    var appSettings = AppSettingsLoader.Load("appsettings.json");
    var runner = new JobRunner();
    var summary = await runner.RunAsync(options, appSettings.Grading);

    Console.WriteLine();
    Console.WriteLine("=== Job Summary ===");
    Console.WriteLine($"Job path: {summary.RunConfiguration.JobPath ?? "(not set)"}");
    Console.WriteLine($"Answer key path: {summary.RunConfiguration.AnswerKeyPath}");
    Console.WriteLine($"Submissions path: {summary.RunConfiguration.SubmissionsPath}");
    Console.WriteLine($"Results path: {summary.RunConfiguration.ResultsPath}");
    Console.WriteLine($"Extracted JSON path: {summary.RunConfiguration.ExtractedJsonPath}");
    Console.WriteLine($"Assignment name: {summary.AssignmentName}");
    Console.WriteLine($"Submissions found: {summary.SubmissionCount}");
    Console.WriteLine($"Extractor mode: {summary.RunConfiguration.ExtractorMode}");
    Console.WriteLine($"Cache mode: {(summary.RunConfiguration.UseCache ? "enabled" : "disabled")}");
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
    string? JobPath,
    string? AnswerKeyPath,
    string? SubmissionsPath,
    string? ResultsPath,
    bool ExtractOnly,
    bool GradeOnly,
    bool UseCache,
    bool ForceReextract)
{
    public static ParseResult Parse(string[] args)
    {
        string? jobPath = null;
        string? answerKeyPath = null;
        string? submissionsPath = null;
        string? resultsPath = null;
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
                    if (i + 1 >= args.Length) return ParseResult.Fail("Missing value for --job");
                    jobPath = args[++i];
                    break;
                case "--answer-key":
                    if (i + 1 >= args.Length) return ParseResult.Fail("Missing value for --answer-key");
                    answerKeyPath = args[++i];
                    break;
                case "--submissions":
                    if (i + 1 >= args.Length) return ParseResult.Fail("Missing value for --submissions");
                    submissionsPath = args[++i];
                    break;
                case "--results":
                    if (i + 1 >= args.Length) return ParseResult.Fail("Missing value for --results");
                    resultsPath = args[++i];
                    break;
                case "--extract-only": extractOnly = true; break;
                case "--grade-only": gradeOnly = true; break;
                case "--use-cache": useCache = true; break;
                case "--force-reextract": forceReextract = true; break;
                default:
                    return ParseResult.Fail($"Unknown argument: {arg}");
            }
        }

        if (extractOnly && gradeOnly)
        {
            return ParseResult.Fail("--extract-only and --grade-only cannot be used together");
        }

        return ParseResult.Ok(new CliOptions(jobPath, answerKeyPath, submissionsPath, resultsPath, extractOnly, gradeOnly, useCache, forceReextract));
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run -- [--job <path>] [--answer-key <path>] [--submissions <path>] [--results <path>] [--extract-only] [--grade-only] [--use-cache] [--force-reextract]");
    }
}

internal sealed record ParseResult(bool Success, CliOptions? Options, string ErrorMessage)
{
    public static ParseResult Ok(CliOptions options) => new(true, options, string.Empty);
    public static ParseResult Fail(string errorMessage) => new(false, null, errorMessage);
}

public sealed class GradingOptions
{
    public string? DefaultJobPath { get; set; }
    public string? AnswerKeyPath { get; set; }
    public string? SubmissionsPath { get; set; }
    public string? ResultsPath { get; set; }
    public string ExtractorMode { get; set; } = "Hybrid";
    public bool UseCache { get; set; } = true;
}

public sealed class GradingRunConfiguration
{
    public string? JobPath { get; set; }
    public string AnswerKeyPath { get; set; } = "";
    public string SubmissionsPath { get; set; } = "";
    public string ResultsPath { get; set; } = "";
    public string ExtractedJsonPath { get; set; } = "";
    public string ExtractorMode { get; set; } = "Hybrid";
    public bool UseCache { get; set; }
}

internal sealed record AppSettings(GradingOptions Grading);

internal static class AppSettingsLoader
{
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings(new GradingOptions());

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var grading = new GradingOptions();

        if (doc.RootElement.TryGetProperty("Grading", out var g))
        {
            grading.DefaultJobPath = ReadString(g, "DefaultJobPath");
            grading.AnswerKeyPath = ReadString(g, "AnswerKeyPath");
            grading.SubmissionsPath = ReadString(g, "SubmissionsPath");
            grading.ResultsPath = ReadString(g, "ResultsPath");
            grading.ExtractorMode = ReadString(g, "ExtractorMode") ?? "Hybrid";
            grading.UseCache = ReadBool(g, "UseCache") ?? true;
        }

        return new AppSettings(grading);
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool? ReadBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && (p.ValueKind is JsonValueKind.True or JsonValueKind.False) ? p.GetBoolean() : null;
}

internal sealed class JobRunner
{
    public async Task<JobRunSummary> RunAsync(CliOptions options, GradingOptions gradingOptions)
    {
        var runConfig = JobContext.Resolve(options, gradingOptions);
        var answerKey = LoadAnswerKey(runConfig.AnswerKeyPath);
        var settingsPath = runConfig.JobPath is null ? Path.Combine(runConfig.ResultsPath, "settings.json") : Path.Combine(runConfig.JobPath, "settings.json");
        var settings = LoadOrCreateSettings(settingsPath, runConfig.ExtractorMode);

        Console.WriteLine($"Job path: {runConfig.JobPath ?? "(not set)"}");
        Console.WriteLine($"Answer key path: {runConfig.AnswerKeyPath}");
        Console.WriteLine($"Submissions path: {runConfig.SubmissionsPath}");
        Console.WriteLine($"Results path: {runConfig.ResultsPath}");
        Console.WriteLine($"Extracted JSON path: {runConfig.ExtractedJsonPath}");
        Console.WriteLine($"Extractor mode: {settings.ExtractorMode}");
        Console.WriteLine($"Cache mode: {(runConfig.UseCache ? "enabled" : "disabled")}");
        Console.WriteLine($"Assignment name: {answerKey.AssignmentName}");

        var submissions = Directory.GetFiles(runConfig.SubmissionsPath).Where(IsSupportedInput).OrderBy(Path.GetFileName).ToList();
        Console.WriteLine($"Submissions found: {submissions.Count}");

        var extracted = new List<ExtractedSubmission>();
        if (!options.GradeOnly)
        {
            foreach (var file in submissions)
            {
                var outputPath = Path.Combine(runConfig.ExtractedJsonPath, SafeFileName(Path.GetFileNameWithoutExtension(file)) + ".json");
                if (runConfig.UseCache && !options.ForceReextract && File.Exists(outputPath))
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
            extracted = Directory.GetFiles(runConfig.ExtractedJsonPath, "*.json").OrderBy(Path.GetFileName).Select(ReadJson<ExtractedSubmission>).ToList();
        }

        var extractionSummaryPath = Path.Combine(runConfig.ResultsPath, "extraction-summary.csv");
        CsvWriter.WriteExtractionSummary(extractionSummaryPath, extracted);

        var gradePath = Path.Combine(runConfig.ResultsPath, "grade-report.csv");
        var reviewPath = Path.Combine(runConfig.ResultsPath, "review-needed.csv");

        var gradeRows = new List<GradeRow>();
        if (!options.ExtractOnly)
        {
            gradeRows = extracted.Select(e => Grade(answerKey, settings, e)).ToList();
            CsvWriter.WriteGradeReport(gradePath, gradeRows);
            CsvWriter.WriteReviewReport(reviewPath, gradeRows.Where(x => x.NeedsReview).ToList());
        }

        return new JobRunSummary(runConfig, answerKey.AssignmentName, submissions.Count, gradeRows.Count, gradeRows.Count(x => x.NeedsReview), gradePath, reviewPath, extractionSummaryPath);
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
            if (ext is ".docx" or ".pdf") rule = ExtractRuleBased(file);
        }

        var needsAiByType = ext is ".jpg" or ".jpeg" or ".png";
        var fewerThanExpected = rule != null && rule.Answers.Count < key.Questions.Count;

        if (settings.ExtractorMode is ExtractorMode.Ai or ExtractorMode.Hybrid)
        {
            if (needsAiByType || rule == null || fewerThanExpected) ai = await ExtractWithAiAsync(file);
        }

        if (ai != null) return ai with { Warnings = warnings.Concat(ai.Warnings).Distinct().ToList() };

        if (rule != null)
        {
            if (needsAiByType || fewerThanExpected) warnings.Add("AI fallback failed; using rule-based extraction.");
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

    private static Task<ExtractedSubmission?> ExtractWithAiAsync(string file) => Task.FromResult<ExtractedSubmission?>(null);

    private static Dictionary<string, string> ParseAnswers(string text)
    {
        var matches = Regex.Matches(text, @"(?m)^\s*(\d+)\s*[:\-]\s*([A-Za-z])\s*$");
        var result = new Dictionary<string, string>();
        foreach (Match m in matches) result[m.Groups[1].Value] = m.Groups[2].Value.ToUpperInvariant();
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

        var confidence = ConfidenceRankExtensions.Parse(submission.Confidence);
        var threshold = ConfidenceRankExtensions.Parse(settings.NeedsReviewWhenConfidenceBelow);

        var needsReview = confidence < threshold
            || warnings.Any()
            || missing.Any()
            || string.IsNullOrWhiteSpace(submission.StudentName)
            || !submission.Answers.Any()
            || (submission.ExtractorUsed == "AIRequired" && confidence != ConfidenceRank.High);

        var percent = total == 0 ? 0 : (double)correct / total * 100;
        return new GradeRow(key.AssignmentName, submission.StudentName, submission.SourceFileName, correct, total, percent,
            string.Join(";", missed), string.Join(";", missing), string.Join(";", extra), needsReview,
            string.Join(" | ", warnings.Distinct()), submission.ExtractorUsed, submission.Confidence);
    }

    private static AnswerKey LoadAnswerKey(string path) => ReadJson<AnswerKey>(path);

    private static JobSettings LoadOrCreateSettings(string path, string extractorMode)
    {
        if (!File.Exists(path))
        {
            var settings = JobSettings.WithExtractorMode(extractorMode);
            WriteJson(path, settings);
            return settings;
        }
        return ReadJson<JobSettings>(path);
    }

    private static T ReadJson<T>(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
               ?? throw new InvalidOperationException($"Could not deserialize {path}");
    }

    private static void WriteJson<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(path, json);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "submission" : safe;
    }
}

internal static class JobContext
{
    public static GradingRunConfiguration Resolve(CliOptions cli, GradingOptions config)
    {
        var jobPath = !string.IsNullOrWhiteSpace(cli.JobPath) ? ResolvePath(cli.JobPath!) : (!string.IsNullOrWhiteSpace(config.DefaultJobPath) ? ResolvePath(config.DefaultJobPath!) : null);

        var answerDefault = jobPath is null ? null : Path.Combine(jobPath, "answer-key.json");
        var submissionsDefault = jobPath is null ? null : Path.Combine(jobPath, "submissions");
        var resultsDefault = jobPath is null ? null : Path.Combine(jobPath, "results");

        var answerKeyPath = FirstPath(cli.AnswerKeyPath, config.AnswerKeyPath, answerDefault);
        var submissionsPath = FirstPath(cli.SubmissionsPath, config.SubmissionsPath, submissionsDefault);
        var resultsPath = FirstPath(cli.ResultsPath, config.ResultsPath, resultsDefault);

        if (string.IsNullOrWhiteSpace(answerKeyPath)) throw new InvalidOperationException("Answer key path is required. Provide --answer-key, Grading:AnswerKeyPath, or a job path/default job path.");
        if (string.IsNullOrWhiteSpace(submissionsPath)) throw new InvalidOperationException("Submissions path is required. Provide --submissions, Grading:SubmissionsPath, or a job path/default job path.");
        if (string.IsNullOrWhiteSpace(resultsPath)) throw new InvalidOperationException("Results path is required. Provide --results, Grading:ResultsPath, or a job path/default job path.");

        if (!File.Exists(answerKeyPath)) throw new InvalidOperationException($"Answer key file not found: {answerKeyPath}");
        if (!Directory.Exists(submissionsPath)) throw new InvalidOperationException($"Submissions folder not found: {submissionsPath}");

        Directory.CreateDirectory(resultsPath);
        var extractedJsonPath = Path.Combine(resultsPath, "extracted-json");
        Directory.CreateDirectory(extractedJsonPath);

        return new GradingRunConfiguration
        {
            JobPath = jobPath,
            AnswerKeyPath = answerKeyPath,
            SubmissionsPath = submissionsPath,
            ResultsPath = resultsPath,
            ExtractedJsonPath = extractedJsonPath,
            ExtractorMode = config.ExtractorMode,
            UseCache = cli.UseCache || (!cli.ForceReextract && config.UseCache)
        };
    }

    private static string? FirstPath(params string?[] candidates)
    {
        var value = candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return value is null ? null : ResolvePath(value);
    }

    private static string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
}

internal enum ExtractorMode { RuleBased, Ai, Hybrid }
internal sealed record AnswerKey(string AssignmentName, Dictionary<string, string> Questions);
internal sealed record JobSettings(ExtractorMode ExtractorMode, List<string> ValidChoices, bool AllowBlankAnswers, bool NeedsReviewWhenAiUsed, string NeedsReviewWhenConfidenceBelow, string StudentNameFallback)
{
    public static JobSettings WithExtractorMode(string mode)
    {
        _ = Enum.TryParse<ExtractorMode>(mode, true, out var parsed);
        if (!Enum.IsDefined(parsed)) parsed = ExtractorMode.Hybrid;
        return new JobSettings(parsed, new() { "A", "B", "C", "D" }, true, false, "high", "FileName");
    }
}

internal sealed record ExtractedSubmission(string SourceFileName, string StudentName, Dictionary<string, string> Answers, string Confidence, string ExtractorUsed, bool UsedAi, List<string> Warnings);
internal sealed record GradeRow(string AssignmentName, string StudentName, string SourceFileName, int CorrectCount, int TotalQuestions, double Percent, string MissedQuestions, string MissingQuestions, string ExtraQuestions, bool NeedsReview, string Warnings, string ExtractorUsed, string Confidence);
internal sealed record JobRunSummary(GradingRunConfiguration RunConfiguration, string AssignmentName, int SubmissionCount, int GradedCount, int ReviewCount, string GradeReportPath, string ReviewReportPath, string ExtractionSummaryPath);

internal static class CsvWriter
{
    public static void WriteGradeReport(string path, List<GradeRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AssignmentName,StudentName,SourceFileName,CorrectCount,TotalQuestions,Percent,MissedQuestions,MissingQuestions,ExtraQuestions,NeedsReview,Warnings,ExtractorUsed,Confidence");
        foreach (var r in rows) sb.AppendLine(string.Join(',', Esc(r.AssignmentName), Esc(r.StudentName), Esc(r.SourceFileName), r.CorrectCount, r.TotalQuestions, r.Percent.ToString("F2"), Esc(r.MissedQuestions), Esc(r.MissingQuestions), Esc(r.ExtraQuestions), r.NeedsReview, Esc(r.Warnings), Esc(r.ExtractorUsed), Esc(r.Confidence)));
        File.WriteAllText(path, sb.ToString());
    }

    public static void WriteReviewReport(string path, List<GradeRow> rows) => WriteGradeReport(path, rows);

    public static void WriteExtractionSummary(string path, List<ExtractedSubmission> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SourceFileName,StudentName,ExtractorUsed,UsedAi,AnswersFound,Confidence,Warnings");
        foreach (var r in rows) sb.AppendLine(string.Join(',', Esc(r.SourceFileName), Esc(r.StudentName), Esc(r.ExtractorUsed), r.UsedAi, r.Answers.Count, Esc(r.Confidence), Esc(string.Join(" | ", r.Warnings))));
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
    public static ConfidenceRank Parse(string value) => value.ToLowerInvariant() switch { "high" => ConfidenceRank.High, "medium" => ConfidenceRank.Medium, _ => ConfidenceRank.Low };
}
