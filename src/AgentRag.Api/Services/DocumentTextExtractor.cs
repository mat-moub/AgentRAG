using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AgentRag.Api.Services;

public sealed class DocumentTextExtractor
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxTextCharacters = 2_000_000;

    public async Task<string> ExtractAsync(IFormFile file, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (file.Length == 0)
            throw new InvalidDataException("The selected file is empty.");
        if (file.Length > MaxFileBytes)
            throw new InvalidDataException("Files must be 10 MB or smaller.");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".pdf" or ".txt" or ".md" or ".csv"))
            throw new InvalidDataException("Unsupported file type. Choose a PDF, TXT, Markdown or CSV file.");

        await using var stream = file.OpenReadStream();
        string text;
        if (extension == ".pdf")
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            text = ExtractPdf(buffer, ct);
        }
        else
        {
            try
            {
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                var result = new StringBuilder();
                var buffer = new char[8192];
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
                {
                    EnsureTextLimit(result.Length + count);
                    result.Append(buffer, 0, count);
                }
                text = result.ToString();
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("Unable to read this text file. Save it as UTF-8 and try again.");
            }
        }

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException(extension == ".pdf"
                ? "This PDF contains no extractable text. Scanned PDFs need OCR before they can be added."
                : "The selected file contains no text.");
        return text;
    }

    private static string ExtractPdf(Stream stream, CancellationToken ct)
    {
        try
        {
            using var document = PdfDocument.Open(stream);
            var result = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var text = ContentOrderTextExtractor.GetText(page);
                EnsureTextLimit(result.Length + text.Length + Environment.NewLine.Length * 2);
                result.AppendLine(text).AppendLine();
            }
            return result.ToString();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or InvalidDataException or OutOfMemoryException))
        {
            throw new InvalidDataException("Unable to read this PDF. It may be damaged or password-protected.", ex);
        }
    }

    private static void EnsureTextLimit(int length)
    {
        if (length > MaxTextCharacters)
            throw new InvalidDataException("The document contains too much text. Split it into smaller files (maximum 2 million characters).");
    }
}
