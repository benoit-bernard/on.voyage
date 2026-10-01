namespace OnVoyage.App;

public partial class MainPage : ContentPage
{
    public MainPage() => InitializeComponent();

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        Handler?.MauiContext?.Services.GetService<MediaElementAudioPlayer>()?.Attach(mediaElement);
    }
}
