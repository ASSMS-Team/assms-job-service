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
    CustomerInactive = 4,

    // StartJobAsync failures. Named after the business rule that fired so the
    // controller can return the right HTTP status code and problem body for each.
    JobNotFound = 5,
    // The job exists but its current status does not allow the requested
    // transition, e.g. trying to start a CREATED or already IN_PROGRESS job.
    NotAssigned = 6,
    // The caller supplied a technician id that does not match the active assignee.
<<<<<<< Updated upstream
    NotTheAssignee = 7
=======
    NotTheAssignee = 7,

    // AddWorkRecordAsync failures.
    // The job exists and the technician is assigned, but the job is not in IN_PROGRESS status.
    JobNotInProgress = 8,
    // Work record content was empty or whitespace.
    MissingContent = 9,
    // The requested work record does not exist or belongs to another job.
    WorkRecordNotFound = 10
>>>>>>> Stashed changes
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
