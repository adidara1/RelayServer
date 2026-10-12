using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Linq;

class RelayServer
{
    private static readonly string HistoryPath =
        Environment.GetEnvironmentVariable("HISTORY_PATH") ?? "/data/chat_history.txt";

    // UTF-8 without the byte-order mark, so no invisible characters are sent
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private static List<TcpClient> clients = new List<TcpClient>();
    
    // Tracks whether each username has an active open app/connection
    private static Dictionary<string, bool> userActivity = new Dictionary<string, bool>();
    
    private static readonly object lockObj = new object();

    static async Task Main(string[] args)
    {
        int port = 8080;
        if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int envPort))
        {
            port = envPort;
        }

        try
        {
            await Database.InitializeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Could not connect: {ex.Message}");
            return;
        }

        TcpListener listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[Server] Relay server started on port {port}...");

        while (true)
        {
            TcpClient client = await listener.AcceptTcpClientAsync();
            Console.WriteLine("[Server] Incoming connection...");
            _ = HandleClientAsync(client);   // login happens inside
        }
    }

    // Reads the first line (LOGIN|name|password or REGISTER|name|password).
    // Returns the user's name on success, or null on failure.
    private static async Task<string?> AuthenticateAsync(StreamReader reader, StreamWriter writer)
    {
        string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (line == null) return null;

        string[] parts = line.Split('|', 3);   // the password may itself contain '|'
        if (parts.Length != 3)
        {
            await writer.WriteLineAsync("ERROR|Invalid request.");
            return null;
        }

        string type = parts[0];
        string username = parts[1].Trim();
        string password = parts[2];

        if (type == "REGISTER")
        {
            var (ok, error) = await Database.RegisterAsync(username, password);
            if (!ok)
            {
                await writer.WriteLineAsync($"ERROR|{error}");
                return null;
            }
            await writer.WriteLineAsync($"OK|{username}");
            return username;
        }

        if (type == "LOGIN")
        {
            string? name = await Database.VerifyLoginAsync(username, password);
            if (name == null)
            {
                await writer.WriteLineAsync("ERROR|Wrong username or password.");
                return null;
            }
            await writer.WriteLineAsync($"OK|{name}");
            return name;
        }

        await writer.WriteLineAsync("ERROR|Invalid request.");
        return null;
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        StreamReader reader = new StreamReader(stream, Encoding.UTF8);
        StreamWriter writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
        string? userName = null;

        try
        {
            userName = await AuthenticateAsync(reader, writer);
            if (userName == null) return;   // the finally block closes the connection

            lock (lockObj)
            {
                SendHistory(client);   // only logged-in users see the history
                clients.Add(client);
                userActivity[userName] = true;
            }
            
            Console.WriteLine($"[Server] {userName} joined. Active status: True");
            await BroadcastUserList();

            string? text;
            while ((text = await reader.ReadLineAsync()) != null)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    Broadcast(userName, text, client);
            }
        }
        catch
        {
            // Client disconnected or login timed out
        }
        finally
        {
            lock (lockObj)
            {
                clients.Remove(client);
                if (userName != null)
                {
                    userActivity[userName] = false;
                }
            }
            
            if (userName != null)
            {
                Console.WriteLine($"[Server] {userName} left. Active status: False");
                await BroadcastUserList();
            }
            else
            {
                Console.WriteLine("[Server] A connection closed.");
            }

            client.Close();
        }
    }

    private static async Task BroadcastUserList()
    {
        List<string> activeNames;
        lock (lockObj)
        {
            activeNames = userActivity.Where(pair => pair.Value).Select(pair => pair.Key).ToList();
        }

        string userListMessage = "USERS|" + string.Join(",", activeNames);

        lock (lockObj)
        {
            foreach (var client in clients)
            {
                try
                {
                    var w = new StreamWriter(client.GetStream(), Utf8NoBom) { AutoFlush = true };
                    w.WriteLine(userListMessage);
                }
                catch { }
            }
        }
    }

    private static void Broadcast(string userName, string text, TcpClient sender)
    {
        // Built once, so live and saved messages look identical
        string line = $"[{DateTime.UtcNow.AddHours(2):HH:mm}] {userName}: {text}";

        lock (lockObj)
        {
            SaveMessage(line);
            foreach (var client in clients)
            {
                if (client == sender) continue;
                try
                {
                    var w = new StreamWriter(client.GetStream(), Utf8NoBom) { AutoFlush = true };
                    w.WriteLine(line);
                }
                catch
                {
                    // Ignore failed writes to dead clients
                }
            }
        }
    }

    private static void SaveMessage(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.AppendAllText(HistoryPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server] Could not save message: {ex.Message}");
        }
    }

    private static void SendHistory(TcpClient client)
    {
        if (!File.Exists(HistoryPath)) return;
        try
        {
            var writer = new StreamWriter(client.GetStream(), Utf8NoBom) { AutoFlush = true };
            foreach (var line in File.ReadLines(HistoryPath).TakeLast(200))
                writer.WriteLine(line);
        }
        catch { }
    }
}
