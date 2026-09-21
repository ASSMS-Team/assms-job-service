namespace JobService.Security;

public static class StaffRoles
{
    public const string Agent = "Agent";
    public const string Dispatcher = "Dispatcher";
    public const string Manager = "Manager";

    // Agents raise jobs, while Dispatchers and Managers triage the shared list
    // and open job details. These are explicit role sets instead of a blanket
    // authenticated policy so the API matches the staff workflow.
    public const string JobCreators = Agent + "," + Manager;
    public const string JobViewers = Dispatcher + "," + Manager;
}
