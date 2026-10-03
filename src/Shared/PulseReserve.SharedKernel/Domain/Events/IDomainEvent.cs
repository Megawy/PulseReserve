namespace PulseReserve.SharedKernel;

/// <summary>
/// Represents a domain event triggered within an aggregate boundary.
/// </summary>
public interface IDomainEvent
{
    DateTime OccurredOnUtc { get; }
}
