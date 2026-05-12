using MessagePack;
using System;
using Windows.IPC.Contracts;
using Windows.IPC.Contracts.Messages;
using Windows.IPC.Extensions;
using Windows.IPC.UWP;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace UwpClient
{
    public sealed partial class MainPage : Page
    {
        private UwpIpcClient _client;

        public MainPage()
        {
            InitializeComponent();
            InitializeClient();
        }

        private void InitializeClient()
        {
            _client = new UwpIpcClient("YourAppPipe");

            _client.StateChanged += async (s, e) =>
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    UpdateConnectionState(e.NewState);
                    Log($"状态变更: {e.OldState} -> {e.NewState}");
                    if (e.NewState != UwpConnectionState.Connected)
                    {
                        ConnectButton.IsEnabled = true;
                    }
                });
            };

            _client.NotificationReceived += async (s, e) =>
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    // 调试：显示 MessageType
                    Log($"Received: {e.MessageType}");

                    // 处理服务器广播
                    if (e.MessageType == MessageTypes.ServerBroadcast)
                    {
                        try
                        {
                            var broadcast = MessagePack.MessagePackSerializer.Deserialize<BroadcastNotification>(e.Payload);
                            Log($"📢 广播: {broadcast.Message}");
                            Log($"   时间: {broadcast.Timestamp:HH:mm:ss}");
                            Log($"   类别: {broadcast.Category}");
                        }
                        catch (Exception ex)
                        {
                            Log($"解析广播失败: {ex.Message}");
                        }
                    }
                    else if (e.MessageType != MessageTypes.Progress)
                    {
                        Log($"收到通知: {e.MessageType}");
                    }
                });
            };

            _client.ProgressChanged += async (s, e) =>
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    ProgressBar.Value = e.Percentage;
                    ProgressBar.Visibility = e.Percentage >= 100
                        ? Visibility.Collapsed
                        : Visibility.Visible;

                    if (!string.IsNullOrEmpty(e.Message))
                    {
                        Log($"进度: {e.Percentage:F1}% - {e.Message}");
                    }
                });
            };
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConnectButton.IsEnabled = false;
                Log("正在连接...");

                await _client.ConnectAsync();

                Log("连接成功！");
            }
            catch (Exception ex)
            {
                Log($"连接失败: {ex.Message}");
                ConnectButton.IsEnabled = true;
            }
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DisconnectButton.IsEnabled = false;
                Log("正在断开连接...");

                await _client.DisconnectAsync();

                Log("已断开连接");
                ServerInfoPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Log($"断开连接失败: {ex.Message}");
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync();
        }

        private async void MessageInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter && SendButton.IsEnabled)
            {
                await SendMessageAsync();
            }
        }

        private async System.Threading.Tasks.Task SendMessageAsync()
        {
            if (string.IsNullOrWhiteSpace(MessageInput.Text))
            {
                Log("请输入消息");
                return;
            }

            try
            {
                SendButton.IsEnabled = false;
                ProgressBar.Visibility = Visibility.Visible;
                ProgressBar.Value = 0;

                var message = MessageInput.Text;
                Log($"发送: {message}");

                // 使用扩展方法
                var echo = await _client.SendEchoStringAsync(message, timeoutMs: 10000);

                Log($"响应: {echo}");

                MessageInput.Text = string.Empty;
                ProgressBar.Value = 100;
            }
            catch (Exception ex)
            {
                Log($"发送失败: {ex.Message}");
            }
            finally
            {
                SendButton.IsEnabled = _client.IsConnected;
                await System.Threading.Tasks.Task.Delay(500);
                ProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private async void ServerInfoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ServerInfoButton.IsEnabled = false;
                Log("获取服务器信息...");

                // 使用扩展方法
                var info = await _client.GetServerInfoAsync();

                ServerVersionText.Text = $"版本: {info.Version}";
                ServerClientsText.Text = $"连接数: {info.ConnectedClients}";
                ServerUptimeText.Text = $"运行时间: {info.Uptime:hh\\:mm\\:ss}";

                ServerInfoPanel.Visibility = Visibility.Visible;
                Log($"服务器: {info.MachineName} (PID: {info.ProcessId})");
            }
            catch (Exception ex)
            {
                Log($"获取服务器信息失败: {ex.Message}");
            }
            finally
            {
                ServerInfoButton.IsEnabled = _client.IsConnected;
            }
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogText.Text = string.Empty;
        }

        private void UpdateConnectionState(UwpConnectionState state)
        {
            switch (state)
            {
                case UwpConnectionState.Disconnected:
                    StatusText.Text = "未连接";
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Gray);
                    ConnectButton.IsEnabled = true;
                    DisconnectButton.IsEnabled = false;
                    SendButton.IsEnabled = false;
                    ServerInfoButton.IsEnabled = false;
                    break;

                case UwpConnectionState.Connecting:
                    StatusText.Text = "连接中...";
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Orange);
                    break;

                case UwpConnectionState.Connected:
                    StatusText.Text = "已连接";
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Green);
                    ConnectButton.IsEnabled = false;
                    DisconnectButton.IsEnabled = true;
                    SendButton.IsEnabled = true;
                    ServerInfoButton.IsEnabled = true;
                    break;

                case UwpConnectionState.Reconnecting:
                    StatusText.Text = "重连中...";
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Yellow);
                    SendButton.IsEnabled = false;
                    ServerInfoButton.IsEnabled = false;
                    break;

                case UwpConnectionState.Faulted:
                    StatusText.Text = "错误";
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Red);
                    ConnectButton.IsEnabled = true;
                    DisconnectButton.IsEnabled = false;
                    SendButton.IsEnabled = false;
                    ServerInfoButton.IsEnabled = false;
                    break;
            }
        }

        private void Log(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            LogText.Text += $"[{timestamp}] {message}\n";
        }
    }
}
