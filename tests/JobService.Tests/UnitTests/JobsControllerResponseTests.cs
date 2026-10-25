using System.Security.Claims;
using JobService.Controllers;
using JobService.DTOs;
using JobService.Models;
using JobService.Security;
using JobService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobService.Tests;

// Pins every HTTP response the jobs controller maps a service outcome to:
// status code, error key, title and message.
public class JobsControllerResponseTests
{
    private const string TechnicianId = "66666666-6666-6666-6666-666666666666";
    private const string OtherTechnicianId = "77777777-7777-7777-7777-777777777777";

    private static (JobsController Controller, FakeJobRepository Repository, FakeAssetValidationClient Validation) Build(
        string? status = "IN_PROGRESS", string actingTechnicianId = TechnicianId, string role = StaffRoles.Technician)
    {
        var repository = new FakeJobRepository();
        if (status is not null)
        {
            repository.JobToReturn = new Job
            {
                Id = "job-id", JobReference = "JOB-ABC123", Status = status, ServiceCategory = "REPAIR", Priority = "HIGH",
                Region = "UVA", AssignmentId = "assignment-id", AssignedTechnicianId = TechnicianId,
                AssignedTechnicianReference = "TEC-034", AssignedAt = DateTime.UtcNow
            };
        }
        var validation = new FakeAssetValidationClient();
        var controller = new JobsController(new Services.JobService(repository, validation, new FakeEventPublisher(), NullLogger<Services.JobService>.Instance))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("role", role), new Claim("technician_id", actingTechnicianId), new Claim("unique_name", "TEC-034")],
                        "test", "unique_name", "role"))
                }
            }
        };
        return (controller, repository, validation);
    }

    private static CreateJobRequest NewJob() => new()
    {
        CustomerId = "cust-1", AssetId = "asset-1", ServiceCategory = "REPAIR",
        ProblemDescription = "AC not cooling", Priority = "HIGH", Region = "UVA"
    };

    private static void AssertFieldError(IActionResult result, int status, string key, string message)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        var problem = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Equal(status, problem.Status);
        var error = Assert.Single(problem.Errors);
        Assert.Equal(key, error.Key);
        Assert.Equal(message, Assert.Single(error.Value));
    }

    private static ProblemDetails AssertProblem(IActionResult result, int status, string title, string detail)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        var problem = Assert.IsAssignableFrom<ProblemDetails>(objectResult.Value);
        Assert.Equal(status, problem.Status);
        Assert.Equal(title, problem.Title);
        Assert.Equal(detail, problem.Detail);
        return problem;
    }

    // ---------- Create ----------

    [Theory]
    [InlineData(AssetValidationOutcome.AssetNotFound, "assetId", "No asset exists with this id.")]
    [InlineData(AssetValidationOutcome.CustomerMismatch, "assetId", "This asset is registered to a different customer.")]
    [InlineData(AssetValidationOutcome.AssetInactive, "assetId", "This asset has been deactivated and cannot have new jobs raised against it.")]
    [InlineData(AssetValidationOutcome.CustomerInactive, "customerId", "This customer has been deactivated and cannot have new jobs raised.")]
    public async Task Create_MapsEachAssetValidationOutcomeToAFieldConflict(AssetValidationOutcome outcome, string key, string message)
    {
        var (controller, repository, validation) = Build(status: null, role: StaffRoles.Agent);
        validation.OutcomeToReturn = outcome;

        AssertFieldError(await controller.Create(NewJob()), 409, key, message);
        Assert.Equal(0, repository.CreateAsyncCallCount);
    }

    [Fact]
    public async Task Create_Returns503WithTheReason_WhenAssetValidationIsUnavailable()
    {
        var (controller, _, validation) = Build(status: null, role: StaffRoles.Agent);
        validation.ExceptionToThrow = new AssetValidationUnavailableException("Customer & Asset Service timed out.");

        var problem = AssertProblem(await controller.Create(NewJob()), 503, "Asset validation is unavailable.",
            "The Customer & Asset Service could not be reached, so the asset could not be validated. No job was created. Please try again.");

        Assert.Equal("Customer & Asset Service timed out.", problem.Extensions["reason"]);
    }

    // ---------- List filters ----------

    [Theory]
    [InlineData("CREATED")]
    [InlineData("ASSIGNED")]
    [InlineData("IN_PROGRESS")]
    [InlineData("completed")]
    public async Task List_AcceptsEverySupportedStatus(string status)
    {
        var (controller, repository, _) = Build(role: StaffRoles.Dispatcher);

        Assert.IsType<OkObjectResult>(await controller.List(status, null));
        Assert.Equal(status.ToUpperInvariant(), repository.ListStatus);
    }

    [Fact]
    public async Task List_RejectsAnUnsupportedStatus_WithATitledFilterError()
    {
        var (controller, repository, _) = Build(role: StaffRoles.Dispatcher);

        var result = await controller.List("CANCELLED", null);

        AssertFieldError(result, 400, "status", "Status must be CREATED or ASSIGNED.");
        Assert.Equal("One or more query filters are invalid.", ((ValidationProblemDetails)((ObjectResult)result).Value!).Title);
        Assert.Null(repository.ListStatus);
    }

    [Fact]
    public async Task List_RejectsANonGuidTechnicianFilter()
    {
        var (controller, _, _) = Build(role: StaffRoles.Dispatcher);

        AssertFieldError(await controller.List(null, "TEC-034"), 400, "assignedTechnicianId", "AssignedTechnicianId must be a valid GUID.");
    }

    // ---------- Reads ----------

    [Fact]
    public async Task Reads_ReturnNotFound_ForUnknownJobs()
    {
        var (controller, _, _) = Build(status: null, role: StaffRoles.Manager);

        Assert.IsType<NotFoundResult>(await controller.GetById("missing"));
        Assert.IsType<NotFoundResult>(await controller.GetStatusHistory("missing"));
        Assert.IsType<NotFoundResult>(await controller.GetByReference("JOB-NOPE00"));
        Assert.IsType<NotFoundResult>(await controller.GetWorkRecords("missing"));
    }

    [Fact]
    public async Task GetByReference_ReturnsTheJob_WhenItExists()
    {
        var (controller, _, _) = Build(role: StaffRoles.Manager);

        var ok = Assert.IsType<OkObjectResult>(await controller.GetByReference("JOB-ABC123"));

        Assert.Equal("JOB-ABC123", Assert.IsType<JobResponse>(ok.Value).JobReference);
    }

    // ---------- Lifecycle and work-record writes ----------

    [Fact]
    public async Task Writes_ByAnotherTechnician_ReturnTheTitledForbiddenProblem()
    {
        var (controller, _, _) = Build(actingTechnicianId: OtherTechnicianId);

        AssertProblem(await controller.Start("job-id"), 403, "Forbidden.", "The authenticated technician is not the active assignee of this job.");
    }

    [Fact]
    public async Task Start_ReturnsLifecycleConflict_WhenTheJobIsNotAssigned()
    {
        var (controller, repository, _) = Build("IN_PROGRESS");
        repository.StartJobAsyncResult = false;

        AssertProblem(await controller.Start("job-id"), 409, "Invalid lifecycle transition.",
            "Only a job in status ASSIGNED can be started. The job is currently in a different status.");
    }

    [Fact]
    public async Task Complete_ReturnsNotInProgressConflict_ForAnAssignedJob()
    {
        var (controller, _, _) = Build("ASSIGNED");

        AssertProblem(await controller.Complete("job-id"), 409, "Job is not in progress.", "Only a job in status IN_PROGRESS can be completed.");
    }

    [Fact]
    public async Task Complete_ReturnsWorkRecordsRequired_WhenNoWorkHasBeenRecorded()
    {
        var (controller, _, _) = Build("IN_PROGRESS");

        var result = await controller.Complete("job-id");

        AssertFieldError(result, 400, "workRecords", "At least one service work record must be recorded before completing the job.");
        var problem = (ValidationProblemDetails)((ObjectResult)result).Value!;
        Assert.Equal("Work records required.", problem.Title);
        Assert.Equal("At least one service work record must be recorded before completing the job.", problem.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddWorkRecord_RejectsBlankContent(string content)
    {
        var (controller, repository, _) = Build();

        AssertFieldError(await controller.AddWorkRecord("job-id", new CreateWorkRecordRequest { Content = content }), 400, "content", "Work record content is required.");
        Assert.Equal(0, repository.AddWorkRecordCallCount);
    }

    [Fact]
    public async Task AddWorkRecord_ReturnsNotInProgressConflict_ForAnAssignedJob()
    {
        var (controller, _, _) = Build("ASSIGNED");

        AssertProblem(await controller.AddWorkRecord("job-id", new CreateWorkRecordRequest { Content = "Checked wiring" }),
            409, "Job is not in progress.", "Work records can only be added to a job in status IN_PROGRESS.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task UpdateWorkRecord_RejectsBlankContent(string content)
    {
        var (controller, _, _) = Build();

        AssertFieldError(await controller.UpdateWorkRecord("job-id", "record-id", new UpdateWorkRecordRequest { Content = content }),
            400, "content", "Work record content is required.");
    }

    [Fact]
    public async Task UpdateWorkRecord_ReturnsNotInProgressConflict_ForAnAssignedJob()
    {
        var (controller, _, _) = Build("ASSIGNED");

        AssertProblem(await controller.UpdateWorkRecord("job-id", "record-id", new UpdateWorkRecordRequest { Content = "New notes" }),
            409, "Job is not in progress.", "Work records can only be updated on a job in status IN_PROGRESS.");
    }

    [Fact]
    public async Task UpdateWorkRecord_ReturnsNotFound_ForAnUnknownRecord()
    {
        var (controller, _, _) = Build();

        Assert.IsType<NotFoundResult>(await controller.UpdateWorkRecord("job-id", "missing-record", new UpdateWorkRecordRequest { Content = "New notes" }));
    }

    [Fact]
    public async Task DeleteWorkRecord_ReturnsNotInProgressConflict_ForAnAssignedJob()
    {
        var (controller, _, _) = Build("ASSIGNED");

        AssertProblem(await controller.DeleteWorkRecord("job-id", "record-id"),
            409, "Job is not in progress.", "Work records can only be deleted from a job in status IN_PROGRESS.");
    }

    [Fact]
    public async Task DeleteWorkRecord_ReturnsNotFound_ForAnUnknownRecord()
    {
        var (controller, _, _) = Build();

        Assert.IsType<NotFoundResult>(await controller.DeleteWorkRecord("job-id", "missing-record"));
    }
}
