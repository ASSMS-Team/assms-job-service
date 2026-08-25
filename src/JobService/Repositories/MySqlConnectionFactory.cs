using MySqlConnector;

namespace JobService.Repositories;

public class MySqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public MySqlConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public MySqlConnection CreateConnection() => new MySqlConnection(_connectionString);
}
