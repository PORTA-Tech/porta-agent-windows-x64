namespace Porta.Agent.Runtime.Layer1;

internal enum Layer1ConsentState
{
    NotInstalled = 0,
    InstalledNotConsented = 1,
    ConsentedActive = 2,
    PausedByUser = 3,
    Revoked = 4
}
