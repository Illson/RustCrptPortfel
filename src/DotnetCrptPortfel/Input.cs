using System.Globalization;
using System.Text;

namespace DotnetCrptPortfel;

internal static class Input
{
    private const string HoldingsHeader = "asset\tquantity";
    private const string QuotesHeader = "asset\tcurrency\tprice\ttimestamp\tsource";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static IReadOnlyList<Holding> ReadHoldings(string path)
    {
        var assets = new HashSet<string>(StringComparer.Ordinal);
        return ReadRows(path, HoldingsHeader, 2, fields =>
        {
            string asset = fields[0];
            ValidateAsset(asset);
            UInt128 quantity = ParsePositive(fields[1], FixedPoint.QuantityDigits, "quantity");
            if (!assets.Add(asset))
            {
                throw new FormatException($"duplicate holding for asset {asset}");
            }

            return new Holding(asset, quantity);
        });
    }

    internal static IReadOnlyList<Quote> ReadQuotes(string path)
    {
        var keys = new HashSet<(string Asset, string Currency, ulong Timestamp)>();
        return ReadRows(path, QuotesHeader, 5, fields =>
        {
            string asset = fields[0];
            string currency = fields[1];
            ValidateAsset(asset);
            if (!ValidCurrency(currency))
            {
                throw new FormatException("currency must be 2–12 uppercase ASCII letters or digits, including a letter");
            }

            UInt128 price = ParsePositive(fields[2], FixedPoint.PriceDigits, "price");
            ulong timestamp;
            try
            {
                timestamp = ParseSeconds(fields[3]);
            }
            catch (FormatException error)
            {
                throw new FormatException($"invalid timestamp: {error.Message}", error);
            }

            string source = fields[4];
            if (string.IsNullOrWhiteSpace(source) || source.Any(char.IsControl))
            {
                throw new FormatException("source must be nonblank and free of control characters");
            }

            if (!keys.Add((asset, currency, timestamp)))
            {
                throw new FormatException(FormattableString.Invariant($"duplicate quote for {asset} in {currency} at {timestamp}"));
            }

            return new Quote(asset, currency, price, timestamp, source);
        });
    }

    internal static bool ValidCurrency(string value) =>
        value.Length is >= 2 and <= 12
        && value.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character))
        && value.Any(char.IsAsciiLetterUpper);

    internal static ulong ParseSeconds(string value)
    {
        if (value.Length == 0 || !value.All(char.IsAsciiDigit))
        {
            throw new FormatException("expected unsigned integer seconds");
        }

        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seconds))
        {
            throw new FormatException("seconds exceed the supported range");
        }

        return seconds;
    }

    private static string ReadText(string path)
    {
        try
        {
            return StrictUtf8.GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException error)
        {
            throw new FormatException($"{path}: stream did not contain valid UTF-8", error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new FormatException($"{path}: {error.Message}", error);
        }
    }

    private static List<T> ReadRows<T>(string path, string header, int fieldCount, Func<string[], T> parseRow)
    {
        string input = ReadText(path);
        if (input.StartsWith('\uFEFF'))
        {
            input = input[1..];
        }

        string[] lines = input.Split('\n');
        if (GetLine(lines, 0) != header)
        {
            string escapedHeader = header.Replace("\t", "\\t", StringComparison.Ordinal);
            throw new FormatException($"{path}:1: expected header \"{escapedHeader}\"");
        }

        var rows = new List<T>();
        for (int index = 1; index < lines.Length; index++)
        {
            string line = GetLine(lines, index);
            if (line.Length == 0)
            {
                continue;
            }

            int lineNumber = index + 1;
            string[] fields = line.Split('\t');
            if (fields.Length != fieldCount)
            {
                throw new FormatException(FormattableString.Invariant(
                    $"{path}:{lineNumber}: expected {fieldCount} tab-separated fields, found {fields.Length}"));
            }

            try
            {
                rows.Add(parseRow(fields));
            }
            catch (FormatException error)
            {
                throw new FormatException(FormattableString.Invariant($"{path}:{lineNumber}: {error.Message}"), error);
            }
        }

        return rows;
    }

    private static string GetLine(string[] lines, int index)
    {
        string line = lines[index];
        // A carriage return is part of the line ending only when followed by a line feed.
        return index < lines.Length - 1 && line.EndsWith('\r') ? line[..^1] : line;
    }

    private static void ValidateAsset(string asset)
    {
        if (asset.Length == 0 || !asset.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':'))
        {
            throw new FormatException("asset ID must contain only ASCII letters, digits and -_.:");
        }
    }

    private static UInt128 ParsePositive(string value, int digits, string field)
    {
        UInt128 amount;
        try
        {
            amount = FixedPoint.Parse(value, digits);
        }
        catch (FormatException error)
        {
            throw new FormatException($"invalid {field}: {error.Message}", error);
        }

        if (amount == 0)
        {
            throw new FormatException($"{field} must be positive");
        }

        return amount;
    }
}
