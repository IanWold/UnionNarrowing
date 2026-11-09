namespace UnionNarrowing.Sample;

// More kinds of narrowing: a union whose case type is a closed hierarchy, a constructor's checks, a loop that ends at a
// break, a throw where a value would be, a dictionary's entries, and an arm's own `when` clause.

public closed record Missing;
public record NotFound(string Id) : Missing;
public record Expired(string Id, DateTime At) : Missing;
public union Lookup(Order, Missing);

public sealed class Dispatch
{
    private readonly Payment _payment;

    // Every constructor checks what it sets, so the payment is never a voucher.
    public Dispatch(Payment payment)
    {
        if (payment is Voucher)
        {
            throw new ArgumentException("Vouchers can't be dispatched.", nameof(payment));
        }

        _payment = payment;
    }

    // The entries a filter let through.
    public static IEnumerable<string> Cards(Dictionary<string, Payment> byOrder) =>
        byOrder.Where(entry => entry.Value is Card).Select(entry => entry.Value switch { Card card => card.Last4 });

    // A closed case type is its leaf types.
    public static string Explain(Lookup lookup)
    {
        if (lookup is Order order)
        {
            return order.Id;
        }

        return lookup switch { NotFound missing => $"no {missing.Id}", Expired expired => $"{expired.Id} expired {expired.At:d}" };
    }

    // The loop only ends at the break, where the payment is a card.
    public static string FirstCard(Func<Payment> next)
    {
        Payment payment;
        while (true)
        {
            payment = next();
            if (payment is Card)
            {
                break;
            }
        }

        return payment switch { Card card => card.Last4 };
    }

    public string Method() =>
        _payment switch { Card => "card", Cash => "cash" };

    // `?? throw` gives what's on its left, which isn't a voucher.
    public static string Paid(Payment? payment)
    {
        if (payment is Voucher)
        {
            return "";
        }

        var paid = payment ?? throw new ArgumentNullException(nameof(payment));
        return paid switch { Card card => card.Last4, Cash cash => $"{cash.Tendered}" };
    }

    // In an arm, its `when` clause held.
    public static string Size(Order order, decimal limit) =>
        order.Payment switch
        {
            Cash cash when cash.Tendered > limit => "large",
            _ when order.Payment is not Voucher => order.Payment switch { Card => "card", Cash => "cash" },
            _ => "voucher",
        };
}
