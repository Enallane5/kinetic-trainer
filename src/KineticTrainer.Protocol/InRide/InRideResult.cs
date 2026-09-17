using System.Diagnostics.CodeAnalysis;

namespace KineticTrainer.Protocol.InRide;

/// <summary>Success value or a Kinetic SDK error code with a human-readable reason. Never throws for bad input.</summary>
public sealed class InRideResult<T>
{
    private InRideResult(T? value, InRideErrorCode error, string? message)
    {
        Value = value;
        Error = error;
        Message = message;
    }

    public T? Value { get; }

    public InRideErrorCode Error { get; }

    public string? Message { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    public bool IsSuccess => Error == InRideErrorCode.None;

    public static InRideResult<T> Ok(T value) => new(value, InRideErrorCode.None, null);

    public static InRideResult<T> Fail(InRideErrorCode error, string message) => new(default, error, message);

    public override string ToString() => IsSuccess ? $"Ok({Value})" : $"{Error} ({(int)Error}): {Message}";
}
