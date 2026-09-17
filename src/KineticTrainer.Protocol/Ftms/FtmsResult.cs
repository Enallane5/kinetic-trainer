using System.Diagnostics.CodeAnalysis;

namespace KineticTrainer.Protocol.Ftms;

/// <summary>Parse result that never throws for malformed input; the reason is always human-readable.</summary>
public sealed class FtmsResult<T>
{
    private FtmsResult(T? value, string? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public string? Error { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static FtmsResult<T> Ok(T value) => new(value, null);

    public static FtmsResult<T> Fail(string error) => new(default, error);

    public override string ToString() => IsSuccess ? $"Ok({Value})" : $"Error: {Error}";
}
