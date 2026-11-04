using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Services;

public class ScreenshotService : IScreenshotService
{
    public async Task<byte[]> TakeScreenshotAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
                    return Array.Empty<byte>();

                var mainWindow = desktop.MainWindow;
                double scaling = mainWindow.RenderScaling;
                int width = (int)Math.Max(1, mainWindow.Bounds.Width * scaling);
                int height = (int)Math.Max(1, mainWindow.Bounds.Height * scaling);

                var pixelSize = new PixelSize(width, height);
                var dpi = new Vector(96 * scaling, 96 * scaling);

                using var rtb = new RenderTargetBitmap(pixelSize, dpi);
                rtb.Render(mainWindow);

                using var ms = new MemoryStream();
                rtb.Save(ms);
                return ms.ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        });
    }
}
