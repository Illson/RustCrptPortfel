namespace DotnetCrptPortfel;

internal sealed record Holding(string Asset, UInt128 Quantity);

internal sealed record Quote(string Asset, string Currency, UInt128 Price, ulong Timestamp, string Source);

internal enum PositionStatus
{
    Priced,
    Stale,
    Missing,
}

internal sealed record Position(Holding Holding, PositionStatus Status, Quote? Quote, UInt128? Value);

internal sealed record Report(IReadOnlyList<Position> Positions, UInt128 PricedSubtotal, bool IsComplete);

internal static class Portfolio
{
    internal static Report Value(
        IReadOnlyList<Holding> holdings,
        IReadOnlyList<Quote> quotes,
        string currency,
        ulong asOf,
        ulong maxAge)
    {
        var latestByAsset = new Dictionary<string, Quote>(StringComparer.Ordinal);
        foreach (Quote quote in quotes)
        {
            if (quote.Currency != currency || quote.Timestamp > asOf)
            {
                continue;
            }

            if (!latestByAsset.TryGetValue(quote.Asset, out Quote? latest) || quote.Timestamp > latest.Timestamp)
            {
                latestByAsset[quote.Asset] = quote;
            }
        }

        var positions = new List<Position>(holdings.Count);
        UInt128 pricedSubtotal = 0;
        foreach (Holding holding in holdings)
        {
            if (!latestByAsset.TryGetValue(holding.Asset, out Quote? quote))
            {
                positions.Add(new(holding, PositionStatus.Missing, null, null));
                continue;
            }

            if (asOf - quote.Timestamp > maxAge)
            {
                positions.Add(new(holding, PositionStatus.Stale, quote, null));
                continue;
            }

            UInt128 value;
            try
            {
                value = checked(holding.Quantity * quote.Price);
            }
            catch (OverflowException exception)
            {
                throw new OverflowException($"value of {holding.Asset} exceeds the supported range", exception);
            }

            try
            {
                pricedSubtotal = checked(pricedSubtotal + value);
            }
            catch (OverflowException exception)
            {
                throw new OverflowException($"portfolio subtotal exceeds the supported range after {holding.Asset}", exception);
            }

            positions.Add(new(holding, PositionStatus.Priced, quote, value));
        }

        bool isComplete = positions.All(position => position.Status == PositionStatus.Priced);
        return new Report(positions, pricedSubtotal, isComplete);
    }
}
