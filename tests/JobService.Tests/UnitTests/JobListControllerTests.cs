using JobService.Controllers;
using JobService.DTOs;
using JobService.Models;
using JobService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobService.Tests;

public class JobListControllerTests
{
    private static JobsController BuildController(FakeJobRepository repository) =>
        new(new Services.JobService(repository, new FakeAssetValidationClient(), new FakeEventPublisher(), NullLogger<Services.JobService>.Instance));

    [Fact]
    public async Task List_RejectsAnUnsupportedStatus()
    {
        var result = await BuildController(new FakeJobRepository()).List("CANCELLED", null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Contains("status", problem.Errors.Keys);
    }

    [Fact]
    public async Task List_RejectsAMalformedTechnicianIdentifier()
    {
        var result = await BuildController(new FakeJobRepository()).List(null, "not-a-guid");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Contains("assignedTechnicianId", problem.Errors.Keys);
    }

    [Fact]
    public async Task List_NormalizesBothValidFilters_AndReturnsAnEmptyList()
    {
        var technicianId = "66666666-6666-6666-6666-666666666666";
        var repository = new FakeJobRepository();

        var result = await BuildController(repository).List("assigned", technicianId);

        Assert.Equal("ASSIGNED", repository.ListStatus);
        Assert.Equal(technicianId, repository.ListAssignedTechnicianId);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<JobResponse>>(ok.Value));
    }

    [Fact]
    public async Task List_ReturnsAssignmentContextFromJobDb()
    {
        var repository = new FakeJobRepository
        {
            JobsToReturn = new[]
            {
                new Job
                {
                    Id = "55555555-5555-5555-5555-555555555555",
                    JobReference = "JOB-7K2M9X",
                    Status = "ASSIGNED",
                    AssignmentId = "77777777-7777-7777-7777-777777777777",
                    AssignedTechnicianId = "66666666-6666-6666-6666-666666666666",
                    AssignedTechnicianReference = "TEC-032",
                    AssignedAt = DateTime.UtcNow
                }
            }
        };

        var result = await BuildController(repository).List(null, null);

        var ok = Assert.IsType<OkObjectResult>(result);
        var job = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<JobResponse>>(ok.Value));
        Assert.Equal("TEC-032", job.Assignment!.TechnicianReference);
    }
}
