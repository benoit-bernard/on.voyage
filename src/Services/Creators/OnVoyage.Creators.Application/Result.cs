using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Application;

/// <summary><c>Suggestion</c> is set when a taken handle is refused: the first free variant.</summary>
public sealed record Error(string Code, string Message, string? Suggestion = null);

public sealed record Result<T>
{
    internal Result(T? value, Error? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public Error? Error { get; }

    public bool IsSuccess => Error is null;
}

public static class Result
{
    public static Result<T> Success<T>(T value) => new(value, null);

    public static Result<T> Failure<T>(string code, string message, string? suggestion = null) => new(default, new Error(code, message, suggestion));

    public static Result<T> Failure<T>(Violation violation) => new(default, new Error(violation.Code, violation.Message));
}
