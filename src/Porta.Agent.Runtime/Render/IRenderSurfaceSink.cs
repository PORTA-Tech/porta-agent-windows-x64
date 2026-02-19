using Porta.Agent.Common.Environment;

namespace Porta.Agent.Runtime.Render;

internal interface IRenderSurfaceSink : IDisposable
{
    void Draw(RenderSurfaceFrame frame);
}
