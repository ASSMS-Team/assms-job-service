using JobService.DTOs;
using JobService.Services;

using Microsoft.AspNetCore.Mvc;

namespace JobService.Controllers;

[ApiController]
[Route("api/jobs")]
[Produces("application/json")]
public class JobsController : ControllerBase
{
    // Qualified: the class shares its name with the root namespace, so the bare
    // name would bind to the namespace and not compile.
    private readonly Services.JobService _jobService;

    public JobsController(Services.JobService jobService)
    {
        _jobService = jobService;
    }

    // [ApiController] returns 400 with ValidationProblemDetails before this runs,
    // so there is no validation code here - only the business rules to branch on.
    /// <summary>
    /// Raises a new job against a customer's asset. The job is created with
    /// status CREATED, a server-generated id and a unique JOB- reference. The
    /// asset and customer are validated against the Customer &amp; Asset Service
    /// before anything is written.
    /// </summary>
    /// <param name="request">Customer id, asset id, service category, problem description, priority and region.</param>
    /// <response code="201">Job created. The body is the stored job and the Location header points at GET /api/jobs/{id}.</response>
    /// <response code="400">A field failed validation - a missing customer id, asset id, service category, problem description, priority or region, a value over its maximum length, or a service category, priority or region outside its permitted values. Errors are keyed by field name.</response>
    /// <response code="409">The asset and customer are a pairing no job can be raised against: no such asset, the asset belongs to a different customer, the asset is inactive, or the customer is inactive. The four are told apart by the key and message in the body.</response>
    /// <response code="503">The Customer &amp; Asset Service could not be reached, so the asset could not be validated. No job was created and the request can be retried unchanged.</response>
    [HttpPost]
    [ProducesResponseType(typeof(JobResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create([FromBody] CreateJobRequest request)
    {
        Result<JobResponse> result;

        try
        {
            result = await _jobService.CreateAsync(request);
        }
        catch (AssetValidationUnavailableException ex)
        {
            // Not a 409: nothing about the request was found to be wrong. The
            // dependency did not answer, which is our failure and not the
            // Agent's, so it is a 5xx and the request is safe to repeat.
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Asset validation is unavailable.",
                Detail = "The Customer & Asset Service could not be reached, so the asset could not be validated. No job was created. Please try again.",
                Status = StatusCodes.Status503ServiceUnavailable,
                Extensions = { ["reason"] = ex.Message }
            });
        }

        // Keyed on a field rather than returned as a 404: the addressed resource
        // is the jobs collection, which exists. It is a value in the body that
        // is wrong, so it renders against that input.
        if (result.Error == ServiceError.AssetNotFound)
        {
            return Conflict(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["assetId"] = new[] { "No asset exists with this id." }
            })
            {
                Status = StatusCodes.Status409Conflict
            });
        }

        if (result.Error == ServiceError.CustomerMismatch)
        {
            // Keyed on "assetId" rather than "customerId": the Agent picks the
            // customer first and then their equipment, so the asset is the
            // field to correct.
            return Conflict(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["assetId"] = new[] { "This asset is registered to a different customer." }
            })
            {
                Status = StatusCodes.Status409Conflict
            });
        }

        if (result.Error == ServiceError.AssetInactive)
        {
            return Conflict(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["assetId"] = new[] { "This asset has been deactivated and cannot have new jobs raised against it." }
            })
            {
                Status = StatusCodes.Status409Conflict
            });
        }

        if (result.Error == ServiceError.CustomerInactive)
        {
            return Conflict(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["customerId"] = new[] { "This customer has been deactivated and cannot have new jobs raised." }
            })
            {
                Status = StatusCodes.Status409Conflict
            });
        }

        return CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value);
    }

    /// <summary>
    /// Returns a single job by id.
    /// </summary>
    /// <param name="id">The server-generated job id (a GUID string) returned when the job was created.</param>
    /// <response code="200">The job with this id.</response>
    /// <response code="404">No job exists with this id.</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(JobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string id)
    {
        var job = await _jobService.GetByIdAsync(id);

        if (job is null)
        {
            return NotFound();
        }

        return Ok(job);
    }

    /// <summary>
    /// Returns a single job by its human-readable reference - the JOB- handle an
    /// Agent has to hand when a customer calls about existing work.
    /// </summary>
    /// <param name="jobReference">The job reference, for example JOB-7K2M9X.</param>
    /// <response code="200">The job with this reference.</response>
    /// <response code="404">No job exists with this reference.</response>
    // A separate segment rather than a second meaning for {id}: the two are
    // different formats and guessing which one was supplied would make a
    // mistyped id look like a missing reference.
    [HttpGet("reference/{jobReference}")]
    [ProducesResponseType(typeof(JobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByReference(string jobReference)
    {
        var job = await _jobService.GetByReferenceAsync(jobReference);

        if (job is null)
        {
            return NotFound();
        }

        return Ok(job);
    }
}
