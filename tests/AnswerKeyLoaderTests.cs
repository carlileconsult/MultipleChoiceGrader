using Xunit;

public class AnswerKeyLoaderTests
{
    [Fact]
    public async Task LoadsSingleQuizAnswerKeyJson()
    {
        var path = await WriteTempJsonAsync("""
        {
          "quizId": "SB3_QuickQuiz15",
          "title": "Quick Quiz 15",
          "answers": [
            { "questionNumber": 1, "correctChoice": "b", "answerText": "text" }
          ]
        }
        """);

        var key = AnswerKeyLoader.Load(path, null);
        Assert.Equal("Quick Quiz 15", key.AssignmentName);
        Assert.Equal("b", key.Questions["1"]);
    }

    [Fact]
    public async Task LoadsCombinedAnswerKeysJson()
    {
        var path = await WriteTempJsonAsync("""
        {
          "answerKeys": [
            { "quizId": "SB3_QuickQuiz15", "title": "Quick Quiz 15", "answers": [{ "questionNumber": 1, "correctChoice": "a" }] },
            { "quizId": "SB3_QuickQuiz16", "title": "Quick Quiz 16", "answers": [{ "questionNumber": 1, "correctChoice": "b" }] }
          ]
        }
        """);

        var key = AnswerKeyLoader.Load(path, "SB3_QuickQuiz16");
        Assert.Equal("Quick Quiz 16", key.AssignmentName);
        Assert.Equal("b", key.Questions["1"]);
    }

    [Fact]
    public async Task CorrectChoiceIsNormalizedToLowercase()
    {
        var path = await WriteTempJsonAsync("""
        { "quizId": "Q1", "title": "Q1", "answers": [{ "questionNumber": 1, "correctChoice": "C" }] }
        """);

        var key = AnswerKeyLoader.Load(path, null);
        Assert.Equal("c", key.Questions["1"]);
    }

    [Fact]
    public async Task MultipleQuizzesWithoutQuizIdGivesHelpfulError()
    {
        var path = await WriteTempJsonAsync("""
        { "answerKeys": [
            { "quizId": "Q1", "title": "Q1", "answers": [{ "questionNumber": 1, "correctChoice": "a" }] },
            { "quizId": "Q2", "title": "Q2", "answers": [{ "questionNumber": 1, "correctChoice": "b" }] }
        ] }
        """);

        var ex = Assert.Throws<InvalidOperationException>(() => AnswerKeyLoader.Load(path, null));
        Assert.Contains("Multiple answer keys were found", ex.Message);
        Assert.Contains("Q1", ex.Message);
        Assert.Contains("Q2", ex.Message);
    }

    [Fact]
    public async Task MultipleQuizzesWithQuizIdSelectsCorrectQuiz()
    {
        var path = await WriteTempJsonAsync("""
        { "answerKeys": [
            { "quizId": "Q1", "title": "Q1", "answers": [{ "questionNumber": 1, "correctChoice": "a" }] },
            { "quizId": "Q2", "title": "Q2", "answers": [{ "questionNumber": 1, "correctChoice": "b" }] }
        ] }
        """);

        var key = AnswerKeyLoader.Load(path, "Q2");
        Assert.Equal("Q2", key.AssignmentName);
    }

    [Fact]
    public async Task InvalidJsonGivesHelpfulError()
    {
        var path = await WriteTempJsonAsync("{ this is not valid json }");
        var ex = Assert.Throws<InvalidOperationException>(() => AnswerKeyLoader.Load(path, null));
        Assert.Contains("Invalid answer key JSON", ex.Message);
    }

    [Fact]
    public async Task MissingAnswersGivesHelpfulError()
    {
        var path = await WriteTempJsonAsync("""
        { "quizId": "Q1", "title": "Q1", "answers": [] }
        """);

        var ex = Assert.Throws<InvalidOperationException>(() => AnswerKeyLoader.Load(path, null));
        Assert.Contains("has no answers", ex.Message);
    }

    [Fact]
    public async Task MissingCorrectChoiceGivesHelpfulError()
    {
        var path = await WriteTempJsonAsync("""
        { "quizId": "Q1", "title": "Q1", "answers": [{ "questionNumber": 1, "correctChoice": "" }] }
        """);

        var ex = Assert.Throws<InvalidOperationException>(() => AnswerKeyLoader.Load(path, null));
        Assert.Contains("missing correctChoice", ex.Message);
    }

    private static async Task<string> WriteTempJsonAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }
}
