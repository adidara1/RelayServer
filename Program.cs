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
private static void SaveMessage(string message)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.AppendAllText(HistoryPath,
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm}] {message}{Environment.NewLine}");
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
        var writer = new StreamWriter(client.GetStream(), Encoding.UTF8) { AutoFlush = true };
        foreach (var line in File.ReadLines(HistoryPath).TakeLast(200)) // last 200 lines
            writer.WriteLine(line);
    }
    catch { }
}
    private static List<TcpClient> clients = new List<TcpClient>();
    private static readonly object lockObj = new object();

    static async Task Main(string[] args)
    {
        // Support cloud hosting environment variables for the port, default to 5000
        int port = 8080;
        if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int envPort))
        {
            port = envPort;
        }

        TcpListener listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[Server] Relay server started on port {port}...");

        while (true)
        {
            TcpClient client = await listener.AcceptTcpClientAsync();
            lock (lockObj)
            {
                SendHistory(client); 
                clients.Add(client);
            }
            Console.WriteLine("[Server] A new user connected!");

            _ = HandleClientAsync(client);
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        StreamReader reader = new StreamReader(stream, Encoding.UTF8);

        try
        {
            string? message;
            while ((message = await reader.ReadLineAsync()) != null)
            {
                Broadcast(message, client);
            }
        }
        catch
        {
            // Handle client disconnect
        }
        finally
        {
            lock (lockObj)
            {
                clients.Remove(client);
            }
            client.Close();
            Console.WriteLine("[Server] A user disconnected.");
        }
    }

    private static void Broadcast(string message, TcpClient sender)
    {
        lock (lockObj)
        {
            SaveMessage(message);
            foreach (var client in clients)
            {
                if (client != sender)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        StreamWriter writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                        writer.WriteLine(message);
                    }
                    catch
                    {
                        // Ignore failed writes to dead clients
                    }
                }
            }
        }
    }
}
