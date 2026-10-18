using JobService.Controllers;
using JobService.DTOs;
using JobService.Messaging.Contracts;
using JobService.Messaging.Producers;
using JobService.Models;
using JobService.Repositories;
using JobService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace JobService.Tests;

public class JobsControllerMockTests
{
    private const string CustomerId = "11111111-1111-1111-1111-111111111111";
    private const string AssetId = "22222222-2222-2222-2222-222222222222";
    private const string TechnicianId = "33333333-3333-3333-3333-333333333333";
    private const string JobId = "55555555-5555-5555-5555-555555555555";

    private readonly Mock<IJobRepository> _repositoryMock;
    private readonly Mock<IAssetValidationClient> _validationClientMock;
    private readonly Mock<IEventPublisher> _eventPublisherMock;
    private readonly JobsController _controller;

    public JobsControllerMockTests()
    {
        _repositoryMock = new Mock<IJobRepository>(MockBehavior.Strict);
        _validationClientMock = new Mock<IAssetValidationClient>(MockBehavior.Strict);
        _eventPublisherMock = new Mock<IEventPublisher>(MockBehavior.Strict);

        var service = new Services.JobService(
            _repositoryMock.Object,
            _validationClientMock.Object,
            _eventPublisherMock.Object,
            NullLogger<Services.JobService>.Instance);

        _controller = new JobsController(service);
    }

    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData("CANCELLED")]
    [InlineData("PENDING")]
    [InlineData("INVALID_STATUS")]
    public async Task List_RejectsUnsupportedStatusFilter_ReturnsBadRequest(string unsupportedStatus)
    {
        // Act
        var result = await _controller.List(unsupportedStatus, null);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Contains("status", problem.Errors.Keys);

        _repositoryMock.Verify(r => r.ListAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    [InlineData("xyz")]
    public async Task List_RejectsMalformedTechnicianGuid_ReturnsBadRequest(string malformedId)
    {
        // Act
        var result = await _controller.List(null, malformedId);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Contains("assignedTechnicianId", problem.Errors.Keys);

        _repositoryMock.Verify(r => r.ListAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("created", "CREATED")]
    [InlineData("assigned", "ASSIGNED")]
    [InlineData("in_progress", "IN_PROGRESS")]
    [InlineData("completed", "COMPLETED")]
    public async Task List_ValidNormalizedStatusAndTechnician_CallsRepositoryAndReturnsOk(string inputStatus, string expectedStatus)
    {
        // Arrange
        _repositoryMock
            .Setup(r => r.ListAsync(expectedStatus, TechnicianId))
            .ReturnsAsync(new List<Job>());

        // Act
        var result = await _controller.List(inputStatus, TechnicianId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.IsAssignableFrom<IReadOnlyList<JobResponse>>(okResult.Value);

        _repositoryMock.Verify(r => r.ListAsync(expectedStatus, TechnicianId), Times.Once);
    }

    [Fact]
    public async Task Create_AssetValidationThrowsUnavailable_Returns503ServiceUnavailable()
    {
        // Arrange
        var request = new CreateJobRequest
        {
            CustomerId = CustomerId,
            AssetId = AssetId,
            ServiceCategory = "REPAIR",
            ProblemDescription = "Heater broken",
            Priority = "HIGH",
            Region = "WESTERN"
        };

        _validationClientMock
            .Setup(v => v.ValidateAsync(AssetId, CustomerId))
            .ThrowsAsync(new AssetValidationUnavailableException("Dependency down"));

        // Act
        var result = await _controller.Create(request);

        // Assert
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(statusResult.Value);
        Assert.Equal("Asset validation is unavailable.", problem.Title);

        _repositoryMock.Verify(r => r.CreateAsync(It.IsAny<Job>()), Times.Never);
    }

    [Theory]
    [InlineData(AssetValidationOutcome.AssetNotFound, "assetId")]
    [InlineData(AssetValidationOutcome.CustomerMismatch, "assetId")]
    [InlineData(AssetValidationOutcome.AssetInactive, "assetId")]
    [InlineData(AssetValidationOutcome.CustomerInactive, "customerId")]
    public async Task Create_AssetValidationFails_Returns409ConflictWithKeyedField(AssetValidationOutcome outcome, string expectedKey)
    {
        // Arrange
        var request = new CreateJobRequest
        {
            CustomerId = CustomerId,
            AssetId = AssetId,
            ServiceCategory = "REPAIR",
            ProblemDescription = "Heater broken",
            Priority = "HIGH",
            Region = "WESTERN"
        };

        _validationClientMock
            .Setup(v => v.ValidateAsync(AssetId, CustomerId))
            .ReturnsAsync(outcome);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var conflictResult = Assert.IsType<ConflictObjectResult>(result);
        var problem = Assert.IsType<ValidationProblemDetails>(conflictResult.Value);
        Assert.Contains(expectedKey, problem.Errors.Keys);

        _repositoryMock.Verify(r => r.CreateAsync(It.IsAny<Job>()), Times.Never);
    }

    [Fact]
    public async Task GetById_JobNotFound_ReturnsNotFound()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByIdAsync(JobId)).ReturnsAsync((Job?)null);

        // Act
        var result = await _controller.GetById(JobId);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _repositoryMock.Verify(r => r.GetByIdAsync(JobId), Times.Once);
    }

    [Fact]
    public async Task GetByReference_JobNotFound_ReturnsNotFound()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByReferenceAsync("JOB-XYZ123")).ReturnsAsync((Job?)null);

        // Act
        var result = await _controller.GetByReference("JOB-XYZ123");

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _repositoryMock.Verify(r => r.GetByReferenceAsync("JOB-XYZ123"), Times.Once);
    }
}
