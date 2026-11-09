namespace UnionNarrowing.Sample;

// Narrowing through members, helpers, assignments and nested patterns.

public record Refund(string Reason);
public union Settlement(Payment, Refund);
public record Account(string Owner, Payment Method);

public static class PaymentExtensions
{
    // A helper whose body is a single test reads as that test where it's called.
    public static bool IsVoucher(this Payment payment) =>
        payment is Voucher;
}

public sealed class Till(Payment payment)
{
    private readonly Payment _payment = payment;

    // A getter that just returns a readonly field.
    public Payment Tender =>
        _payment;

    // A local function that captures the value is only called after the guard.
    public static string Captured(Payment payment)
    {
        if (payment is Voucher)
        {
            return "voucher";
        }

        return Describe();

        string Describe() =>
            payment switch { Card c => c.Last4, Cash c => $"{c.Tendered}" };
    }

    // A tuple with a bool in it, and a guard on the bool too.
    public static string Fee(Payment payment, bool express)
    {
        if (payment is Voucher || express)
        {
            return "none";
        }

        return (payment, express) switch { (Card, false) => "card", (Cash, _) => "cash" };
    }

    // The element LINQ hands back passed the predicate.
    public static string FirstTender(IEnumerable<Payment> payments) =>
        payments.First(p => p is not Voucher) switch { Card c => c.Last4, Cash c => $"{c.Tendered}" };

    public string Kind()
    {
        if (Tender.IsVoucher())
        {
            return "voucher";
        }

        return Tender switch { Card => "card", Cash => "cash" };
    }

    // A property pattern on the owner rules out the member's case.
    public static string Method(Account account)
    {
        if (account is { Method: Voucher })
        {
            return "voucher";
        }

        return account.Method switch { Card => "card", Cash => "cash" };
    }

    // Null ruled out by a Where filter, which nullable flow state doesn't follow (CS8655).
    public static IEnumerable<string> Names(IEnumerable<Event?> events) =>
        events.Where(e => e is not null).Select(e => e switch { Created => "created", Shipped => "shipped", Ended => "ended" });

    // An assignment says what a value holds: a voucher becomes cash.
    public static decimal Normalized(Payment payment)
    {
        if (payment is Voucher)
        {
            payment = new Cash(0);
        }

        return payment switch { Card => 1, Cash c => c.Tendered };
    }

    // A union member of the object switched on.
    public static string Owner(Account account)
    {
        if (account.Method is Voucher)
        {
            return "voucher";
        }

        return account switch { { Method: Card } => "card", { Method: Cash } => "cash" };
    }

    // A union whose case is a union.
    public static string Settle(Settlement settlement)
    {
        if (settlement is Refund)
        {
            return "refund";
        }

        if (settlement is Payment and Voucher)
        {
            return "voucher";
        }

        return settlement switch { Payment and (Card or Cash) => "paid" };
    }
}
