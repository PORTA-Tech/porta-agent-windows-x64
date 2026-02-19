using Porta.Agent.Common.Environment;

namespace Porta.Agent.Runtime.Render;

internal sealed class NullRenderSurfaceSink : IRenderSurfaceSink
{
    public static readonly NullRenderSurfaceSink Instance = new();

    private NullRenderSurfaceSink()
    {
    }

    public void Draw(RenderSurfaceFrame frame)
    {
        _ = frame;
    }

    public void Dispose()
    {
    }
}
