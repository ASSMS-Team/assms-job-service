using MySqlConnector;

namespace JobService.Repositories;

public interface IDbConnectionFactory
{
    MySqlConnection CreateConnection();
}
