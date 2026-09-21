using JobService.Controllers;
using JobService.Security;
using Microsoft.AspNetCore.Authorization;

namespace JobService.Tests;

public class JobAuthorizationTests
{
    [Theory]
    [InlineData(nameof(JobsController.List))]
    [InlineData(nameof(JobsController.GetById))]
    [InlineData(nameof(JobsController.GetByReference))]
    public void ReadEndpoints_RequireDispatcherOrManager(string actionName)
    {
        var action = typeof(JobsController).GetMethods().Single(method => method.Name == actionName);
        var authorize = Assert.Single(action.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());

        Assert.Equal(StaffRoles.JobViewers, authorize.Roles);
    }

    [Fact]
    public void Create_RequiresAgentOrManager()
    {
        var action = typeof(JobsController).GetMethod(nameof(JobsController.Create))!;
        var authorize = Assert.Single(action.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());

        Assert.Equal(StaffRoles.JobCreators, authorize.Roles);
    }
}
