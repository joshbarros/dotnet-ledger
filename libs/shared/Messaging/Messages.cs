using System.Text.Json;

namespace Ledger.Shared.Messaging;

public record ReserveFunds(Guid PaymentId, Guid OwnerId, Guid AccountId, long AmountCents, string Reference);
public record FundsHeld(Guid PaymentId, Guid OwnerId, Guid AccountId, long AmountCents, string Reference);
public record FundsShort(Guid PaymentId, string Reference, long RequestedCents, long AvailableCents);
public record ReleaseFunds(Guid PaymentId, string Reason);
public record FundsReleased(Guid PaymentId, long AmountCents, string Reason);
public record ReceiptIssued(Guid PaymentId, string Number);
public record ReceiptRejected(Guid PaymentId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
