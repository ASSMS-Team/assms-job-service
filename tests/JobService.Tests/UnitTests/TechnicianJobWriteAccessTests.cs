using System.Security.Claims;
using JobService.Controllers;
using JobService.DTOs;
using JobService.Models;
using JobService.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobService.Tests;

// The acting technician for lifecycle and work-record writes comes from the
// token and the job's assignment. A caller can no longer act for another
// technician by naming that technician's id in the request.
public class TechnicianJobWriteAccessTests
{
    private const string TechnicianId = "66666666-6666-6666-6666-666666666666";
    private const string OtherId = "77777777-7777-7777-7777-777777777777";
    private const string RecordId = "record-id";

    private static (JobsController Controller, FakeJobRepository Repository) Controller(
        string? linkedId, string status, bool assigned = true, string username = "TEC-034")
    {
        var repository = new FakeJobRepository { JobToReturn = new Job
        {
            Id = "job-id", JobReference = "JOB-ABC123", Status = status,
            ServiceCategory = "REPAIR", Priority = "HIGH",
            AssignmentId = assigned ? "assignment-id" : null,
            AssignedTechnicianId = assigned ? TechnicianId : null,
            AssignedTechnicianReference = assigned ? "TEC-034" : null,
            AssignedAt = assigned ? DateTime.UtcNow : null
        }};
        repository.StoredWorkRecords.Add(new ServiceWorkRecord
        {
            Id = RecordId, JobId = "job-id", TechnicianId = TechnicianId, Content = "Replaced fan",
            RecordedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        });
        var controller = new JobsController(new Services.JobService(repository, new FakeAssetValidationClient(), new FakeEventPublisher(), NullLogger<Services.JobService>.Instance));
        var claims = new List<Claim> { new("role", StaffRoles.Technician), new("sub", OtherId), new("unique_name", username) };
        if (linkedId is not null) claims.Add(new("technician_id", linkedId));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "unique_name", "role")) }};
        return (controller, repository);
    }

    private static string StatusFor(string action) => action == "start" ? "ASSIGNED" : "IN_PROGRESS";

    private static Task<IActionResult> Write(JobsController controller, string action, string jobId = "job-id") => action switch
    {
        "start" => controller.Start(jobId),
        "complete" => controller.Complete(jobId),
        "add" => controller.AddWorkRecord(jobId, new CreateWorkRecordRequest { Content = "Checked wiring" }),
        "update" => controller.UpdateWorkRecord(jobId, RecordId, new UpdateWorkRecordRequest { Content = "Replaced fan motor" }),
        _ => controller.DeleteWorkRecord(jobId, RecordId)
    };

    private static void AssertNothingChanged(FakeJobRepository repository)
    {
        Assert.Equal(0, repository.StartJobAsyncCallCount);
        Assert.Equal(0, repository.CompleteJobAsyncCallCount);
        Assert.Equal(0, repository.AddWorkRecordCallCount);
        var record = Assert.Single(repository.StoredWorkRecords);
        Assert.Equal("Replaced fan", record.Content);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task LinkedAssigneeCanWrite(string action)
    {
        var (controller, _) = Controller(TechnicianId, StatusFor(action));
        var result = await Write(controller, action);
        Assert.True(result is OkObjectResult or CreatedAtActionResult or NoContentResult, result.GetType().Name);
    }

    [Theory]
    [InlineData("start", OtherId)]
    [InlineData("complete", OtherId)]
    [InlineData("add", OtherId)]
    [InlineData("update", OtherId)]
    [InlineData("delete", OtherId)]
    [InlineData("start", "invalid")]
    [InlineData("complete", "invalid")]
    [InlineData("add", "invalid")]
    [InlineData("update", "invalid")]
    [InlineData("delete", "invalid")]
    public async Task OtherTechnicianOrInvalidLinkIsForbiddenAndChangesNothing(string action, string linkedId)
    {
        // The username still matches the reference: an explicit link that does
        // not match must not fall back to the legacy reference comparison.
        var (controller, repository) = Controller(linkedId, StatusFor(action));
        var result = Assert.IsType<ObjectResult>(await Write(controller, action));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        AssertNothingChanged(repository);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task UnlinkedTechnicianWithAnotherUsernameIsForbidden(string action)
    {
        var (controller, repository) = Controller(null, StatusFor(action), username: "TEC-999");
        var result = Assert.IsType<ObjectResult>(await Write(controller, action));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        AssertNothingChanged(repository);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task TechnicianCannotWriteToUnassignedJob(string action)
    {
        var (controller, repository) = Controller(TechnicianId, StatusFor(action), assigned: false);
        var result = Assert.IsType<ObjectResult>(await Write(controller, action));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        AssertNothingChanged(repository);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task UnknownJobReturnsNotFound(string action)
    {
        var (controller, repository) = Controller(TechnicianId, StatusFor(action));
        repository.JobToReturn = null;
        Assert.IsType<NotFoundResult>(await Write(controller, action, "missing-job"));
    }

    [Fact]
    public async Task LegacyReferenceAssigneeActsAsTheAssignmentTechnician()
    {
        var (controller, repository) = Controller(null, "ASSIGNED");
        Assert.IsType<OkObjectResult>(await controller.Start("job-id"));
        Assert.Equal(TechnicianId, repository.StartJobAsyncCalledWithTechnicianId);
        Assert.Equal(TechnicianId, Assert.Single(repository.StoredStatusHistories).ActorId);
    }

    [Fact]
    public async Task WorkRecordIsAttributedToTheTokenTechnician()
    {
        var (controller, repository) = Controller(TechnicianId, "IN_PROGRESS");
        Assert.IsType<CreatedAtActionResult>(await controller.AddWorkRecord("job-id", new CreateWorkRecordRequest { Content = "Checked wiring" }));
        Assert.Equal(TechnicianId, repository.LastAddedWorkRecord!.TechnicianId);
    }
}
