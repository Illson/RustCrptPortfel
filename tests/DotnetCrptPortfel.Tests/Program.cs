using System.Diagnostics;
using System.Text;

namespace DotnetCrptPortfel.Tests;

internal static class Program
{
    private const string HoldingsHeader = "asset\tquantity\n";
    private const string QuotesHeader = "asset\tcurrency\tprice\ttimestamp\tsource\n";
    private const string ResultHeader = "asset\tquantity\tstatus\tprice\ttimestamp\tsource\tvalue";
    private const string MaximumPrice = "3402823669209384634633746074317.68211455";
    private const string MaximumValue = "3402823669209.38463463374607431768211455";
    private const string SmallestQuantity = "0.000000000000000001";

    private static int Main()
    {
        Action[] tests =
        [
            ProvidesHelp,
            SelectsLatestEligibleQuotes,
            ReportsStaleAndMissingPositions,
            PreservesAllFractionalDigitsAndSourceText,
            AcceptsHeaderOnlyFiles,
            ReportsDuplicateRowsAtPhysicalLineNumbers,
            RejectsMalformedQuantities,
            RejectsInvalidQuotes,
            RejectsUnsupportedEncoding,
            ChecksArgumentValues,
            AcceptsMaximumPriceAndTimestamp,
            RejectsPositionValueOverflow,
            RejectsSubtotalOverflow,
        ];

        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {test.Method.Name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Method.Name} – {error.Message}");
            }
        }

        Console.WriteLine($"Passed: {tests.Length - failed}, Failed: {failed}, Skipped: 0");
        return failed == 0 ? 0 : 1;
    }

    private static void ProvidesHelp()
    {
        string[][] helpForms = [[], ["--help"], ["-h"], ["value", "--help"], ["value", "-h"]];
        foreach (var arguments in helpForms)
        {
            var result = Run(arguments);
            AssertEqual("", result.Error);
            AssertEqual(0, result.ExitCode);
            foreach (var text in new[] { "dotnetCrptPortfel value", "--currency", "--as-of", "--max-age" })
            {
                AssertContains(result.Output, text);
            }
        }
    }

    private static void SelectsLatestEligibleQuotes()
    {
        using var fixture = new Fixture(
            "\uFEFFasset\tquantity\r\nBTC\t0.1\r\n\r\nETH\t0.3\r\n",
            QuotesHeader +
            "BTC\tUSD\t99\t1001\tfuture\n" +
            "BTC\tEUR\t50\t1000\tother-currency\n" +
            "BTC\tUSD\t0.2\t900\tfixture\n" +
            "BTC\tUSD\t0.1\t899\tolder\n" +
            "ETH\tUSD\t0.4\t1000\tfixture");

        AssertReport(fixture.Value(), 0,
            "BTC\t0.1\tpriced\t0.2\t900\tfixture\t0.02",
            "ETH\t0.3\tpriced\t0.4\t1000\tfixture\t0.12",
            "total\t0.14\tUSD");
    }

    private static void ReportsStaleAndMissingPositions()
    {
        using var fixture = new Fixture(
            HoldingsHeader + "AAA\t2\nBBB\t3\nCCC\t4\nDDD\t5\n",
            QuotesHeader +
            "AAA\tUSD\t0.25\t1000\tfixture\n" +
            "BBB\tUSD\t1\t899\tfixture\n" +
            "CCC\tUSD\t2\t1001\tfuture\n" +
            "DDD\tEUR\t3\t1000\tother-currency\n");

        AssertReport(fixture.Value(), 2,
            "AAA\t2\tpriced\t0.25\t1000\tfixture\t0.5",
            "BBB\t3\tstale\t1\t899\tfixture\t",
            "CCC\t4\tmissing\t\t\t\t",
            "DDD\t5\tmissing\t\t\t\t",
            "quoted_subtotal\t0.5\tUSD");
    }

    private static void PreservesAllFractionalDigitsAndSourceText()
    {
        using var fixture = new Fixture(
            HoldingsHeader + $"ETH:1\t{SmallestQuantity}\n",
            QuotesHeader + "ETH:1\tUSD\t0.00000001\t1000\tпример");

        AssertReport(fixture.Value(), 0,
            $"ETH:1\t{SmallestQuantity}\tpriced\t0.00000001\t1000\tпример\t0.00000000000000000000000001",
            "total\t0.00000000000000000000000001\tUSD");
    }

    private static void AcceptsHeaderOnlyFiles()
    {
        using var fixture = new Fixture(HoldingsHeader, QuotesHeader);
        AssertReport(fixture.Value(), 0, "total\t0\tUSD");
    }

    private static void ReportsDuplicateRowsAtPhysicalLineNumbers()
    {
        using var fixture = new Fixture(HoldingsHeader + "ETH\t1\n\nETH\t2\n", QuotesHeader);
        AssertError(fixture.Value(), "holdings.tsv:4: duplicate holding for asset ETH");

        File.WriteAllText(fixture.HoldingsPath, HoldingsHeader + "ETH\t1\n");
        File.WriteAllText(fixture.QuotesPath, QuotesHeader + "ETH\tUSD\t1\t10\ta\nETH\tUSD\t2\t010\tb");
        AssertError(fixture.Value(), "quotes.tsv:3: duplicate quote for ETH in USD at 10");
    }

    private static void RejectsMalformedQuantities()
    {
        string[] invalid =
        [
            "", "0", ".1", "1.", "0.1.2", "-1", "+1", "1e3", " 1", "1 ", "１", "1,2",
            "0.0000000000000000001",
        ];
        using var fixture = new Fixture(HoldingsHeader, QuotesHeader);
        foreach (var quantity in invalid)
        {
            File.WriteAllText(fixture.HoldingsPath, HoldingsHeader + $"BTC\t{quantity}\n");
            AssertError(fixture.Value(), "holdings.tsv:2:");
        }
    }

    private static void RejectsInvalidQuotes()
    {
        string[] invalidRows =
        [
            "ETH\tUSD\t1\t10",
            "ETH\tusd\t1\t10\ta",
            "ETH\tUSⅮ\t1\t10\ta",
            "ETH\tUSD\t0\t10\ta",
            "ETH\tUSD\t1.230000000\t10\ta",
            "ETH\tUSD\t3402823669209384634633746074317.68211456\t10\ta",
            "ETH\tUSD\t3402823669209384634633746074318\t10\ta",
            "ETH\tUSD\t340282366920938463463374607431768211456\t10\ta",
            "ETH\tUSD\t1\t+10\ta",
            "ETH\tUSD\t1\t18446744073709551616\ta",
            "ETH\tUSD\t1\t10\t ",
            "ETH\tUSD\t1\t10\t\u001B[31m",
        ];
        using var fixture = new Fixture(HoldingsHeader, QuotesHeader);
        foreach (var row in invalidRows)
        {
            File.WriteAllText(fixture.QuotesPath, QuotesHeader + row);
            AssertError(fixture.Value(), "quotes.tsv:2:");
        }
    }

    private static void RejectsUnsupportedEncoding()
    {
        using var fixture = new Fixture(HoldingsHeader, QuotesHeader);
        File.WriteAllText(fixture.HoldingsPath, HoldingsHeader, Encoding.Unicode);
        AssertError(fixture.Value(), "holdings.tsv");

        File.WriteAllText(fixture.HoldingsPath, HoldingsHeader);
        File.WriteAllBytes(fixture.QuotesPath, [..Encoding.UTF8.GetBytes(QuotesHeader + "BTC\tUSD\t1\t1000\t"), 0xFF]);
        AssertError(fixture.Value(), "quotes.tsv");
    }

    private static void ChecksArgumentValues()
    {
        using var fixture = new Fixture(HoldingsHeader, QuotesHeader);
        string[] files = ["value", fixture.HoldingsPath, fixture.QuotesPath];
        AssertError(Run(files), "--currency");
        AssertError(Run([..files, "--currency"]), "--currency");
        AssertError(Run([..files, "--currency", "USD", "--currency", "USD"]), "--currency");
        AssertError(Run([..files, "--currency", "123", "--as-of", "1000", "--max-age", "0"]), "--currency");
        AssertError(Run([..files, "--currency", "USD", "--as-of", "+1000", "--max-age", "0"]), "--as-of");
        AssertError(Run([..files, "--currency", "USD", "--as-of", "1000", "--max-age", "18446744073709551616"]), "--max-age");
    }

    private static void AcceptsMaximumPriceAndTimestamp()
    {
        const string maxTimestamp = "18446744073709551615";
        using var fixture = new Fixture(
            HoldingsHeader + $"BTC\t{SmallestQuantity}\n",
            QuotesHeader + $"BTC\tUSD\t{MaximumPrice}\t{maxTimestamp}\tfixture");

        AssertReport(fixture.Value(asOf: maxTimestamp, maxAge: "0"), 0,
            $"BTC\t{SmallestQuantity}\tpriced\t{MaximumPrice}\t{maxTimestamp}\tfixture\t{MaximumValue}",
            $"total\t{MaximumValue}\tUSD");
    }

    private static void RejectsPositionValueOverflow()
    {
        using var fixture = new Fixture(
            HoldingsHeader + "BTC\t0.000000000000000002\n",
            QuotesHeader + $"BTC\tUSD\t{MaximumPrice}\t1000\tfixture");
        AssertError(fixture.Value(), "value of BTC exceeds");
    }

    private static void RejectsSubtotalOverflow()
    {
        using var fixture = new Fixture(
            HoldingsHeader + $"BTC\t{SmallestQuantity}\nETH\t{SmallestQuantity}\n",
            QuotesHeader + $"BTC\tUSD\t{MaximumPrice}\t1000\tfixture\nETH\tUSD\t0.00000001\t1000\tfixture");
        AssertError(fixture.Value(), "subtotal exceeds");
    }

    private static void AssertReport(Result result, int exitCode, params string[] rows)
    {
        AssertEqual("", result.Error);
        AssertEqual(exitCode, result.ExitCode);
        AssertEqual(ResultHeader + "\n" + string.Join("\n", rows) + "\n",
            result.Output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void AssertError(Result result, string message)
    {
        AssertContains(result.Error, message);
        AssertEqual(1, result.ExitCode);
        AssertEqual("", result.Output);
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected [{expected}], got [{actual}].");
        }
    }

    private static void AssertContains(string actual, string expected)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected to find [{expected}] in [{actual}].");
        }
    }

    private static Result Run(params string[] arguments)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "dotnetCrptPortfel.dll"));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start CLI.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new Result(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    private sealed record Result(int ExitCode, string Output, string Error);

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("dotnetCrptPortfel-");

        public Fixture(string holdings, string quotes)
        {
            HoldingsPath = Path.Combine(directory.FullName, "holdings.tsv");
            QuotesPath = Path.Combine(directory.FullName, "quotes.tsv");
            File.WriteAllText(HoldingsPath, holdings);
            File.WriteAllText(QuotesPath, quotes);
        }

        public string HoldingsPath { get; }
        public string QuotesPath { get; }

        public Result Value(string asOf = "1000", string maxAge = "100") =>
            Run("value", HoldingsPath, QuotesPath, "--max-age", maxAge, "--currency", "USD", "--as-of", asOf);

        public void Dispose()
        {
            File.Delete(HoldingsPath);
            File.Delete(QuotesPath);
            directory.Delete();
        }
    }
}
