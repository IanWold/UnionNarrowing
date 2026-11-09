namespace UnionNarrowing.Sample;

// One of each kind of narrowing the suppressor understands. CS8509 is an error in this project, so every switch here
// has to be suppressed for it to build.

public record Order(string Id, Payment Payment);

public record Card(string Last4);
public record Cash(decimal Tendered);
public record Voucher(string Code);
public union Payment(Card, Cash, Voucher);

public closed record Event;
public record Created(string Id) : Event;
public record Shipped(string Id, DateTime At) : Event;
public closed record Ended : Event;
public record Delivered(string Id) : Ended;
public record Cancelled(string Id, string Reason) : Ended;

public sealed class Checkout(Payment fallback)
{
    private readonly Payment _fallback = fallback;

    // A copy, and a deconstruction from the switch.
    public static (Payment Next, int Step) Advance(Payment payment, int step)
    {
        var current = payment;
        if (payment is Voucher)
        {
            return (current, step);
        }

        (current, step) = (current switch { Card => new Cash(0), Cash => new Voucher("change") }, step + 1);
        return (current, step);
    }

    // A negated guard: past it, only cards are left.
    public static string CardSuffix(Payment payment)
    {
        if (payment is not Card)
        {
            return "";
        }

        return payment switch { Card card => card.Last4 };
    }

    // An else branch and an else-if chain.
    public static string Describe(Payment payment)
    {
        if (payment is Voucher voucher)
        {
            return $"voucher {voucher.Code}";
        }
        else if (payment is Cash cash)
        {
            return $"cash {cash.Tendered}";
        }
        else
        {
            Console.WriteLine("card payment");
        }

        return payment switch { Card card => $"card {card.Last4}" };
    }

    // A state-machine step: the switch's result is assigned back to what it switches on.
    public static Payment Downgrade(Payment payment)
    {
        if (payment is Voucher)
        {
            return payment;
        }

        payment = payment switch { Card card => new Cash(0), Cash cash => new Voucher($"{cash.Tendered}") };
        return payment;
    }

    // A readonly field, and a positional record property.
    public string Fallback() =>
        _fallback is Voucher ? "none" : _fallback switch { Card => "card", Cash => "cash" };

    public static string FirstNonCash(IEnumerator<Payment> payments)
    {
        var payment = payments.Current;
        while (payment is Cash && payments.MoveNext())
        {
            payment = payments.Current;
        }

        if (payment is Cash)
        {
            return "all cash";
        }

        return payment switch { Card => "card", Voucher => "voucher" };
    }

    // A lambda over a parameter nothing assigns.
    public static IEnumerable<string> Labels(Payment payment, IEnumerable<int> counts)
    {
        if (payment is Voucher)
        {
            return [];
        }

        return counts.Select(n => payment switch { Card => $"{n} by card", Cash => $"{n} in cash" });
    }

    public static bool NeedsChange(Order order) =>
        order.Payment is Cash && order.Payment switch { Cash cash => cash.Tendered > 0 };

    // A nested switch in a discard arm, and a tuple switch.
    public static int Rank(Payment payment) =>
        payment switch
        {
            Voucher => 0,
            _ => payment switch { Card => 1, Cash => 2 },
        };

    // A do loop: ruled out on the way in, and by the condition every time round after that.
    public static int Retries(Func<Payment> next)
    {
        var payment = next();
        if (payment is Voucher)
        {
            return 0;
        }

        var tries = 0;
        do
        {
            tries += payment switch { Card => 1, Cash => 2 };
            payment = next();
        }
        while (payment is not Voucher);

        return tries;
    }

    public static bool SameKind(Payment a, Payment b)
    {
        if (a is Voucher || b is Voucher)
        {
            return false;
        }

        return (a, b) switch
        {
            (Card, Card) or (Cash, Cash) => true,
            (Card, Cash) or (Cash, Card) => false,
        };
    }

    // A closed hierarchy, with a nested closed type.
    public static string Status(Event e)
    {
        if (e is Ended)
        {
            return "done";
        }

        return e switch { Created => "new", Shipped s => $"shipped {s.At:d}" };
    }

    // Loop conditions: in the body, and after the loop.
    public static decimal Tendered(IEnumerable<Payment> payments)
    {
        using var each = payments.GetEnumerator();
        var payment = each.MoveNext() ? each.Current : new Cash(0);
        var total = 0m;
        while (payment is Cash)
        {
            total += payment switch { Cash cash => cash.Tendered };
            payment = each.MoveNext() ? each.Current : new Card("0000");
        }

        return total;
    }
}
