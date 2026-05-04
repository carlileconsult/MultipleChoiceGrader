using Xunit;

public class ExtractorRoutingTests
{
    private readonly ExtractorRouter _router = new(new ISubmissionContentExtractor[] {
        new PdfSubmissionContentExtractor(new StubOcrService(true), true),
        new WordSubmissionContentExtractor(),
        new ImageSubmissionContentExtractor(new StubOcrService(true), true),
        new HeicSubmissionContentExtractor(new StubOcrService(true), true)
    });

    [Theory]
    [InlineData("a.pdf", typeof(PdfSubmissionContentExtractor))]
    [InlineData("a.docx", typeof(WordSubmissionContentExtractor))]
    [InlineData("a.png", typeof(ImageSubmissionContentExtractor))]
    [InlineData("a.jpg", typeof(ImageSubmissionContentExtractor))]
    [InlineData("a.jpeg", typeof(ImageSubmissionContentExtractor))]
    [InlineData("a.heic", typeof(HeicSubmissionContentExtractor))]
    public void RoutesByExtension(string file, Type expected)
        => Assert.Equal(expected, _router.Resolve(file)?.GetType());

    [Fact]
    public void UnsupportedReturnsNull() => Assert.Null(_router.Resolve("a.txt"));
}
