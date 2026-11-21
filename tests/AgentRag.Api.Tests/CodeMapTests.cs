using AgentRag.Api.Services;

public class CodeMapTests
{
    private static CodeFile File(string path, int length = 100, string? fingerprint = null) => new(path, Math.Abs(path.GetHashCode()), fingerprint ?? "sha-" + path, length);

    [Fact]
    public void Modules_SmallRepository_IsOneModule()
    {
        var modules = CodeMap.Modules([File("src/a/x.cs"), File("src/b/y.cs"), File("top.cs")], 1000);

        var module = Assert.Single(modules);
        Assert.Equal(("", "(root)", 3), (module.Key, module.Name, module.Files.Count));
    }

    [Fact]
    public void Modules_LargeDirectory_IsSplitBySubdirectory()
    {
        var files = new[] { File("src/a/x.cs"), File("src/a/y.cs"), File("src/b/z.cs"), File("src/top.cs", 50) };

        var modules = CodeMap.Modules(files, 250);

        Assert.Equal(["src", "src/a", "src/b"], modules.Select(m => m.Key));
        Assert.Equal(["src/top.cs"], modules[0].Files.Select(f => f.Path));
        Assert.Equal(["src/a/x.cs", "src/a/y.cs"], modules[1].Files.Select(f => f.Path));
        Assert.Equal(files.Length, modules.Sum(m => m.Files.Count));
    }

    [Fact]
    public void Modules_OversizedLeafDirectory_IsCutIntoNumberedParts()
    {
        var files = Enumerable.Range(1, 5).Select(i => File($"src/big/f{i}.cs")).Append(File("src/small/s.cs", 10)).ToList();

        var modules = CodeMap.Modules(files, 250);

        Assert.Equal(["src/big", "src/big#2", "src/big#3", "src/small"], modules.Select(m => m.Key));
        Assert.Equal([2, 2, 1, 1], modules.Select(m => m.Files.Count));
        Assert.All(modules.Where(m => m.Key.StartsWith("src/big", StringComparison.Ordinal)), m => Assert.True(m.Files.Sum(f => f.Length) <= 250));
        Assert.Equal(files.Count, modules.Sum(m => m.Files.Count));
    }

    [Fact]
    public void Modules_FileLargerThanTheBudget_GetsItsOwnModule()
    {
        var modules = CodeMap.Modules([File("a/one.cs", 900), File("a/two.cs", 10)], 100);

        Assert.Equal(2, modules.Count);
        Assert.Equal("a#2", modules[1].Key);
    }

    [Fact]
    public void Modules_NoFiles_IsEmpty() => Assert.Empty(CodeMap.Modules([], 100));

    [Theory]
    [InlineData("export const LMNP_PROJECTION_YEARS = 20;", true)]
    [InlineData("export const computeTotal = (amount: number) => amount;", true)]
    [InlineData("const MAX_RETRIES = 3", true)]
    [InlineData("    public const int ChunkSize = 800;", true)]
    [InlineData("    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);", true)]
    [InlineData("const val MAX_ITEMS = 10", true)]
    [InlineData("SOCIAL_CONTRIBUTIONS_RATE = 0.186", true)]
    [InlineData("export function computeLoanSchedule(loan: Loan) {", true)]
    [InlineData("    public async Task<int> CountAsync(CancellationToken ct)", true)]
    [InlineData("  const schedule = computeLoanSchedule(loan);", false)]
    [InlineData("  computeLoanSchedule as computeSharedLoanSchedule,", false)]
    [InlineData("if (TOTAL == 3) return;", false)]
    [InlineData("  type DepreciationCapRule,", false)]
    [InlineData("  type LmnpBuildingType as BuildingKind", false)]
    [InlineData("export type LmnpBuildingType = 'apartment' | 'house';", true)]
    [InlineData("type Totals<T> = Record<string, T>", true)]
    public void IsDeclaration_RecognisesDeclarationsAndConstants_NotUses(string line, bool expected) =>
        Assert.Equal(expected, CodeMap.IsDeclaration(line));

    [Fact]
    public void Outline_IncludesConstantsWithTheirLines()
    {
        const string code = "export const YEARS = 20;\nconst local = 1;\n\nexport function total(): number {\n  return YEARS;\n}\n";

        Assert.Equal([(1, "export const YEARS = 20;"), (4, "export function total(): number {")], CodeMap.Outline(code));
    }

    [Fact]
    public void Fingerprint_ChangesWhenAFileIsChangedAddedOrRemoved_AndIsStableOtherwise()
    {
        var module = new CodeModule("src", [File("src/a.cs"), File("src/b.cs")]);
        var same = new CodeModule("src", [File("src/a.cs"), File("src/b.cs")]);
        var changed = new CodeModule("src", [File("src/a.cs"), File("src/b.cs", fingerprint: "other")]);
        var added = new CodeModule("src", [.. module.Files, File("src/c.cs")]);
        var removed = new CodeModule("src", [module.Files[0]]);

        Assert.Equal(CodeMap.Fingerprint(module), CodeMap.Fingerprint(same));
        var all = new[] { module, changed, added, removed }.Select(CodeMap.Fingerprint).ToList();
        Assert.Equal(4, all.Distinct().Count());
        Assert.All(all, f => Assert.Equal(40, f.Length));
    }

    [Fact]
    public void Outline_CSharp_FindsDeclarationsAndRoutesWithOneBasedLines()
    {
        var text = "using System;\n\nnamespace Shop;\n\npublic class OrdersController\n{\n    [HttpGet(\"{id}\")]\n    public IActionResult Get(int id) => Ok();\n}\n";

        var outline = CodeMap.Outline(text);

        Assert.Equal(
        [
            (3, "namespace Shop;"),
            (5, "public class OrdersController"),
            (7, "[HttpGet(\"{id}\")]"),
            (8, "public IActionResult Get(int id) => Ok();")
        ], outline);
    }

    [Fact]
    public void Outline_TypeScript_FindsExportedFunctionsAndInterfaces()
    {
        var text = "import x from 'y'\r\n\r\nexport interface Order { id: number }\r\nconst a = 1\r\nexport async function createOrder(input: Order) {\r\n}\r\nexport default function Home() {}\r\n";

        var outline = CodeMap.Outline(text);

        Assert.Equal([3, 5, 7], outline.Select(o => o.Line));
        Assert.Equal("export async function createOrder(input: Order) {", outline[1].Text);
    }

    [Fact]
    public void Outline_ManyDeclarations_StopsAtTheMaximum()
    {
        var text = string.Concat(Enumerable.Range(1, 100).Select(i => $"class C{i} {{}}\n"));

        var outline = CodeMap.Outline(text, 10);

        Assert.Equal(10, outline.Count);
        Assert.Equal(10, outline[^1].Line);
    }

    [Fact]
    public void Digest_ListsFilesWithOutlineAndStartOfContent_AndSkipsMissingTexts()
    {
        var order = new CodeFile("src/OrderService.cs", 1, "a", 0);
        var lost = new CodeFile("src/Lost.cs", 2, "b", 0);
        var text = "namespace Shop;\npublic class OrderService\n{\n}\n";
        var module = new CodeModule("src", [order, lost]);

        var digest = CodeMap.Digest(module, new Dictionary<int, string> { [1] = text }, 4000);

        Assert.Contains("### src/OrderService.cs (5 lines)", digest);
        Assert.Contains("2: public class OrderService", digest);
        // Every line shown carries its number, so the model can cite any of them.
        Assert.Contains("Content (line: text):\n1: namespace Shop;\n2: public class OrderService\n", digest.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("Lost.cs", digest);
    }

    [Fact]
    public void Digest_HugeModule_StaysWithinTwiceTheBudgetPlusOneFile()
    {
        var files = Enumerable.Range(1, 50).Select(i => new CodeFile($"src/F{i}.cs", i, "x", 20_000)).ToList();
        var texts = files.ToDictionary(f => f.DocumentId, _ => string.Concat(Enumerable.Repeat("class A {}\n", 2000)));

        var digest = CodeMap.Digest(new CodeModule("src", files), texts, 5000);

        Assert.True(digest.Length < (5000 * 2) + 13_000);
        Assert.Contains("### src/F1.cs", digest);
        Assert.DoesNotContain("### src/F50.cs", digest);
        // The model is told what it did not see.
        Assert.Contains("Files not shown for lack of room: ", digest);
        Assert.EndsWith("src/F50.cs\n", digest.ReplaceLineEndings("\n"));
    }
}
