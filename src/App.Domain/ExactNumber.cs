using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portfel.Domain;

[JsonConverter(typeof(ExactNumberConverter))]
public readonly struct ExactNumber : IEquatable<ExactNumber>, IComparable<ExactNumber>
{
    private readonly BigInteger coefficient;
    private readonly int scale;

    private ExactNumber(BigInteger coefficient, int scale)
    {
        while (scale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }
        this.coefficient = coefficient;
        this.scale = scale;
    }

    public static ExactNumber Zero => default;
    public int Sign => coefficient.Sign;
    public static ExactNumber From(long integer) => new(integer, 0);

    public static ExactNumber Parse(string text)
    {
        if (text.Length is 0 or > 257)
            throw new FormatException("Недопустимая длина десятичного числа.");

        ReadOnlySpan<char> unsigned = text.AsSpan(text[0] == '-' ? 1 : 0);
        int point = unsigned.IndexOf('.');
        ReadOnlySpan<char> whole = point < 0 ? unsigned : unsigned[..point];
        ReadOnlySpan<char> fraction = point < 0 ? [] : unsigned[(point + 1)..];
        if (whole.IsEmpty || (point >= 0 && fraction.IsEmpty)
            || fraction.Length > 96 || !Digits(whole) || !Digits(fraction))
            throw new FormatException("Ожидается десятичная запись с точкой и до 96 дробных знаков.");

        var value = BigInteger.Parse(string.Concat(whole, fraction), CultureInfo.InvariantCulture);
        return new(text[0] == '-' ? -value : value, fraction.Length);
    }

    private static bool Digits(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
            if (!char.IsAsciiDigit(character)) return false;
        return true;
    }

    public ExactNumber Divide(ExactNumber divisor, int digits)
    {
        if (digits is < 0 or > 96) throw new ArgumentOutOfRangeException(nameof(digits));
        int exponent = digits + divisor.scale - scale;
        BigInteger numerator = coefficient * Power(Math.Max(exponent, 0));
        BigInteger denominator = divisor.coefficient * Power(Math.Max(-exponent, 0));
        BigInteger result = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        int midpoint = (BigInteger.Abs(remainder) * 2).CompareTo(BigInteger.Abs(denominator));
        if (midpoint > 0 || (midpoint == 0 && !result.IsEven))
            result += numerator.Sign * denominator.Sign;
        return new(result, digits);
    }

    private static BigInteger Power(int exponent) => BigInteger.Pow(10, exponent);
    private BigInteger AtScale(int target) => coefficient * Power(target - scale);

    public static ExactNumber operator +(ExactNumber left, ExactNumber right)
    {
        int common = Math.Max(left.scale, right.scale);
        return new(left.AtScale(common) + right.AtScale(common), common);
    }

    public static ExactNumber operator -(ExactNumber left, ExactNumber right) =>
        left + new ExactNumber(-right.coefficient, right.scale);

    public static ExactNumber operator *(ExactNumber left, ExactNumber right) =>
        new(left.coefficient * right.coefficient, left.scale + right.scale);

    public int CompareTo(ExactNumber other)
    {
        int common = Math.Max(scale, other.scale);
        return AtScale(common).CompareTo(other.AtScale(common));
    }

    public bool Equals(ExactNumber other) => scale == other.scale && coefficient == other.coefficient;
    public static bool operator ==(ExactNumber left, ExactNumber right) => left.Equals(right);
    public static bool operator !=(ExactNumber left, ExactNumber right) => !left.Equals(right);
    public override bool Equals(object? obj) => obj is ExactNumber other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(coefficient, scale);

    public override string ToString()
    {
        string digits = BigInteger.Abs(coefficient).ToString(CultureInfo.InvariantCulture);
        if (scale > 0)
        {
            digits = digits.PadLeft(scale + 1, '0');
            digits = digits.Insert(digits.Length - scale, ".");
        }
        return coefficient.Sign < 0 ? "-" + digits : digits;
    }
}

public sealed class ExactNumberConverter : JsonConverter<ExactNumber>
{
    public override ExactNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Число должно быть строкой.");
        try { return ExactNumber.Parse(reader.GetString()!); }
        catch (FormatException exception) { throw new JsonException(exception.Message, exception); }
    }

    public override void Write(Utf8JsonWriter writer, ExactNumber value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
