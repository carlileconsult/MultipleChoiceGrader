using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

FatalErrorLogger.InitializeFallbackLogRoot(Path.Combine(AppContext.BaseDirectory, "logs"));
AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    if (eventArgs.ExceptionObject is Exception ex)
    {
        FatalErrorLogger.WriteUnhandled("AppDomain.CurrentDomain.UnhandledException", ex);
    }
};
TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
{
    FatalErrorLogger.WriteUnhandled("TaskScheduler.UnobservedTaskException", eventArgs.Exception);
    eventArgs.SetObserved();
};

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
    var runConfig = JobContext.Resolve(options, appSettings.Grading, appSettings.OpenAI);
    FatalErrorLogger.SetPreferredOutputRoot(runConfig.ResultsPath);

    DiagnosticsPrinter.PrintRuntimeConfiguration(runConfig, appSettings.Diagnostics);
    var runner = new JobRunner();
    var summary = await runner.RunAsync(runConfig, appSettings.Grading, appSettings.OpenAI, appSettings.Diagnostics);

    Console.WriteLine();
    Console.WriteLine("=== Job Summary ===");
    Console.WriteLine($"Processed: {summary.Processed}");
    Console.WriteLine($"Graded: {summary.Graded}");
    Console.WriteLine($"Manual review: {summary.ManualReview}");
    Console.WriteLine($"OpenAI extraction failures: {summary.OpenAiExtractionFailures}");
    Console.WriteLine($"Output report: {summary.GradeReportPath}");
    Console.WriteLine($"Extracted answers folder: {summary.ExtractedAnswersFolder}");

    return 0;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("========================================");
    Console.Error.WriteLine("FATAL ERROR");
    Console.Error.WriteLine("========================================");
    Console.Error.WriteLine(ex.ToString());
    var fatalPath = FatalErrorLogger.WriteFatal(ex);
    Console.Error.WriteLine("========================================");
    Console.Error.WriteLine($"Fatal diagnostics written to: {fatalPath}");
    Console.ResetColor();

    var diagnostics = AppSettingsLoader.TryLoadDiagnostics("appsettings.json");
    if (diagnostics.PauseOnFatalError)
    {
        Console.Error.WriteLine("Press Enter to exit...");
        Console.ReadLine();
    }

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

    public static void PrintUsage() => Console.WriteLine("Usage: dotnet run -- [--job <path>] [--answer-key <path>] [--submissions <path>] [--results <path>]");
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
    public string? SingleFilePath { get; set; }
    public string ExtractionMode { get; set; } = "Local";
    public decimal ManualReviewConfidenceThreshold { get; set; } = 0.70m;
    public string? QuizId { get; set; }
}
public sealed class OpenAiOptions { public string? ApiKey { get; set; } public string Model { get; set; } = "gpt-4.1-mini"; }
public sealed class DiagnosticsOptions { public bool Verbose { get; set; } = true; public bool PauseOnFatalError { get; set; } }
internal sealed record AppSettings(GradingOptions Grading, OpenAiOptions OpenAI, DiagnosticsOptions Diagnostics);

internal static class AppSettingsLoader
{
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings(new GradingOptions(), new OpenAiOptions(), new DiagnosticsOptions());
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var g = new GradingOptions();
        var o = new OpenAiOptions();
        if (doc.RootElement.TryGetProperty("Grading", out var ge))
        {
            g.DefaultJobPath = ReadString(ge, "DefaultJobPath");
            g.AnswerKeyPath = ReadString(ge, "AnswerKeyPath");
            g.SubmissionsPath = ReadString(ge, "SubmissionsPath") ?? ReadString(ge, "SubmissionsFolder");
            g.ResultsPath = ReadString(ge, "ResultsPath") ?? ReadString(ge, "OutputFolder");
            g.QuizId = ReadString(ge, "QuizId");
            g.SingleFilePath = ReadString(ge, "SingleFilePath");
            g.ExtractionMode = ReadString(ge, "ExtractionMode") ?? "Local";
            g.ManualReviewConfidenceThreshold = ReadDecimal(ge, "ManualReviewConfidenceThreshold") ?? 0.70m;
        }
        if (doc.RootElement.TryGetProperty("OpenAI", out var oe))
        {
            o.ApiKey = ReadString(oe, "ApiKey");
            o.Model = ReadString(oe, "Model") ?? "gpt-4.1-mini";
        }
        var d = new DiagnosticsOptions();
        if (doc.RootElement.TryGetProperty("Diagnostics", out var de))
        {
            d.Verbose = ReadBool(de, "Verbose") ?? true;
            d.PauseOnFatalError = ReadBool(de, "PauseOnFatalError") ?? false;
        }
        return new AppSettings(g, o, d);
    }
    public static DiagnosticsOptions TryLoadDiagnostics(string path)
    {
        try { return Load(path).Diagnostics; }
        catch { return new DiagnosticsOptions(); }
    }
    private static string? ReadString(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static decimal? ReadDecimal(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.TryGetDecimal(out var d) ? d : null;
    private static bool? ReadBool(JsonElement e, string n) => e.TryGetProperty(n, out var p) && (p.ValueKind is JsonValueKind.True or JsonValueKind.False) ? p.GetBoolean() : null;
}

public interface IAnswerExtractionService { Task<ExtractedSubmissionAnswers> ExtractAnswersAsync(string filePath, QuizAnswerKey answerKey, CancellationToken cancellationToken = default); }
public sealed class OpenAiAnswerExtractionService(OpenAiOptions options, HttpClient? httpClient = null) : IAnswerExtractionService
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();
    public async Task<ExtractedSubmissionAnswers> ExtractAnswersAsync(string filePath, QuizAnswerKey answerKey, CancellationToken cancellationToken = default)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey)) return Fail(filePath, "ExtractionMode is OpenAI but OPENAI_API_KEY is not configured.");
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".heic") return Fail(filePath, "HEIC conversion is not available. Convert to JPG or PNG and rerun.", "HEIC conversion is not available. Convert to JPG or PNG and rerun.");
        if (!new[] { ".pdf", ".docx", ".doc", ".png", ".jpg", ".jpeg" }.Contains(ext)) return Fail(filePath, "Unsupported file type.");

        var content = ext is ".doc" or ".docx" ? await File.ReadAllTextAsync(filePath, cancellationToken) : Convert.ToBase64String(await File.ReadAllBytesAsync(filePath, cancellationToken));
        var prompt = PromptBuilder.Build(answerKey);
        var payload = JsonSerializer.Serialize(new { model = options.Model, input = new object[] { new { role = "system", content = new object[] { new { type = "input_text", text = prompt } } }, new { role = "user", content = new object[] { new { type = "input_text", text = $"fileName={Path.GetFileName(filePath)}; fileType={ext}; payload={content}" } } } } });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode) return Fail(filePath, $"OpenAI request failed: {(int)resp.StatusCode}");
        var text = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!TryExtractJson(text, out var extracted)) return Fail(filePath, "OpenAI returned invalid JSON.");
        Normalize(extracted!, Path.GetFileName(filePath), answerKey);
        return extracted!;
    }
    private static bool TryExtractJson(string responseJson, out ExtractedSubmissionAnswers? result)
    {
        result = null;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var outputText = doc.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString() ?? "";
            result = JsonSerializer.Deserialize<ExtractedSubmissionAnswers>(outputText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return result is not null;
        }
        catch { return false; }
    }
    private static void Normalize(ExtractedSubmissionAnswers r, string fileName, QuizAnswerKey key)
    {
        r.FileName = fileName; r.ExtractionProvider = "OpenAI";
        foreach (var a in r.Answers)
        {
            a.SelectedChoice = string.IsNullOrWhiteSpace(a.SelectedChoice) ? null : a.SelectedChoice.Trim().ToLowerInvariant();
            if (a.SelectedChoice is not ("a" or "b" or "c" or "d")) a.SelectedChoice = null;
            if (a.SelectedChoice is null) { a.Confidence = 0; a.NeedsManualReview = true; }
        }
        foreach (var q in key.Answers.Where(x => r.Answers.All(a => a.QuestionNumber != x.QuestionNumber)))
            r.Answers.Add(new ExtractedAnswer { QuestionNumber = q.QuestionNumber, SelectedChoice = null, Confidence = 0, NeedsManualReview = true, Evidence = "Missing in model output" });
    }
    private static ExtractedSubmissionAnswers Fail(string filePath, string reason, string? warning = null) => new() { FileName = Path.GetFileName(filePath), ExtractionProvider = "OpenAI", Success = false, FailureReason = reason, Warnings = warning is null ? new() : new() { warning } };
}

public static class PromptBuilder { public static string Build(QuizAnswerKey answerKey) => $"""
You are extracting answers from a student's multiple-choice quiz submission.
Do not grade the quiz.
Do not decide whether answers are correct.
Only identify the answer choice selected by the student for each question.
Return one answer object for every question in the answer key.
If an answer is blank, unclear, crossed out, or unreadable: selectedChoice=null, confidence=0, needsManualReview=true.
Valid selected choices are: a, b, c, d. Normalize uppercase to lowercase.
Use provided answer key only for question numbers.
Do not use correctChoice to infer student's answer.
If submission appears to be answer key add warning: Submission may be an answer key, not a student response.
Question numbers: {string.Join(',', answerKey.Answers.Select(a => a.QuestionNumber))}
Return JSON matching ExtractedSubmissionAnswers.
"""; }

public class ExtractedSubmissionAnswers { public string FileName { get; set; } = ""; public string ExtractionProvider { get; set; } = ""; public bool Success { get; set; } = true; public string? FailureReason { get; set; } public List<ExtractedAnswer> Answers { get; set; } = new(); public List<string> Warnings { get; set; } = new(); }
public class ExtractedAnswer { public int QuestionNumber { get; set; } public string? SelectedChoice { get; set; } public decimal Confidence { get; set; } public string? Evidence { get; set; } public bool NeedsManualReview { get; set; } }
public sealed class QuestionGrade { public int QuestionNumber { get; set; } public string? Selected { get; set; } public string Correct { get; set; } = ""; public string Result { get; set; } = "Incorrect"; public decimal Confidence { get; set; } }
public sealed class SubmissionGradeResult { public string FileName { get; set; } = ""; public string FileType { get; set; } = ""; public string GradingStatus { get; set; } = "Graded"; public int Score { get; set; } public int TotalQuestions { get; set; } public decimal Percent { get; set; } public bool ManualReviewRequired { get; set; } public string ManualReviewReason { get; set; } = ""; public string Warnings { get; set; } = ""; public string ExtractionProvider { get; set; } = ""; public string ExtractedAnswersFilePath { get; set; } = ""; public List<QuestionGrade> QuestionGrades { get; set; } = new(); }

internal sealed class JobRunner
{
    public async Task<JobSummary> RunAsync(GradingRunConfiguration runConfig, GradingOptions gradingOptions, OpenAiOptions openAiOptions, DiagnosticsOptions diagnostics)
    {
        var selectedQuiz = AnswerKeyLoader.LoadQuiz(runConfig.AnswerKeyPath, gradingOptions.QuizId);
        var extraction = ResolveExtractionService(gradingOptions, openAiOptions);
        var files = runConfig.SingleFilePath is not null ? new List<string> { runConfig.SingleFilePath } : Directory.GetFiles(runConfig.SubmissionsPath).OrderBy(Path.GetFileName).ToList();
        var extractedDir = Path.Combine(runConfig.ResultsPath, "extracted-answers"); Directory.CreateDirectory(extractedDir);
        var results = new List<SubmissionGradeResult>(); int openAiFailures = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            Console.WriteLine($"Processing {index + 1} of {files.Count}: {Path.GetFileName(file)}");
            Console.WriteLine($"Extraction provider: {gradingOptions.ExtractionMode}");
            try
            {
                var ex = await extraction.ExtractAnswersAsync(file, selectedQuiz);
                if (!ex.Success) openAiFailures++;
                var graded = GradeSubmission(ex, selectedQuiz, gradingOptions.ManualReviewConfidenceThreshold);
                var debugPath = Path.Combine(extractedDir, $"{Path.GetFileNameWithoutExtension(file)}.json");
                graded.ExtractedAnswersFilePath = debugPath;
                await File.WriteAllTextAsync(debugPath, JsonSerializer.Serialize(new { ex.FileName, ex.ExtractionProvider, ex.Success, ex.FailureReason, ex.Warnings, ex.Answers, graded.QuestionGrades }, new JsonSerializerOptions { WriteIndented = true }));
                results.Add(graded);
                Console.WriteLine(graded.ManualReviewRequired ? $"Result: ManualReview - {graded.ManualReviewReason}" : "Result: Graded");
            }
            catch (Exception ex)
            {
                openAiFailures++;
                var fileName = Path.GetFileName(file);
                var failureReason = $"Unhandled file processing error: {ex.GetType().Name}: {ex.Message}";
                var graded = new SubmissionGradeResult
                {
                    FileName = fileName,
                    FileType = Path.GetExtension(fileName).TrimStart('.'),
                    ExtractionProvider = gradingOptions.ExtractionMode,
                    ManualReviewRequired = true,
                    ManualReviewReason = failureReason,
                    GradingStatus = "ManualReview",
                    Warnings = "File processing exception; see debug JSON.",
                    TotalQuestions = selectedQuiz.Answers.Count
                };
                var debugPath = Path.Combine(extractedDir, $"{Path.GetFileNameWithoutExtension(file)}.json");
                graded.ExtractedAnswersFilePath = debugPath;
                await File.WriteAllTextAsync(debugPath, JsonSerializer.Serialize(new { FileName = fileName, Success = false, FailureReason = failureReason, Exception = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
                results.Add(graded);
                FatalErrorLogger.WriteFileError(ex, file, runConfig, diagnostics, debugPath);
                Console.WriteLine($"Result: ManualReview - {failureReason}");
            }
        }
        var reportPath = Path.Combine(runConfig.ResultsPath, "grade-report.csv");
        CsvWriter.WriteGradeReport(reportPath, results, selectedQuiz.Answers.Select(a => a.QuestionNumber).ToList());
        return new JobSummary(files.Count, results.Count(r => !r.ManualReviewRequired), results.Count(r => r.ManualReviewRequired), openAiFailures, reportPath, extractedDir);
    }
    private static IAnswerExtractionService ResolveExtractionService(GradingOptions options, OpenAiOptions openAiOptions)
    {
        if (string.Equals(options.ExtractionMode, "OpenAI", StringComparison.OrdinalIgnoreCase)) return new OpenAiAnswerExtractionService(openAiOptions);
        throw new InvalidOperationException("Only OpenAI extraction mode is implemented in this build.");
    }
    internal static SubmissionGradeResult GradeSubmission(ExtractedSubmissionAnswers extracted, QuizAnswerKey key, decimal threshold)
    {
        var r = new SubmissionGradeResult { FileName = extracted.FileName, FileType = Path.GetExtension(extracted.FileName).TrimStart('.'), ExtractionProvider = extracted.ExtractionProvider, Warnings = string.Join(" | ", extracted.Warnings), TotalQuestions = key.Answers.Count };
        if (!extracted.Success) { r.ManualReviewRequired = true; r.ManualReviewReason = extracted.FailureReason ?? "Extraction failed."; r.GradingStatus = "ManualReview"; return r; }
        foreach (var q in key.Answers.OrderBy(x => x.QuestionNumber))
        {
            var a = extracted.Answers.FirstOrDefault(x => x.QuestionNumber == q.QuestionNumber) ?? new ExtractedAnswer { QuestionNumber = q.QuestionNumber, NeedsManualReview = true };
            var manual = a.SelectedChoice is null || a.Confidence < threshold || a.NeedsManualReview;
            var result = manual ? "ManualReview" : (string.Equals(a.SelectedChoice, q.CorrectChoice, StringComparison.OrdinalIgnoreCase) ? "Correct" : "Incorrect");
            r.QuestionGrades.Add(new QuestionGrade { QuestionNumber = q.QuestionNumber, Selected = a.SelectedChoice, Correct = q.CorrectChoice, Result = result, Confidence = a.Confidence });
            if (result == "Correct") r.Score++;
        }
        r.ManualReviewRequired = r.QuestionGrades.Any(x => x.Result == "ManualReview");
        r.ManualReviewReason = r.ManualReviewRequired ? "One or more questions require manual review." : "";
        r.GradingStatus = r.ManualReviewRequired ? "ManualReview" : "Graded";
        r.Percent = r.TotalQuestions == 0 ? 0 : (decimal)r.Score / r.TotalQuestions * 100;
        return r;
    }
}

internal static class CsvWriter
{
    public static void WriteGradeReport(string path, List<SubmissionGradeResult> rows, List<int> questionNumbers)
    {
        var sb = new StringBuilder("FileName,FileType,GradingStatus,Score,TotalQuestions,Percent,ManualReviewRequired,ManualReviewReason,Warnings,ExtractionProvider,ExtractedAnswersFilePath");
        foreach (var q in questionNumbers) sb.Append($",Q{q}_Selected,Q{q}_Correct,Q{q}_Result,Q{q}_Confidence");
        sb.AppendLine();
        foreach (var r in rows)
        {
            var cells = new List<string> { Esc(r.FileName), Esc(r.FileType), Esc(r.GradingStatus), r.Score.ToString(), r.TotalQuestions.ToString(), r.Percent.ToString("F2"), r.ManualReviewRequired.ToString().ToLowerInvariant(), Esc(r.ManualReviewReason), Esc(r.Warnings), Esc(r.ExtractionProvider), Esc(r.ExtractedAnswersFilePath) };
            foreach (var q in questionNumbers)
            {
                var g = r.QuestionGrades.FirstOrDefault(x => x.QuestionNumber == q);
                cells.Add(Esc(g?.Selected ?? "")); cells.Add(Esc(g?.Correct ?? "")); cells.Add(Esc(g?.Result ?? "")); cells.Add((g?.Confidence ?? 0).ToString("F2"));
            }
            sb.AppendLine(string.Join(',', cells));
        }
        File.WriteAllText(path, sb.ToString());
    }
    private static string Esc(string s) => $"\"{(s ?? "").Replace("\"", "\"\"")}\"";
}

internal sealed record GradingRunConfiguration(string AnswerKeyPath, string SubmissionsPath, string ResultsPath, string? SingleFilePath, string? QuizId, string ExtractionMode, bool OpenAiKeyConfigured);
internal static class JobContext
{
    public static GradingRunConfiguration Resolve(CliOptions cli, GradingOptions config, OpenAiOptions openAiOptions)
    {
        var answerKeyPath = ResolvePath(cli.AnswerKeyPath ?? config.AnswerKeyPath ?? "");
        if (string.IsNullOrWhiteSpace(answerKeyPath) || !File.Exists(answerKeyPath)) throw new InvalidOperationException($"Answer key file was not found: {answerKeyPath}");
        var submissionsPath = ResolvePath(cli.SubmissionsPath ?? config.SubmissionsPath ?? "");
        var resultsPath = ResolvePath(cli.ResultsPath ?? config.ResultsPath ?? "");
        if (string.IsNullOrWhiteSpace(resultsPath)) resultsPath = Path.Combine(AppContext.BaseDirectory, "output");
        Directory.CreateDirectory(resultsPath);
        var singleFile = string.IsNullOrWhiteSpace(config.SingleFilePath) ? null : ResolvePath(config.SingleFilePath!);
        if (singleFile is not null && !File.Exists(singleFile)) throw new InvalidOperationException($"SingleFilePath was configured but the file was not found: {singleFile}");
        if (singleFile is null && (string.IsNullOrWhiteSpace(submissionsPath) || !Directory.Exists(submissionsPath))) throw new InvalidOperationException($"Submissions folder was not found: {submissionsPath}");
        if (string.Equals(config.ExtractionMode, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? openAiOptions.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("ExtractionMode is OpenAI but OPENAI_API_KEY or OpenAI:ApiKey is not configured.");
        }
        var openAiKeyConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? openAiOptions.ApiKey);
        return new GradingRunConfiguration(answerKeyPath, submissionsPath, resultsPath, singleFile, config.QuizId, config.ExtractionMode, openAiKeyConfigured);
    }
    private static string ResolvePath(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(p);
}

internal static class DiagnosticsPrinter
{
    public static void PrintRuntimeConfiguration(GradingRunConfiguration runConfig, DiagnosticsOptions diagnostics)
    {
        if (!diagnostics.Verbose) return;
        Console.WriteLine("Runtime configuration:");
        Console.WriteLine($"AnswerKeyPath: {runConfig.AnswerKeyPath}");
        Console.WriteLine($"SubmissionsFolder: {runConfig.SubmissionsPath}");
        Console.WriteLine($"OutputFolder: {runConfig.ResultsPath}");
        Console.WriteLine($"QuizId: {runConfig.QuizId}");
        Console.WriteLine($"SingleFilePath: {runConfig.SingleFilePath}");
        Console.WriteLine($"ExtractionMode: {runConfig.ExtractionMode}");
        Console.WriteLine($"OpenAI API key configured: {runConfig.OpenAiKeyConfigured}");
    }
}

internal static class FatalErrorLogger
{
    private static string _fallbackLogRoot = Path.Combine(AppContext.BaseDirectory, "logs");
    private static string? _preferredOutputRoot;
    public static void InitializeFallbackLogRoot(string path) => _fallbackLogRoot = path;
    public static void SetPreferredOutputRoot(string path) => _preferredOutputRoot = path;
    public static string WriteFatal(Exception ex) => WriteLog("fatal-error", ex.ToString());
    public static void WriteUnhandled(string source, Exception ex) => WriteLog("unhandled-exception", $"{source}{Environment.NewLine}{ex}");
    public static void WriteFileError(Exception ex, string filePath, GradingRunConfiguration config, DiagnosticsOptions diagnostics, string debugPath)
        => WriteLog("file-processing-error", $"File: {filePath}{Environment.NewLine}DebugPath: {debugPath}{Environment.NewLine}{BuildRuntimeConfig(config, diagnostics)}{Environment.NewLine}{ex}");
    private static string WriteLog(string filePrefix, string content)
    {
        var logRoot = _preferredOutputRoot is not null ? Path.Combine(_preferredOutputRoot, "logs") : _fallbackLogRoot;
        Directory.CreateDirectory(logRoot);
        var logFile = Path.Combine(logRoot, $"{filePrefix}.txt");
        var payload = $"Timestamp (UTC): {DateTime.UtcNow:O}{Environment.NewLine}{content}{Environment.NewLine}";
        File.WriteAllText(logFile, payload);
        return logFile;
    }
    private static string BuildRuntimeConfig(GradingRunConfiguration config, DiagnosticsOptions diagnostics)
        => $"AnswerKeyPath: {config.AnswerKeyPath}{Environment.NewLine}SubmissionsFolder: {config.SubmissionsPath}{Environment.NewLine}OutputFolder: {config.ResultsPath}{Environment.NewLine}QuizId: {config.QuizId}{Environment.NewLine}SingleFilePath: {config.SingleFilePath}{Environment.NewLine}ExtractionMode: {config.ExtractionMode}{Environment.NewLine}OpenAI API key configured: {config.OpenAiKeyConfigured}{Environment.NewLine}Diagnostics.Verbose: {diagnostics.Verbose}";
}

internal sealed record AnswerKey(string AssignmentName, Dictionary<string, string> Questions);
public sealed class AnswerKeyFile { public List<QuizAnswerKey> AnswerKeys { get; set; } = new(); }
public sealed class QuizAnswerKey { public string QuizId { get; set; } = ""; public string Title { get; set; } = ""; public List<AnswerKeyItem> Answers { get; set; } = new(); }
public sealed class AnswerKeyItem { public int QuestionNumber { get; set; } public string CorrectChoice { get; set; } = ""; }
internal static class AnswerKeyLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static AnswerKey Load(string path, string? configuredQuizId) { var q = LoadQuiz(path, configuredQuizId); return new AnswerKey(string.IsNullOrWhiteSpace(q.Title) ? q.QuizId : q.Title, q.Answers.ToDictionary(a => a.QuestionNumber.ToString(), a => a.CorrectChoice)); }
    public static QuizAnswerKey LoadQuiz(string path, string? configuredQuizId)
    {
        var json = File.ReadAllText(path);
        var combined = JsonSerializer.Deserialize<AnswerKeyFile>(json, JsonOptions);
        var quizzes = combined?.AnswerKeys?.Count > 0 ? combined.AnswerKeys : new List<QuizAnswerKey> { JsonSerializer.Deserialize<QuizAnswerKey>(json, JsonOptions)! };
        quizzes = quizzes.Where(x => x is not null).ToList();
        foreach (var q in quizzes) foreach (var a in q.Answers) a.CorrectChoice = a.CorrectChoice.Trim().ToLowerInvariant();
        return quizzes.Count == 1 ? quizzes[0] : quizzes.First(x => x.QuizId.Equals(configuredQuizId, StringComparison.OrdinalIgnoreCase));
    }
}
internal sealed record JobSummary(int Processed, int Graded, int ManualReview, int OpenAiExtractionFailures, string GradeReportPath, string ExtractedAnswersFolder);
