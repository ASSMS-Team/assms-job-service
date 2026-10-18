using System.Security.Claims;
using JobService.Controllers;
using JobService.Models;
using JobService.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobService.Tests;

public class TechnicianJobAccessTests
{
    private const string TechnicianId = "66666666-6666-6666-6666-666666666666";
    private const string OtherId = "77777777-7777-7777-7777-777777777777";
    private static JobsController Controller(string role, string? linkedId, bool assigned = true)
    {
        var repository = new FakeJobRepository { JobToReturn = new Job
        {
            Id = "job-id", Status = "ASSIGNED", AssignmentId = assigned ? "assignment-id" : null,
            AssignedTechnicianId = assigned ? TechnicianId : null,
            AssignedTechnicianReference = assigned ? "TEC-034" : null,
            AssignedAt = assigned ? DateTime.UtcNow : null
        }};
        var controller = new JobsController(new Services.JobService(repository, new FakeAssetValidationClient(), new FakeEventPublisher(), NullLogger<Services.JobService>.Instance));
        var claims = new List<Claim> { new("role", role), new("sub", OtherId), new("unique_name", "TEC-034") };
        if (linkedId is not null) claims.Add(new("technician_id", linkedId));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "unique_name", "role")) }};
        return controller;
    }
    private static Task<IActionResult> Read(JobsController controller, string view) => view switch
    {
        "details" => controller.GetById("job-id"),
        "history" => controller.GetStatusHistory("job-id"),
        _ => controller.GetWorkRecords("job-id")
    };

    [Theory]
    [InlineData("details")]
    [InlineData("history")]
    [InlineData("records")]
    public async Task LinkedAssigneeCanReadDespiteDifferentStaffId(string view) =>
        Assert.IsType<OkObjectResult>(await Read(Controller(StaffRoles.Technician, TechnicianId), view));

    [Theory]
    [InlineData("details", OtherId)]
    [InlineData("history", OtherId)]
    [InlineData("records", OtherId)]
    [InlineData("details", "invalid")]
    [InlineData("history", "invalid")]
    [InlineData("records", "invalid")]
    public async Task ExplicitWrongOrInvalidLinkCannotFallBackToMatchingReference(string view, string linkedId) =>
        Assert.IsType<ForbidResult>(await Read(Controller(StaffRoles.Technician, linkedId), view));

    [Theory]
    [InlineData("details")]
    [InlineData("history")]
    [InlineData("records")]
    public async Task TechnicianCannotReadUnassignedJob(string view) =>
        Assert.IsType<ForbidResult>(await Read(Controller(StaffRoles.Technician, TechnicianId, false), view));

    [Theory]
    [InlineData("details")]
    [InlineData("history")]
    [InlineData("records")]
    public async Task LegacyReferenceAssigneeCanRead(string view) =>
        Assert.IsType<OkObjectResult>(await Read(Controller(StaffRoles.Technician, null), view));

    [Theory]
    [InlineData("details", StaffRoles.Dispatcher)]
    [InlineData("history", StaffRoles.Dispatcher)]
    [InlineData("records", StaffRoles.Dispatcher)]
    [InlineData("details", StaffRoles.Manager)]
    [InlineData("history", StaffRoles.Manager)]
    [InlineData("records", StaffRoles.Manager)]
    public async Task ExistingStaffAccessIsPreserved(string view, string role) =>
        Assert.IsType<OkObjectResult>(await Read(Controller(role, null, false), view));
}
