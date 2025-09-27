// ChatServer/Program.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class ChatMessage {
    public string Type { get; set; }   // msg|join|leave|pm|sys
    public string From { get; set; }
    public string To { get; set; }     // for pm
    public string Text { get; set; }
    public long Ts { get; set; }       // unix seconds
}

class ClientInfo {
    public TcpClient Tcp { get; set; }
    public string Username { get; set; }
    public StreamReader Reader { get; set; }
    public StreamWriter Writer { get; set; }
    public NetworkStream Stream { get; set; }
}

class Program {
    static readonly Dictionary<string, ClientInfo> clients = new();
    static readonly object clientsLock = new();

    static async Task Main(string[] args) {
        int port = 5000;
        if (args.Length > 0 && int.TryParse(args[0], out var p)) port = p;

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[Server] listening on port {port} (Ctrl+C to stop)");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => {
            e.Cancel = true;
            cts.Cancel();
            listener.Stop();
        };

        try {
            while (!cts.IsCancellationRequested) {
                TcpClient tcp = null;
                try {
                    tcp = await listener.AcceptTcpClientAsync();
                } catch (SocketException) {
                    break;
                }
                _ = HandleClientAsync(tcp);
            }
        } finally {
            Console.WriteLine("[Server] stopping...");
        }
    }

    static async Task HandleClientAsync(TcpClient tcp) {
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

        string username = null;
        try {
            // Expect first message to be a "join" JSON
            var first = await reader.ReadLineAsync();
            if (first == null) { tcp.Close(); return; }

            var joinMsg = JsonSerializer.Deserialize<ChatMessage>(first);
            if (joinMsg?.Type != "join" || string.IsNullOrWhiteSpace(joinMsg.From)) {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new ChatMessage { Type = "sys", Text = "Invalid join" }));
                tcp.Close();
                return;
            }

            username = joinMsg.From.Trim();

            // Ensure unique username (if collision, append number)
            List<string> existingUsers;
            lock (clientsLock) {
                if (clients.ContainsKey(username)) {
                    var baseName = username;
                    int i = 1;
                    while (clients.ContainsKey(username)) username = $"{baseName}{i++}";
                }
                existingUsers = clients.Keys.ToList(); // capture current users
                clients[username] = new ClientInfo { Tcp = tcp, Username = username, Reader = reader, Writer = writer, Stream = stream };
            }

            Console.WriteLine($"[Server] {username} connected");

            // Send existing users to the new client so its client UI can populate the list
            foreach (var user in existingUsers) {
                var joinBack = new ChatMessage { Type = "join", From = user, Text = $"{user} (already online)", Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                await writer.WriteLineAsync(JsonSerializer.Serialize(joinBack));
            }

            // Announce this join to everyone
            await BroadcastAsync(new ChatMessage { Type = "join", From = username, Text = $"{username} joined", Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

            // Read loop
            while (true) {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                var msg = JsonSerializer.Deserialize<ChatMessage>(line);
                if (msg == null) continue;

                msg.From = username; // ensure correct FROM
                msg.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                switch (msg.Type) {
                    case "msg":
                        await BroadcastAsync(msg);
                        break;
                    case "pm":
                        await SendPrivateAsync(msg.To, msg);
                        break;
                    case "leave":
                        goto DISCONNECT;
                    default:
                        // ignore
                        break;
                }
            }

        } catch (Exception ex) {
            Console.WriteLine($"[Server] client error ({username}): {ex.Message}");
        }

    DISCONNECT:
        // cleanup
        if (username != null) {
            lock (clientsLock) { clients.Remove(username); }
            Console.WriteLine($"[Server] {username} disconnected");
            await BroadcastAsync(new ChatMessage { Type = "leave", From = username, Text = $"{username} left", Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        }
        try { tcp.Close(); } catch { }
    }
      
    static async Task BroadcastAsync(ChatMessage msg) {
        var json = JsonSerializer.Serialize(msg);
        List<ClientInfo> snapshot;
        lock (clientsLock) { snapshot = clients.Values.ToList(); }

        foreach (var c in snapshot) {
            try {
                await c.Writer.WriteLineAsync(json);
            } catch {
                // ignore write errors (client likely disconnected)
            }
        }
    }

    static async Task SendPrivateAsync(string to, ChatMessage msg) {
        if (string.IsNullOrWhiteSpace(to)) return;
        ClientInfo target = null;
        lock (clientsLock) { clients.TryGetValue(to, out target); }

        if (target != null) {
            try {
                await target.Writer.WriteLineAsync(JsonSerializer.Serialize(msg));
            } catch { }
        } else {
            // notify sender that target not found
            ClientInfo sender = null;
            lock (clientsLock) { clients.TryGetValue(msg.From, out sender); }
            if (sender != null) {
                try {
                    await sender.Writer.WriteLineAsync(JsonSerializer.Serialize(new ChatMessage { Type = "sys", Text = $"User '{to}' not found", Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
                } catch { }
            }
        }
    }
}
