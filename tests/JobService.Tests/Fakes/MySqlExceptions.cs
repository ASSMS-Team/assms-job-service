using System.Reflection;

using MySqlConnector;

namespace JobService.Tests;

// Builds the driver exceptions a service is expected to catch.
//
// MySqlException has no public constructor - the driver builds it, never
// callers - so it has to be reached through the internal
// (MySqlErrorCode, string) one. Building a real one rather than a stand-in
// matters: the service filters on `catch (MySqlException ex) when (ex.Number
// == 1062)`, and a substitute exception type would take the wrong branch and
// let the test pass for the wrong reason.
//
// The reflection lives here once. It is the part most likely to break on a
// MySqlConnector upgrade, and a second copy is a second thing to remember to
// fix - so every caller goes through this.
public static class MySqlExceptions
{
    // MySQL's ER_DUP_ENTRY: the error a unique index raises when a write would
    // duplicate a key that is already there.
    private const int DuplicateEntryErrorNumber = 1062;

    // message is the driver's text, which callers pass so the exception names
    // the index the write actually collided with. Only Number is behaviour -
    // the service branches on it and nothing else - but a realistic message is
    // what makes a failing test readable.
    public static MySqlException DuplicateKey(string message) =>
        (MySqlException)Activator.CreateInstance(
            typeof(MySqlException),
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            args: new object[] { (MySqlErrorCode)DuplicateEntryErrorNumber, message },
            culture: null)!;
}
