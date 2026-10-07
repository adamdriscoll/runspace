using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Runspace.Desktop;

namespace Runspace.Desktop.Tests;

public sealed partial class ConsoleTests
{
    [AvaloniaFact]
    public void LogoAndMultiResolutionIconAreEmbedded()
    {
        using var logoStream = AssetLoader.Open(new Uri("avares://Runspace.Desktop/Assets/runspace-logo.png"));
        using var logo = new Bitmap(logoStream);
        Assert.Equal(new PixelSize(1254, 1254), logo.PixelSize);

        using var iconStream = AssetLoader.Open(new Uri("avares://Runspace.Desktop/Assets/runspace.ico"));
        using var reader = new BinaryReader(iconStream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        Assert.Equal(sizes.Length, reader.ReadUInt16());
        foreach (var size in sizes)
        {
            var encodedSize = size == 256 ? 0 : size;
            Assert.Equal(encodedSize, reader.ReadByte());
            Assert.Equal(encodedSize, reader.ReadByte());
            Assert.Equal(0, reader.ReadByte());
            Assert.Equal(0, reader.ReadByte());
            Assert.Equal(1, reader.ReadUInt16());
            Assert.Equal(32, reader.ReadUInt16());
            var length = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            Assert.InRange((long)offset + length, 1, iconStream.Length);
            var nextEntry = iconStream.Position;
            iconStream.Position = offset;
            var frame = reader.ReadBytes(checked((int)length));
            Assert.Equal((int)length, frame.Length);
            using var frameStream = new MemoryStream(frame);
            using var image = new Bitmap(frameStream);
            Assert.Equal(new PixelSize(size, size), image.PixelSize);
            iconStream.Position = nextEntry;
        }
    }

    [AvaloniaFact]
    public async Task MainWindowAndDialogsUseApplicationIcon()
    {
        var window = new MainWindow(new FixtureSession());
        window.Show();
        try
        {
            var icon = Assert.IsType<WindowIcon>(Application.Current!.Resources["RunspaceIcon"]);
            Assert.Same(icon, window.Icon);
            var message = Dialogs.MessageAsync(window, "About Runspace", "Runspace");
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Same(icon, dialog.Icon);
            dialog.Close();
            await message;
        }
        finally { window.Close(); }
    }
}
