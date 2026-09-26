using JobService.DTOs;
using JobService.Security;
using JobService.Services;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobService.Controllers;

[ApiController]
[Route("api/jobs")]
[Produces("application/json")]
public class JobsController : ControllerBase
{
    private static readonly HashSet<string> SupportedStatuses = new(StringComparer.Ordinal)
    {
        "CREATED", "ASSIGNED", "IN_PROGRESS", "COMPLETED"
    };
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
    [Authorize(Roles = StaffRoles.JobCreators)]
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
    /// Lists Job Service-owned jobs. The optional status and assignedTechnicianId
    /// filters are combined with AND semantics. Assignment context is the local
    /// JobAssigned projection in jobdb; this endpoint never reads dispatchdb.
    /// </summary>
    [HttpGet]
    [Authorize(Roles = StaffRoles.JobViewers)]
    [ProducesResponseType(typeof(IReadOnlyList<JobResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? assignedTechnicianId)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        if (normalizedStatus is not null && !SupportedStatuses.Contains(normalizedStatus))
        {
            return BadRequest(InvalidFilter("status", "Status must be CREATED or ASSIGNED."));
        }

        string? normalizedTechnicianId = null;
        if (!string.IsNullOrWhiteSpace(assignedTechnicianId))
        {
            if (!Guid.TryParse(assignedTechnicianId, out var technicianId))
            {
                return BadRequest(InvalidFilter("assignedTechnicianId", "AssignedTechnicianId must be a valid GUID."));
            }

            normalizedTechnicianId = technicianId.ToString();
        }

        return Ok(await _jobService.ListAsync(normalizedStatus, normalizedTechnicianId));
    }

    /// <summary>
    /// Returns a single job by id.
    /// </summary>
    /// <param name="id">The server-generated job id (a GUID string) returned when the job was created.</param>
    /// <response code="200">The job with this id.</response>
    /// <response code="404">No job exists with this id.</response>
    [HttpGet("{id}")]
    [Authorize(Roles = StaffRoles.JobViewers)]
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
    /// Returns the status history of a job.
    /// </summary>
    /// <param name="id">The server-generated job id.</param>
    /// <response code="200">The status history of the job.</response>
    /// <response code="404">No job exists with this id.</response>
    [HttpGet("{id}/history")]
    [Authorize(Roles = StaffRoles.JobViewers)]
    [ProducesResponseType(typeof(IReadOnlyList<JobStatusHistoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatusHistory(string id)
    {
        var history = await _jobService.GetStatusHistoryAsync(id);

        if (history is null)
        {
            return NotFound();
        }

        return Ok(history);
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
    [Authorize(Roles = StaffRoles.JobViewers)]
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

    /// <summary>
    /// Starts an assigned job on behalf of its active technician. The job must
    /// be in status ASSIGNED and the caller must be the active assignee.
    /// On success the job moves to IN_PROGRESS and a JobStatusChanged event is
    /// published to the job-status-changed Kafka topic.
    /// </summary>
    /// <param name="id">The server-generated job id (a GUID string).</param>
    /// <param name="request">The technician id asserting they are starting the job.</param>
    /// <response code="200">The job has been moved to IN_PROGRESS. The response body is the updated job.</response>
    /// <response code="400">TechnicianId was missing or not a valid GUID.</response>
    /// <response code="403">The caller is not the active assignee of this job.</response>
    /// <response code="404">No job exists with this id.</response>
    /// <response code="409">The job is not in a state that allows the start transition (it is not ASSIGNED).</response>
    [HttpPost("{id}/start")]
    [ProducesResponseType(typeof(JobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Start(string id, [FromBody] StartJobRequest request)
    {
        if (!Guid.TryParse(request.TechnicianId, out _))
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["technicianId"] = new[] { "TechnicianId must be a valid GUID." }
                })
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        var result = await _jobService.StartJobAsync(id, request.TechnicianId);

        if (result.Error == ServiceError.JobNotFound)
            return NotFound();

        if (result.Error == ServiceError.NotTheAssignee)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Forbidden.",
                Detail = "The supplied technician id is not the active assignee of this job.",
                Status = StatusCodes.Status403Forbidden
            });
        }

        if (result.Error == ServiceError.NotAssigned)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Invalid lifecycle transition.",
                Detail = "Only a job in status ASSIGNED can be started. The job is currently in a different status.",
                Status = StatusCodes.Status409Conflict
            });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Completes an in-progress job on behalf of its active technician. The job must
    /// be in status IN_PROGRESS, caller must be the active assignee, and required work
    /// records must exist. On success the job moves to COMPLETED and a JobStatusChanged
    /// event is published.
    /// </summary>
    /// <param name="id">The server-generated job id (a GUID string).</param>
    /// <param name="request">The technician id asserting they are completing the job.</param>
    /// <response code="200">The job has been moved to COMPLETED. The response body is the updated job.</response>
    /// <response code="400">TechnicianId was missing/invalid or work records are required.</response>
    /// <response code="403">The caller is not the active assignee of this job.</response>
    /// <response code="404">No job exists with this id.</response>
    /// <response code="409">The job is not in status IN_PROGRESS.</response>
    [HttpPost("{id}/complete")]
    [ProducesResponseType(typeof(JobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Complete(string id, [FromBody] CompleteJobRequest request)
    {
        if (!Guid.TryParse(request.TechnicianId, out _))
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["technicianId"] = new[] { "TechnicianId must be a valid GUID." }
                })
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        var result = await _jobService.CompleteJobAsync(id, request.TechnicianId);

        if (result.Error == ServiceError.JobNotFound)
        {
            return NotFound();
        }

        if (result.Error == ServiceError.NotTheAssignee)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Forbidden.",
                Detail = "The supplied technician id is not the active assignee of this job.",
                Status = StatusCodes.Status403Forbidden
            });
        }

        if (result.Error == ServiceError.JobNotInProgress)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Job is not in progress.",
                Detail = "Only a job in status IN_PROGRESS can be completed.",
                Status = StatusCodes.Status409Conflict
            });
        }

        if (result.Error == ServiceError.WorkRecordsRequired)
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["workRecords"] = new[] { "At least one service work record must be recorded before completing the job." }
                })
            {
                Title = "Work records required.",
                Detail = "At least one service work record must be recorded before completing the job.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Adds a service work record to an active job. The job must be in status IN_PROGRESS
    /// and the caller must be the active assignee.
    /// </summary>
    /// <param name="id">The server-generated job id (a GUID string).</param>
    /// <param name="request">The technician id and work record content.</param>
    /// <response code="201">The work record was created and saved.</response>
    /// <response code="400">TechnicianId was missing/invalid or content was missing/empty.</response>
    /// <response code="403">The caller is not the active assignee of this job.</response>
    /// <response code="404">No job exists with this id.</response>
    /// <response code="409">The job is not in status IN_PROGRESS.</response>
    [HttpPost("{id}/work-records")]
    [ProducesResponseType(typeof(ServiceWorkRecordResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddWorkRecord(string id, [FromBody] CreateWorkRecordRequest request)
    {
        if (!Guid.TryParse(request.TechnicianId, out _))
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["technicianId"] = new[] { "TechnicianId must be a valid GUID." }
                })
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["content"] = new[] { "Work record content is required." }
                })
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        var result = await _jobService.AddWorkRecordAsync(id, request.TechnicianId, request.Content);

        if (result.Error == ServiceError.JobNotFound)
            return NotFound();

        if (result.Error == ServiceError.NotTheAssignee)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Forbidden.",
                Detail = "The supplied technician id is not the active assignee of this job.",
                Status = StatusCodes.Status403Forbidden
            });
        }

        if (result.Error == ServiceError.JobNotInProgress)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Job is not in progress.",
                Detail = "Work records can only be added to a job in status IN_PROGRESS.",
                Status = StatusCodes.Status409Conflict
            });
        }

        if (result.Error == ServiceError.MissingContent)
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["content"] = new[] { "Work record content is required." }
                })
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        return CreatedAtAction(
            nameof(GetWorkRecords),
            new { id },
            result.Value);
    }

    /// <summary>
    /// Retrieves all service work records for the specified job.
    /// </summary>
    /// <param name="id">The server-generated job id (a GUID string).</param>
    /// <response code="200">The list of service work records for this job.</response>
    /// <response code="404">No job exists with this id.</response>
    [HttpGet("{id}/work-records")]
    [Authorize(Roles = StaffRoles.JobViewers)]
    [ProducesResponseType(typeof(IReadOnlyList<ServiceWorkRecordResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWorkRecords(string id)
    {
        var records = await _jobService.GetWorkRecordsAsync(id);

        if (records is null)
        {
            return NotFound();
        }

        return Ok(records);
    }

    /// <summary>
    /// Updates a work record on an in-progress job. Only the active assignee may update it.
    /// </summary>
    [HttpPut("{id}/work-records/{recordId}")]
    [ProducesResponseType(typeof(ServiceWorkRecordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateWorkRecord(string id, string recordId, [FromBody] UpdateWorkRecordRequest request)
    {
        if (!Guid.TryParse(request.TechnicianId, out _))
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["technicianId"] = new[] { "TechnicianId must be a valid GUID." }
                })
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["content"] = new[] { "Work record content is required." }
                })
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        var result = await _jobService.UpdateWorkRecordAsync(id, recordId, request.TechnicianId, request.Content);

        if (result.Error is ServiceError.JobNotFound or ServiceError.WorkRecordNotFound)
        {
            return NotFound();
        }

        if (result.Error == ServiceError.NotTheAssignee)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Forbidden.",
                Detail = "The supplied technician id is not the active assignee of this job.",
                Status = StatusCodes.Status403Forbidden
            });
        }

        if (result.Error == ServiceError.JobNotInProgress)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Job is not in progress.",
                Detail = "Work records can only be updated on a job in status IN_PROGRESS.",
                Status = StatusCodes.Status409Conflict
            });
        }

        if (result.Error == ServiceError.MissingContent)
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["content"] = new[] { "Work record content is required." }
                })
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Deletes a draft work record from an in-progress job. Only the active assignee may delete it.
    /// </summary>
    /// <param name="id">The server-generated job id (a GUID string).</param>
    /// <param name="recordId">The server-generated work record id.</param>
    /// <param name="technicianId">The id of the technician requesting the deletion.</param>
    /// <response code="204">The work record was removed.</response>
    /// <response code="400">TechnicianId was missing or not a valid GUID.</response>
    /// <response code="403">The caller is not the active assignee of this job.</response>
    /// <response code="404">No job or work record exists with this id.</response>
    /// <response code="409">The job is not in status IN_PROGRESS.</response>
    [HttpDelete("{id}/work-records/{recordId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteWorkRecord(
        string id,
        string recordId,
        [FromQuery] string? technicianId)
    {
        var effectiveTechnicianId = technicianId;
        if (string.IsNullOrWhiteSpace(effectiveTechnicianId) && Request.Headers.TryGetValue("X-Technician-Id", out var headerTechId))
        {
            effectiveTechnicianId = headerTechId.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(effectiveTechnicianId) && Request.HasJsonContentType())
        {
            try
            {
                using var reader = new StreamReader(Request.Body);
                var bodyStr = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(bodyStr))
                {
                    using var doc = JsonDocument.Parse(bodyStr);
                    if (doc.RootElement.TryGetProperty("technicianId", out var prop))
                    {
                        effectiveTechnicianId = prop.GetString();
                    }
                }
            }
            catch
            {
                // Fall through to validation below
            }
        }

        if (string.IsNullOrWhiteSpace(effectiveTechnicianId) || !Guid.TryParse(effectiveTechnicianId, out _))
        {
            return BadRequest(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["technicianId"] = new[] { "TechnicianId must be a valid GUID." }
                })
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        var result = await _jobService.DeleteWorkRecordAsync(id, recordId, effectiveTechnicianId);

        if (result.Error is ServiceError.JobNotFound or ServiceError.WorkRecordNotFound)
        {
            return NotFound();
        }

        if (result.Error == ServiceError.NotTheAssignee)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Forbidden.",
                Detail = "The supplied technician id is not the active assignee of this job.",
                Status = StatusCodes.Status403Forbidden
            });
        }

        if (result.Error == ServiceError.JobNotInProgress)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Job is not in progress.",
                Detail = "Work records can only be deleted from a job in status IN_PROGRESS.",
                Status = StatusCodes.Status409Conflict
            });
        }

        return NoContent();
    }

    private static ValidationProblemDetails InvalidFilter(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = new[] { message } })
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more query filters are invalid."
        };
}
