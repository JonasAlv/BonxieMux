using System.Threading.Tasks;

namespace Skua.Core.Interfaces;

public interface IScreenshotService
{
    Task<byte[]> TakeScreenshotAsync();
}
