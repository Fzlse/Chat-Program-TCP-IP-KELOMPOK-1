// ChatClient/MainWindow.xaml.cs
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ChatClient {
    public partial class MainWindow : Window {
        ObservableCollection<string> Messages = new();
        ObservableCollection<string> Users = new();

        TcpClient _tcp;
        StreamReader _reader;
        StreamWriter _writer;
        CancellationTokenSource _cts;
        string _username;

        public MainWindow() {
            InitializeComponent();
            MessagesListBox.ItemsSource = Messages;
            UsersListBox.ItemsSource = Users;
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e) {
            var ip = IpTextBox.Text.Trim();
            if (!int.TryParse(PortTextBox.Text.Trim(), out var port)) { MessageBox.Show("Invalid port"); return; }
            _username = UserTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(_username)) { MessageBox.Show("Enter username"); return; }

            _tcp = new TcpClient();
            try {
                await _tcp.ConnectAsync(ip, port);
                var stream = _tcp.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                // send join
                var join = new ChatMessage { Type = "join", From = _username, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                await _writer.WriteLineAsync(JsonSerializer.Serialize(join));

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));

                ConnectButton.IsEnabled = false;
                DisconnectButton.IsEnabled = true;
                SendButton.IsEnabled = true;
                Messages.Add("[system] connected");
            } catch (Exception ex) {
                MessageBox.Show($"Connect failed: {ex.Message}");
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token) {
            try {
                while (!token.IsCancellationRequested) {
                    var line = await _reader.ReadLineAsync();
                    if (line == null) break;

                    var msg = JsonSerializer.Deserialize<ChatMessage>(line);
                    if (msg == null) continue;
                    var when = DateTimeOffset.FromUnixTimeSeconds(msg.Ts).ToLocalTime().ToString("HH:mm:ss");

                    switch (msg.Type) {
                        case "join":
                            Dispatcher.Invoke(() => {
                                if (!Users.Contains(msg.From)) Users.Add(msg.From);
                                Messages.Add($"[{when}] * {msg.From} joined");
                            });
                            break;
                        case "leave":
                            Dispatcher.Invoke(() => {
                                if (Users.Contains(msg.From)) Users.Remove(msg.From);
                                Messages.Add($"[{when}] * {msg.From} left");
                            });
                            break;
                        case "msg":
                            Dispatcher.Invoke(() => Messages.Add($"[{when}] {msg.From}: {msg.Text}"));
                            break;
                        case "pm":
                            Dispatcher.Invoke(() => Messages.Add($"[{when}] (PM) {msg.From} -> {msg.To}: {msg.Text}"));
                            break;
                        case "sys":
                            // optional: if server sends system messages
                            Dispatcher.Invoke(() => Messages.Add($"[{when}] [system] {msg.Text}"));
                            break;
                        default:
                            break;
                    }
                }
            } catch (Exception ex) {
                Dispatcher.Invoke(() => Messages.Add($"[system] receive loop error: {ex.Message}"));
            } finally {
                Dispatcher.Invoke(() => {
                    Messages.Add("[system] disconnected");
                    ConnectButton.IsEnabled = true;
                    DisconnectButton.IsEnabled = false;
                    SendButton.IsEnabled = false;
                    Users.Clear();
                });
                try { _tcp?.Close(); } catch { }
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e) {
            var text = InputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            ChatMessage msg;
            if (text.StartsWith("/w ")) {
                // private message: /w <user> <text>
                var parts = text.Split(' ', 3);
                if (parts.Length < 3) { Messages.Add("[system] PM format: /w user message"); return; }
                msg = new ChatMessage { Type = "pm", From = _username, To = parts[1], Text = parts[2], Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            } else {
                msg = new ChatMessage { Type = "msg", From = _username, Text = text, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            }

            try {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(msg));
                InputTextBox.Clear();
                // we do NOT add local copy; server will broadcast back and update UI
            } catch (Exception ex) {
                Messages.Add($"[system] send error: {ex.Message}");
            }
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e) {
            try {
                if (_writer != null) {
                    var leave = new ChatMessage { Type = "leave", From = _username, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(leave));
                }
            } catch { }
            _cts?.Cancel();
            try { _tcp?.Close(); } catch { }
            ConnectButton.IsEnabled = true;
            DisconnectButton.IsEnabled = false;
            SendButton.IsEnabled = false;
        }
    }

    public class ChatMessage {
        public string Type { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Text { get; set; }
        public long Ts { get; set; }
    }
}
