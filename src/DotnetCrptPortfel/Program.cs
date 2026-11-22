using System.Globalization;
using System.Text;

namespace DotnetCrptPortfel;

internal static class Program
{
    private const string Help =
        "Usage:\n" +
        "  dotnetCrptPortfel value <holdings.tsv> <quotes.tsv> --currency <CODE> --as-of <UNIX_SECONDS> --max-age <SECONDS>\n" +
        "\n" +
        "  --currency   Currency of the valuation (for example USD)\n" +
        "  --as-of      Valuation time in Unix seconds\n" +
        "  --max-age    Maximum age of a quote in seconds, inclusive\n" +
        "\n" +
        "Exit status: 0 for a complete valuation, 2 for a partial valuation, 1 for an error.\n";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        TextWriter output = Console.Out;
        output.NewLine = "\n";
        Console.Error.NewLine = "\n";

        string writeContext = "help";
        try
        {
            if (args is [] or ["--help" or "-h"] or ["value", "--help" or "-h"])
            {
                output.Write(Help);
                output.Flush();
                return 0;
            }

            Options options = ParseOptions(args);
            IReadOnlyList<Holding> holdings = Input.ReadHoldings(options.Holdings);
            IReadOnlyList<Quote> quotes = Input.ReadQuotes(options.Quotes);
            Report report = Portfolio.Value(holdings, quotes, options.Currency, options.AsOf, options.MaxAge);

            writeContext = "valuation";
            WriteReport(output, report, options.Currency);
            output.Flush();
            return report.IsComplete ? 0 : 2;
        }
        catch (Exception error) when (error is FormatException or OverflowException)
        {
            Console.Error.WriteLine($"error: {error.Message}");
            return 1;
        }
        catch (IOException error)
        {
            Console.Error.WriteLine($"error: cannot write {writeContext}: {error.Message}");
            return 1;
        }
    }

    private static Options ParseOptions(string[] args)
    {
        if (args.Length < 3)
        {
            throw new FormatException("expected 'value <holdings.tsv> <quotes.tsv>'; see --help");
        }

        if (args[0] != "value")
        {
            throw new FormatException($"unknown command '{args[0]}'; see --help");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 3; i < args.Length; i += 2)
        {
            string flag = args[i];
            if (flag is not ("--currency" or "--as-of" or "--max-age"))
            {
                throw new FormatException($"unknown option '{flag}'; see --help");
            }

            if (i + 1 == args.Length)
            {
                throw new FormatException($"missing value for {flag}");
            }

            if (!values.TryAdd(flag, args[i + 1]))
            {
                throw new FormatException($"option {flag} was specified twice");
            }
        }

        string currency = RequireOption(values, "--currency");
        if (!Input.ValidCurrency(currency))
        {
            throw new FormatException("--currency must use 2–12 uppercase ASCII letters or digits, including a letter");
        }

        ulong asOf = ParseSecondsOption(values, "--as-of");
        ulong maxAge = ParseSecondsOption(values, "--max-age");
        return new Options(args[1], args[2], currency, asOf, maxAge);
    }

    private static string RequireOption(Dictionary<string, string> values, string flag) =>
        values.TryGetValue(flag, out string? value)
            ? value
            : throw new FormatException($"missing required option {flag}");

    private static ulong ParseSecondsOption(Dictionary<string, string> values, string flag)
    {
        string value = RequireOption(values, flag);
        try
        {
            return Input.ParseSeconds(value);
        }
        catch (FormatException error)
        {
            throw new FormatException($"{flag}: {error.Message}", error);
        }
    }

    private static void WriteReport(TextWriter output, Report report, string currency)
    {
        output.WriteLine("asset\tquantity\tstatus\tprice\ttimestamp\tsource\tvalue");
        foreach (Position position in report.Positions)
        {
            string status = position.Status switch
            {
                PositionStatus.Priced => "priced",
                PositionStatus.Stale => "stale",
                PositionStatus.Missing => "missing",
                _ => throw new InvalidOperationException($"Unknown position status {position.Status}."),
            };

            string quantity = FixedPoint.Format(position.Holding.Quantity, FixedPoint.QuantityDigits);
            Quote? quote = position.Quote;
            string price = quote is null ? "" : FixedPoint.Format(quote.Price, FixedPoint.PriceDigits);
            string timestamp = quote?.Timestamp.ToString(CultureInfo.InvariantCulture) ?? "";
            string value = position.Value is UInt128 units ? FixedPoint.Format(units, FixedPoint.ValueDigits) : "";
            output.WriteLine(string.Join('\t', position.Holding.Asset, quantity, status, price, timestamp, quote?.Source, value));
        }

        string totalLabel = report.IsComplete ? "total" : "quoted_subtotal";
        string total = FixedPoint.Format(report.PricedSubtotal, FixedPoint.ValueDigits);
        output.WriteLine($"{totalLabel}\t{total}\t{currency}");
    }

    private sealed record Options(string Holdings, string Quotes, string Currency, ulong AsOf, ulong MaxAge);
}
