namespace Acme.Application.Common;

public readonly record struct Result<T>
{
    public T? Value { get; }
    public string? Error { get; }
    public ResultKind Kind { get; }

    private Result(T? value, string? error, ResultKind kind)
    {
        Value = value;
        Error = error;
        Kind = kind;
    }

    public bool IsSuccess => Kind == ResultKind.Success;

    public static Result<T> Success(T value) => new(value, null, ResultKind.Success);
    public static Result<T> NotFound(string error) => new(default, error, ResultKind.NotFound);
    public static Result<T> Conflict(string error) => new(default, error, ResultKind.Conflict);
    public static Result<T> Invalid(string error) => new(default, error, ResultKind.Invalid);
    public static Result<T> Unauthorized(string error) => new(default, error, ResultKind.Unauthorized);
    public static Result<T> Forbidden(string error) => new(default, error, ResultKind.Forbidden);
}

public enum ResultKind
{
    Success,
    NotFound,
    Conflict,
    Invalid,
    Unauthorized,
    Forbidden,
}
