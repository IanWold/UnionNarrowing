namespace UnionNarrowing.Sample;

// More of the narrowing the suppressor understands, in the shapes real code tends to take.

public sealed class Ledger(Payment opening)
{
    private static string Describe(Payment payment) =>
        payment switch { Card c => $"card {c.Last4}", Cash c => $"cash {c.Tendered}" };

    // A switch statement as the guard, and a guard on Value.
    public static decimal Change(Payment payment, decimal due)
    {
        switch (payment)
        {
            case Voucher:
                return 0;
        }

        if (payment.Value is Card)
        {
            return 0;
        }

        return payment switch { Cash cash => cash.Tendered - due };
    }

    // A primary constructor parameter nothing reassigns.
    public string Opening()
    {
        if (opening is Voucher)
        {
            return "voucher";
        }

        return opening switch { Card => "card", Cash => "cash" };
    }

    // A private helper only ever called after the guard.
    public static string Receipt(Payment payment) =>
        payment is Voucher v ? v.Code : Describe(payment);

    // LINQ: the Where filter rules Voucher out for the Select.
    public static IEnumerable<string> Tenders(IEnumerable<Payment> payments) =>
        payments.Where(p => p is not Voucher).Select(p => p switch { Card c => c.Last4, Cash c => $"{c.Tendered}" });

    // A switch that throws for one case rules it out after.
    public static string Validate(Payment payment)
    {
        var kind = payment switch { Voucher => throw new NotSupportedException(), Card => "card", Cash => "cash" };
        return payment switch { Card c => $"{kind} {c.Last4}", Cash c => $"{kind} {c.Tendered}" };
    }
}
