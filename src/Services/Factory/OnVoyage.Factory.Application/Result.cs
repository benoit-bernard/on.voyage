namespace OnVoyage.Factory.Application;

public sealed record Error(string Code, string Message);

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

    public static Result<T> Failure<T>(string code, string message) => new(default, new Error(code, message));
}
