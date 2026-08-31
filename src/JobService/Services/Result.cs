namespace JobService.Services;

public enum ServiceError
{
    None = 0,
    // The four business refusals the asset validation can return. They are
    // named after what is wrong rather than folded into one "invalid", because
    // the controller's message has to tell the Agent which correction to make.
    AssetNotFound = 1,
    CustomerMismatch = 2,
    AssetInactive = 3,
    CustomerInactive = 4
}

// Expected failures are returned, not thrown, so the controller's branching is
// explicit and exceptions stay reserved for genuinely exceptional things - the
// customer service being unreachable being the one that qualifies here.
public class Result<T>
{
    private Result(bool isSuccess, T? value, ServiceError error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public ServiceError Error { get; }

    public static Result<T> Success(T value) => new(true, value, ServiceError.None);

    public static Result<T> Failure(ServiceError error) => new(false, default, error);
}
