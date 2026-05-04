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

internal sealed record CliOptions(string? JobPath, string? AnswerKeyPath, string? SubmissionsPath, string? ResultsPath, bool ExtractOnly, bool GradeOnly, bool UseCache, bool ForceReextract)
{
    public static ParseResult Parse(string[] args)
    {
        string? jobPath = null, answerKeyPath = null, submissionsPath = null, resultsPath = null;
        bool extractOnly = false, gradeOnly = false, useCache = false, forceReextract = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--job": jobPath = args[++i]; break;
                case "--answer-key": answerKeyPath = args[++i]; break;
                case "--submissions": submissionsPath = args[++i]; break;
                case "--results": resultsPath = args[++i]; break;
                case "--extract-only": extractOnly = true; break;
                case "--grade-only": gradeOnly = true; break;
                case "--use-cache": useCache = true; break;
                case "--force-reextract": forceReextract = true; break;
                default: return ParseResult.Fail($"Unknown argument: {args[i]}");
            }
        }

        if (extractOnly && gradeOnly) return ParseResult.Fail("--extract-only and --grade-only cannot be used together");
        return ParseResult.Ok(new CliOptions(jobPath, answerKeyPath, submissionsPath, resultsPath, extractOnly, gradeOnly, useCache, forceReextract));
    }

    public static void PrintUsage() => Console.WriteLine("Usage: dotnet run -- [--job <path>] [--answer-key <path>] [--submissions <path>] [--results <path>] [--extract-only] [--grade-only] [--use-cache] [--force-reextract]");
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
    public List<string> SupportedExtensions { get; set; } = new() { ".pdf", ".docx", ".doc", ".png", ".jpg", ".jpeg", ".heic" };
    public bool EnableOcr { get; set; } = true;
    public string OcrLanguage { get; set; } = "eng";
    public bool UseCache { get; set; } = true;
    public string? QuizId { get; set; }
}

public sealed class GradingRunConfiguration
{
    public string? JobPath { get; set; }
    public string AnswerKeyPath { get; set; } = "";
    public string SubmissionsPath { get; set; } = "";
    public string ResultsPath { get; set; } = "";
    public string ExtractedJsonPath { get; set; } = "";
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
            grading.UseCache = ReadBool(g, "UseCache") ?? true;
            grading.QuizId = ReadString(g, "QuizId");
            grading.EnableOcr = ReadBool(g, "EnableOcr") ?? true;
            grading.OcrLanguage = ReadString(g, "OcrLanguage") ?? "eng";
            if (g.TryGetProperty("SupportedExtensions", out var exts) && exts.ValueKind == JsonValueKind.Array)
            {
                grading.SupportedExtensions = exts.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }
        }
        return new AppSettings(grading);
    }

    private static string? ReadString(JsonElement element, string name) => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static bool? ReadBool(JsonElement element, string name) => element.TryGetProperty(name, out var p) && (p.ValueKind is JsonValueKind.True or JsonValueKind.False) ? p.GetBoolean() : null;
}

public sealed class SubmissionContent { public string FilePath { get; set; } = ""; public string FileName { get; set; } = ""; public string FileType { get; set; } = ""; public string ExtractedText { get; set; } = ""; public List<string> ExtractedImagePaths { get; set; } = new(); public List<string> Warnings { get; set; } = new(); public string ExtractionMethod { get; set; } = "Failed"; }
public interface ISubmissionContentExtractor { bool CanHandle(string filePath); Task<SubmissionContent> ExtractAsync(string filePath, CancellationToken cancellationToken = default); }
public interface IOcrService { Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken = default); }
public sealed class StubOcrService(bool enableOcr) : IOcrService { public Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken = default) => Task.FromResult(enableOcr ? string.Empty : string.Empty); }

public sealed class ExtractorRouter
{
    private readonly IReadOnlyList<ISubmissionContentExtractor> _extractors;
    public ExtractorRouter(IEnumerable<ISubmissionContentExtractor> extractors) => _extractors = extractors.ToList();
    public ISubmissionContentExtractor? Resolve(string path) => _extractors.FirstOrDefault(x => x.CanHandle(path));
}

public sealed class PdfSubmissionContentExtractor(IOcrService ocr, bool enableOcr) : ISubmissionContentExtractor
{
    public bool CanHandle(string filePath) => string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);
    public async Task<SubmissionContent> ExtractAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var content = Base(filePath, "pdf", "DirectText");
        content.Warnings.Add("Direct PDF text extraction library is not configured in this build.");
        if (enableOcr)
        {
            content.ExtractionMethod = "PdfOcr";
            content.Warnings.Add("PDF OCR fallback requested, but PDF rendering is unavailable; manual review recommended.");
            content.ExtractedText = await ocr.ExtractTextAsync(filePath, cancellationToken);
        }
        return content;
    }
    private static SubmissionContent Base(string filePath, string type, string method) => new() { FilePath = filePath, FileName = Path.GetFileName(filePath), FileType = type, ExtractionMethod = method };
}
public sealed class WordSubmissionContentExtractor : ISubmissionContentExtractor
{
    public bool CanHandle(string filePath) => new[] { ".docx", ".doc" }.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);
    public Task<SubmissionContent> ExtractAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var result = new SubmissionContent { FilePath = filePath, FileName = Path.GetFileName(filePath), FileType = ext.TrimStart('.'), ExtractionMethod = "WordText" };
        if (ext == ".doc") result.Warnings.Add(".doc extraction is not supported in this build; please convert to .docx or PDF.");
        else result.Warnings.Add(".docx text extraction library is not configured in this build.");
        return Task.FromResult(result);
    }
}
public sealed class ImageSubmissionContentExtractor(IOcrService ocr, bool enableOcr) : ISubmissionContentExtractor
{
    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };
    public bool CanHandle(string filePath) => Exts.Contains(Path.GetExtension(filePath));
    public async Task<SubmissionContent> ExtractAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var result = new SubmissionContent { FilePath = filePath, FileName = Path.GetFileName(filePath), FileType = Path.GetExtension(filePath).TrimStart('.'), ExtractionMethod = "ImageOcr" };
        if (!enableOcr) result.Warnings.Add("OCR disabled; no text extracted from image.");
        else result.ExtractedText = await ocr.ExtractTextAsync(filePath, cancellationToken);
        return result;
    }
}
public sealed class HeicSubmissionContentExtractor(IOcrService ocr, bool enableOcr) : ISubmissionContentExtractor
{
    public bool CanHandle(string filePath) => string.Equals(Path.GetExtension(filePath), ".heic", StringComparison.OrdinalIgnoreCase);
    public async Task<SubmissionContent> ExtractAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var result = new SubmissionContent { FilePath = filePath, FileName = Path.GetFileName(filePath), FileType = "heic", ExtractionMethod = "HeicConvertedOcr" };
        result.Warnings.Add("HEIC conversion is platform-dependent and not available in this build; manual review recommended.");
        if (enableOcr) result.ExtractedText = await ocr.ExtractTextAsync(filePath, cancellationToken);
        return result;
    }
}

internal sealed class JobRunner
{
    public async Task<JobRunSummary> RunAsync(CliOptions options, GradingOptions gradingOptions)
    {
        var runConfig = JobContext.Resolve(options, gradingOptions);
        var answerKey = AnswerKeyLoader.Load(runConfig.AnswerKeyPath, gradingOptions.QuizId);
        var submissions = Directory.GetFiles(runConfig.SubmissionsPath).OrderBy(Path.GetFileName).ToList();

        var ocr = new StubOcrService(gradingOptions.EnableOcr);
        var router = new ExtractorRouter(new ISubmissionContentExtractor[] { new PdfSubmissionContentExtractor(ocr, gradingOptions.EnableOcr), new WordSubmissionContentExtractor(), new ImageSubmissionContentExtractor(ocr, gradingOptions.EnableOcr), new HeicSubmissionContentExtractor(ocr, gradingOptions.EnableOcr) });
        var supported = new HashSet<string>(gradingOptions.SupportedExtensions.Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : $".{x.ToLowerInvariant()}"));

        var extracted = new List<ExtractedSubmission>();
        foreach (var file in submissions)
        {
            Console.WriteLine($"File discovered: {Path.GetFileName(file)}");
            try
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!supported.Contains(ext))
                {
                    Console.WriteLine($"Warning: Unsupported file skipped: {file}");
                    extracted.Add(new ExtractedSubmission(Path.GetFileName(file), "", new(), "low", "Unsupported", false, new() { "Unsupported file type." }, true, "Unsupported file type."));
                    continue;
                }

                var extractor = router.Resolve(file);
                if (extractor is null)
                {
                    extracted.Add(new ExtractedSubmission(Path.GetFileName(file), "", new(), "low", "Unsupported", false, new() { "No extractor available." }, true, "No extractor available."));
                    continue;
                }

                Console.WriteLine($"Extractor selected: {extractor.GetType().Name}");
                var content = await extractor.ExtractAsync(file);
                var answers = ParseAnswers(content.ExtractedText);
                var student = ParseStudent(content.ExtractedText) ?? Path.GetFileNameWithoutExtension(file);
                var manualReview = string.IsNullOrWhiteSpace(content.ExtractedText) || content.ExtractedText.Trim().Length < 3;
                var reason = manualReview ? "No readable text could be extracted from submission." : "";
                if (manualReview) Console.WriteLine($"Submission marked for manual review: {content.FileName}");
                extracted.Add(new ExtractedSubmission(content.FileName, student, answers, manualReview ? "low" : "medium", content.ExtractionMethod, content.ExtractionMethod.Contains("Ocr"), content.Warnings, manualReview, reason));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing {file}: {ex.Message}");
                extracted.Add(new ExtractedSubmission(Path.GetFileName(file), "", new(), "low", "Failed", false, new() { ex.Message }, true, "Failed to process submission."));
            }
        }

        var gradeRows = extracted.Select(e => Grade(answerKey, e)).ToList();
        var extractionSummaryPath = Path.Combine(runConfig.ResultsPath, "extraction-summary.csv");
        var gradePath = Path.Combine(runConfig.ResultsPath, "grade-report.csv");
        var reviewPath = Path.Combine(runConfig.ResultsPath, "review-needed.csv");
        CsvWriter.WriteExtractionSummary(extractionSummaryPath, extracted);
        CsvWriter.WriteGradeReport(gradePath, gradeRows);
        CsvWriter.WriteReviewReport(reviewPath, gradeRows.Where(x => x.ManualReviewRequired).ToList());

        return new JobRunSummary(runConfig, answerKey.AssignmentName, submissions.Count, gradeRows.Count, gradeRows.Count(x => x.ManualReviewRequired), gradePath, reviewPath, extractionSummaryPath);
    }

    private static GradeRow Grade(AnswerKey key, ExtractedSubmission sub)
    {
        if (sub.ManualReviewRequired) return new GradeRow(key.AssignmentName, sub.StudentName, sub.SourceFileName, 0, key.Questions.Count, 0, "ManualReview", true, sub.ManualReviewReason, string.Join(" | ", sub.Warnings), sub.ExtractorUsed, sub.Confidence, sub.FileType);
        var correct = key.Questions.Count(q => sub.Answers.TryGetValue(q.Key, out var a) && string.Equals(a, q.Value, StringComparison.OrdinalIgnoreCase));
        var percent = key.Questions.Count == 0 ? 0 : (double)correct / key.Questions.Count * 100;
        return new GradeRow(key.AssignmentName, sub.StudentName, sub.SourceFileName, correct, key.Questions.Count, percent, "Graded", false, "", string.Join(" | ", sub.Warnings), sub.ExtractorUsed, sub.Confidence, sub.FileType);
    }

    private static Dictionary<string, string> ParseAnswers(string text)
    {
        var matches = Regex.Matches(text ?? "", @"(?m)^\s*(\d+)\s*[:\-]\s*([A-Za-z])\s*$");
        return matches.ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());
    }
    private static string? ParseStudent(string text) { var m = Regex.Match(text ?? "", @"(?im)^\s*name\s*[:\-]\s*(.+)$"); return m.Success ? m.Groups[1].Value.Trim() : null; }
}

internal static class JobContext
{
    public static GradingRunConfiguration Resolve(CliOptions cli, GradingOptions config)
    {
        var jobPath = !string.IsNullOrWhiteSpace(cli.JobPath) ? ResolvePath(cli.JobPath!) : (!string.IsNullOrWhiteSpace(config.DefaultJobPath) ? ResolvePath(config.DefaultJobPath!) : null);
        var answerKeyPath = FirstPath(cli.AnswerKeyPath, config.AnswerKeyPath, jobPath is null ? null : Path.Combine(jobPath, "answer-key.json")) ?? throw new InvalidOperationException("Answer key path required.");
        var submissionsPath = FirstPath(cli.SubmissionsPath, config.SubmissionsPath, jobPath is null ? null : Path.Combine(jobPath, "submissions")) ?? throw new InvalidOperationException("Submissions path required.");
        var resultsPath = FirstPath(cli.ResultsPath, config.ResultsPath, jobPath is null ? null : Path.Combine(jobPath, "results")) ?? throw new InvalidOperationException("Results path required.");
        if (!File.Exists(answerKeyPath)) throw new InvalidOperationException($"Answer key file not found: {answerKeyPath}");
        if (!Directory.Exists(submissionsPath)) throw new InvalidOperationException($"Submissions folder not found: {submissionsPath}");
        Directory.CreateDirectory(resultsPath);
        var extractedJsonPath = Path.Combine(resultsPath, "extracted-json");
        Directory.CreateDirectory(extractedJsonPath);
        return new GradingRunConfiguration { JobPath = jobPath, AnswerKeyPath = answerKeyPath, SubmissionsPath = submissionsPath, ResultsPath = resultsPath, ExtractedJsonPath = extractedJsonPath, UseCache = cli.UseCache || (!cli.ForceReextract && config.UseCache) };
    }
    private static string? FirstPath(params string?[] candidates) => candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) is { } v ? ResolvePath(v) : null;
    private static string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
}

internal sealed record AnswerKey(string AssignmentName, Dictionary<string, string> Questions);
public sealed class AnswerKeyFile
{
    public List<QuizAnswerKey> AnswerKeys { get; set; } = new();
}

public sealed class QuizAnswerKey
{
    public string QuizId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Source { get; set; }
    public string? Section { get; set; }
    public string? Chapter { get; set; }
    public List<AnswerKeyItem> Answers { get; set; } = new();
}

public sealed class AnswerKeyItem
{
    public int QuestionNumber { get; set; }
    public string CorrectChoice { get; set; } = "";
    public string? AnswerText { get; set; }
}

internal static class AnswerKeyLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static AnswerKey Load(string path, string? configuredQuizId)
    {
        var json = File.ReadAllText(path);
        try
        {
            if (TryLoadLegacy(json, out var legacy)) return legacy!;

            var quizzes = LoadQuizzes(json);
            ValidateAndNormalize(quizzes);
            var selected = SelectQuiz(quizzes, configuredQuizId);
            return new AnswerKey(string.IsNullOrWhiteSpace(selected.Title) ? selected.QuizId : selected.Title, selected.Answers.ToDictionary(a => a.QuestionNumber.ToString(), a => a.CorrectChoice));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid answer key JSON in '{path}': {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Invalid answer key file '{path}': {ex.Message}", ex);
        }
    }

    private static bool TryLoadLegacy(string json, out AnswerKey? answerKey)
    {
        answerKey = JsonSerializer.Deserialize<AnswerKey>(json, JsonOptions);
        return answerKey is not null && !string.IsNullOrWhiteSpace(answerKey.AssignmentName) && answerKey.Questions is not null && answerKey.Questions.Count > 0;
    }

    private static List<QuizAnswerKey> LoadQuizzes(string json)
    {
        var combined = JsonSerializer.Deserialize<AnswerKeyFile>(json, JsonOptions);
        if (combined?.AnswerKeys?.Count > 0) return combined.AnswerKeys;

        var single = JsonSerializer.Deserialize<QuizAnswerKey>(json, JsonOptions);
        if (single is not null && (!string.IsNullOrWhiteSpace(single.QuizId) || single.Answers.Count > 0)) return new List<QuizAnswerKey> { single };

        throw new InvalidOperationException("Could not parse answer key as legacy format, single quiz format, or combined answerKeys format.");
    }

    private static void ValidateAndNormalize(List<QuizAnswerKey> quizzes)
    {
        if (quizzes.Count == 0) throw new InvalidOperationException("No quizzes were found in the answer key file.");
        foreach (var quiz in quizzes)
        {
            if (string.IsNullOrWhiteSpace(quiz.QuizId)) throw new InvalidOperationException("Each quiz must include quizId.");
            if (quiz.Answers.Count == 0) throw new InvalidOperationException($"Quiz '{quiz.QuizId}' has no answers.");
            foreach (var answer in quiz.Answers)
            {
                if (answer.QuestionNumber <= 0) throw new InvalidOperationException($"Quiz '{quiz.QuizId}' has an invalid questionNumber '{answer.QuestionNumber}'.");
                if (string.IsNullOrWhiteSpace(answer.CorrectChoice)) throw new InvalidOperationException($"Quiz '{quiz.QuizId}' question {answer.QuestionNumber} is missing correctChoice.");
                answer.CorrectChoice = answer.CorrectChoice.Trim().ToLowerInvariant();
            }
        }
    }

    private static QuizAnswerKey SelectQuiz(List<QuizAnswerKey> quizzes, string? configuredQuizId)
    {
        if (quizzes.Count == 1) return quizzes[0];
        var available = string.Join(Environment.NewLine, quizzes.Select(x => $"- {x.QuizId}"));
        if (string.IsNullOrWhiteSpace(configuredQuizId))
            throw new InvalidOperationException($"Multiple answer keys were found. Please set Grading:QuizId in appsettings.json.{Environment.NewLine}Available quizIds:{Environment.NewLine}{available}");
        var selected = quizzes.FirstOrDefault(x => string.Equals(x.QuizId, configuredQuizId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            throw new InvalidOperationException($"Grading:QuizId '{configuredQuizId}' was not found.{Environment.NewLine}Available quizIds:{Environment.NewLine}{available}");
        return selected;
    }
}
internal sealed record ExtractedSubmission(string SourceFileName, string StudentName, Dictionary<string, string> Answers, string Confidence, string ExtractorUsed, bool UsedAi, List<string> Warnings, bool ManualReviewRequired, string ManualReviewReason)
{ public string FileType => Path.GetExtension(SourceFileName).TrimStart('.').ToLowerInvariant(); }
internal sealed record GradeRow(string AssignmentName, string StudentName, string SourceFileName, int CorrectCount, int TotalQuestions, double Percent, string GradingStatus, bool ManualReviewRequired, string ManualReviewReason, string Warnings, string ExtractionMethod, string Confidence, string FileType);
internal sealed record JobRunSummary(GradingRunConfiguration RunConfiguration, string AssignmentName, int SubmissionCount, int GradedCount, int ReviewCount, string GradeReportPath, string ReviewReportPath, string ExtractionSummaryPath);

internal static class CsvWriter
{
    public static void WriteGradeReport(string path, List<GradeRow> rows)
    {
        var sb = new StringBuilder("FileName,FileType,GradingStatus,Score,ManualReviewRequired,ManualReviewReason,Warnings,ExtractionMethod\n");
        foreach (var r in rows) sb.AppendLine(string.Join(',', Esc(r.SourceFileName), Esc(r.FileType), Esc(r.GradingStatus), r.Percent.ToString("F2"), r.ManualReviewRequired, Esc(r.ManualReviewReason), Esc(r.Warnings), Esc(r.ExtractionMethod)));
        File.WriteAllText(path, sb.ToString());
    }
    public static void WriteReviewReport(string path, List<GradeRow> rows) => WriteGradeReport(path, rows);
    public static void WriteExtractionSummary(string path, List<ExtractedSubmission> rows)
    {
        var sb = new StringBuilder("SourceFileName,ExtractorUsed,ManualReviewRequired,Warnings\n");
        foreach (var r in rows) sb.AppendLine(string.Join(',', Esc(r.SourceFileName), Esc(r.ExtractorUsed), r.ManualReviewRequired, Esc(string.Join(" | ", r.Warnings))));
        File.WriteAllText(path, sb.ToString());
    }
    private static string Esc(string s) => $"\"{(s ?? "").Replace("\"", "\"\"")}\"";
}
