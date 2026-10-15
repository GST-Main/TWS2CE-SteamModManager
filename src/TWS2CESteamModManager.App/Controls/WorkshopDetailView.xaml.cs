using System.Windows.Controls;

namespace TWS2CESteamModManager.App.Controls;

public partial class WorkshopDetailView : UserControl
{
    public WorkshopDetailView() => InitializeComponent();
    public void FocusCloseButton() => CloseButton.Focus();
}
