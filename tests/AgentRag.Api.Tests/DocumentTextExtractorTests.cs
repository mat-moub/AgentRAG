using System.Text;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Http;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

public class DocumentTextExtractorTests
{
    private readonly DocumentTextExtractor extractor = new();

    [Theory]
    [InlineData("guide.TXT")]
    [InlineData("guide.md")]
    [InlineData("guide.csv")]
    public async Task ExtractAsync_Text_PreservesUnicodeAndLines(string name)
    {
        const string text = "Résumé du guide\nDeuxième ligne : été.";
        Assert.Equal(text, await extractor.ExtractAsync(File(name, Encoding.UTF8.GetBytes(text))));
    }

    [Fact]
    public async Task ExtractAsync_MultipagePdf_ExtractsTextInPageOrder()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(595, 842).AddText("First page text", 12, new PdfPoint(30, 700), font);
        builder.AddPage(595, 842).AddText("Second page text", 12, new PdfPoint(30, 700), font);
        var text = await extractor.ExtractAsync(File("guide.PDF", builder.Build()));
        Assert.Contains("First page text", text);
        Assert.Contains("Second page text", text);
        Assert.True(text.IndexOf("First", StringComparison.Ordinal) < text.IndexOf("Second", StringComparison.Ordinal));
        Assert.Contains(Environment.NewLine + Environment.NewLine, text);
    }

    [Fact]
    public async Task ExtractAsync_PdfWithoutText_ExplainsOcrRequirement()
    {
        using var builder = new PdfDocumentBuilder();
        builder.AddPage(595, 842);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(File("scan.pdf", builder.Build())));
        Assert.Contains("OCR", error.Message);
    }

    [Theory]
    [InlineData("broken.pdf", "not a PDF", "Unable to read this PDF")]
    [InlineData("guide.docx", "content", "Unsupported file type")]
    [InlineData("empty.txt", "", "empty")]
    [InlineData("blank.md", " \n\t", "contains no text")]
    public async Task ExtractAsync_InvalidFile_ReportsValidationError(string name, string content, string message)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(File(name, Encoding.UTF8.GetBytes(content))));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task ExtractAsync_OversizedFile_RejectsBeforeReading()
    {
        var file = new FormFile(Stream.Null, 0, DocumentTextExtractor.MaxFileBytes + 1, "file", "big.pdf");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(file));
        Assert.Contains("10 MB", error.Message);
    }

    [Fact]
    public async Task ExtractAsync_TooMuchText_RejectsDocument()
    {
        var file = File("big.txt", Encoding.UTF8.GetBytes(new string('a', DocumentTextExtractor.MaxTextCharacters + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(file));
    }

    [Fact]
    public async Task ExtractAsync_Cancelled_DoesNotContinue()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(File("guide.txt", "text"u8.ToArray()), cancellation.Token));
    }

    private static IFormFile File(string name, byte[] bytes) => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);
}
