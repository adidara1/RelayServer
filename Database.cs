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
    // --- Password hashing: only a salted hash is stored, never the password ---

private static string HashPassword(string password)
{
    byte[] salt = RandomNumberGenerator.GetBytes(16);
    byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
    return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
}

private static bool CheckPassword(string password, string stored)
{
    var parts = stored.Split(':');
    if (parts.Length != 2) return false;

    byte[] salt = Convert.FromBase64String(parts[0]);
    byte[] expected = Convert.FromBase64String(parts[1]);
    byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
    return CryptographicOperations.FixedTimeEquals(expected, actual);
}

// --- Register: the UNIQUE column on username rejects duplicates ---

public static async Task<(bool Ok, string Error)> RegisterAsync(string username, string password)
{
    if (!Regex.IsMatch(username, "^[A-Za-z0-9_]{3,20}$"))
        return (false, "Username must be 3-20 letters, numbers or underscores.");
    if (password.Length < 6)
        return (false, "Password must be at least 6 characters.");

    try
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        await using var cmd = new MySqlCommand(
            "INSERT INTO users (username, password_hash) VALUES (@u, @h)", conn);
        cmd.Parameters.AddWithValue("@u", username);
        cmd.Parameters.AddWithValue("@h", HashPassword(password));
        await cmd.ExecuteNonQueryAsync();

        return (true, "");
    }
    catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
    {
        return (false, "That username is already taken.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DB] Register failed: {ex.Message}");
        return (false, "Server error, please try again.");
    }
}

// --- Login: returns the username as stored in the DB, or null if wrong ---

public static async Task<string?> VerifyLoginAsync(string username, string password)
{
    try
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        await using var cmd = new MySqlCommand(
            "SELECT username, password_hash FROM users WHERE username = @u", conn);
        cmd.Parameters.AddWithValue("@u", username);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        string storedName = reader.GetString(0);
        string storedHash = reader.GetString(1);
        return CheckPassword(password, storedHash) ? storedName : null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DB] Login check failed: {ex.Message}");
        return null;
    }
}
}
