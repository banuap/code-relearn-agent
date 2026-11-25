using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace LegacyOrders.Repositories;

public interface ICustomerRepository
{
    Task<decimal?> GetAvailableCreditAsync(int customerId);
    Task<int> PurgeInactiveAsync(int years);
}

public class CustomerRepository : ICustomerRepository
{
    private readonly IConfiguration _config;
    public CustomerRepository(IConfiguration config) => _config = config;

    private SqlConnection Open() => new SqlConnection(_config.GetConnectionString("Orders"));

    public async Task<decimal?> GetAvailableCreditAsync(int customerId)
    {
        using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<decimal?>(
            "usp_GetAvailableCredit", new { CustomerId = customerId }, commandType: CommandType.StoredProcedure);
    }

    public async Task<int> PurgeInactiveAsync(int years)
    {
        using var conn = Open();
        await conn.OpenAsync();
        using var cmd = new SqlCommand("DELETE FROM tblCustomer WHERE LastOrderDate < DATEADD(year, -@years, GETDATE())", conn);
        cmd.Parameters.AddWithValue("@years", years);
        return await cmd.ExecuteNonQueryAsync();
    }
}
