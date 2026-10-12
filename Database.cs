using MySqlConnector;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

public static class Database
{
    private static readonly string ConnectionString = new MySqlConnectionStringBuilder
    {
        Server   = Environment.GetEnvironmentVariable("MYSQLHOST") ?? "localhost",
        Port     = uint.TryParse(Environment.GetEnvironmentVariable("MYSQLPORT"), out var p) ? p : 3306,
        UserID   = Environment.GetEnvironmentVariable("MYSQLUSER") ?? "root",
        Password = Environment.GetEnvironmentVariable("MYSQLPASSWORD") ?? "",
        Database = Environment.GetEnvironmentVariable("MYSQLDATABASE") ?? "railway",
    }.ConnectionString;

    public static MySqlConnection CreateConnection() => new(ConnectionString);

    // Runs once at startup: checks the connection and creates the table if missing
    public static async Task InitializeAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        const string sql = @"
            CREATE TABLE IF NOT EXISTS users (
                id            INT AUTO_INCREMENT PRIMARY KEY,
                username      VARCHAR(20)  NOT NULL,
                password_hash VARCHAR(255) NOT NULL,
                created_at    TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                UNIQUE KEY uq_username (username)
            );";

        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();

        Console.WriteLine("[DB] Connected, users table ready.");
    }
}
