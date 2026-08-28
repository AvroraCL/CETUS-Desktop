using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cetus.Sidebar;

/// <summary>
/// One terminal tab. Tabs share the single PowerShell session owned by the
/// sidebar view, so every open terminal shows the same output stream.
/// </summary>
public partial class TerminalTabContent : UserControl
{
    private const int MaximumCharacters = 250_000;

    private readonly SidebarTerminalSession _session;
    private bool _attached;

    public TerminalTabContent(SidebarTerminalSession session)
    {
        InitializeComponent();
        _session = session;
        Loaded += (_, _) => Attach();
    }

    /// <summary>Starts receiving the shared session stream (idempotent).</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _session.OutputReceived += OnSessionOutput;
        _session.Exited += OnSessionExited;
        if (!_session.IsRunning)
        {
            try
            {
                _session.Start();
                Append("CETUS PowerShell · 输入命令后按 Enter", isError: false);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                Append($"终端启动失败：{error.Message}", isError: true);
            }
        }
    }

    /// <summary>Stops receiving the stream; the shared session keeps running.</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        _session.OutputReceived -= OnSessionOutput;
        _session.Exited -= OnSessionExited;
    }

    private void OnSessionOutput(string line, bool isError) =>
        Dispatcher.InvokeAsync(() => Append(line, isError));

    private void OnSessionExited() =>
        Dispatcher.InvokeAsync(() => Append("PowerShell 已退出。", isError: true));

    private void OnRunClicked(object sender, RoutedEventArgs e) => SendCommand();

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SendCommand();
        }
    }

    private void SendCommand()
    {
        string command = InputBox.Text;
        InputBox.Clear();
        try
        {
            _session.SendCommand(command);
        }
        catch (InvalidOperationException error)
        {
            Append(error.Message, isError: true);
        }
    }

    private void Append(string line, bool isError)
    {
        if (OutputBox.Text.Length > MaximumCharacters)
        {
            OutputBox.Text = OutputBox.Text[^150_000..];
        }

        if (OutputBox.Text.Length > 0)
        {
            OutputBox.AppendText(Environment.NewLine);
        }

        OutputBox.AppendText(isError ? $"! {line}" : line);
        OutputBox.ScrollToEnd();
    }
}
