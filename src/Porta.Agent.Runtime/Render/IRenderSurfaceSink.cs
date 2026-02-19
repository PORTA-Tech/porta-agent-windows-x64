using Porta.Agent.Common.Environment;

namespace Porta.Agent.Runtime.Render;

internal interface IRenderSurfaceSink
{
    void Draw(RenderSurfaceFrame frame);
}
