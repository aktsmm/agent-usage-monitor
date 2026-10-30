using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

static class AutomationNameSmoke
{
    /// <summary>Reads the name a screen reader would receive for an explicitly named control.</summary>
    public static string NameOf(Func<Control> create)
    {
        string? name = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { name = UIElementAutomationPeer.CreatePeerForElement(create())?.GetName(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));
        if (error is not null) throw error;
        return name ?? "";
    }
}
