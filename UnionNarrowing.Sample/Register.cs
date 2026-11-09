namespace UnionNarrowing.Sample;

// Narrowing through `?.`, outer tuple switches, switch results, designations, helpers and initializers.

public record Customer(string Name, Account? Account);

internal static class Tenders
{
    // Called only after guards, from inside the assembly.
    internal static string Label(Payment payment) =>
        payment switch { Card => "card", Cash => "cash" };
}

public static class Register
{
    // Never assigned anywhere but here.
    private static readonly Payment Float = new Cash(100);

    // A designation inside a subpattern is the member it binds.
    public static string Bound(Account account) =>
        account switch
        {
            { Method: not Voucher and var method } => method switch { Card => "card", Cash => "cash" },
            _ => "voucher",
        };

    // `?.` in the test: past it, wherever Account.Method is read, it isn't a Voucher.
    public static string Method(Customer customer)
    {
        if (customer.Account?.Method is Voucher)
        {
            return "voucher";
        }

        return customer.Account is null ? "none" : Tenders.Label(customer.Account.Method);
    }

    public static decimal OpeningFloat() =>
        Float switch { Cash c => c.Tendered };

    // Nested patterns in a tuple switch.
    public static string Pair(Settlement settlement, bool express)
    {
        if (settlement is Refund)
        {
            return "refund";
        }

        if (settlement is Payment and Voucher)
        {
            return "voucher";
        }

        return (settlement, express) switch { (Payment and (Card or Cash), _) => "paid" };
    }

    // A switch expression's result: a voucher is replaced with cash.
    public static decimal Settled(Payment payment)
    {
        payment = payment switch { Voucher => new Cash(0), var other => other };
        return payment switch { Card => 1, Cash c => c.Tendered };
    }

    // An outer switch over a tuple literal.
    public static string Split(Payment first, Payment second) =>
        (first, second) switch
        {
            (Voucher, _) => "voucher first",
            _ => first switch { Card => "card first", Cash => "cash first" },
        };

    // A deconstructed foreach over a filtered chain.
    public static decimal Tendered(IEnumerable<(Payment Payment, decimal Amount)> lines)
    {
        var total = 0m;
        foreach (var (payment, amount) in lines.Where(l => l.Payment is not Voucher))
        {
            total += payment switch { Card => amount, Cash => amount };
        }

        return total;
    }
}
