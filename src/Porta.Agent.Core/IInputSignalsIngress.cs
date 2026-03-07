namespace Porta.Agent.Core;

public interface IInputSignalsIngress
{
    void IngestInputSignals(InputSignalsPacket packet);
}
