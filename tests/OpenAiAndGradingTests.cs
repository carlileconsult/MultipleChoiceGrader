using Xunit;

public class OpenAiAndGradingTests
{
    [Fact]
    public async Task MissingApiKey_ReturnsClearError()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        var service = new OpenAiAnswerExtractionService(new OpenAiOptions { ApiKey = "" });
        var quiz = new QuizAnswerKey { QuizId = "Q", Answers = new() { new AnswerKeyItem { QuestionNumber = 1, CorrectChoice = "a" } } };
        var result = await service.ExtractAnswersAsync("student.jpg", quiz);
        Assert.False(result.Success);
        Assert.Equal("ExtractionMode is OpenAI but OPENAI_API_KEY is not configured.", result.FailureReason);
    }

    [Fact]
    public void LowConfidence_AndNullSelected_TriggerManualReview()
    {
        var extracted = new ExtractedSubmissionAnswers
        {
            FileName = "s.jpg",
            ExtractionProvider = "OpenAI",
            Success = true,
            Answers = new() {
                new ExtractedAnswer { QuestionNumber = 1, SelectedChoice = "a", Confidence = 0.2m },
                new ExtractedAnswer { QuestionNumber = 2, SelectedChoice = null, Confidence = 0m }
            }
        };
        var quiz = new QuizAnswerKey { QuizId = "Q", Answers = new() { new AnswerKeyItem { QuestionNumber = 1, CorrectChoice = "a" }, new AnswerKeyItem { QuestionNumber = 2, CorrectChoice = "b" } } };
        var graded = JobRunner.GradeSubmission(extracted, quiz, 0.7m);
        Assert.True(graded.ManualReviewRequired);
        Assert.All(graded.QuestionGrades, q => Assert.Equal("ManualReview", q.Result));
    }

    [Fact]
    public void LocalGrading_ComparesCorrectly_AndCsvHasPerQuestionColumns()
    {
        var extracted = new ExtractedSubmissionAnswers { FileName = "s.jpg", ExtractionProvider = "OpenAI", Success = true, Answers = new() { new ExtractedAnswer { QuestionNumber = 1, SelectedChoice = "b", Confidence = 0.9m }, new ExtractedAnswer { QuestionNumber = 2, SelectedChoice = "a", Confidence = 0.95m } } };
        var quiz = new QuizAnswerKey { QuizId = "Q", Answers = new() { new AnswerKeyItem { QuestionNumber = 1, CorrectChoice = "b" }, new AnswerKeyItem { QuestionNumber = 2, CorrectChoice = "c" } } };
        var graded = JobRunner.GradeSubmission(extracted, quiz, 0.7m);
        Assert.Equal(1, graded.Score);

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csv");
        CsvWriter.WriteGradeReport(path, new() { graded }, new() { 1, 2 });
        var csv = File.ReadAllText(path);
        Assert.Contains("Q1_Selected", csv);
        Assert.Contains("Q1_Correct", csv);
        Assert.Contains("Q1_Result", csv);
        Assert.Contains("Q1_Confidence", csv);
    }
}
