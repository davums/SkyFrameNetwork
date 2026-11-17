namespace Ledger.Mobile;

public partial class App : Application
{
    public App(MobilePage page)
    {
        InitializeComponent();
        MainPage = page;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);
        window.Width = 410;
        window.Height = 820;
        return window;
    }
}