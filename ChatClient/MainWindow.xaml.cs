using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace ChatClient
{
    public partial class MainWindow : Window
    {
        ObservableCollection<string> Messages = new();
        ObservableCollection<string> Users = new();

        TcpClient _tcp;
        StreamReader _reader;
        StreamWriter _writer;
        CancellationTokenSource _cts;
        string _username;
        private bool _isDarkTheme = false;

        // typing support
        private System.Timers.Timer _typingTimer;
        private bool _isTyping = false;
        private readonly Dictionary<string, string> _typingIndicators = new();

        // NEW: tandai kalau disconnect itu disengaja
        private volatile bool _disconnectRequested = false;

        public MainWindow()
        {
            InitializeComponent();
            MessagesListBox.ItemsSource = Messages;
            UsersListBox.ItemsSource = Users;
            InitTypingTimer();
        }

        // Theme toggle
        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDarkTheme)
            {
                Resources["BgColor"] = new SolidColorBrush(Color.FromRgb(245, 240, 255));
                Resources["FgColor"] = new SolidColorBrush(Color.FromRgb(40, 20, 70));
                Resources["ButtonBg"] = new SolidColorBrush(Color.FromRgb(200, 170, 255));
                Resources["ButtonFg"] = new SolidColorBrush(Colors.Black);
                Resources["TextBoxBg"] = new SolidColorBrush(Color.FromRgb(255, 250, 255));
                Resources["TextBoxFg"] = new SolidColorBrush(Color.FromRgb(40, 20, 70));
                Resources["ChatBg"] = new SolidColorBrush(Colors.White);
                Resources["UsersBg"] = new SolidColorBrush(Colors.White);
                ThemeButton.Content = "🌙";
                _isDarkTheme = false;
            }
            else
            {
                Resources["BgColor"] = new SolidColorBrush(Color.FromRgb(26, 20, 37));
                Resources["FgColor"] = new SolidColorBrush(Colors.White);
                Resources["ButtonBg"] = new SolidColorBrush(Color.FromRgb(106, 13, 173));
                Resources["ButtonFg"] = new SolidColorBrush(Colors.White);
                Resources["TextBoxBg"] = new SolidColorBrush(Color.FromRgb(46, 26, 71));
                Resources["TextBoxFg"] = new SolidColorBrush(Colors.White);
                Resources["ChatBg"] = new SolidColorBrush(Color.FromRgb(46, 26, 71));
                Resources["UsersBg"] = new SolidColorBrush(Color.FromRgb(46, 26, 71));
                ThemeButton.Content = "☀️";
                _isDarkTheme = true;
            }
        }

        // Connect
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var ip = IpTextBox.Text.Trim();
            if (!int.TryParse(PortTextBox.Text.Trim(), out var port))
            {
                MessageBox.Show("Invalid port"); return;
            }
            _username = UserTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(_username))
            {
                MessageBox.Show("Enter username"); return;
            }

            _disconnectRequested = false; // reset flag ketika connect
            _tcp = new TcpClient();
            try
            {
                await _tcp.ConnectAsync(ip, port);
                var stream = _tcp.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var join = new ChatMessage { Type = "join", From = _username, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                await _writer.WriteLineAsync(JsonSerializer.Serialize(join));

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));

                ConnectButton.IsEnabled = false;
                DisconnectButton.IsEnabled = true;
                SendButton.IsEnabled = true;
                Messages.Add("[system] connected");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connect failed: {ex.Message}");
            }
        }

        // Disconnect (graceful)
        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _disconnectRequested = true;          // <— tandai manual disconnect
            _typingTimer?.Stop();                 // stop typing timer agar tidak kirim sinyal lagi

            try
            {
                if (_writer != null)
                {
                    var leave = new ChatMessage { Type = "leave", From = _username, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(leave));
                }
            }
            catch { /* ignore */ }

            try { _cts?.Cancel(); } catch { /* ignore */ }

            // Tutup koneksi secara sopan; shutdown dulu baru close (kalau socketnya ada)
            try { _tcp?.Client?.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
            try { _tcp?.Close(); } catch { /* ignore */ }

            // ⚠️ Jangan ubah state tombol di sini.
            // Biar ReceiveLoopAsync yang handle di finally untuk menghindari race.
        }

        // Send
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var text = InputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            ChatMessage msg;
            if (text.StartsWith("/w "))
            {
                var parts = text.Split(' ', 3);
                if (parts.Length < 3) { Messages.Add("[system] PM format: /w user message"); return; }
                msg = new ChatMessage { Type = "pm", From = _username, To = parts[1], Text = parts[2], Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            }
            else
            {
                msg = new ChatMessage { Type = "msg", From = _username, Text = text, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            }

            try
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(msg));
                InputTextBox.Clear();
            }
            catch (Exception ex)
            {
                Messages.Add($"[system] send error: {ex.Message}");
            }
        }

        // Receive loop
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync(); // bisa null (EOF) atau throw saat disconnect
                    if (line == null) break;

                    var msg = JsonSerializer.Deserialize<ChatMessage>(line);
                    if (msg == null) continue;
                    var when = DateTimeOffset.FromUnixTimeSeconds(msg.Ts).ToLocalTime().ToString("HH:mm:ss");

                    switch (msg.Type)
                    {
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
                                // kalau user yang left lagi ngetik, buang indikatornya
                                if (_typingIndicators.TryGetValue(msg.From, out var indicator))
                                {
                                    Messages.Remove(indicator);
                                    _typingIndicators.Remove(msg.From);
                                }
                            });
                            break;
                        case "msg":
                            Dispatcher.Invoke(() => Messages.Add($"[{when}] {msg.From}: {msg.Text}"));
                            break;
                        case "pm":
                            Dispatcher.Invoke(() => Messages.Add($"[{when}] (PM) {msg.From} -> {msg.To}: {msg.Text}"));
                            break;
                        case "sys":
                            Dispatcher.Invoke(() => Messages.Add($"[{when}] [system] {msg.Text}"));
                            break;
                        case "typing":
                            Dispatcher.Invoke(() => {
                                if (!_typingIndicators.ContainsKey(msg.From))
                                {
                                    string indicator = $"[{when}] * {msg.From} is typing...";
                                    _typingIndicators[msg.From] = indicator;
                                    Messages.Add(indicator);
                                }
                            });
                            break;
                        case "stop_typing":
                            Dispatcher.Invoke(() => {
                                if (_typingIndicators.TryGetValue(msg.From, out var indicator))
                                {
                                    Messages.Remove(indicator);
                                    _typingIndicators.Remove(msg.From);
                                }
                            });
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                // HANYA tampilkan error kalau bukan disconnect yang diminta user
                if (!_disconnectRequested)
                {
                    Dispatcher.Invoke(() => Messages.Add($"[system] receive loop error: {ex.Message}"));
                }
            }
            finally
            {
                // Cleanup terpusat di sini biar gak balapan dengan tombol Disconnect
                Dispatcher.Invoke(() => {
                    Messages.Add("[system] disconnected");
                    ConnectButton.IsEnabled = true;
                    DisconnectButton.IsEnabled = false;
                    SendButton.IsEnabled = false;
                    Users.Clear();
                    _typingIndicators.Clear();
                });

                try { _reader?.Dispose(); } catch { }
                try { _writer?.Dispose(); } catch { }
                try { _tcp?.Close(); } catch { }

                _reader = null;
                _writer = null;
                _tcp = null;

                _disconnectRequested = false; // reset flag
            }
        }

        // Typing indicator
        private void InitTypingTimer()
        {
            _typingTimer = new System.Timers.Timer(2000);
            _typingTimer.Elapsed += async (s, e) => {
                if (_isTyping)
                {
                    _isTyping = false;
                    await SendTypingSignal("stop_typing");
                }
            };
            _typingTimer.AutoReset = false;
        }

        private async Task SendTypingSignal(string type)
        {
            if (_writer == null) return;
            try
            {
                var msg = new ChatMessage { Type = type, From = _username, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                await _writer.WriteLineAsync(JsonSerializer.Serialize(msg));
            }
            catch { /* ignore */ }
        }

        private async void InputTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_writer == null) return;
            if (!_isTyping)
            {
                _isTyping = true;
                await SendTypingSignal("typing");
            }
            _typingTimer.Stop();
            _typingTimer.Start();
        }
    }

    public class ChatMessage
    {
        public string Type { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Text { get; set; }
        public long Ts { get; set; }
    }
}
