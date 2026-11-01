using TeamLigma.Panel.Services;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Views;

public sealed partial class ServersView : UserControl, IPanelPage
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool _busy;

    public ServersView()
    {
        InitializeComponent();
        World.Mode = ConsoleMode.Line;
        Auth.Mode = ConsoleMode.CtrlCOnly;
        MySql.Mode = ConsoleMode.MySql;
        _timer.Tick += async (_, _) => await Poll();
    }

    public void SetActive(bool active)
    {
        if (active) { _timer.Start(); _ = Poll(); }
        else _timer.Stop();
    }

    public void OnOverview(Overview o)
    {
        foreach (var s in o.Servers)
        {
            var panel = s.Name switch { "world" => World, "auth" => Auth, "mysql" => MySql, _ => null };
            panel?.UpdateStatus(s);
        }
    }

    async Task Poll()
    {
        if (_busy) return;
        _busy = true;
        try { await Task.WhenAll(World.PollAsync(), Auth.PollAsync(), MySql.PollAsync()); }
        finally { _busy = false; }
    }
}
